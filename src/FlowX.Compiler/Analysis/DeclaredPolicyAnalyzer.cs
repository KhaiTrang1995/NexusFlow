using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using FlowX.Compiler.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FlowX.Compiler.Analysis;

/// <summary>
/// Reports a declared policy the runtime will not apply: FLOWX1032 and FLOWX1033.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What is actually executed, established from the call sites rather than from the
/// documents.</strong> <c>FlowEngine</c> reads two policy properties in the whole engine —
/// <c>ExecutionPlan.HasCompensationPolicies</c> and <c>StepNode.CompensationRetry</c> — and
/// both are inside <c>CompensateAsync</c>, on the failure path. Underneath them,
/// <c>PolicyChain.Ordered</c> is read in exactly one place in <c>src/</c>:
/// <c>CompensationPolicy.From</c>, which <c>continue</c>s past every descriptor whose kind is
/// not <c>CompensationRetry</c>. <c>StepNode.Policies</c> — the chain that wraps the step
/// itself — is read by nothing at all. So one of the nine kinds <c>PolicySet</c> offers is
/// applied, and this analyzer is the build-time statement of that fact.
/// </para>
/// <para>
/// <strong>The cut is by what a policy wraps, not by which stage it runs in.</strong>
/// <c>Audit</c> is a <c>PolicyStage.Consistency</c> policy — stage 7, the same stage as
/// <c>CompensationRetry</c> — and it is inert, because <c>PolicyChain.ForStep</c> moves only
/// <c>CompensationRetry</c> onto the compensation's chain. A rule written against "stages 1–6
/// do not execute" would be silent on every declared audit, which in a banking flow is the
/// declaration that most reads as a control.
/// </para>
/// <para>
/// <strong>Two ids, because they have opposite lifetimes.</strong> FLOWX1032 reports a policy
/// P4 will execute and is deleted when it does; FLOWX1033 reports a <c>CompensationRetry</c>
/// on a step with no compensation, which no release executes because there is nothing for it
/// to wrap, and it survives P4. One id would give a team one suppression for two decisions
/// with different expiry dates.
/// </para>
/// <para>
/// <strong>A <see cref="DiagnosticAnalyzer"/> rather than a generator diagnostic</strong>,
/// matching <see cref="ExecutionProfileAnalyzer"/> and <see cref="TriggerDeclarationAnalyzer"/>.
/// The question is answered from one <c>.WithPolicy(...)</c> call and the set it names — no
/// graph, no step indices, nothing the generator uniquely knows — and it is worth answering
/// on the keystroke that types <c>.Timeout(...)</c> rather than when the generator next runs.
/// It also keeps <c>FlowAnalyzer</c>, which already carries FLOWX1014 and FLOWX1018 over the
/// same DSL call, from growing a third policy concern.
/// </para>
/// <para>
/// <strong>Silent wherever the emitter is silent.</strong> <see cref="PolicySetReader"/>
/// resolves only a set declared as a field or property initialiser in source, and returns
/// nothing rather than guessing; <c>FlowEmitter.PolicyArguments</c> emits nothing for exactly
/// that case. Reporting on a set the compiler could not read would name policies the author
/// cannot find, and could name a set that in fact holds nothing but a compensation retry.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DeclaredPolicyAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The one policy kind any code path in this runtime applies.</summary>
    /// <remarks>
    /// <c>PolicySet</c>'s method name, not <c>FlowX.Core</c>'s descriptor kind constant, for
    /// the reason <c>FlowAnalyzer</c> keeps its own copy: <see cref="PolicySetReader"/>
    /// returns <c>link.MethodName</c>, so what it hands back is whatever the author literally
    /// called. The two coincide because <c>PolicySet</c> builds its descriptors with
    /// <c>nameof</c>, and this assembly targets netstandard2.0 and can see neither.
    /// </remarks>
    private const string CompensationRetryKind = "CompensationRetry";

    /// <summary>The call this rule is about.</summary>
    private const string WithPolicyMethod = "WithPolicy";

    /// <summary>The call that decides whether a compensation retry has anything to wrap.</summary>
    private const string CompensateWithMethod = "CompensateWith";

    /// <summary>The call that begins the step segment both of the above attach to.</summary>
    private const string StepMethod = "Step";

    /// <summary>
    /// <c>FlowX.IStepBuilder&lt;TIn, TOut&gt;</c>, as metadata names it.
    /// </summary>
    /// <remarks>
    /// The semantic test that tells this chain from any other fluent <c>WithPolicy</c> in the
    /// compilation. <c>IStepBuilder</c> is also what makes the syntactic walk below exact: it
    /// declares exactly two methods, and the only thing that returns it is
    /// <c>IFlowBuilder.Step&lt;…&gt;</c>, so a step's segment of the chain is precisely a
    /// <c>Step</c> followed by <c>CompensateWith</c> and <c>WithPolicy</c> calls in any order.
    /// </remarks>
    private const string StepBuilderMetadataName = "IStepBuilder`2";

    /// <summary>The namespace <see cref="StepBuilderMetadataName"/> lives in.</summary>
    private const string AbstractionsNamespace = "FlowX";

    /// <summary><c>FlowX.PolicySet</c>, the type every <c>.WithPolicy(...)</c> argument has.</summary>
    private const string PolicySetTypeName = "PolicySet";

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(
            FlowXDiagnostics.PolicyIsNotExecutedByTheRuntime,
            FlowXDiagnostics.CompensationRetryHasNoCompensation,
            FlowXDiagnostics.StepDeclaresMoreThanOnePolicySet,
            FlowXDiagnostics.CompensationRetryRetriesNothing,
            FlowXDiagnostics.PolicySetCannotBeRead);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            return;
        }

        // The generated plan is a second part of the flow's class and contains no builder
        // chain; analysing it would report the hand-written declaration against a file
        // nobody can edit or suppress in.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        if (context.Node is not InvocationExpressionSyntax invocation ||
            MethodName(invocation) != WithPolicyMethod ||
            invocation.ArgumentList.Arguments.Count == 0 ||
            !IsStepBuilderCall(invocation, context.SemanticModel, context.CancellationToken))
        {
            return;
        }

        var argument = invocation.ArgumentList.Arguments[0].Expression;

        // The expression, not the whole argument, exactly as FlowAnalyzer stores it: this is
        // the text the author would edit, and a named argument's name is not part of it.
        var set = argument.ToString();
        var location = NameOf(invocation);

        // FLOWX1034 first, and it returns. Every other rule here says something about what
        // the compiled plan and the manifest carry, and a superseded set is carried by
        // neither — telling an author their discarded Timeout will not be executed is true
        // of a declaration that no longer exists.
        if (SupersedingSet(invocation) is { } winner)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                FlowXDiagnostics.StepDeclaresMoreThanOnePolicySet, location, set, winner));

            return;
        }

        var contents = PolicySetReader.Resolve(argument, context.SemanticModel);

        if (!contents.IsReadable)
        {
            ReportUnreadableSet(context, argument, set, location);
            return;
        }

        var kinds = contents.Kinds;

        if (kinds.Count == 0)
        {
            return;
        }

        ReportInertPolicies(context, kinds, set, location);

        if (ReportDroppedCompensationRetry(context, kinds, invocation, set, location))
        {
            return;
        }

        ReportCompensationRetryThatRetriesNothing(context, contents, invocation, set, location);
    }

    /// <summary>
    /// FLOWX1036 — the set is a real <c>PolicySet</c> reference and its contents cannot be
    /// read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Gated on the argument's type rather than on its symbol.</strong> An expression
    /// the compiler could not bind at all is a compile error the compiler already reports, and
    /// a second message on every keystroke between <c>.WithPolicy(</c> and the name would make
    /// this rule the noisiest thing in an editor. Asking for the type covers the shapes a
    /// symbol lookup misses too — a conditional, an invocation, an element access — all of
    /// which are unreadable sets and none of which resolves to a symbol.
    /// </para>
    /// <para>
    /// <c>Type</c> and not <c>ConvertedType</c>: the parameter is a <c>PolicySet</c>, so the
    /// converted type of a broken argument is <c>PolicySet</c> as well, and reading it would
    /// put the gate back where it started.
    /// </para>
    /// </remarks>
    private static void ReportUnreadableSet(
        SyntaxNodeAnalysisContext context,
        ExpressionSyntax argument,
        string set,
        Location location)
    {
        var type = context.SemanticModel.GetTypeInfo(argument, context.CancellationToken).Type;

        if (type?.MetadataName != PolicySetTypeName ||
            type.ContainingNamespace?.ToDisplayString() != AbstractionsNamespace)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            FlowXDiagnostics.PolicySetCannotBeRead, location, set));
    }

    /// <summary>
    /// FLOWX1035 — a compensation retry whose declared attempt count retries nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Only where the retry reaches the plan.</strong> The caller returns before this
    /// when FLOWX1033 fired, and this asks for the compensation directly rather than trusting
    /// that: on a step whose chain the walk could not follow, FLOWX1033 is silent and so is
    /// this, because absence of a compensation is then something nobody observed.
    /// </para>
    /// <para>
    /// <strong>Read from syntax, never from a second semantic model.</strong> The set is
    /// nearly always declared in a different file, a <see cref="SemanticModel"/> belongs to
    /// one tree, and RS1030 forbids an analyzer from asking the compilation for another. So
    /// the count is a literal or it is unknown — <c>DeadlineCoherenceAnalyzer</c> works under
    /// exactly this restriction, and each spelling not recognised costs a false negative
    /// rather than a wrong number.
    /// </para>
    /// <para>
    /// <strong>Silent for a well-known set.</strong> A set resolved out of
    /// <c>PolicySetReader.WellKnownSets</c> has no initialiser to read, which is right twice
    /// over: there is no syntax, and FlowX fixed those arguments itself.
    /// </para>
    /// </remarks>
    private static void ReportCompensationRetryThatRetriesNothing(
        SyntaxNodeAnalysisContext context,
        PolicySetContents contents,
        InvocationExpressionSyntax invocation,
        string set,
        Location location)
    {
        if (contents.Initialiser is not { } initialiser || !DeclaresCompensation(invocation))
        {
            return;
        }

        if (StepCapabilityName(invocation) is not { } step)
        {
            return;
        }

        foreach (var policy in FlowChainWalker.Walk(initialiser))
        {
            if (policy.MethodName != CompensationRetryKind ||
                LiteralCount(Argument(policy, AttemptsParameter, 0)) is not { } attempts ||
                attempts > 1)
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                FlowXDiagnostics.CompensationRetryRetriesNothing,
                location,
                step,
                attempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
                set));

            return;
        }
    }

    /// <summary>The parameter <see cref="CompensationRetryKind"/>'s attempt count is written as.</summary>
    private const string AttemptsParameter = "attempts";

    /// <summary>The named argument if it was written that way, else the one in that position.</summary>
    private static ExpressionSyntax? Argument(ChainLink link, string name, int position)
    {
        var arguments = link.Invocation.ArgumentList.Arguments;

        foreach (var argument in arguments)
        {
            if (argument.NameColon?.Name.Identifier.ValueText == name)
            {
                return argument.Expression;
            }
        }

        return position < arguments.Count && arguments[position].NameColon is null
            ? arguments[position].Expression
            : null;
    }

    /// <summary>
    /// An integer written down, or <see langword="null"/> for anything the compiler would have
    /// to fold.
    /// </summary>
    /// <remarks>
    /// The unary minus is spelled out because <c>-1</c> is not a literal in C# syntax — it is
    /// a prefix expression over one — and <c>CompensationPolicy.From</c> clamps a negative
    /// count to a single attempt exactly as it clamps zero. A rule blind to the minus sign
    /// would be silent on the one spelling that is furthest from a retry.
    /// </remarks>
    private static int? LiteralCount(ExpressionSyntax? expression) => expression switch
    {
        LiteralExpressionSyntax literal when literal.Token.Value is int count => count,
        PrefixUnaryExpressionSyntax negated when negated.IsKind(SyntaxKind.UnaryMinusExpression) =>
            LiteralCount(negated.Operand) is { } count ? -count : null,
        _ => null,
    };

    /// <summary>
    /// The set named by a later <c>.WithPolicy(...)</c> on the same step, or
    /// <see langword="null"/> when this call is the step's own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Upwards only, and that is the whole rule: <c>StepModel.WithPolicy</c> assigns, so the
    /// <em>last</em> call on the step is the one that survives and every earlier one is
    /// discarded. Reporting from the discarded call puts the message on the line whose set
    /// disappeared rather than on the line that is working, and makes three calls report twice
    /// rather than once.
    /// </para>
    /// <para>
    /// Bounded by <c>IStepBuilder</c>'s two methods, like every other walk here: a
    /// <c>.Step&lt;T&gt;()</c> between two <c>.WithPolicy(...)</c> calls ends this step's
    /// segment, which is what keeps the rule off the ordinary flow that declares a policy on
    /// each of its steps.
    /// </para>
    /// </remarks>
    private static string? SupersedingSet(InvocationExpressionSyntax withPolicy)
    {
        for (var call = Enclosing(withPolicy); call is not null; call = Enclosing(call))
        {
            var name = MethodName(call);

            if (name == WithPolicyMethod)
            {
                return call.ArgumentList.Arguments.Count > 0
                    ? call.ArgumentList.Arguments[0].Expression.ToString()
                    : null;
            }

            if (name != CompensateWithMethod)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// FLOWX1032 — every kind in the set except the one the runtime applies.
    /// </summary>
    /// <remarks>
    /// One report naming every inert kind, rather than one report per kind. A set of eight
    /// reported eight times on one line is how a catalogue gets suppressed wholesale, which
    /// is the argument <see cref="ExecutionProfileAnalyzer"/> already makes for reporting
    /// once at the declaration rather than once per consequence. The kinds arrive ordinally
    /// sorted from <see cref="PolicySetReader"/>, so the message is the same on every build
    /// of the same source.
    /// </remarks>
    private static void ReportInertPolicies(
        SyntaxNodeAnalysisContext context,
        IReadOnlyList<string> kinds,
        string set,
        Location location)
    {
        var inert = kinds.Where(static kind => kind != CompensationRetryKind).ToList();

        if (inert.Count == 0)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            FlowXDiagnostics.PolicyIsNotExecutedByTheRuntime,
            location,
            set,
            string.Join(", ", inert)));
    }

    /// <summary>
    /// FLOWX1033 — a compensation retry on a step that declares no compensation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Both orders are legal and neither reports.</strong> <c>.CompensateWith&lt;T&gt;()</c>
    /// and <c>.WithPolicy(...)</c> both return <c>IStepBuilder</c>, so the compensation may sit
    /// either side of the policy in source. A rule that read only the receiver chain would
    /// fire on <c>.WithPolicy(...).CompensateWith&lt;T&gt;()</c>, which is a rule that reports
    /// on correct code half the time — the trap <c>ReportCompensationPolicyConflicts</c>
    /// documents for FLOWX1014 and answers the other way, by reporting from whichever call
    /// completed the pairing.
    /// </para>
    /// <para>
    /// <strong>Silent when the step cannot be found.</strong> A chain broken across statements
    /// — <c>var step = flow.Step&lt;T&gt;(); step.WithPolicy(p);</c> — hides the
    /// <c>CompensateWith</c> as effectively as it hides the <c>Step</c>, so absence of a
    /// compensation is not something this walk observed. It is silent rather than reporting on
    /// what it could not see; FLOWX1032 is unaffected, because that rule is a statement about
    /// the set rather than about the step.
    /// </para>
    /// </remarks>
    /// <returns>
    /// Whether the report was raised. The caller uses it to stay off FLOWX1035: a retry with
    /// no compensation reaches no plan node at all, so how many attempts it asked for is not
    /// the finding, and two reports on one line would leave the author choosing which to act
    /// on.
    /// </returns>
    private static bool ReportDroppedCompensationRetry(
        SyntaxNodeAnalysisContext context,
        IReadOnlyList<string> kinds,
        InvocationExpressionSyntax invocation,
        string set,
        Location location)
    {
        if (!kinds.Contains(CompensationRetryKind) || DeclaresCompensation(invocation))
        {
            return false;
        }

        if (StepCapabilityName(invocation) is not { } step)
        {
            return false;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            FlowXDiagnostics.CompensationRetryHasNoCompensation,
            location,
            ImmutableDictionary<string, string?>.Empty.Add(
                CallReachesNoPlanNodeProperty,
                (kinds.Count == 1).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            step,
            set));

        return true;
    }

    /// <summary>
    /// Set on a FLOWX1033 report when deleting the whole <c>.WithPolicy(...)</c> call would
    /// provably change nothing the engine executes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// True exactly when the set declares <c>CompensationRetry</c> and nothing else, because
    /// then <c>FlowEmitter.PolicyArguments</c> emits neither half: the forward chain needs a
    /// kind that is not the compensation retry, and the compensation chain needs a
    /// compensation. The call compiles to no argument at all, so removing it is a
    /// behaviour-preserving edit rather than a choice between two designs.
    /// </para>
    /// <para>
    /// <strong>Carried on the diagnostic rather than recomputed by the fix.</strong>
    /// <c>FlowX.Compiler.CodeFixes</c> deliberately does not reference this assembly — a
    /// development dependency does not flow transitively, and a fixes assembly whose
    /// reference the host cannot resolve is dropped without a message — so it cannot call
    /// <see cref="PolicySetReader"/>. A property bag is the seam Roslyn provides for exactly
    /// this, and it keeps one implementation of "what does this set contain" rather than two
    /// that drift.
    /// </para>
    /// </remarks>
    public const string CallReachesNoPlanNodeProperty = "FlowX.CallReachesNoPlanNode";

    /// <summary>
    /// Whether the step this <c>.WithPolicy(...)</c> attaches to declares a compensation.
    /// </summary>
    /// <remarks>
    /// The step's segment of the chain is walked in both directions, because the two calls
    /// commute. It is bounded in both by the same rule: <c>IStepBuilder</c> declares only
    /// <c>CompensateWith</c> and <c>WithPolicy</c>, so any other method name is the end of
    /// this step and the beginning of something else.
    /// </remarks>
    private static bool DeclaresCompensation(InvocationExpressionSyntax withPolicy) =>
        FoundInSegment(withPolicy, Receiver) || FoundInSegment(withPolicy, Enclosing);

    /// <summary>
    /// Whether a <c>.CompensateWith&lt;T&gt;()</c> sits in this step's segment, walking in
    /// the direction <paramref name="next"/> gives.
    /// </summary>
    /// <param name="withPolicy">The call the walk starts beside.</param>
    /// <param name="next">
    /// <see cref="Receiver"/> to walk down towards the <c>Step</c>, <see cref="Enclosing"/>
    /// to walk up towards whatever follows it.
    /// </param>
    /// <remarks>
    /// One function for both directions, because the stopping rule is the same in both and
    /// writing it twice is how the two come to disagree about which method names end a step.
    /// </remarks>
    private static bool FoundInSegment(
        InvocationExpressionSyntax withPolicy,
        System.Func<InvocationExpressionSyntax, InvocationExpressionSyntax?> next)
    {
        for (var call = next(withPolicy); call is not null; call = next(call))
        {
            var name = MethodName(call);

            if (name == CompensateWithMethod)
            {
                return true;
            }

            if (name != WithPolicyMethod)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// The capability type named by the <c>.Step&lt;…&gt;</c> this policy attaches to, as the
    /// author wrote it, or <see langword="null"/> when the chain does not reach one.
    /// </summary>
    /// <remarks>
    /// Written form rather than the resolved symbol's full name, so the message quotes the
    /// text in the file. The message's job is to say which of a flow's steps is meant, and a
    /// namespace-qualified name would be a longer way of saying it.
    /// </remarks>
    private static string? StepCapabilityName(InvocationExpressionSyntax withPolicy)
    {
        for (var call = Receiver(withPolicy); call is not null; call = Receiver(call))
        {
            var name = MethodName(call);

            if (name == StepMethod)
            {
                return TypeArgument(call);
            }

            if (name != WithPolicyMethod && name != CompensateWithMethod)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>The first type argument of a call, or <see langword="null"/> if it has none.</summary>
    private static string? TypeArgument(InvocationExpressionSyntax invocation) =>
        (invocation.Expression as MemberAccessExpressionSyntax)?.Name is GenericNameSyntax generic &&
        generic.TypeArgumentList.Arguments.Count > 0
            ? generic.TypeArgumentList.Arguments[0].ToString()
            : null;

    /// <summary>
    /// Whether this invocation is <c>IStepBuilder.WithPolicy</c> and not some other fluent
    /// method with the same name.
    /// </summary>
    /// <remarks>
    /// The syntactic checks above are cheap and run on every invocation in the compilation;
    /// this one costs a symbol lookup and runs only on the ones already named
    /// <c>WithPolicy</c>. Ordering them that way is what keeps the analyzer off the hot path
    /// of an ordinary file.
    /// </remarks>
    private static bool IsStepBuilderCall(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        System.Threading.CancellationToken cancellationToken) =>
        semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is IMethodSymbol method &&
        method.ContainingType?.OriginalDefinition is { } declaring &&
        declaring.MetadataName == StepBuilderMetadataName &&
        declaring.ContainingNamespace?.ToDisplayString() == AbstractionsNamespace;

    /// <summary>The method name of a fluent call, or <see langword="null"/> if it is not one.</summary>
    private static string? MethodName(InvocationExpressionSyntax invocation) =>
        (invocation.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.ValueText;

    /// <summary>The invocation this one is called on, walking down the chain.</summary>
    private static InvocationExpressionSyntax? Receiver(InvocationExpressionSyntax invocation) =>
        (invocation.Expression as MemberAccessExpressionSyntax)?.Expression as InvocationExpressionSyntax;

    /// <summary>The invocation called on this one, walking up the chain.</summary>
    private static InvocationExpressionSyntax? Enclosing(InvocationExpressionSyntax invocation) =>
        invocation.Parent is MemberAccessExpressionSyntax member && member.Expression == invocation
            ? member.Parent as InvocationExpressionSyntax
            : null;

    /// <summary>
    /// The <c>WithPolicy</c> identifier's own span.
    /// </summary>
    /// <remarks>
    /// The member name, deliberately, and for <c>ChainLink.CallLocation</c>'s reason: a fluent
    /// chain nests its receiver inside every later call, so an invocation's span begins at the
    /// head of the chain. Reporting there would put every policy finding in a flow on the
    /// first line of <c>Define</c>, and a <c>#pragma</c> aimed at it would cover the lot.
    /// </remarks>
    private static Location NameOf(InvocationExpressionSyntax invocation) =>
        invocation.Expression is MemberAccessExpressionSyntax member
            ? member.Name.GetLocation()
            : invocation.GetLocation();
}
