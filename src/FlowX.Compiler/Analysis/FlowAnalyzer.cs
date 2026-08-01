using System.Collections.Generic;
using System.Linq;
using FlowX.Compiler.Diagnostics;
using FlowX.Compiler.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FlowX.Compiler.Analysis;

/// <summary>A flow model, or the diagnostics explaining why there is not one.</summary>
public sealed class AnalysisResult
{
    private AnalysisResult(FlowModel? model, IReadOnlyList<Diagnostic> diagnostics)
    {
        Model = model;
        Diagnostics = diagnostics;
    }

    /// <summary>The analysed flow, or <c>null</c> when analysis failed.</summary>
    public FlowModel? Model { get; }

    /// <summary>Everything to report, whether or not a model was produced.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>True when a model was produced and nothing blocking was found.</summary>
    public bool IsSuccess => Model is not null && !Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    internal static AnalysisResult Success(FlowModel model, IReadOnlyList<Diagnostic> diagnostics) =>
        new AnalysisResult(model, diagnostics);

    internal static AnalysisResult Failure(IReadOnlyList<Diagnostic> diagnostics) =>
        new AnalysisResult(null, diagnostics);
}

/// <summary>
/// Turns a flow declaration into a <see cref="FlowModel"/>, reporting diagnostics for
/// anything it cannot.
/// </summary>
/// <remarks>
/// <para>
/// The <strong>only</strong> place where Roslyn symbols and the model meet. Everything
/// above this reads syntax; everything below it reads plain objects. That boundary is
/// the R1 mitigation, and <c>ModelLayerHasNoRoslynDependency</c> keeps it honest.
/// </para>
/// <para>
/// Analysis is deliberately forgiving about what it does not understand: an unknown
/// chain method is skipped rather than reported. The DSL grows across phases, and a
/// generator that errors on every method it has not learned yet would block P1 work on
/// P0 code.
/// </para>
/// </remarks>
public static class FlowAnalyzer
{
    private const string FlowAttribute = "FlowX.FlowAttribute";
    private const string FlowDeadlineAttribute = "FlowX.FlowDeadlineAttribute";
    private const string SensitiveAttribute = "FlowX.SensitiveAttribute";

    /// <summary>Analyses one flow type.</summary>
    /// <param name="flowType">The class carrying <c>[Flow]</c>.</param>
    /// <param name="declaration">Its syntax, used for locations and for the Define body.</param>
    /// <param name="semanticModel">The model that resolves the chain's type arguments.</param>
    public static AnalysisResult Analyze(
        INamedTypeSymbol flowType,
        ClassDeclarationSyntax declaration,
        SemanticModel semanticModel)
    {
        var diagnostics = new List<Diagnostic>();

        if (flowType is null || declaration is null || semanticModel is null)
        {
            return AnalysisResult.Failure(diagnostics);
        }

        var flowAttribute = flowType.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == FlowAttribute);

        if (flowAttribute is null || flowAttribute.ConstructorArguments.Length == 0)
        {
            return AnalysisResult.Failure(diagnostics);
        }

        // FLOWX1001 — the generated plan is emitted as a second part of this class.
        if (!declaration.Modifiers.Any(m => m.ValueText == "partial"))
        {
            diagnostics.Add(Diagnostic.Create(
                FlowXDiagnostics.FlowMustBePartial,
                declaration.Identifier.GetLocation(),
                flowType.Name));

            return AnalysisResult.Failure(diagnostics);
        }

        // FLOWX1005 — inheritance hides control flow from the compiled graph.
        var baseFlow = flowType.BaseType;

        if (baseFlow is not null && baseFlow.GetAttributes()
                .Any(a => a.AttributeClass?.ToDisplayString() == FlowAttribute))
        {
            diagnostics.Add(Diagnostic.Create(
                FlowXDiagnostics.FlowInheritsFlow,
                declaration.Identifier.GetLocation(),
                flowType.Name,
                baseFlow.Name));

            return AnalysisResult.Failure(diagnostics);
        }

        var define = FindDefine(declaration);
        var links = FlowChainWalker.Walk(FindDefineBody(define), FindBuilderParameterName(define));
        var steps = BuildSteps(links, semanticModel, diagnostics);

        // FLOWX1023 — an empty flow has no observable behaviour.
        if (steps.Count == 0)
        {
            diagnostics.Add(Diagnostic.Create(
                FlowXDiagnostics.FlowHasNoSteps,
                declaration.Identifier.GetLocation(),
                flowType.Name));

            return AnalysisResult.Failure(diagnostics);
        }

        var profile = ReadProfile(flowAttribute);

        // FLOWX1017 — an in-memory wait does not survive a deployment. Searched across
        // nested blocks too: a suspension point hidden inside a `When` is no more durable
        // than one at the top level, and only looking at the top level is how a rule like
        // this quietly stops applying the day branching lands.
        if (profile != "Durable" &&
            steps.SelectMany(s => s.SelfAndNested).Any(s => s.Kind == StepKindModel.AwaitSignal))
        {
            diagnostics.Add(Diagnostic.Create(
                FlowXDiagnostics.AwaitSignalRequiresDurable,
                declaration.Identifier.GetLocation(),
                flowType.Name,
                profile));

            return AnalysisResult.Failure(diagnostics);
        }

        var contracts = ReadFlowContracts(flowType);
        var returnClause = FindReturnClause(links);

        var model = new FlowModel(
            flowId: flowAttribute.ConstructorArguments[0].Value as string ?? flowType.Name,
            version: ReadNamedString(flowAttribute, "Version") ?? "1.0.0",
            profile: profile,
            deadline: ReadDeadline(flowType),
            containingNamespace: flowType.ContainingNamespace.IsGlobalNamespace
                ? string.Empty
                : flowType.ContainingNamespace.ToDisplayString(),
            typeName: flowType.Name,
            inputTypeName: contracts.Input,
            outputTypeName: contracts.Output,
            sensitiveInputMembers: contracts.SensitiveInput,
            sensitiveOutputMembers: contracts.SensitiveOutput,
            steps: steps,
            declarationLocation: FormatLocation(declaration.Identifier.GetLocation()),
            returnProjection: returnClause?.Text,
            returnLocation: returnClause?.Location,
            usings: ReadUsings(declaration));

        return AnalysisResult.Success(model, diagnostics);
    }

    /// <summary>
    /// Reads the <c>.Return(...)</c> lambda's source text, or <c>null</c> when the flow
    /// declares none.
    /// </summary>
    /// <remarks>
    /// The last <c>Return</c> wins, matching what the chain actually does: a builder that
    /// saw two would have overwritten the first.
    /// </remarks>
    private static ReturnClause? FindReturnClause(IReadOnlyList<ChainLink> links)
    {
        for (var i = links.Count - 1; i >= 0; i--)
        {
            var link = links[i];

            if (link.MethodName != "Return")
            {
                continue;
            }

            var arguments = link.Invocation.ArgumentList.Arguments;

            if (arguments.Count == 0)
            {
                return null;
            }

            return new ReturnClause(
                arguments[0].Expression.ToString(),
                FormatLocation(arguments[0].Expression.GetLocation()));
        }

        return null;
    }

    /// <summary>
    /// The <c>using</c> directives in scope where the flow was declared, as source text.
    /// </summary>
    /// <remarks>
    /// Both file-level and namespace-level directives, because the emitted projection is
    /// the author's verbatim text and has to resolve the same names it did in their file.
    /// Global usings are not included: they are already in scope in the generated file,
    /// which is part of the same compilation.
    /// </remarks>
    private static List<string> ReadUsings(ClassDeclarationSyntax declaration)
    {
        var usings = new List<string>();

        for (SyntaxNode? node = declaration; node is not null; node = node.Parent)
        {
            var directives = node switch
            {
                CompilationUnitSyntax unit => unit.Usings,
                NamespaceDeclarationSyntax ns => ns.Usings,
                FileScopedNamespaceDeclarationSyntax file => file.Usings,
                _ => default,
            };

            foreach (var directive in directives)
            {
                var text = directive.ToString();

                if (!usings.Contains(text))
                {
                    usings.Add(text);
                }
            }
        }

        return usings;
    }

    /// <summary>The text and location of a <c>.Return(...)</c> lambda.</summary>
    private sealed class ReturnClause
    {
        internal ReturnClause(string text, string? location)
        {
            Text = text;
            Location = location;
        }

        internal string Text { get; }

        internal string? Location { get; }
    }

    private static List<StepModel> BuildSteps(
        IReadOnlyList<ChainLink> links,
        SemanticModel semanticModel,
        List<Diagnostic> diagnostics)
    {
        var nextIndex = 0;

        return BuildBlock(links, semanticModel, diagnostics, ref nextIndex);
    }

    /// <summary>
    /// Builds one block of the chain — the whole <c>Define</c> body, or the body of a
    /// <c>then</c> or <c>Otherwise</c> lambda.
    /// </summary>
    /// <param name="links">The block's chain, in source order.</param>
    /// <param name="semanticModel">Resolves the chain's type arguments.</param>
    /// <param name="diagnostics">Collects everything worth reporting.</param>
    /// <param name="nextIndex">
    /// The flat index counter, shared by every block. Nesting is a modelling convenience;
    /// the compiled graph is one flat array, so indices are handed out here in the order
    /// the array will be laid out and never renumbered afterwards.
    /// </param>
    private static List<StepModel> BuildBlock(
        IReadOnlyList<ChainLink> links,
        SemanticModel semanticModel,
        List<Diagnostic> diagnostics,
        ref int nextIndex)
    {
        var steps = new List<StepModel>();

        for (var i = 0; i < links.Count; i++)
        {
            var link = links[i];

            switch (link.MethodName)
            {
                case "Step":
                    AddCapabilityStep(link, semanticModel, diagnostics, steps, ref nextIndex);
                    break;

                case "Emit":
                case "EmitOnFailure":
                    AddEventStep(link, semanticModel, diagnostics, steps, ref nextIndex);
                    break;

                case "AwaitSignal":
                    AddSignalStep(link, semanticModel, diagnostics, steps, ref nextIndex);
                    break;

                case "Delay":
                case "OnTimeout":
                    // FLOWX1031. These two reach the `default:` arm below and are skipped,
                    // which is the right treatment for a method the generator has not
                    // learned — and the wrong silence for one the DSL already publishes.
                    // Naming them here is the whole difference between the two cases: the
                    // call is still not laid out, and the author is told it is not.
                    ReportSuspension(link, diagnostics, DiagnosticSeverity.Warning, WhatIsLost(link.MethodName));
                    break;

                case "Fail":
                    // Terminal. Everything else in this switch says what happens next;
                    // this says there is no next, which is why the block ends here rather
                    // than carrying on and laying out steps the engine can never reach.
                    AddFailStep(link, steps, ref nextIndex);
                    ReportUnreachable(links, i, diagnostics);

                    return steps;

                case "CompensateWith":
                    AttachCompensation(link, semanticModel, steps);
                    break;

                case "WithPolicy":
                    AttachPolicy(link, semanticModel, diagnostics, steps);
                    break;

                case "When":
                    // `Otherwise` is the *next link*, not an argument: the DSL spells the
                    // pair `.When(predicate, then).Otherwise(alternative)`, so the two
                    // halves of one conditional arrive as two siblings. Consuming both
                    // here is what stops the alternative being modelled as a step of its
                    // own that runs unconditionally after the `then` block.
                    i += AddConditionStep(
                        link, NextOtherwise(links, i), semanticModel, diagnostics, steps, ref nextIndex);
                    break;

                case "Switch":
                    // Same reasoning as `When`: `.Case(...)` and `.Default(...)` are the
                    // *next links*, not arguments, because the DSL spells the whole thing
                    // as one chain. Consuming them here is what stops each case block
                    // being modelled as steps that run unconditionally in sequence.
                    i += AddSwitchStep(links, i, semanticModel, diagnostics, steps, ref nextIndex);
                    break;

                case "Parallel":
                    // Unlike When and Switch, the whole shape is one call: the branches
                    // arrive as a lambda argument rather than as later links, because
                    // `.Branch(...)` belongs to IParallelBuilder and not to IFlowBuilder.
                    // So nothing after this link is consumed.
                    AddParallelStep(link, semanticModel, diagnostics, steps, ref nextIndex);
                    break;

                case "ForEach":
                    // One call, like Parallel and unlike When and Switch: the body arrives
                    // as a lambda argument rather than as a later link, so nothing after
                    // this link is consumed.
                    AddForEachStep(link, semanticModel, diagnostics, steps, ref nextIndex);
                    break;

                case "SubFlow":
                    // One call and — uniquely — no block at all. The steps it runs belong
                    // to another flow's own model, so there is nothing to lay out and no
                    // trial pass to do.
                    AddSubFlowStep(link, semanticModel, diagnostics, steps, ref nextIndex);
                    break;

                default:
                    // Return, and anything the DSL grows in a later phase. Skipped rather
                    // than reported — see the class remarks on why a generator must not
                    // error on methods it has not learned yet.
                    break;
            }
        }

        return steps;
    }

    /// <summary>
    /// Models a <c>.Fail(error)</c>: one terminal step carrying the author's error
    /// expression, verbatim.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Verbatim, and no attempt to read the error's code.</strong> Following
    /// <c>OrderErrors.UnsupportedChannel</c> back to the <c>new Error(code, …)</c> that
    /// produced it is real work with a real failure mode — a factory in a referenced
    /// assembly, a code composed at run time — and <see cref="ErrorCatalogueReader"/>
    /// already does it, soundly, with an <c>IsComplete</c> flag for the cases it cannot
    /// follow. A second, weaker resolver here would publish a guess where that one
    /// publishes nothing. So the expression reaches the generated dispatcher, which is
    /// where the case values already live, and the manifest records only that the arm
    /// fails.
    /// </para>
    /// <para>
    /// No diagnostic for a <c>.Fail</c> the compiler cannot read, because there is nothing
    /// to read: unlike a <c>SubFlowMode</c>, the expression is copied through untouched and
    /// executes exactly as written however it was written.
    /// </para>
    /// </remarks>
    private static void AddFailStep(ChainLink link, List<StepModel> steps, ref int nextIndex)
    {
        var arguments = link.Invocation.ArgumentList.Arguments;

        // `.Fail(error)` takes one argument, so a call without it does not compile.
        // Reachable only from a half-typed buffer, where the C# compiler is already saying
        // something more useful than a FlowX diagnostic would.
        if (arguments.Count == 0)
        {
            return;
        }

        steps.Add(StepModel.Fail(
            nextIndex++,
            arguments[0].Expression.ToString(),
            FormatLocation(arguments[0].Expression.GetLocation()),
            FormatLocation(link.CallLocation)));
    }

    /// <summary>Chain methods that would have become a step had the block not already ended.</summary>
    private static readonly HashSet<string> StepProducingMethods = new HashSet<string>(System.StringComparer.Ordinal)
    {
        "Step", "Emit", "EmitOnFailure", "AwaitSignal", "When", "Switch", "Parallel",
        "ForEach", "SubFlow", "Fail", "Delay",
    };

    /// <summary>
    /// Reports FLOWX1027 for the first step declared after a <c>.Fail(...)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Once, against the first one, rather than once per unreachable link. The author made
    /// a single mistake — they wrote a terminal step in the middle of a block — and a
    /// diagnostic per step after it would report the same mistake five times and bury it.
    /// </para>
    /// <para>
    /// <c>Return</c> is deliberately not in the list. It declares no step: it is the
    /// flow's output projection, it is read off the chain rather than laid out, and the
    /// engine already does not run it when the flow failed. Reporting it would fire on
    /// <c>flow.Fail(e).Return(…)</c>, which is the one shape a flow that always rejects
    /// has to be written in for the DSL to type-check.
    /// </para>
    /// </remarks>
    private static void ReportUnreachable(IReadOnlyList<ChainLink> links, int failIndex, List<Diagnostic> diagnostics)
    {
        for (var i = failIndex + 1; i < links.Count; i++)
        {
            if (!StepProducingMethods.Contains(links[i].MethodName))
            {
                continue;
            }

            diagnostics.Add(Diagnostic.Create(
                FlowXDiagnostics.StepIsUnreachableAfterFail,
                links[i].CallLocation,
                EnclosingFlowName(links[i]),
                links[i].MethodName));

            return;
        }
    }

    /// <summary>The <c>.Otherwise(...)</c> immediately following the link at <paramref name="index"/>, if any.</summary>
    private static ChainLink? NextOtherwise(IReadOnlyList<ChainLink> links, int index) =>
        index + 1 < links.Count && links[index + 1].MethodName == "Otherwise"
            ? links[index + 1]
            : null;

    /// <summary>
    /// Models a conditional and lays out its blocks in the flat index space.
    /// </summary>
    /// <returns>How many further links were consumed: 1 for an <c>Otherwise</c>, 0 without.</returns>
    private static int AddConditionStep(
        ChainLink when,
        ChainLink? otherwise,
        SemanticModel semanticModel,
        List<Diagnostic> diagnostics,
        List<StepModel> steps,
        ref int nextIndex)
    {
        var arguments = when.Invocation.ArgumentList.Arguments;

        if (arguments.Count < 2)
        {
            // `.When(predicate, then)` takes both, so a call missing one does not compile.
            // Reachable only from a half-typed buffer in the IDE, where the C# compiler is
            // already saying something more useful than a FlowX diagnostic would.
            return 0;
        }

        var branchIndex = nextIndex++;
        var then = BuildBlock(FlowChainWalker.WalkBlock(when, 1), semanticModel, diagnostics, ref nextIndex);
        var alternative = new List<StepModel>();

        if (otherwise is not null)
        {
            // The jump that closes the `then` block occupies the slot before the
            // alternative, so the alternative is numbered from one past it. An
            // `.Otherwise(o => { })` that declares nothing needs no jump to skip over it,
            // and the reserved slot is given back rather than left as a gap — a gap would
            // fail StepGraph's contiguity check at type initialisation.
            var afterJump = nextIndex + 1;
            alternative = BuildBlock(FlowChainWalker.WalkBlock(otherwise, 0), semanticModel, diagnostics, ref afterJump);

            if (alternative.Count > 0)
            {
                nextIndex = afterJump;
            }
        }

        steps.Add(StepModel.Condition(
            branchIndex,
            arguments[0].Expression.ToString(),
            then,
            alternative,
            FormatLocation(arguments[0].Expression.GetLocation()),
            FormatLocation(when.CallLocation)));

        return otherwise is null ? 0 : 1;
    }

    /// <summary>
    /// Models a <c>.Switch(selector).Case(…).Default(…)</c> and lays its blocks out in the
    /// flat index space.
    /// </summary>
    /// <param name="links">The enclosing block's chain.</param>
    /// <param name="switchIndexInChain">Position of the <c>.Switch</c> link in it.</param>
    /// <param name="semanticModel">Resolves the selector's value type.</param>
    /// <param name="diagnostics">Collects everything worth reporting.</param>
    /// <param name="steps">The block being built.</param>
    /// <param name="nextIndex">The shared flat index counter.</param>
    /// <returns>How many further links were consumed — one per <c>Case</c>, plus one for a <c>Default</c>.</returns>
    /// <remarks>
    /// <para>
    /// The layout walk is the general form of the one <see cref="AddConditionStep"/> does
    /// for two blocks: a block is built at one past the previously committed index when
    /// the previous block needs a closing jump, and the reserved slot is given back when
    /// the block turns out to declare nothing. A reserved-and-unused slot would be a gap,
    /// and <c>StepGraph</c> rejects a gap at type initialisation.
    /// </para>
    /// <para>
    /// A <c>Switch</c> with no <c>Case</c> at all is not modelled as a switch: there is
    /// nothing to select between, so its <c>Default</c> block always runs and is laid out
    /// inline. Emitting a one-armed selector instead would put a decision in the manifest
    /// that the flow does not make.
    /// </para>
    /// </remarks>
    private static int AddSwitchStep(
        IReadOnlyList<ChainLink> links,
        int switchIndexInChain,
        SemanticModel semanticModel,
        List<Diagnostic> diagnostics,
        List<StepModel> steps,
        ref int nextIndex)
    {
        var switchLink = links[switchIndexInChain];
        var arguments = switchLink.Invocation.ArgumentList.Arguments;

        var caseLinks = new List<ChainLink>();
        ChainLink? defaultLink = null;
        var consumed = 0;

        for (var i = switchIndexInChain + 1; i < links.Count; i++)
        {
            if (links[i].MethodName == "Case")
            {
                caseLinks.Add(links[i]);
                consumed++;
                continue;
            }

            if (links[i].MethodName == "Default")
            {
                defaultLink = links[i];
                consumed++;
            }

            // `Default` returns the plain builder, so nothing belonging to this switch can
            // follow it — and the first link that is neither ends the switch either way.
            break;
        }

        if (caseLinks.Count == 0)
        {
            if (defaultLink is not null)
            {
                steps.AddRange(BuildBlock(
                    FlowChainWalker.WalkBlock(defaultLink, 0), semanticModel, diagnostics, ref nextIndex));
            }

            return consumed;
        }

        // `.Switch(selector)` takes one argument, so a call without it does not compile.
        // Reachable only from a half-typed buffer, where the C# compiler is already
        // saying something more useful than a FlowX diagnostic would.
        if (arguments.Count == 0)
        {
            return consumed;
        }

        var valueType = ResolveSingleTypeArgument(switchLink, semanticModel);

        if (valueType is null)
        {
            // The emitted selector is a typed field; without the type there is no
            // compilable shape to emit, and guessing `object` would box every value the
            // cases are compared against.
            return consumed;
        }

        var switchIndex = nextIndex++;
        var committed = nextIndex;
        var previousNeedsJump = false;

        var cases = new List<SwitchCaseModel>(caseLinks.Count);

        foreach (var caseLink in caseLinks)
        {
            var caseArguments = caseLink.Invocation.ArgumentList.Arguments;

            if (caseArguments.Count < 2)
            {
                continue;
            }

            var block = LayOutBlock(
                caseLink, 1, semanticModel, diagnostics, ref committed, ref previousNeedsJump);

            cases.Add(new SwitchCaseModel(
                caseArguments[0].Expression.ToString(),
                block,
                FormatLocation(caseArguments[0].Expression.GetLocation())));
        }

        var alternative = defaultLink is null
            ? new List<StepModel>()
            : LayOutBlock(defaultLink, 0, semanticModel, diagnostics, ref committed, ref previousNeedsJump);

        nextIndex = committed;

        steps.Add(StepModel.Switch(
            switchIndex,
            arguments[0].Expression.ToString(),
            valueType,
            cases,
            alternative,
            FormatLocation(arguments[0].Expression.GetLocation()),
            FormatLocation(switchLink.CallLocation)));

        return consumed;
    }

    /// <summary>
    /// Models a <c>.Parallel(p =&gt; p.Branch…(), merge)</c> and lays its branches out in the
    /// flat index space.
    /// </summary>
    /// <param name="link">The <c>.Parallel</c> call.</param>
    /// <param name="semanticModel">Resolves the branches' capability types.</param>
    /// <param name="diagnostics">Collects everything worth reporting.</param>
    /// <param name="steps">The block being built.</param>
    /// <param name="nextIndex">The shared flat index counter.</param>
    /// <remarks>
    /// <para>
    /// Simpler than <see cref="AddSwitchStep"/> in one respect and fussier in another. It
    /// is simpler because branches need no closing jumps — a branch's range ends where the
    /// next one begins — so the blocks are laid out back to back with no reserved slots.
    /// It is fussier because a fork with fewer than two runnable branches is not a fork,
    /// and whether a branch is runnable is only known after it has been built.
    /// </para>
    /// <para>
    /// <strong>Why that is worth a trial pass.</strong> The fork occupies an index of its
    /// own, so the branches have to be numbered from one past it — and if the fork then
    /// turns out not to exist, every one of those numbers is wrong by one, which
    /// <c>StepGraph</c> rejects as a gap at type initialisation. Building once into a
    /// scratch diagnostics list and keeping whichever layout is correct costs a rebuild in
    /// a case nobody writes on purpose, and avoids either renumbering an immutable model or
    /// reporting every diagnostic twice.
    /// </para>
    /// <para>
    /// A <c>Parallel</c> with fewer than two <c>.Branch(...)</c> calls is laid out inline,
    /// exactly as a <c>Switch</c> with no <c>Case</c> is: running one thing concurrently is
    /// running it, and publishing a fork the flow does not make would put a lie in the
    /// manifest.
    /// </para>
    /// </remarks>
    private static void AddParallelStep(
        ChainLink link,
        SemanticModel semanticModel,
        List<Diagnostic> diagnostics,
        List<StepModel> steps,
        ref int nextIndex)
    {
        var arguments = link.Invocation.ArgumentList.Arguments;

        // `.Parallel(branches, merge)` takes both, so a call missing one does not compile.
        // Reachable only from a half-typed buffer, where the C# compiler is already saying
        // something more useful than a FlowX diagnostic would.
        if (arguments.Count < 2)
        {
            return;
        }

        var branchLinks = new List<ChainLink>();

        foreach (var candidate in FlowChainWalker.WalkBlock(link, 0))
        {
            if (candidate.MethodName == "Branch")
            {
                branchLinks.Add(candidate);
            }
        }

        if (branchLinks.Count < 2)
        {
            LayOutInline(branchLinks, semanticModel, diagnostics, steps, ref nextIndex);
            return;
        }

        // Trial: number the branches from one past the fork's own slot.
        var trialDiagnostics = new List<Diagnostic>();
        var trialCursor = nextIndex + 1;
        var branches = BuildBranches(branchLinks, semanticModel, trialDiagnostics, ref trialCursor);
        var runnable = 0;

        foreach (var branch in branches)
        {
            if (branch.Steps.Count > 0)
            {
                runnable++;
            }
        }

        if (runnable < 2)
        {
            // Not a fork after all — an empty `.Branch(b => { })`, or a branch whose body
            // is a method group the walker cannot see into. Discard the trial, including
            // its diagnostics, and lay the survivors out as an ordinary sequence.
            LayOutInline(branchLinks, semanticModel, diagnostics, steps, ref nextIndex);
            return;
        }

        var forkIndex = nextIndex;

        nextIndex = trialCursor;
        diagnostics.AddRange(trialDiagnostics);

        var merge = arguments[1].Expression;

        steps.Add(StepModel.Parallel(
            forkIndex,
            branches,
            merge.ToString(),
            ReadMergeKind(merge),
            FormatLocation(link.CallLocation)));
    }

    /// <summary>
    /// Models a <c>.ForEach(selector, body, options)</c> and lays its body out in the flat
    /// index space.
    /// </summary>
    /// <param name="link">The <c>.ForEach</c> call.</param>
    /// <param name="semanticModel">Resolves the element type and the body's capabilities.</param>
    /// <param name="diagnostics">Collects everything worth reporting.</param>
    /// <param name="steps">The block being built.</param>
    /// <param name="nextIndex">The shared flat index counter.</param>
    /// <remarks>
    /// <para>
    /// The same trial-layout shape <see cref="AddParallelStep"/> uses, and for the same
    /// reason: the loop occupies an index of its own, so its body has to be numbered from
    /// one past it — and if the body turns out to be empty, that reservation is a gap, which
    /// <c>StepGraph</c> rejects at type initialisation. Building into a scratch diagnostics
    /// list and keeping the layout only when there is a body costs a rebuild in a case
    /// nobody writes on purpose.
    /// </para>
    /// <para>
    /// <strong>A <c>ForEach</c> with an empty body is not laid out at all</strong> — not
    /// even as its steps, because there are none. That is the same treatment a
    /// <c>Switch</c> with no <c>Case</c> and a <c>Parallel</c> with one branch get: a shape
    /// the flow does not really have is not published as one.
    /// </para>
    /// </remarks>
    private static void AddForEachStep(
        ChainLink link,
        SemanticModel semanticModel,
        List<Diagnostic> diagnostics,
        List<StepModel> steps,
        ref int nextIndex)
    {
        var arguments = link.Invocation.ArgumentList.Arguments;

        // `.ForEach(selector, body, options)` takes all three, so a call missing one does
        // not compile. Reachable only from a half-typed buffer, where the C# compiler is
        // already saying something more useful than a FlowX diagnostic would.
        if (arguments.Count < 3)
        {
            return;
        }

        var itemType = ResolveSingleTypeArgument(link, semanticModel);

        if (itemType is null)
        {
            // The emitted selector is a typed field; without the element type there is no
            // compilable shape to emit, and guessing `object` would box every element.
            return;
        }

        var trialDiagnostics = new List<Diagnostic>();
        var trialCursor = nextIndex + 1;

        var body = BuildBlock(
            FlowChainWalker.WalkBlock(link, 1), semanticModel, trialDiagnostics, ref trialCursor);

        if (body.Count == 0)
        {
            return;
        }

        var loopIndex = nextIndex;

        nextIndex = trialCursor;
        diagnostics.AddRange(trialDiagnostics);

        steps.Add(StepModel.ForEach(
            loopIndex,
            arguments[0].Expression.ToString(),
            itemType,
            body,
            arguments[2].Expression.ToString(),
            FormatLocation(arguments[0].Expression.GetLocation()),
            FormatLocation(link.CallLocation)));
    }

    /// <summary>
    /// Models a <c>.SubFlow&lt;TFlow, TSubIn&gt;(map, mode)</c>.
    /// </summary>
    /// <param name="link">The <c>.SubFlow</c> call.</param>
    /// <param name="semanticModel">Resolves the child flow and the mapped input type.</param>
    /// <param name="diagnostics">Collects everything worth reporting.</param>
    /// <param name="steps">The block being built.</param>
    /// <param name="nextIndex">The shared flat index counter.</param>
    /// <remarks>
    /// <para>
    /// <strong>The simplest of the composite shapes to lay out, because it has no
    /// layout.</strong> A conditional reserves slots for jumps, a switch walks blocks in
    /// order, a fork and a loop both need a trial pass in case the block turns out to be
    /// empty. This takes one index and stops: the child's steps are in the child's model,
    /// and this flow says only <em>which</em> child.
    /// </para>
    /// <para>
    /// <strong>Two refusals rather than two silences.</strong> A target with no
    /// <c>[Flow]</c> attribute has no compiled plan to run, and <c>AwaitCompletion</c> has
    /// no journal to suspend into. Skipping either would drop the step from the plan and the
    /// manifest and ship a flow missing the composition its author wrote, so both are
    /// FLOWX1026. What is <em>not</em> reported here is a cycle: that is a question about
    /// the whole compilation and no single chain link can answer it —
    /// <see cref="SubFlowCycleAnalyzer"/> does.
    /// </para>
    /// </remarks>
    private static void AddSubFlowStep(
        ChainLink link,
        SemanticModel semanticModel,
        List<Diagnostic> diagnostics,
        List<StepModel> steps,
        ref int nextIndex)
    {
        var arguments = link.Invocation.ArgumentList.Arguments;

        // `.SubFlow<TFlow, TSubIn>(map)` takes the mapping, so a call without it does not
        // compile. Reachable only from a half-typed buffer, where the C# compiler is
        // already saying something more useful than a FlowX diagnostic would.
        if (arguments.Count == 0 || link.TypeArguments.Count == 0)
        {
            return;
        }

        var flowName = EnclosingFlowName(link);
        var target = ResolveType(link.TypeArguments[0], semanticModel);

        if (target is null || target.TypeKind == TypeKind.Error)
        {
            // The type does not bind, which means the file does not compile, and that
            // message is better than this one.
            return;
        }

        var flowAttribute = target.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == FlowAttribute);

        // FLOWX1026 — a flow class with no [Flow] has no generated plan, so there is
        // nothing to compose and nothing the emitter could name.
        if (flowAttribute is null || flowAttribute.ConstructorArguments.Length == 0)
        {
            diagnostics.Add(Diagnostic.Create(
                FlowXDiagnostics.SubFlowCannotBeComposed,
                link.TypeArguments[0].GetLocation(),
                flowName,
                $"'{target.Name}' carries no [Flow] attribute, so nothing generates a plan " +
                "for it and there is no compiled flow to run"));

            return;
        }

        var mode = ReadSubFlowMode(arguments);

        // FLOWX1026 — AwaitCompletion suspends the parent, and a suspension point needs a
        // journal to suspend into. Neither degenerate form is honest: running it inline
        // changes the parent's deadline and failure semantics, and skipping it drops
        // business logic.
        if (mode is null or "AwaitCompletion")
        {
            diagnostics.Add(Diagnostic.Create(
                FlowXDiagnostics.SubFlowCannotBeComposed,
                link.CallLocation,
                flowName,
                mode is null
                    ? "the mode is an expression this compiler cannot read, and the mode " +
                      "decides whether the parent waits, whether the child's failure is the " +
                      "parent's and whose deadline applies — write SubFlowMode.Inline or " +
                      "SubFlowMode.Detached directly"
                    : "AwaitCompletion suspends the parent until the child completes, which " +
                      "needs a durable suspension point, and there is no journal to suspend " +
                      "into in this release"));

            return;
        }

        var inputType = ResolveSubFlowInput(link, semanticModel);

        if (inputType is null)
        {
            // The emitted mapping is a typed field; without the type there is no compilable
            // shape to emit, and guessing `object` would box every input.
            return;
        }

        steps.Add(StepModel.SubFlow(
            nextIndex++,
            flowAttribute.ConstructorArguments[0].Value as string ?? target.Name,
            Display(target),
            inputType,
            arguments[0].Expression.ToString(),
            mode,
            FormatLocation(arguments[0].Expression.GetLocation()),
            FormatLocation(link.CallLocation)));
    }

    /// <summary>
    /// Names the <c>SubFlowMode</c> a call selects, or <c>null</c> when it cannot be read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read syntactically, from <c>SubFlowMode.Detached</c> or a bare <c>Detached</c>, and
    /// defaulted to <c>Inline</c> when the argument is omitted — which is the DSL's own
    /// default parameter value and therefore the only answer that can be right.
    /// </para>
    /// <para>
    /// <strong>Unlike <c>merge:</c> and <c>options:</c>, an unreadable mode is refused
    /// rather than copied through.</strong> Those two carry numbers, and a plan that copies
    /// the author's expression verbatim executes correctly however it was written; only the
    /// manifest's label is lost. A mode is not a number — it decides whether the parent
    /// waits for the child, whether the child's failure fails the parent, and whose deadline
    /// applies. A mode the compiler cannot read is a mode FLOWX1026 cannot check and the
    /// manifest cannot publish, so it is reported instead of guessed.
    /// </para>
    /// </remarks>
    private static string? ReadSubFlowMode(SeparatedSyntaxList<ArgumentSyntax> arguments)
    {
        if (arguments.Count < 2)
        {
            return "Inline";
        }

        var name = arguments[1].Expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            _ => null,
        };

        return name switch
        {
            "Inline" => "Inline",
            "Detached" => "Detached",
            "AwaitCompletion" => "AwaitCompletion",
            _ => null,
        };
    }

    /// <summary>
    /// The fully-qualified input contract C# inferred for a <c>.SubFlow&lt;TFlow,
    /// TSubIn&gt;(...)</c> call.
    /// </summary>
    /// <remarks>
    /// Read from the resolved method's <em>second</em> type argument rather than from the
    /// lambda body, because that is what C# itself inferred — and the emitted mapping is a
    /// field typed at it, so a second opinion that disagreed with the compiler's would not
    /// compile.
    /// </remarks>
    private static string? ResolveSubFlowInput(ChainLink link, SemanticModel semanticModel)
    {
        var method = semanticModel.GetSymbolInfo(link.Invocation).Symbol as IMethodSymbol;

        return method is { TypeArguments.Length: 2 } && method.TypeArguments[1].TypeKind != TypeKind.Error
            ? Display(method.TypeArguments[1])
            : null;
    }

    /// <summary>The name of the flow class a chain link was written in, for a message.</summary>
    /// <remarks>
    /// Taken from the syntax rather than passed down, because <see cref="BuildBlock"/> is
    /// reached from six places and threading the flow's name through all of them to reach
    /// one diagnostic would be a parameter every caller has to carry and none of them reads.
    /// </remarks>
    private static string EnclosingFlowName(ChainLink link)
    {
        for (SyntaxNode? node = link.Invocation; node is not null; node = node.Parent)
        {
            if (node is ClassDeclarationSyntax declaration)
            {
                return declaration.Identifier.ValueText;
            }
        }

        return "(unknown)";
    }

    /// <summary>Builds every branch block, numbering them back to back from the cursor.</summary>
    private static List<ParallelBranchModel> BuildBranches(
        List<ChainLink> branchLinks,
        SemanticModel semanticModel,
        List<Diagnostic> diagnostics,
        ref int cursor)
    {
        var branches = new List<ParallelBranchModel>(branchLinks.Count);

        foreach (var branchLink in branchLinks)
        {
            branches.Add(new ParallelBranchModel(
                BuildBranchBlock(branchLink, semanticModel, diagnostics, ref cursor),
                FormatLocation(branchLink.CallLocation)));
        }

        return branches;
    }

    /// <summary>
    /// Builds one branch: a single capability for <c>.Branch&lt;T&gt;()</c>, or a whole
    /// chain for <c>.Branch(b =&gt; …)</c>.
    /// </summary>
    /// <remarks>
    /// The two overloads are one concept with two spellings, and both have to produce the
    /// same shape of block — otherwise a branch's steps would be invisible to
    /// <c>SelfAndNested</c> in one form and not the other, and a capability invoked inside
    /// a fork would go missing from the dispatcher.
    /// </remarks>
    private static List<StepModel> BuildBranchBlock(
        ChainLink branchLink,
        SemanticModel semanticModel,
        List<Diagnostic> diagnostics,
        ref int cursor)
    {
        if (branchLink.TypeArguments.Count > 0)
        {
            var single = new List<StepModel>(1);

            AddCapabilityStep(branchLink, semanticModel, diagnostics, single, ref cursor);
            return single;
        }

        return BuildBlock(
            FlowChainWalker.WalkBlock(branchLink, 0), semanticModel, diagnostics, ref cursor);
    }

    /// <summary>
    /// Lays branches out as an ordinary sequence, for a <c>Parallel</c> that is not one.
    /// </summary>
    /// <remarks>
    /// The steps are kept — the author asked for them and they are real work — but no fork
    /// node is emitted, so the plan, the manifest and a rendered diagram all say the flow
    /// runs them in order, which is exactly what it does.
    /// </remarks>
    private static void LayOutInline(
        List<ChainLink> branchLinks,
        SemanticModel semanticModel,
        List<Diagnostic> diagnostics,
        List<StepModel> steps,
        ref int nextIndex)
    {
        foreach (var branchLink in branchLinks)
        {
            steps.AddRange(BuildBranchBlock(branchLink, semanticModel, diagnostics, ref nextIndex));
        }
    }

    /// <summary>
    /// Names the <c>MergeKind</c> a <c>merge:</c> argument selects, or <c>null</c> when it
    /// cannot be read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read syntactically, from the shape of <c>MergeStrategy.AllSettled</c> or
    /// <c>MergeStrategy.Quorum(n)</c>, because that is what the type offers: three static
    /// properties and a factory method. There is no constant to fold — <c>MergeStrategy</c>
    /// is a struct precisely so that <c>Quorum</c> can carry a number, which an <c>enum</c>
    /// could not.
    /// </para>
    /// <para>
    /// <strong>Nothing depends on this being right.</strong> The compiled plan copies the
    /// author's expression verbatim, so a strategy chosen through a variable or a helper
    /// still executes exactly as written; only the manifest's label is lost, and it is
    /// omitted rather than guessed. That asymmetry is deliberate: the plan must be correct,
    /// and the manifest must not lie.
    /// </para>
    /// </remarks>
    private static string? ReadMergeKind(ExpressionSyntax merge)
    {
        var expression = merge;

        // `MergeStrategy.Quorum(2)` — the kind is the method being called.
        if (expression is InvocationExpressionSyntax invocation)
        {
            expression = invocation.Expression;
        }

        var name = expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            _ => null,
        };

        return name switch
        {
            "AllMustSucceed" => "AllMustSucceed",
            "AllSettled" => "AllSettled",
            "FirstSuccess" => "FirstSuccess",
            "Quorum" => "Quorum",
            _ => null,
        };
    }

    /// <summary>
    /// Builds one block of a switch, reserving a slot for the previous block's closing
    /// jump and giving it back when this block declares nothing.
    /// </summary>
    private static List<StepModel> LayOutBlock(
        ChainLink link,
        int argumentIndex,
        SemanticModel semanticModel,
        List<Diagnostic> diagnostics,
        ref int committed,
        ref bool previousNeedsJump)
    {
        var cursor = committed + (previousNeedsJump ? 1 : 0);
        var block = BuildBlock(
            FlowChainWalker.WalkBlock(link, argumentIndex), semanticModel, diagnostics, ref cursor);

        if (block.Count > 0)
        {
            committed = cursor;
            previousNeedsJump = true;
        }

        return block;
    }

    /// <summary>
    /// The fully-qualified type C# inferred for a one-type-argument builder call: the
    /// value a <c>.Switch(...)</c> selects on, or the element a <c>.ForEach(...)</c>
    /// iterates.
    /// </summary>
    /// <remarks>
    /// Read from the resolved method's type argument rather than from the lambda body,
    /// because that is what C# itself inferred and therefore what every <c>.Case(...)</c>
    /// was type-checked against — and, for a loop, what the body's steps bind against.
    /// Inferring it again from the body would be a second opinion that can disagree with
    /// the compiler's.
    /// </remarks>
    private static string? ResolveSingleTypeArgument(ChainLink link, SemanticModel semanticModel)
    {
        var method = semanticModel.GetSymbolInfo(link.Invocation).Symbol as IMethodSymbol;

        return method is { TypeArguments.Length: 1 } && method.TypeArguments[0].TypeKind != TypeKind.Error
            ? Display(method.TypeArguments[0])
            : null;
    }

    private static void AddCapabilityStep(
        ChainLink link,
        SemanticModel semanticModel,
        List<Diagnostic> diagnostics,
        List<StepModel> steps,
        ref int nextIndex)
    {
        if (link.TypeArguments.Count == 0)
        {
            return;
        }

        var symbol = ResolveType(link.TypeArguments[0], semanticModel);

        if (symbol is null)
        {
            return;
        }

        // FLOWX1002 — a step invokes a capability, and this type is not one.
        if (!CapabilityReader.IsCapability(symbol))
        {
            diagnostics.Add(Diagnostic.Create(
                FlowXDiagnostics.StepIsNotACapability,
                link.TypeArguments[0].GetLocation(),
                symbol.Name));

            return;
        }

        // FLOWX1015 — a capability has exactly one input and one output type.
        var contracts = CapabilityReader.CountCapabilityContracts(symbol);

        if (contracts > 1)
        {
            diagnostics.Add(Diagnostic.Create(
                FlowXDiagnostics.CapabilityHasMultipleContracts,
                link.TypeArguments[0].GetLocation(),
                symbol.Name,
                contracts));

            return;
        }

        var info = CapabilityReader.Read(symbol);

        if (info is null)
        {
            return;
        }

        // FLOWX1010 — there is no permissive default.
        if (!info.DeclaresAuthorization)
        {
            diagnostics.Add(Diagnostic.Create(
                FlowXDiagnostics.CapabilityMissingAuthorization,
                link.TypeArguments[0].GetLocation(),
                info.Id));
        }

        // FLOWX1030 — and no unnamed permission. Reported here rather than beside
        // FLOWX1010's condition because it presupposes FLOWX1010 passed: an undeclared
        // stance reads as Public, which names nothing and is supposed to.
        else if (NamesNothing(info))
        {
            diagnostics.Add(Diagnostic.Create(
                FlowXDiagnostics.AuthorizationStanceNamesNothing,
                link.TypeArguments[0].GetLocation(),
                info.Id,
                info.AuthorizationMode));
        }

        var mapping = ReadInputMapping(link, semanticModel, info, diagnostics);

        steps.Add(StepModel.Capability(
            nextIndex++,
            info.TypeName,
            info.Id,
            info.Version,
            info.IsIdempotent,
            info.SideEffects,
            FormatLocation(link.CallLocation),
            info.AuthorizationMode,
            info.AuthorizationValue,
            info.InputTypeName,
            info.OutputTypeName,
            mapping?.Text,
            mapping?.TypeName,
            mapping?.Location));
    }

    /// <summary>
    /// Reads the explicit input mapping of a
    /// <c>.Step&lt;TCapability, TStepIn&gt;(map)</c>, or <c>null</c> for the overload that
    /// binds from the state bag.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The two overloads are one step kind.</strong> Only where the input comes
    /// from differs, so this returns the extra facts and <see cref="AddCapabilityStep"/>
    /// builds the same <see cref="StepModel.Capability"/> either way. The alternative — a
    /// second kind — would have made every reader of a capability step ask which one it
    /// was looking at, for a difference the descriptor, the compensation and the manifest
    /// entry are all blind to.
    /// </para>
    /// <para>
    /// <c>TStepIn</c> is read from the resolved method's <em>second</em> type argument
    /// rather than inferred from the lambda body, for the reason
    /// <see cref="ResolveSubFlowInput"/> gives: that is what C# itself inferred, and the
    /// emitted mapping is a field typed at it, so a second opinion that disagreed with the
    /// compiler's would not compile.
    /// </para>
    /// <para>
    /// FLOWX1029 is reported here rather than in a <c>DiagnosticAnalyzer</c> because the
    /// answer is already in hand: the capability's contract has just been read for the
    /// step, and asking the same question again in the editor would mean resolving it
    /// twice. It is an error and it suppresses the mapping, so a flow that cannot compile
    /// does not also emit a dispatcher that cannot compile — one message about the
    /// author's own line beats that message plus a CS1503 in generated source.
    /// </para>
    /// </remarks>
    private static InputMapping? ReadInputMapping(
        ChainLink link,
        SemanticModel semanticModel,
        CapabilityInfo info,
        List<Diagnostic> diagnostics)
    {
        var arguments = link.Invocation.ArgumentList.Arguments;

        // `.Step<TCapability>()` — one type argument and no mapping. Also the shape a
        // `.Branch<TCapability>()` arrives in, which has no mapped form at all.
        if (link.TypeArguments.Count < 2 || arguments.Count == 0)
        {
            return null;
        }

        if (semanticModel.GetSymbolInfo(link.Invocation).Symbol is not IMethodSymbol
            {
                TypeArguments.Length: 2,
            } method ||
            method.TypeArguments[1].TypeKind == TypeKind.Error)
        {
            // The call does not bind, which means the file does not compile, and that
            // message is better than this one. Emitting an untyped mapping instead would
            // guess `object` and box every input.
            return null;
        }

        var mapped = method.TypeArguments[1];

        // FLOWX1029 — the mapping's result is handed straight to the capability, and C#
        // constrains TStepIn to nothing, so this is the only place the mismatch can be
        // caught before it becomes a CS1503 inside generated code.
        if (!IsAcceptedBy(semanticModel, mapped, method.TypeArguments[0]))
        {
            diagnostics.Add(Diagnostic.Create(
                FlowXDiagnostics.StepInputMappingHasWrongType,
                link.TypeArguments[1].GetLocation(),
                info.Id,
                EnclosingFlowName(link),
                Display(mapped),
                info.InputTypeName ?? "its input contract"));

            return null;
        }

        return new InputMapping(
            arguments[0].Expression.ToString(),
            Display(mapped),
            FormatLocation(arguments[0].Expression.GetLocation()));
    }

    /// <summary>
    /// Whether a value of <paramref name="mapped"/> can be passed where the capability
    /// declares its input.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Assignability, not identity — which is the opposite of the rule FLOWX1020 applies,
    /// deliberately. That rule asks what a <c>Dictionary&lt;Type, object&gt;</c> lookup
    /// finds and a lookup is exact; this asks what a C# argument accepts, and an argument
    /// takes anything implicitly convertible to it. Requiring identity here would report a
    /// mapping that compiles perfectly.
    /// </para>
    /// <para>
    /// The capability's input is taken from the resolved <c>ICapability&lt;,&gt;</c> rather
    /// than from <c>CapabilityInfo</c>'s display string, because comparing symbols is exact
    /// where comparing names is a guess about how two assemblies spell the same type.
    /// </para>
    /// </remarks>
    private static bool IsAcceptedBy(SemanticModel semanticModel, ITypeSymbol mapped, ITypeSymbol capability)
    {
        var declared = CapabilityReader.InputContract(capability);

        // Nothing to compare against: a capability with no readable contract is
        // FLOWX1002's or FLOWX1015's business, and this rule has no defensible answer.
        if (declared is null)
        {
            return true;
        }

        if (SymbolEqualityComparer.Default.Equals(mapped, declared))
        {
            return true;
        }

        var conversion = semanticModel.Compilation.ClassifyCommonConversion(mapped, declared);

        return conversion.Exists && conversion.IsImplicit;
    }

    /// <summary>What a <c>.Step&lt;TCapability, TStepIn&gt;(map)</c> supplies its input with.</summary>
    private sealed class InputMapping
    {
        internal InputMapping(string text, string typeName, string? location)
        {
            Text = text;
            TypeName = typeName;
            Location = location;
        }

        /// <summary>The mapping lambda's source text, copied verbatim.</summary>
        internal string Text { get; }

        /// <summary>Fully-qualified type C# inferred for <c>TStepIn</c>.</summary>
        internal string TypeName { get; }

        /// <summary><c>file:line</c> of the mapping expression.</summary>
        internal string? Location { get; }
    }

    private static void AddEventStep(
        ChainLink link,
        SemanticModel semanticModel,
        List<Diagnostic> diagnostics,
        List<StepModel> steps,
        ref int nextIndex)
    {
        if (link.TypeArguments.Count == 0)
        {
            return;
        }

        var symbol = ResolveType(link.TypeArguments[0], semanticModel);

        if (symbol is null)
        {
            return;
        }

        var arguments = link.Invocation.ArgumentList.Arguments;

        // FLOWX1024, provisionally. Whether this event can actually be staged depends on two
        // things this analysis cannot see — the flow's profile is known here, but whether the
        // compilation declares a serialiser context for the contract is a question about
        // every tree in the build, and asking it per flow would trade the generator's
        // incrementality for a warning. So the site travels to FlowPlanGenerator.Produce,
        // which has the answer and either drops this or replaces it with one naming the
        // reason. Diagnostic properties are the Roslyn-shaped way to carry that; the
        // alternative was threading a collector through nine block builders.
        diagnostics.Add(Diagnostic.Create(
            FlowXDiagnostics.EmitIsNotYetPublished,
            link.CallLocation,
            EmitSiteProperties(symbol),
            symbol.Name,
            EmitReasons.Provisional));

        steps.Add(StepModel.Emit(
            nextIndex++,
            ToEventIdentity(symbol.Name),
            FormatLocation(link.CallLocation),
            Display(symbol),

            // The author's own factory, copied verbatim, so the generated DescribeStep
            // builds the body the same way `.Return(...)` builds the output. A call with no
            // argument does not compile, so the null branch is reachable only from a
            // half-typed buffer where C# is already saying something more useful.
            arguments.Count == 0 ? null : arguments[0].Expression.ToString(),
            arguments.Count == 0 ? null : FormatLocation(arguments[0].Expression.GetLocation())));
    }

    /// <summary>
    /// What a provisional <c>FLOWX1024</c> carries to the pipeline that decides its fate.
    /// </summary>
    private static System.Collections.Immutable.ImmutableDictionary<string, string?> EmitSiteProperties(
        ITypeSymbol contract) =>
        System.Collections.Immutable.ImmutableDictionary<string, string?>.Empty
            .Add(EmitReasons.ContractProperty, Display(contract))
            .Add(EmitReasons.NameProperty, contract.Name);

    private static void AddSignalStep(
        ChainLink link,
        SemanticModel semanticModel,
        List<Diagnostic> diagnostics,
        List<StepModel> steps,
        ref int nextIndex)
    {
        if (link.TypeArguments.Count == 0)
        {
            return;
        }

        var symbol = ResolveType(link.TypeArguments[0], semanticModel);

        if (symbol is null)
        {
            return;
        }

        // FLOWX1031, as an error — the one report in this rule that stops the build rather
        // than annotating it, and the reason is not that suspension matters more than a
        // timer. It is that this is the only one of the three that puts something into the
        // plan. `Delay` and `OnTimeout` are dropped, so the emitted graph says less than the
        // source and nothing untrue; this reaches the graph, the dispatcher and the manifest
        // carrying a duration nobody wrote, because the step model has no field to carry the
        // author's and `StepNode.ForAwaitSignal` demands one. Refusing the flow is what
        // stops the fabrication: an error here leaves `IsSuccess` false, and
        // `FlowPlanGenerator` emits neither the plan nor the manifest entry.
        ReportSuspension(link, diagnostics, DiagnosticSeverity.Error, WhatIsLost(link.MethodName));

        // The step is still modelled. FLOWX1017 reads the built steps to find a suspension
        // point under the wrong profile, and dropping the model here would silently retire
        // a shipped rule as a side effect of adding this one.
        steps.Add(StepModel.AwaitSignal(
            nextIndex++,
            ToEventIdentity(symbol.Name),
            FormatLocation(link.CallLocation)));
    }

    /// <summary>
    /// Reports FLOWX1031 against one call, at the severity that call has earned.
    /// </summary>
    /// <remarks>
    /// One id for three constructs, because they are one fact — this release cannot honour a
    /// flow that waits — and two ids would give a team two suppressions, two pages and two
    /// expiry dates for one gap. The severity is chosen per report, which is what
    /// FLOWX1011 and FLOWX1025 already do; <c>docs/diagnostics/FLOWX1031.md</c> is the
    /// argument, and the short form is that an omission and a falsification are not the same
    /// finding.
    /// </remarks>
    /// <param name="link">The offending call, whose name span the report points at.</param>
    /// <param name="diagnostics">Collects everything worth reporting.</param>
    /// <param name="severity">Error for a construct that fabricates, warning for one that is dropped.</param>
    /// <param name="consequence">What the author loses, in the terms of this construct.</param>
    private static void ReportSuspension(
        ChainLink link,
        List<Diagnostic> diagnostics,
        DiagnosticSeverity severity,
        string consequence) =>
        diagnostics.Add(Diagnostic.Create(
            FlowXDiagnostics.SuspensionIsNotHonoured,
            link.CallLocation,
            severity,
            additionalLocations: null,
            properties: null,
            EnclosingFlowName(link),
            link.MethodName,
            consequence));

    /// <summary>What each unhonoured construct costs, said in that construct's own terms.</summary>
    /// <remarks>
    /// Three sentences rather than one, because the three failures are not the same failure.
    /// A message that said "this does not work" for all of them would leave the reader of an
    /// <c>OnTimeout</c> report with no way to know that the steps inside the block are absent
    /// from the manifest they are about to publish.
    /// </remarks>
    private static string WhatIsLost(string methodName) => methodName switch
    {
        "AwaitSignal" =>
            "the step completes immediately, so the flow does not wait, and the plan would " +
            "carry a one-hour timeout in place of the duration declared here",
        "Delay" =>
            "the call produces no step at all, so the flow continues without waiting",
        _ =>
            "the block is discarded, so its steps reach no plan, no dispatcher and no manifest",
    };

    private static void AttachCompensation(ChainLink link, SemanticModel semanticModel, List<StepModel> steps)
    {
        if (steps.Count == 0 || link.TypeArguments.Count == 0)
        {
            return;
        }

        var info = CapabilityReader.Read(ResolveType(link.TypeArguments[0], semanticModel));

        if (info is null)
        {
            return;
        }

        // Attaches to the step already built — .CompensateWith follows the .Step it undoes.
        // Modelled as a full capability step so it reaches the manifest with its own
        // authorisation stance and side effects, not merely as a name on the step it undoes.
        var last = steps.Count - 1;

        steps[last] = steps[last].WithCompensation(StepModel.Capability(
            steps[last].Index,
            info.TypeName,
            info.Id,
            info.Version,
            info.IsIdempotent,
            info.SideEffects,
            FormatLocation(link.CallLocation),
            info.AuthorizationMode,
            info.AuthorizationValue,
            info.InputTypeName,
            info.OutputTypeName));
    }

    /// <summary>
    /// Whether a declared stance demands a name and was given none — FLOWX1030.
    /// </summary>
    /// <remarks>
    /// <c>Public</c>, <c>Authenticated</c> and <c>Internal</c> are complete in themselves.
    /// <c>Permission</c> and <c>Policy</c> are not: each is a claim that some named grant
    /// is required, and without the name the manifest publishes an authorisation stance
    /// nothing can be checked against.
    /// </remarks>
    private static bool NamesNothing(CapabilityInfo info) =>
        info.AuthorizationValue is null
        && info.AuthorizationMode is "Permission" or "Policy";

    private static void AttachPolicy(
        ChainLink link,
        SemanticModel semanticModel,
        List<Diagnostic> diagnostics,
        List<StepModel> steps)
    {
        if (steps.Count == 0 || link.Invocation.ArgumentList.Arguments.Count == 0)
        {
            return;
        }

        var argument = link.Invocation.ArgumentList.Arguments[0];
        var kinds = PolicySetReader.Read(argument.Expression, semanticModel);

        var last = steps.Count - 1;
        var step = steps[last].WithPolicy(argument.ToString(), kinds.ToArray());

        ReportPolicyConflicts(step, link, diagnostics);

        steps[last] = step;
    }

    /// <summary>
    /// Reports policies whose safety precondition the capability does not meet.
    /// </summary>
    /// <remarks>
    /// Both of these were documented as compile errors long before anything raised them,
    /// which is the failure mode they exist to prevent: a control that reads as enforced
    /// and is not. FLOWX1014 in particular was described as what stops a duplicate
    /// charge.
    /// </remarks>
    private static void ReportPolicyConflicts(StepModel step, ChainLink link, List<Diagnostic> diagnostics)
    {
        if (step.CapabilityId is null)
        {
            return;
        }

        // FLOWX1014 — retrying a non-idempotent operation duplicates its effect.
        if (!step.IsIdempotent && step.PolicyKinds.Contains("Retry"))
        {
            diagnostics.Add(Diagnostic.Create(
                FlowXDiagnostics.RetryRequiresIdempotency,
                link.CallLocation,
                step.CapabilityId));
        }

        // FLOWX1018 — a cache hit returns a success without performing the effect.
        if (step.SideEffects.Length > 0 && step.PolicyKinds.Contains("Cache"))
        {
            diagnostics.Add(Diagnostic.Create(
                FlowXDiagnostics.CacheRequiresNoSideEffects,
                link.CallLocation,
                step.CapabilityId));
        }
    }

    private static ArrowExpressionClauseSyntax? FindArrow(MethodDeclarationSyntax method) =>
        method.ExpressionBody;

    private static MethodDeclarationSyntax? FindDefine(ClassDeclarationSyntax declaration) =>
        declaration.Members
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.ValueText == "Define");

    private static SyntaxNode? FindDefineBody(MethodDeclarationSyntax? define) =>
        define is null ? null : (SyntaxNode?)FindArrow(define) ?? define.Body;

    /// <summary>
    /// The name of <c>Define</c>'s builder parameter, which is what tells the walker a
    /// chain from any other call in the method. See <see cref="FlowChainWalker.Walk"/>.
    /// </summary>
    private static string? FindBuilderParameterName(MethodDeclarationSyntax? define) =>
        define?.ParameterList.Parameters.Count > 0
            ? define.ParameterList.Parameters[0].Identifier.ValueText
            : null;

    private static ITypeSymbol? ResolveType(TypeSyntax syntax, SemanticModel semanticModel) =>
        semanticModel.GetSymbolInfo(syntax).Symbol as ITypeSymbol
        ?? semanticModel.GetTypeInfo(syntax).Type;

    private static FlowContracts ReadFlowContracts(INamedTypeSymbol flowType)
    {
        for (var current = flowType.BaseType; current is not null; current = current.BaseType)
        {
            if (current.MetadataName == "Flow`2" && current.TypeArguments.Length == 2)
            {
                return new FlowContracts(
                    Display(current.TypeArguments[0]),
                    Display(current.TypeArguments[1]),
                    ReadSensitiveMembers(current.TypeArguments[0]),
                    ReadSensitiveMembers(current.TypeArguments[1]));
            }
        }

        return new FlowContracts("object", "object", [], []);
    }

    /// <summary>
    /// Names of the contract's members carrying <c>[Sensitive]</c>, in declaration order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Positional records put the attribute on the primary constructor parameter, written
    /// <c>[property: Sensitive]</c>, and Roslyn surfaces it on the generated property. Both
    /// spellings are read, because a user who wrote one and got nothing would reasonably
    /// conclude the attribute does not work.
    /// </para>
    /// <para>
    /// Reading is the whole of it: this records <em>which</em> members are sensitive so the
    /// manifest can say so and a reviewer can check. Redaction is not implemented — see the
    /// attribute's own remarks, which used to claim otherwise.
    /// </para>
    /// </remarks>
    private static string[] ReadSensitiveMembers(ITypeSymbol contract)
    {
        var names = new List<string>();

        foreach (var member in contract.GetMembers())
        {
            if (member is not IPropertySymbol and not IFieldSymbol)
            {
                continue;
            }

            var onMember = member.GetAttributes()
                .Any(a => a.AttributeClass?.ToDisplayString() == SensitiveAttribute);

            var onParameter = contract
                .GetMembers(".ctor")
                .OfType<IMethodSymbol>()
                .SelectMany(c => c.Parameters)
                .Any(parameter =>
                    string.Equals(parameter.Name, member.Name, System.StringComparison.OrdinalIgnoreCase) &&
                    parameter.GetAttributes()
                        .Any(a => a.AttributeClass?.ToDisplayString() == SensitiveAttribute));

            if ((onMember || onParameter) && !names.Contains(member.Name))
            {
                names.Add(member.Name);
            }
        }

        names.Sort(System.StringComparer.Ordinal);
        return names.ToArray();
    }

    /// <summary>A flow's input and output contracts, and which of their members are sensitive.</summary>
    private sealed class FlowContracts
    {
        internal FlowContracts(string input, string output, string[] sensitiveInput, string[] sensitiveOutput)
        {
            Input = input;
            Output = output;
            SensitiveInput = sensitiveInput;
            SensitiveOutput = sensitiveOutput;
        }

        internal string Input { get; }

        internal string Output { get; }

        internal string[] SensitiveInput { get; }

        internal string[] SensitiveOutput { get; }
    }

    private static string Display(ITypeSymbol symbol) =>
        symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty);

    private static string ReadProfile(AttributeData flowAttribute)
    {
        var value = flowAttribute.NamedArguments
            .FirstOrDefault(a => a.Key == "Profile").Value.Value;

        // The enum arrives as its underlying int. Ephemeral is zero, which is the whole
        // point of ADR-0003: a flow that says nothing gets the cheap profile.
        return value switch
        {
            1 => "Durable",
            2 => "Streaming",
            _ => "Ephemeral",
        };
    }

    private static string? ReadNamedString(AttributeData attribute, string name) =>
        attribute.NamedArguments.FirstOrDefault(a => a.Key == name).Value.Value as string;

    private static string? ReadDeadline(INamedTypeSymbol flowType)
    {
        var attribute = flowType.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == FlowDeadlineAttribute);

        return attribute is null || attribute.ConstructorArguments.Length == 0
            ? null
            : attribute.ConstructorArguments[0].Value as string;
    }

    /// <summary>Converts a contract type name into an event identity: <c>OrderPlaced</c> → <c>order.placed</c>.</summary>
    /// <remarks>
    /// A convention, and a temporary one. The identity belongs on the event contract as
    /// an attribute so it can be versioned independently of the CLR type name; deriving
    /// it here means renaming a class silently renames a published event. Tracked for
    /// P1 alongside the event catalogue.
    /// </remarks>
    private static string ToEventIdentity(string typeName)
    {
        var builder = new System.Text.StringBuilder();

        for (var i = 0; i < typeName.Length; i++)
        {
            var c = typeName[i];

            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    builder.Append('.');
                }

                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        var identity = builder.ToString();

        // An identity needs at least one separator to be well formed.
        return identity.Contains(".") ? identity : "event." + identity;
    }

    private static string? FormatLocation(Location location)
    {
        if (location is null || !location.IsInSource)
        {
            return null;
        }

        var span = location.GetLineSpan();

        return span.Path + ":" + (span.StartLinePosition.Line + 1).ToString(
            System.Globalization.CultureInfo.InvariantCulture);
    }
}
