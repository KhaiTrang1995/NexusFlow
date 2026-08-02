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
    /// <remarks>
    /// <para>
    /// An error normally stops the plan being emitted, because emitting a partial plan on top
    /// of one buries the real error under a cascade of "type not found".
    /// </para>
    /// <para>
    /// <strong><c>FLOWX1006</c> is the exception, and it has to be.</strong> It is raised
    /// provisionally — analysis cannot see whether the compilation declares a serialiser
    /// context for the contract, so <c>FlowPlanGenerator.Produce</c> either drops it or
    /// restates it — and a provisional that blocked emission would delete the flow's whole
    /// plan for a finding that settles to nothing. Even when it settles to an error there is
    /// no cascade to avoid: the emitter simply leaves that contract out of the state bag, so
    /// the plan it produces is valid and the journal is the only thing missing something.
    /// Withholding the plan instead would replace one accurate error with a page of
    /// "PlaceOrderFlow does not contain a definition for Plan".
    /// </para>
    /// </remarks>
    public bool IsSuccess => Model is not null && !Diagnostics.Any(IsBlocking);

    /// <summary>Whether a diagnostic stops the plan being emitted.</summary>
    private static bool IsBlocking(Diagnostic diagnostic) =>
        diagnostic.Severity == DiagnosticSeverity.Error &&
        !diagnostic.Properties.ContainsKey(StateBagReasons.ContractProperty);

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
    private const string SubjectAttribute = "FlowX.SubjectAttribute";

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

        // FLOWX1017 — an in-memory wait does not survive a deployment, and a timer outside a
        // journal has nowhere to record when it is due. Searched across nested blocks too: a
        // wait hidden inside a `When` is no more durable than one at the top level, and only
        // looking at the top level is how a rule like this quietly stops applying the day
        // branching lands.
        //
        // Both kinds, since the timer half of WP-63 made `Delay` a step. Before that there was
        // nothing to report about it — the call reached the analyzer's `default:` arm and
        // produced no step at all — so the rule named one construct because one was all a plan
        // could carry.
        // Three kinds now rather than two. A poll is a timer per attempt and reads which
        // attempt it is on out of the journal that parked it, so outside one it has neither
        // anywhere to record when the next call is due nor any way to count the ones already
        // made — which leaves a hot loop, and is the same sentence twice over.
        //
        // Asked as "does this journal?" rather than as `profile != "Durable"`, because every
        // clause above is about a journal and not one of them is about `Durable`. A `Streaming`
        // flow has one: the runtime reads the profile in a single place and asks
        // `ExecutionProfiles.IsJournaled` there, a suspension is committed off the cursor that
        // question opens, `FlowTimerScan` and `FlowHost.SignalAsync` resume by instance id and
        // read no profile at all, and `FlowStreamScan.DispositionFor` already counts a suspended
        // window's flow as started and checkpoints past it. Refusing it here was the fifth
        // `== Durable` that meant "does this journal?", left standing when the other four went.
        if (!ExecutionProfiles.Journals(profile) &&
            steps.SelectMany(s => s.SelfAndNested)
                .FirstOrDefault(s => s.Kind is StepKindModel.AwaitSignal or StepKindModel.Delay
                    or StepKindModel.Poll)
                is { } wait)
        {
            diagnostics.Add(Diagnostic.Create(
                FlowXDiagnostics.AwaitSignalRequiresDurable,
                declaration.Identifier.GetLocation(),
                flowType.Name,
                profile,
                wait.Kind switch
                {
                    StepKindModel.Delay => "Delay",
                    StepKindModel.Poll => "PollUntil",
                    _ => "AwaitSignal",
                }));

            return AnalysisResult.Failure(diagnostics);
        }

        var contracts = ReadFlowContracts(flowType);
        var returnClause = FindReturnClause(links);
        var subject = ResolveSubject(flowType, declaration, profile, contracts, diagnostics);

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
            subjectMember: subject,
            steps: steps,
            declarationLocation: FormatLocation(declaration.Identifier.GetLocation()),
            returnProjection: returnClause?.Text,
            returnLocation: returnClause?.Location,
            usings: ReadUsings(declaration));

        AddStateBagDiagnostics(model, declaration, diagnostics);

        return AnalysisResult.Success(model, diagnostics);
    }

    /// <summary>
    /// Raises one provisional <c>FLOWX1006</c> per contract this flow's journal has to write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>What is in the state bag.</strong> The flow's input, which the engine puts
    /// there before the first step — that is how <c>ctx.Get&lt;TIn&gt;()</c> resolves inside a
    /// generated dispatcher — and the output contract of every capability step, which the
    /// dispatcher writes back with <c>ctx.Set</c>. A mapped step's <em>input</em> is not in the
    /// bag and is deliberately not checked: it is a local at the call site and no journal row
    /// holds it.
    /// </para>
    /// <para>
    /// <strong>Raised only for a flow whose bag reaches a journal</strong> — <c>Durable</c> and
    /// <c>Streaming</c> both. An ephemeral flow keeps no journal, so nothing serialises its bag
    /// and the rule protects nothing there. That is unlike <c>FLOWX1024</c>, whose provisional is
    /// raised under every profile because "the flow is <c>Ephemeral</c>" is one of <em>its</em>
    /// two reasons; here the profile is not a reason, it is the trigger.
    /// </para>
    /// <para>
    /// Located at the flow's declaration rather than at each step, and that is a choice worth
    /// stating: the finding is about this flow's journal and the repair is one attribute on a
    /// serialiser context somewhere else entirely, so a caret on the step would point at the
    /// one place the fix does not go. It also means a single <c>#pragma</c> around the flow
    /// suppresses the set, which is the unit a reader would want.
    /// </para>
    /// </remarks>
    private static void AddStateBagDiagnostics(
        FlowModel model, ClassDeclarationSyntax declaration, List<Diagnostic> diagnostics)
    {
        if (!model.IsJournaled)
        {
            return;
        }

        var contracts = new List<string> { model.InputTypeName };

        foreach (var step in model.AllSteps)
        {
            if (step.Kind == StepKindModel.Capability && step.CapabilityOutput is { Length: > 0 } output)
            {
                contracts.Add(output);
            }

            // A signal's payload is put into the bag by the engine when it is delivered, and
            // journaled by the commit that records the suspension point — so it is in the bag
            // in exactly the sense a step's output is, and is checked for exactly the same
            // reason. Leaving it out would mean an instance resumed by a signal and then
            // crashed came back with the wait satisfied and what it delivered lost, silently.
            if (step.Kind == StepKindModel.AwaitSignal &&
                step.SignalContractTypeName is { Length: > 0 } signal)
            {
                contracts.Add(signal);
            }
        }

        var location = declaration.Identifier.GetLocation();

        foreach (var contract in contracts
            .Distinct(System.StringComparer.Ordinal)
            .OrderBy(static c => c, System.StringComparer.Ordinal))
        {
            diagnostics.Add(Diagnostic.Create(
                FlowXDiagnostics.StateIsNotSerialisable,
                location,
                System.Collections.Immutable.ImmutableDictionary<string, string?>.Empty
                    .Add(StateBagReasons.ContractProperty, contract)
                    .Add(StateBagReasons.NameProperty, SimpleName(contract))
                    .Add(StateBagReasons.FlowProperty, model.FlowId),
                SimpleName(contract),
                model.FlowId,
                StateBagReasons.Provisional));
        }
    }

    /// <summary>The last segment of a fully qualified name — what a reader calls the type.</summary>
    internal static string SimpleName(string qualified)
    {
        var separator = qualified.LastIndexOf('.');

        return separator >= 0 && separator < qualified.Length - 1
            ? qualified.Substring(separator + 1)
            : qualified;
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
                    // `OnTimeout` is the *next link*, not an argument: the DSL spells the pair
                    // `.AwaitSignal<T>(timeout).OnTimeout(block)`, so the two halves of one
                    // wait arrive as two siblings — the same shape `When` and `Otherwise`
                    // have. Consuming both here is what stops the escalation being modelled
                    // as steps that run unconditionally after the wait.
                    i += AddSignalStep(
                        link, NextOnTimeout(links, i), semanticModel, diagnostics, steps, ref nextIndex);
                    break;

                case "Delay":
                    AddDelayStep(link, steps, ref nextIndex);
                    break;

                case "PollUntil":
                    // `OnTimeout` is the next link, exactly as it is for `AwaitSignal`: both
                    // spell the pair `.X(...).OnTimeout(block)`, and consuming both here is
                    // what stops the escalation being modelled as steps that run
                    // unconditionally after the poll.
                    i += AddPollStep(
                        link, NextOnTimeout(links, i), semanticModel, diagnostics, steps, ref nextIndex);
                    break;

                case "Fail":
                    // Terminal. Everything else in this switch says what happens next;
                    // this says there is no next, which is why the block ends here rather
                    // than carrying on and laying out steps the engine can never reach.
                    AddFailStep(link, steps, ref nextIndex);
                    ReportUnreachable(links, i, diagnostics);

                    return steps;

                case "CompensateWith":
                    AttachCompensation(link, semanticModel, diagnostics, steps);
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
        "ForEach", "SubFlow", "Fail", "Delay", "PollUntil",
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

    /// <summary>The <c>.OnTimeout(...)</c> immediately following the link at <paramref name="index"/>, if any.</summary>
    /// <remarks>
    /// Immediately, and not "somewhere after". <c>OnTimeout</c> belongs to
    /// <c>IAwaitBuilder</c>, which only an <c>AwaitSignal</c> returns, so a call anywhere else
    /// does not compile — but a flow with two waits in it has two of these, and matching the
    /// wrong one would attach an escalation to a wait the author did not write it for.
    /// </remarks>
    private static ChainLink? NextOnTimeout(IReadOnlyList<ChainLink> links, int index) =>
        index + 1 < links.Count && links[index + 1].MethodName == "OnTimeout"
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
        if (ReadCapabilityStep(link, semanticModel, diagnostics, nextIndex) is { } step)
        {
            steps.Add(step);
            nextIndex++;
        }
    }

    /// <summary>
    /// Reads the capability a builder call names, at a supplied flat index, reporting
    /// everything that is wrong with the declaration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Split out of <see cref="AddCapabilityStep"/> when <c>PollUntil</c> arrived, because a
    /// poll's attempt is a capability step in every sense that matters — the descriptors, the
    /// dispatcher's switch, the manifest's capability list and every rule from
    /// <c>FLOWX1002</c> to <c>FLOWX1037</c> apply to it unchanged. A second reader would have
    /// been six diagnostics with two implementations, and the polled capability would have been
    /// the one place in a flow where a missing authorisation stance went unreported.
    /// </para>
    /// <para>
    /// The index is a parameter rather than a <c>ref</c> counter because the caller decides the
    /// layout: a <c>.Step</c> takes the next index, a poll's attempt takes the one after the
    /// poll node.
    /// </para>
    /// </remarks>
    private static StepModel? ReadCapabilityStep(
        ChainLink link,
        SemanticModel semanticModel,
        List<Diagnostic> diagnostics,
        int index)
    {
        if (link.TypeArguments.Count == 0)
        {
            return null;
        }

        var symbol = ResolveType(link.TypeArguments[0], semanticModel);

        if (symbol is null)
        {
            return null;
        }

        // FLOWX1002 — a step invokes a capability, and this type is not one.
        if (!CapabilityReader.IsCapability(symbol))
        {
            diagnostics.Add(Diagnostic.Create(
                FlowXDiagnostics.StepIsNotACapability,
                link.TypeArguments[0].GetLocation(),
                symbol.Name));

            return null;
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

            return null;
        }

        var info = CapabilityReader.Read(symbol);

        if (info is null)
        {
            return null;
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

        // FLOWX1037 — the stance was declared and named, and nothing can decide it. Third in
        // the chain and reported after the other two rather than beside them: a Policy stance
        // with no name is FLOWX1030's finding, and reporting both on one declaration would
        // name two remedies for one edit.
        else if (NotEnforceable(info))
        {
            diagnostics.Add(Diagnostic.Create(
                FlowXDiagnostics.AuthorizationStanceNotEnforceable,
                link.TypeArguments[0].GetLocation(),
                info.Id,
                info.AuthorizationMode));
        }

        var mapping = ReadInputMapping(link, semanticModel, info, diagnostics);

        return StepModel.Capability(
            index,
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
            mapping?.Location);
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

    /// <summary>
    /// Models a <c>.AwaitSignal&lt;TSignal&gt;(timeout)</c> and the <c>.OnTimeout(...)</c>
    /// that may follow it, laying the escalation block out in the flat index space.
    /// </summary>
    /// <returns>How many further links were consumed: 1 for an <c>OnTimeout</c>, 0 without.</returns>
    /// <remarks>
    /// <para>
    /// <strong>The block is laid out immediately after the wait, and no jump closes it.</strong>
    /// That is the one layout available: the other path out of a suspension point is "the rest
    /// of the flow", which has no end for a jump to skip. So the escalation is contiguous, the
    /// signal path is the index one past it, and the two rejoin there — which is why a
    /// conditional needs a jump between its blocks and this does not.
    /// </para>
    /// <para>
    /// An <c>.OnTimeout(f =&gt; { })</c> that declares nothing produces no block and no target,
    /// exactly as an empty <c>.Otherwise</c> produces no jump: a target equal to the next index
    /// would read as an escalation that runs nothing, and the engine would walk through an
    /// expired wait into the steps that bind a payload nothing delivered.
    /// </para>
    /// </remarks>
    private static int AddSignalStep(
        ChainLink link,
        ChainLink? onTimeout,
        SemanticModel semanticModel,
        List<Diagnostic> diagnostics,
        List<StepModel> steps,
        ref int nextIndex)
    {
        if (link.TypeArguments.Count == 0)
        {
            return onTimeout is null ? 0 : 1;
        }

        var symbol = ResolveType(link.TypeArguments[0], semanticModel);

        if (symbol is null)
        {
            return onTimeout is null ? 0 : 1;
        }

        var arguments = link.Invocation.ArgumentList.Arguments;
        var declared = arguments.Count == 0 ? null : arguments[0].Expression;
        var waitIndex = nextIndex++;

        var block = onTimeout is null
            ? new List<StepModel>()
            : BuildBlock(FlowChainWalker.WalkBlock(onTimeout, 0), semanticModel, diagnostics, ref nextIndex);

        steps.Add(StepModel.AwaitSignal(
            waitIndex,
            ToEventIdentity(symbol.Name),

            // Copied verbatim, like every other expression the plan carries. A call with no
            // argument does not compile — the DSL declares only
            // `AwaitSignal<TSignal>(TimeSpan timeout)` — so the null branch is reachable
            // only from a half-typed buffer, where C# is already saying something more
            // useful and the emitter refuses the model rather than inventing a duration.
            declared?.ToString(),

            // The signal's payload is seeded into the state bag under this contract, which
            // makes it a journaled contract in the sense FLOWX1006 checks and the sense
            // `DescribeStep` and `RestoreState` have to carry.
            Display(symbol),
            FormatLocation(link.CallLocation),

            // And the same duration again, folded, for the manifest. See ADR-0021 §2.2 for
            // why one declaration reaches two artifacts in two forms.
            FoldDeclaredWait(declared, semanticModel),

            block));

        return onTimeout is null ? 0 : 1;
    }

    /// <summary>
    /// Models a <c>.PollUntil&lt;TCapability&gt;(until, interval, timeout)</c> and lays out its
    /// attempt and its escalation block.
    /// </summary>
    /// <returns>How many further links were consumed: 1 for an <c>OnTimeout</c>, 0 without.</returns>
    /// <remarks>
    /// <para>
    /// The layout is <c>poll · attempt · escalation…</c> and the indices are handed out in
    /// exactly that order, so the attempt is always <c>poll + 1</c> — which is the fact the
    /// engine relies on to find the body it re-enters without the node having to carry a
    /// target for it.
    /// </para>
    /// <para>
    /// <strong>The attempt is read by <see cref="ReadCapabilityStep"/> and not by a reader of
    /// its own.</strong> Everything true of a capability a <c>.Step</c> names is true of the
    /// one a poll names, including every rule about its stance and its contracts, and a second
    /// reader is how the polled capability would have become the one place in a flow where
    /// those went unchecked.
    /// </para>
    /// </remarks>
    private static int AddPollStep(
        ChainLink link,
        ChainLink? onTimeout,
        SemanticModel semanticModel,
        List<Diagnostic> diagnostics,
        List<StepModel> steps,
        ref int nextIndex)
    {
        var consumed = onTimeout is null ? 0 : 1;
        var pollIndex = nextIndex;
        var attempt = ReadCapabilityStep(link, semanticModel, diagnostics, pollIndex + 1);

        if (attempt is null)
        {
            // The capability could not be read, and whatever stopped it has already been
            // reported — by this analyzer or by C# itself. Laying out a poll around nothing
            // would produce a plan whose body index names a step that is not there.
            return consumed;
        }

        // FLOWX1044 — a poll calls its capability an unbounded number of times with one
        // request's worth of input, which is what `Idempotent = true` declares to be safe.
        if (!attempt.IsIdempotent)
        {
            diagnostics.Add(Diagnostic.Create(
                FlowXDiagnostics.PollRequiresIdempotency,
                link.TypeArguments[0].GetLocation(),
                attempt.CapabilityId));
        }

        var arguments = link.Invocation.ArgumentList.Arguments;
        var until = Argument(arguments, "until", 0);
        var interval = Argument(arguments, "interval", 1);
        var timeout = Argument(arguments, "timeout", 2);

        var budget = FoldDeclaredWait(timeout, semanticModel);

        // FLOWX1043 — the second attempt falls due after the budget has gone, so the loop is
        // one call and an escalation. Silent whenever either duration is one this compiler
        // cannot evaluate, which is FLOWX1019's stance: a rule that guessed at a schedule read
        // from configuration would fire on flows that are correct at run time.
        if (DeclaredBackoff.FoldFirstGap(interval?.ToString()) is { } gap && budget is not null &&
            Duration(gap) is { } first && Duration(budget) is { } window && first > window)
        {
            diagnostics.Add(Diagnostic.Create(
                FlowXDiagnostics.PollIntervalOutlastsItsTimeout,
                link.CallLocation,
                attempt.CapabilityId,
                gap,
                budget));
        }

        // Two indices are spent before the block: the poll node and its attempt.
        nextIndex = pollIndex + 2;

        var block = onTimeout is null
            ? new List<StepModel>()
            : BuildBlock(FlowChainWalker.WalkBlock(onTimeout, 0), semanticModel, diagnostics, ref nextIndex);

        steps.Add(StepModel.Poll(
            pollIndex,
            attempt,

            // Copied verbatim, like every other expression the plan carries. A call missing an
            // argument does not compile — the DSL declares one overload and all three
            // parameters are required — so the null branches are reachable only from a
            // half-typed buffer, where C# is already saying something more useful and the
            // emitter refuses the model rather than inventing a schedule for it.
            until?.ToString(),
            interval?.ToString(),
            timeout?.ToString(),
            until is null ? null : FormatLocation(until.GetLocation()),
            FormatLocation(link.CallLocation),

            // And the same duration again, folded, for the manifest. ADR-0021 §2.2 is why one
            // declaration reaches two artifacts in two forms; the interval is deliberately not
            // folded, because the manifest publishes structure and has no field for a tuning
            // number — MaxDegreeOfParallelism's stance, on the same kind of number.
            budget,

            block));

        return consumed;
    }

    /// <summary>Reads an ISO-8601 duration this compiler folded, for one comparison.</summary>
    /// <remarks>
    /// Only ever handed a string <see cref="DeclaredDuration"/> or
    /// <see cref="DeclaredBackoff"/> produced, so a failure is a defect in one of those rather
    /// than something an author wrote — and the honest answer to it is still "say nothing",
    /// because the rule this feeds is a warning about arithmetic and not a claim about the
    /// build.
    /// </remarks>
    private static System.TimeSpan? Duration(string iso8601)
    {
        try
        {
            return System.Xml.XmlConvert.ToTimeSpan(iso8601);
        }
        catch (System.FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// One argument of a builder call, found by name when the author named it and by position
    /// when they did not.
    /// </summary>
    /// <remarks>
    /// Needed here and not for the older constructs because <c>PollUntil</c> is the first call
    /// in this DSL with three arguments of which two are durations. <c>.When(predicate, then)</c>
    /// and <c>.ForEach(selector, body, options)</c> are read positionally and that is safe: the
    /// arguments have different types, so a transposition does not compile. Two
    /// <c>TimeSpan</c>-shaped arguments would transpose silently, which is why the DSL names
    /// them and why this reads the names.
    /// </remarks>
    private static ExpressionSyntax? Argument(
        SeparatedSyntaxList<ArgumentSyntax> arguments, string name, int position)
    {
        foreach (var argument in arguments)
        {
            if (argument.NameColon?.Name.Identifier.ValueText == name)
            {
                return argument.Expression;
            }
        }

        // A named argument earlier in the list shifts nothing — C# requires positional
        // arguments to come first — so a positional read is only correct while every argument
        // up to this one is positional.
        return position < arguments.Count && arguments[position].NameColon is null
            ? arguments[position].Expression
            : null;
    }

    /// <summary>
    /// Evaluates the declared wait for the manifest, following a named constant exactly one
    /// step to its declaration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The one level of indirection is the whole reason this method exists rather
    /// than a bare call to <see cref="DeclaredDuration.Fold"/>.</strong>
    /// <c>samples/workflow</c> declares <c>Waits.Countersignature</c> as a named property
    /// rather than a literal at the call site — deliberately, because a duration is a
    /// business decision and belongs where it can be read without opening a flow — and a
    /// <c>timeout</c> field the repository's only waiting flow could not populate would be a
    /// field with a producer on paper and none in practice, which is the failure
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0017-manifest-v1-freeze-criteria.md">ADR-0017</a>'s
    /// F1 is about.
    /// </para>
    /// <para>
    /// <strong>Exactly one level, and no recursion.</strong> A constant defined in terms of
    /// another constant is a chain this compiler does not walk: each hop is a chance to
    /// resolve to a declaration in a referenced assembly whose syntax is not in this
    /// compilation, and the honest answer there is the same omission any other unreadable
    /// expression gets. One hop covers the form authors write; two would buy an edge case at
    /// the cost of a loop with a termination argument to make.
    /// </para>
    /// </remarks>
    private static string? FoldDeclaredWait(ExpressionSyntax? declared, SemanticModel semanticModel)
    {
        if (declared is null)
        {
            return null;
        }

        if (DeclaredDuration.Fold(declared.ToString()) is { } folded)
        {
            return folded;
        }

        var symbol = semanticModel.GetSymbolInfo(declared).Symbol;

        if (symbol is not IFieldSymbol and not IPropertySymbol)
        {
            return null;
        }

        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            var initialiser = Initialiser(reference.GetSyntax());

            if (initialiser is not null && DeclaredDuration.Fold(initialiser.ToString()) is { } value)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>The expression a field or property declaration initialises itself with.</summary>
    /// <remarks>
    /// Three shapes, because C# has three ways of writing the same constant: a field's
    /// <c>= …</c>, a property's <c>{ get; } = …</c>, and an expression-bodied property's
    /// <c>=&gt; …</c>. A property with a statement body has none, and is refused with
    /// everything else.
    /// </remarks>
    private static ExpressionSyntax? Initialiser(SyntaxNode declaration) => declaration switch
    {
        VariableDeclaratorSyntax field => field.Initializer?.Value,
        PropertyDeclarationSyntax property => property.Initializer?.Value ?? property.ExpressionBody?.Expression,
        _ => null,
    };

    /// <summary>Models a <c>.Delay(duration)</c> call.</summary>
    /// <remarks>
    /// One index and no block. This call used to reach the <c>default:</c> arm and be skipped
    /// entirely — the step after it took the index it would have had, and a flow written to
    /// wait a day ran straight through. That was the whole subject of a diagnostic, and the
    /// diagnostic is deleted with the gap rather than kept as a warning nobody can act on.
    /// </remarks>
    private static void AddDelayStep(ChainLink link, List<StepModel> steps, ref int nextIndex)
    {
        var arguments = link.Invocation.ArgumentList.Arguments;

        steps.Add(StepModel.Delay(
            nextIndex++,

            // Verbatim, for the reason the signal's timeout is: the generator does not
            // constant-fold, so a wait written as `Waits.Cooling` reaches the plan as that. A
            // call with no argument does not compile, so the null branch is reachable only
            // from a half-typed buffer — and the emitter refuses such a model rather than
            // inventing a duration for it.
            arguments.Count == 0 ? null : arguments[0].Expression.ToString(),
            FormatLocation(link.CallLocation)));
    }

    private static void AttachCompensation(
        ChainLink link,
        SemanticModel semanticModel,
        List<Diagnostic> diagnostics,
        List<StepModel> steps)
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

        // The policy may already be on the step: `.WithPolicy(...).CompensateWith<T>()` is
        // as legal as the order the samples use, because both calls return IStepBuilder.
        // See ReportCompensationPolicyConflicts for why exactly one of the two call sites
        // can ever fire.
        ReportCompensationPolicyConflicts(steps[last], link, diagnostics);
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

    /// <summary>
    /// Whether a declared stance is one the runtime cannot decide — FLOWX1037.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One mode, and the set is spelled out here rather than derived from
    /// <c>StepAuthorization</c> because this assembly may not reference <c>FlowX.Core</c> —
    /// an analyzer runs inside the compiler host and takes no dependency on the runtime it
    /// compiles for. <c>AuthorizationStancesMatchTheAbstraction</c> pins the two against each
    /// other, so a stance that becomes enforceable cannot leave this rule reporting it.
    /// </para>
    /// <para>
    /// Written as a set rather than a single comparison for the reason
    /// <c>DeclaredPolicyAnalyzer.ExecutedKinds</c> is: the line is a list of members and not
    /// a range, and the next stance to become undecidable — or decidable — should be one
    /// edit here.
    /// </para>
    /// </remarks>
    private static bool NotEnforceable(CapabilityInfo info) =>
        System.Array.IndexOf(StancesTheRuntimeCannotDecide, info.AuthorizationMode) >= 0;

    /// <summary>
    /// The stance names <c>FLOWX1037</c> reports, as <c>CapabilityReader</c> spells them.
    /// </summary>
    /// <remarks>
    /// Public so that <c>AuthorizationStancesMatchTheAbstraction</c> can pin it against
    /// <c>StepAuthorization.IsRefusedAtBuildTime</c> — the arrangement
    /// <c>DeclaredPolicyAnalyzer.ExecutedKinds</c> and <c>PolicyStageFitnessTests</c> already
    /// use, and for the same reason: this assembly targets netstandard2.0, loads into the
    /// compiler process and cannot reference the runtime it compiles for, so this is a copy,
    /// and an unpinned copy of a security decision drifts in silence. The day a stance
    /// becomes decidable, that gate goes red rather than this rule quietly reporting a stance
    /// that now works.
    /// </remarks>
    public static readonly string[] StancesTheRuntimeCannotDecide = ["Policy"];

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

        // The expression, not the whole argument, as everywhere else in this file. The
        // difference used to be invisible because nothing read the text back; the emitter now
        // copies it into a call of its own, where the argument *name* of a named argument —
        // `.WithPolicy(policy: Policies.Undo)` — cannot travel with it.
        var last = steps.Count - 1;
        var step = steps[last].WithPolicy(argument.Expression.ToString(), kinds.ToArray());

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

        // FLOWX1014 — retrying a non-idempotent operation duplicates its effect. `Retry`
        // wraps the step, so it is the step's own declaration that decides it; an idempotent
        // reversal hanging off the same call says nothing about whether the capture may be
        // asked twice.
        if (!step.IsIdempotent && step.PolicyKinds.Contains(RetryKind))
        {
            diagnostics.Add(Diagnostic.Create(
                FlowXDiagnostics.RetryRequiresIdempotency,
                link.CallLocation,
                step.CapabilityId,
                RetryKind));
        }

        ReportCompensationPolicyConflicts(step, link, diagnostics);

        // FLOWX1018 — a cache hit returns a success without performing the effect.
        if (step.SideEffects.Length > 0 && step.PolicyKinds.Contains(CacheKind))
        {
            diagnostics.Add(Diagnostic.Create(
                FlowXDiagnostics.CacheRequiresNoSideEffects,
                link.CallLocation,
                step.CapabilityId));
        }
    }

    /// <summary>
    /// FLOWX1014 over the capability a <c>CompensationRetry</c> would actually re-dispatch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The rule always meant this case and never reached it.</strong> The check above
    /// asks about the step and the <c>Retry</c> kind, and the string <c>CompensationRetry</c>
    /// appeared nowhere in this file — so a retry declared over a non-idempotent reversal was
    /// reported by nothing. <c>PolicyChain.ForCompensation</c> did carry the same rule, and
    /// carried it against a descriptor the emitter had hardcoded to idempotent, so it could
    /// not refuse a compiled plan either. Neither gap cost anything while no DSL surface could
    /// declare a compensation retry; both became live the moment one could.
    /// </para>
    /// <para>
    /// <strong>Called from both <c>.WithPolicy</c> and <c>.CompensateWith</c>, and fires from
    /// exactly one.</strong> Both return <c>IStepBuilder</c>, so an author may write them in
    /// either order and only the second of the pair sees a step carrying both halves: at
    /// <c>.WithPolicy</c> time there is a compensation only if <c>.CompensateWith</c> came
    /// first, and at <c>.CompensateWith</c> time there are policy kinds only if
    /// <c>.WithPolicy</c> did. So the report lands on the call that completed the pairing,
    /// which is the last line the author wrote about it, and a rule that would otherwise
    /// depend on the order of two interchangeable calls does not.
    /// </para>
    /// <para>
    /// A compensation the reader could not resolve carries no id and is skipped, for the
    /// reason an unreadable policy set is: a diagnostic raised on a guess names a capability
    /// the author cannot find.
    /// </para>
    /// </remarks>
    private static void ReportCompensationPolicyConflicts(
        StepModel step, ChainLink link, List<Diagnostic> diagnostics)
    {
        if (step.Compensation is not { IsIdempotent: false, CapabilityId: { } compensationId })
        {
            return;
        }

        if (!step.PolicyKinds.Contains(CompensationRetryKind))
        {
            return;
        }

        diagnostics.Add(Diagnostic.Create(
            FlowXDiagnostics.RetryRequiresIdempotency,
            link.CallLocation,
            compensationId,
            CompensationRetryKind));
    }

    /// <summary>The policy kind that wraps the step. Judged by the step's idempotency.</summary>
    /// <remarks>
    /// Named here rather than spelled at each use, so that the two halves of FLOWX1014 read
    /// as the pair they are: <see cref="RetryKind"/> against the step,
    /// <see cref="CompensationRetryKind"/> against its undo.
    /// </remarks>
    private const string RetryKind = "Retry";

    /// <summary>
    /// The policy kind that wraps the step's <em>undo</em>. Judged by the compensation's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>These are <c>PolicySet</c>'s method names, not <c>FlowX.Core</c>'s kind
    /// constants.</strong> <see cref="PolicySetReader"/> walks the declared chain and returns
    /// <c>link.MethodName</c>, so what lands in <c>StepModel.PolicyKinds</c> is whatever the
    /// author literally called. The two coincide because <c>PolicySet</c> builds its
    /// descriptors with <c>nameof</c>, which is also why this cannot be a reference to
    /// <c>CompensationPolicy.CompensationRetryKind</c>: that constant names the descriptor
    /// kind, this names the DSL surface, and this assembly targets netstandard2.0 and can see
    /// neither.
    /// </para>
    /// <para>
    /// Pinned by the generator tests rather than by a fitness test, and pinned to the right
    /// thing: they declare a real <c>.CompensationRetry(...)</c> in real source and assert the
    /// report, so renaming the builder method fails them. A fitness test against Core's
    /// constant would keep passing while the analyzer had gone silent.
    /// </para>
    /// </remarks>
    private const string CompensationRetryKind = "CompensationRetry";

    /// <summary>The policy kind whose precondition is an absence of side effects — FLOWX1018.</summary>
    private const string CacheKind = "Cache";

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
                    ReadSensitiveMembers(current.TypeArguments[1]),
                    ReadMarkedMembers(current.TypeArguments[0], SubjectAttribute),
                    ReadMarkedMembers(current.TypeArguments[1], SubjectAttribute));
            }
        }

        return new FlowContracts("object", "object", [], [], [], []);
    }

    /// <summary>
    /// Decides which member names this flow's data subject, reporting <c>FLOWX1047</c> for a
    /// declaration the runtime would have to ignore.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four refusals, one message, and the order they are checked in is the order a reader
    /// would want them: what is wrong with the contract first, then what is wrong with the flow
    /// carrying it. A flow that trips two of them is told about the contract, because that is
    /// the one whose fix is not "delete the marker".
    /// </para>
    /// <para>
    /// The location is the marked member's own declaration wherever there is one, so the squiggle
    /// lands on the attribute the author wrote. A contract from a referenced assembly has no
    /// syntax in this compilation, and then the flow's own identifier carries it — which is also
    /// the only sensible place for the profile refusal, whose subject is the flow rather than
    /// the member.
    /// </para>
    /// </remarks>
    private static string? ResolveSubject(
        INamedTypeSymbol flowType,
        ClassDeclarationSyntax declaration,
        string profile,
        FlowContracts contracts,
        List<Diagnostic> diagnostics)
    {
        var flowLocation = declaration.Identifier.GetLocation();

        if (contracts.SubjectOutput.Length > 0)
        {
            diagnostics.Add(Diagnostic.Create(
                FlowXDiagnostics.SubjectCannotBeRecorded,
                contracts.SubjectOutput[0].Location ?? flowLocation,
                flowType.Name,
                string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    SubjectReasons.OnOutputFormat,
                    contracts.Output)));
        }

        if (contracts.SubjectInput.Length == 0)
        {
            return null;
        }

        if (contracts.SubjectInput.Length > 1)
        {
            diagnostics.Add(Diagnostic.Create(
                FlowXDiagnostics.SubjectCannotBeRecorded,
                contracts.SubjectInput[1].Location ?? flowLocation,
                flowType.Name,
                string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    SubjectReasons.AmbiguousFormat,
                    contracts.Input,
                    string.Join(", ", contracts.SubjectInput.Select(m => "'" + m.Name + "'")))));

            return null;
        }

        var marked = contracts.SubjectInput[0];

        if (!string.Equals(marked.TypeName, "string", System.StringComparison.Ordinal))
        {
            diagnostics.Add(Diagnostic.Create(
                FlowXDiagnostics.SubjectCannotBeRecorded,
                marked.Location ?? flowLocation,
                flowType.Name,
                string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    SubjectReasons.NotAStringFormat,
                    marked.Name,
                    marked.TypeName)));

            return null;
        }

        if (!string.Equals(profile, "Durable", System.StringComparison.Ordinal))
        {
            diagnostics.Add(Diagnostic.Create(
                FlowXDiagnostics.SubjectCannotBeRecorded,
                flowLocation,
                flowType.Name,
                string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    SubjectReasons.NotJournaledFormat,
                    profile)));

            return null;
        }

        return marked.Name;
    }

    /// <summary>
    /// The contract's members carrying <paramref name="attribute"/>, in declaration order, with
    /// the type and the source location each one was declared at.
    /// </summary>
    /// <remarks>
    /// Both spellings are read for <see cref="ReadSensitiveMembers"/>'s reason: a positional
    /// record's marker sits on the primary constructor parameter, written
    /// <c>[property: Subject]</c>, and Roslyn surfaces it on the generated property — but an
    /// author who wrote it on a plain property and got nothing would reasonably conclude the
    /// attribute does not work.
    /// </remarks>
    private static MarkedMember[] ReadMarkedMembers(ITypeSymbol contract, string attribute)
    {
        var marked = new List<MarkedMember>();

        foreach (var member in contract.GetMembers())
        {
            if (member is not IPropertySymbol and not IFieldSymbol)
            {
                continue;
            }

            var onMember = member.GetAttributes()
                .Any(a => a.AttributeClass?.ToDisplayString() == attribute);

            var onParameter = contract
                .GetMembers(".ctor")
                .OfType<IMethodSymbol>()
                .SelectMany(c => c.Parameters)
                .Any(parameter =>
                    string.Equals(parameter.Name, member.Name, System.StringComparison.OrdinalIgnoreCase) &&
                    parameter.GetAttributes()
                        .Any(a => a.AttributeClass?.ToDisplayString() == attribute));

            if (!onMember && !onParameter)
            {
                continue;
            }

            var type = member switch
            {
                IPropertySymbol property => property.Type,
                IFieldSymbol field => field.Type,
                _ => null,
            };

            marked.Add(new MarkedMember(
                member.Name,
                type is null ? "?" : type.ToDisplayString(),
                member.Locations.FirstOrDefault(l => l.IsInSource)));
        }

        return marked.ToArray();
    }

    /// <summary>A contract member carrying a marker, and where it was written.</summary>
    private sealed class MarkedMember
    {
        internal MarkedMember(string name, string typeName, Location? location)
        {
            Name = name;
            TypeName = typeName;
            Location = location;
        }

        internal string Name { get; }

        internal string TypeName { get; }

        internal Location? Location { get; }
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

    /// <summary>A flow's input and output contracts, and what each of them marks.</summary>
    private sealed class FlowContracts
    {
        internal FlowContracts(
            string input,
            string output,
            string[] sensitiveInput,
            string[] sensitiveOutput,
            MarkedMember[] subjectInput,
            MarkedMember[] subjectOutput)
        {
            Input = input;
            Output = output;
            SensitiveInput = sensitiveInput;
            SensitiveOutput = sensitiveOutput;
            SubjectInput = subjectInput;
            SubjectOutput = subjectOutput;
        }

        internal string Input { get; }

        internal string Output { get; }

        internal string[] SensitiveInput { get; }

        internal string[] SensitiveOutput { get; }

        internal MarkedMember[] SubjectInput { get; }

        internal MarkedMember[] SubjectOutput { get; }
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
