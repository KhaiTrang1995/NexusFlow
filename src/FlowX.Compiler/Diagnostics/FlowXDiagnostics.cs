using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace FlowX.Compiler.Diagnostics;

/// <summary>
/// The <c>FLOWX####</c> catalogue.
/// </summary>
/// <remarks>
/// <para>
/// Every descriptor here carries a title, a message that names the offending symbol,
/// a description saying <em>what to do instead</em>, and a help URI. That is not
/// polish — a diagnostic a developer has to search the web for is a diagnostic that
/// costs more than the mistake it catches, and the fitness function
/// <c>EveryDiagnosticIsHelpful</c> fails the build if any field is missing.
/// </para>
/// <para>
/// Only the rules the P0 generator can actually enforce are here. Codes reserved for
/// later phases are deliberately absent rather than stubbed: a descriptor nothing
/// raises is a promise the compiler is not keeping.
/// </para>
/// </remarks>
public static class FlowXDiagnostics
{
    private const string Category = "FlowX";
    private const string HelpRoot = "https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/";

    /// <summary>FLOWX1001 — a flow type is not declared <c>partial</c>.</summary>
    public static readonly DiagnosticDescriptor FlowMustBePartial = Create(
        "FLOWX1001",
        "Flow must be partial",
        "Flow '{0}' must be declared 'partial' so its compiled plan can be generated into it",
        "The generator emits the execution plan and the step dispatcher as a second part of " +
        "your class. Add the 'partial' modifier to the class declaration.");

    /// <summary>FLOWX1002 — a step names a type that is not a capability.</summary>
    public static readonly DiagnosticDescriptor StepIsNotACapability = Create(
        "FLOWX1002",
        "Step type is not a capability",
        "'{0}' is used as a step but does not implement ICapability<TIn, TOut>",
        "A step invokes a capability. Implement ICapability<TIn, TOut> on the type, or " +
        "remove the step.");

    /// <summary>FLOWX1003 — a capability references a transport or plugin assembly.</summary>
    public static readonly DiagnosticDescriptor CapabilityReferencesTransport = Create(
        "FLOWX1003",
        "Capability references a transport",
        "Capability '{0}' references transport type '{1}'",
        "A capability must not know how it was invoked, or the same flow cannot run behind " +
        "HTTP, Kafka and cron unchanged. Move the transport concern into a trigger or a plugin.");

    /// <summary>FLOWX1004 — a capability invokes another capability.</summary>
    public static readonly DiagnosticDescriptor CapabilityInvokesCapability = Create(
        "FLOWX1004",
        "Capability invokes another capability",
        "Capability '{0}' invokes capability '{1}'",
        "Capabilities form a set, not a graph — that is what makes the architecture " +
        "analysable and each capability testable alone. Compose them in a flow instead.");

    /// <summary>FLOWX1005 — one flow inherits from another.</summary>
    public static readonly DiagnosticDescriptor FlowInheritsFlow = Create(
        "FLOWX1005",
        "Flow inherits from another flow",
        "Flow '{0}' inherits from flow '{1}'",
        "Inheritance hides control flow from the compiled graph and from the manifest. " +
        "Extract the shared steps into a sub-flow and compose it.");

    /// <summary>FLOWX1010 — a capability does not declare an authorisation stance.</summary>
    public static readonly DiagnosticDescriptor CapabilityMissingAuthorization = Create(
        "FLOWX1010",
        "Capability does not declare an authorisation stance",
        "Capability '{0}' does not declare Authorization",
        "There is no permissive default. Declare Authorization explicitly — including " +
        "Authorization.Public, which is a reviewable statement rather than an omission.");

    /// <summary>
    /// FLOWX1011 — a flow condition, selector or projection reads something outside the
    /// flow's state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>{0}</c> is what the construct is called — "condition", "Switch selector",
    /// "Return projection" — and it appears twice, once singular and once pluralised with
    /// a trailing <c>s</c>. The rule covers every <c>IFlowBuilder</c> delegate that takes
    /// the flow context, so a message hard-coding "condition" would name the wrong
    /// construct in five of eight cases, and a reader who is pointed at the wrong noun
    /// stops trusting the diagnostic.
    /// </para>
    /// <para>
    /// A <strong>warning</strong> by default and reported as an <strong>error</strong>
    /// when the flow declares <c>Profile = ExecutionProfile.Durable</c>, which is the
    /// asymmetry ADR-0003 ratified for the determinism rules: a durable flow is replayed
    /// and must take the branch it took the first time, an ephemeral one is not replayed
    /// at all.
    /// </para>
    /// <para>
    /// ADR-0003 and <c>06-Execution-Engine.md</c> §5 say <em>informational</em> for the
    /// ephemeral case. A warning, deliberately: <c>Ephemeral</c> is the only profile the
    /// runtime executes today, an Info diagnostic never appears in a build log, and the
    /// rule would therefore have shipped doing nothing anywhere — which is the state
    /// FLOWX1011 was already in. The flow is also one attribute away from being replayed,
    /// and <c>FlowErrors.PredicateFailed</c> already calls an impure predicate a defect
    /// under either profile.
    /// </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor PredicateMustBePure = Create(
        "FLOWX1011",
        "Condition, selector or projection reads something outside the flow's state",
        "The {0} in flow '{1}' reads '{2}', which is {3}; {0}s may read only the flow " +
        "context, the flow input and prior step results",
        "A branch decision, a step input and the flow's own result must each be a function " +
        "of what the flow knows, or the same instance behaves differently on two runs and a " +
        "durable replay diverges from the run it is replaying. Read time, identity and " +
        "randomness through the context — ctx.UtcNow, ctx.NewId(), ctx.Random — which the " +
        "journal reproduces, and move anything needing the outside world into a capability " +
        "whose result the flow can then read.",
        DiagnosticSeverity.Warning);

    /// <summary>FLOWX1013 — two branches of a <c>Parallel</c> write the same context slot.</summary>
    /// <remarks>
    /// <para>
    /// The one rule in this catalogue whose subject is a race. Branches of a fork share the
    /// flow's context, and the state bag is keyed by contract type — so two branches
    /// producing the same type are two threads writing one key, and which value the step
    /// after the join reads depends on which branch finished last. The runtime cannot
    /// detect it: both writes are legal, both succeed, and the result is simply one of the
    /// two.
    /// </para>
    /// <para>
    /// An error rather than a warning, on the same grounds as FLOWX1014: what it prevents
    /// is not a mistake that shows up in a test, it is a value that is right in
    /// development and wrong under load.
    /// </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor ParallelBranchesMustWriteDisjointSlots = Create(
        "FLOWX1013",
        "Parallel branches must write disjoint context slots",
        "Branches {0} and {1} of the parallel step in flow '{2}' both produce '{3}', so " +
        "they race to write the same context slot",
        "Parallel branches share the flow's context, which is keyed by contract type, so " +
        "two branches producing the same type race and the winner is whichever finished " +
        "last. Give each branch its own output contract — a distinct record per check is " +
        "usually the honest modelling anyway — or run the steps in sequence, where the " +
        "second overwriting the first is a decision rather than an accident.");

    /// <summary>FLOWX1014 — a retry policy is attached to a non-idempotent capability.</summary>
    public static readonly DiagnosticDescriptor RetryRequiresIdempotency = Create(
        "FLOWX1014",
        "Retry requires an idempotent capability",
        "Capability '{0}' declares Idempotent = false, so a Retry policy cannot be attached",
        "Retrying a non-idempotent operation duplicates its effect; for a payment capture " +
        "that is a duplicate charge. Make the capability idempotent and declare it, or " +
        "handle the failure in the flow.");

    /// <summary>FLOWX1015 — a capability implements <c>ICapability</c> more than once.</summary>
    public static readonly DiagnosticDescriptor CapabilityHasMultipleContracts = Create(
        "FLOWX1015",
        "Capability implements more than one contract",
        "Capability '{0}' implements ICapability<,> {1} times",
        "A capability has exactly one input and one output type. Split it into separate " +
        "capabilities, one per business operation.");

    /// <summary>FLOWX1016 — a capability throws where it should return <c>Result.Fail</c>.</summary>
    /// <remarks>
    /// <para>
    /// Rule 2 of <c>07-Capability-Model.md §3</c>, and the mitigation ADR-0007 names for
    /// its own most-complained-about consequence. Half the rule enforces itself — the
    /// interface returns <c>ValueTask&lt;Result&lt;TOut&gt;&gt;</c>, so a capability cannot
    /// fail to return a <c>Result</c>. The other half, <em>expected failures are values</em>,
    /// was checked by nothing: a <c>throw</c> compiles, the engine catches it at the
    /// capability boundary and counts it as a defect, and the business outcome it really
    /// described is then absent from the signature, from the manifest's error catalogue and
    /// from the retry classification the error category drives.
    /// </para>
    /// <para>
    /// A <strong>warning</strong>, and the reason is exactly what the rule cannot prove.
    /// <em>Expected</em> is a judgement about a domain, not a property of a type: this
    /// analyzer sees a <c>throw new</c> and decides from the exception's type alone, which
    /// is a list and not a proof. The catalogue's bar for an error is a mistake that is
    /// structurally impossible to recover from at run time, and this one is not — the
    /// engine catches it. A rule that stops the build on a judgement it cannot make is a
    /// rule that gets suppressed file-wide, and a suppressed rule protects nothing. This
    /// repository builds with <c>TreatWarningsAsErrors</c>, so it is still a break here; a
    /// consumer who disagrees downgrades it once in <c>.editorconfig</c> rather than with a
    /// pragma per capability.
    /// </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor ExpectedFailureIsThrown = Create(
        "FLOWX1016",
        "Expected failures are values, not exceptions",
        "Capability '{0}' throws '{1}'; an outcome a caller could reasonably handle is a " +
        "Result.Fail(Error) value, not an exception",
        "Business outcomes — declined, out of stock, not cancellable — are values. Thrown, " +
        "they are invisible in the signature, absent from the manifest's error catalogue, " +
        "indistinguishable in telemetry from a genuine defect, and cost 5-20 microseconds " +
        "each against a 5 microsecond platform budget (ADR-0007). Return " +
        "Result.Fail(new Error(code, message, category)) instead, and keep throwing only for " +
        "defects — a null argument, an unimplemented branch, a disposed object — which the " +
        "engine already reports as defects rather than as outcomes.",
        DiagnosticSeverity.Warning);

    /// <summary>FLOWX1017 — a suspension point in a non-durable flow.</summary>
    public static readonly DiagnosticDescriptor AwaitSignalRequiresDurable = Create(
        "FLOWX1017",
        "AwaitSignal requires the Durable profile",
        "Flow '{0}' uses AwaitSignal but runs under the {1} profile",
        "An in-memory wait does not survive a deployment, a crash or a scale-in. Set " +
        "Profile = ExecutionProfile.Durable on the flow.");

    /// <summary>FLOWX1018 — a cache policy on a capability with side effects.</summary>
    public static readonly DiagnosticDescriptor CacheRequiresNoSideEffects = Create(
        "FLOWX1018",
        "Cache requires a capability with no side effects",
        "Capability '{0}' declares side effects, so a Cache policy cannot be attached",
        "A cache hit returns a success without performing the effect. Remove the Cache " +
        "policy, or split the read out into its own capability.");

    /// <summary>FLOWX1019 — a flow deadline its own steps cannot fit inside.</summary>
    /// <remarks>
    /// <para>
    /// The arithmetic <c>14-Performance.md §7</c> and <c>FlowDeadlineAttribute</c> both
    /// describe: a step's <c>Timeout</c> policy bounds one attempt, a <c>Retry</c> policy
    /// multiplies the attempts, and the flow's <c>[FlowDeadline]</c> is an absolute budget
    /// that a retry never resets. When the attempts alone outlast the budget, the last
    /// steps of the flow can never run — the deadline cancels them — and the failure
    /// arrives as a timeout several steps away from the policy that caused it.
    /// </para>
    /// <para>
    /// A <strong>warning</strong>, which is what both documents say and is also the right
    /// answer: the sum is the <em>worst</em> case, not the expected one. A retry only
    /// happens when an attempt fails, so an incoherent budget is a flow that is correct
    /// until the day its dependency is slow — real, but not a structural impossibility,
    /// and a deliberately pessimistic budget is a legitimate thing to declare.
    /// </para>
    /// <para>
    /// <c>{3}</c> is the arithmetic written out, step by step, because the useful thing is
    /// never that the sum is too large — it is which step's policy to change.
    /// </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor DeadlineCannotFitSteps = Create(
        "FLOWX1019",
        "Flow deadline is shorter than the step timeouts it must contain",
        "Flow '{0}' declares a deadline of {1}, but its steps can spend at least {2} " +
        "before it: {3}",
        "The flow deadline is absolute and is never reset by a retry, so a step whose " +
        "attempts outlast it is cancelled mid-way and the steps after it never run at all. " +
        "The number below is a floor, not an estimate: it counts only the steps whose " +
        "timeout the compiler can read, and it ignores retry backoff, which adds more. " +
        "Lengthen the deadline, shorten the step timeout, reduce the retry attempts, or " +
        "split the work into a second flow with a budget of its own.",
        DiagnosticSeverity.Warning);

    /// <summary>FLOWX1020 — a step consumes a type no earlier step produces.</summary>
    /// <remarks>
    /// The message lists what the flow <em>can</em> supply as well as what is missing.
    /// Naming only the absent type leaves the developer to reconstruct the state bag in
    /// their head from the chain above the cursor; naming the alternatives usually makes
    /// the fix — a reorder, or an explicit mapping — obvious from the message alone.
    /// </remarks>
    public static readonly DiagnosticDescriptor StepInputIsNeverProduced = Create(
        "FLOWX1020",
        "Step consumes a contract no earlier step produces",
        "Step '{0}' consumes '{1}', which nothing before it in flow '{2}' produces; the " +
        "context can supply: {3}",
        "Step inputs are bound out of the flow's state bag by exact type: the engine seeds " +
        "the flow's own input, and everything else is there because an earlier step " +
        "returned it. Move the step after one that produces the type, add a step that " +
        "does, or supply it explicitly with .Step<TCapability, TStepIn>(ctx => ...).");

    /// <summary>FLOWX1021 — a flow composes itself, directly or through a chain.</summary>
    /// <remarks>
    /// <para>
    /// <c>docs/08-Flow-Definition.md</c> §3.7 has said since before anything could declare a
    /// sub-flow that "cycles are a compile error (FLOWX1021). The flow graph is a DAG,
    /// always." Until <see cref="Analysis.SubFlowCycleAnalyzer"/> existed the id was
    /// reserved and the sentence was a promise the compiler was not keeping.
    /// </para>
    /// <para>
    /// <c>{1}</c> is the cycle written out — <c>order.place → order.fulfil → order.place</c>
    /// — because the useful thing about a cycle is never that there is one, it is which
    /// edge to cut. A message naming only the flow would send a reader to look for a
    /// <c>SubFlow</c> call that may be three flows away.
    /// </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor SubFlowCycle = Create(
        "FLOWX1021",
        "Sub-flow composition forms a cycle",
        "Flow '{0}' composes itself: {1}",
        "The flow graph is a DAG. A cycle in it has no bottom — each level rents a context, " +
        "opens a compensation scope and shortens the deadline, so the recursion ends in a " +
        "stack overflow or a deadline nobody can explain, and neither failure names the flow " +
        "that caused it. Break the cycle by extracting the steps the two flows share into a " +
        "third flow that composes neither, or by making the repeated work a capability, " +
        "which is a set and not a graph.");

    /// <summary>FLOWX1026 — a <c>.SubFlow(...)</c> call the compiler will not turn into a step.</summary>
    /// <remarks>
    /// <para>
    /// One id for two refusals, because they are the same sentence from the author's point
    /// of view: <em>this composition cannot become a step, and here is why</em>. The two are
    /// an <c>AwaitCompletion</c> mode, which needs a durable suspension point that does not
    /// exist, and a target that carries no <c>[Flow]</c> attribute, which has no compiled
    /// plan to run. Splitting them would give two pages saying the same thing about the same
    /// line.
    /// </para>
    /// <para>
    /// An error rather than a warning, and that is the whole point. Both cases would
    /// otherwise be silent: the step would simply not be emitted, and a flow would ship
    /// missing the composition its author wrote. A dropped step is the one diagnostic
    /// severity question this catalogue does not have to think about.
    /// </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor SubFlowCannotBeComposed = Create(
        "FLOWX1026",
        "Sub-flow cannot be composed",
        "The sub-flow step in flow '{0}' cannot be composed: {1}",
        "A '.SubFlow(...)' that the compiler cannot turn into a step would be dropped from " +
        "the plan and from the manifest, so the flow would ship without the composition its " +
        "author wrote. Compose a type that carries [Flow], and use SubFlow<T>() or " +
        "SubFlow<T>(SubFlowMode.Detached) — AwaitCompletion needs a durable suspension " +
        "point, and there is no journal to suspend into in this release.");

    /// <summary>FLOWX1023 — a flow declares no steps.</summary>
    public static readonly DiagnosticDescriptor FlowHasNoSteps = Create(
        "FLOWX1023",
        "Flow declares no steps",
        "Flow '{0}' declares no steps",
        "An empty flow has no observable behaviour. This is almost always a Define method " +
        "that returned early or a chain that was never assigned.");

    /// <summary>FLOWX1024 — an <c>.Emit&lt;T&gt;()</c> step that nothing will publish.</summary>
    /// <remarks>
    /// A warning, not an error, and the only one in the set. The step is real: it is in
    /// the compiled plan and in the manifest, and a consumer reading the manifest will
    /// believe the event is published. Until the outbox exists (design principle P8) it
    /// is not, and the gap between what the manifest promises and what the process does
    /// is exactly the kind of thing that is discovered in production. Saying so at build
    /// time is the cheapest place to find out.
    /// </remarks>
    public static readonly DiagnosticDescriptor EmitIsNotYetPublished = Create(
        "FLOWX1024",
        "Emit step is recorded but not published",
        "Flow step '.Emit<{0}>' is compiled into the plan but no event is published yet",
        "Transactional outbox publication is not implemented in this release. The step " +
        "appears in the plan and the manifest, so downstream consumers will expect the " +
        "event — suppress this warning only once you have confirmed nothing depends on " +
        "it being delivered.",
        DiagnosticSeverity.Warning);

    /// <summary>FLOWX1025 — a trigger attribute the compiler cannot read.</summary>
    /// <remarks>
    /// <para>
    /// A <strong>warning</strong>, and the reasoning is the same shape as FLOWX1024's:
    /// the source is not wrong, the artifact is incomplete. The flow declares a trigger,
    /// the build succeeds, and <c>flowx.manifest.json</c> simply has no entry for it —
    /// which <c>flowx diff</c> cannot tell apart from "this flow has no trigger", so the
    /// gate that classifies a removed trigger as breaking silently loses its input.
    /// </para>
    /// <para>
    /// Not an error, deliberately. The attribute usually belongs to a third-party
    /// transport plugin, so the developer seeing this often cannot fix it in their own
    /// repository — and <c>17-Plugin-System.md §1</c> commits to the opposite of a
    /// platform where using a plugin's trigger fails the build. This repository builds
    /// with <c>TreatWarningsAsErrors</c>, so it is a break <em>here</em>; a consumer who
    /// has accepted the gap can downgrade it in <c>.editorconfig</c>, which is a decision
    /// recorded in their repository rather than a suppression scattered through source.
    /// </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor TriggerCannotBeRead = Create(
        "FLOWX1025",
        "Trigger attribute cannot be read by the compiler",
        "Trigger '{0}' on flow '{1}' is not one the compiler can read, so this flow " +
        "publishes no trigger in the manifest",
        "A trigger's Kind is an abstract property each attribute overrides — executable " +
        "code, not attribute data — so the compiler can only read the trigger attributes " +
        "FlowX.Abstractions ships, and it will not invent a kind for any other. The " +
        "consequence is not cosmetic: the flow's triggers are absent from the manifest, " +
        "and 'flowx diff' reads that absence as 'this flow has no trigger' rather than as " +
        "'the compiler could not tell', so removing the trigger later is not reported as " +
        "breaking. Declare the flow with one of the built-in trigger attributes — " +
        "HttpTrigger, KafkaTrigger, CronTrigger, StreamTrigger or AgentTrigger, one of " +
        "which normally matches the transport's kind even when the plugin ships its own — " +
        "or accept the gap and downgrade this rule in .editorconfig, knowing the manifest " +
        "no longer describes how this flow is reached.",
        DiagnosticSeverity.Warning);

    /// <summary>FLOWX1027 — a step declared after a <c>.Fail(...)</c>, which ends the flow.</summary>
    /// <remarks>
    /// <para>
    /// A <strong>warning</strong>, and the model is C#'s own <c>CS0162</c>: the source is
    /// not wrong, part of it simply cannot run. <c>.Fail(error)</c> is terminal — the flow
    /// ends there with a business error and unwinds what it completed — so a step after one
    /// in the same block is unreachable by construction rather than by circumstance.
    /// </para>
    /// <para>
    /// <strong>The unreachable steps are not compiled.</strong> Laying them out would put
    /// them in the plan, in <c>flowx.manifest.json</c> and in a rendered diagram, where a
    /// reviewer or an agent reading the published contract would believe the flow does
    /// work it can never do. Dropping them silently would be worse still, which is what
    /// this diagnostic is for.
    /// </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor StepIsUnreachableAfterFail = Create(
        "FLOWX1027",
        "Step is unreachable after Fail",
        "Flow '{0}' declares '.{1}(...)' after a '.Fail(...)', which ends the flow, so it " +
        "can never run and is not compiled",
        "'.Fail(error)' terminates the flow with a business error: the engine takes the " +
        "failure path, the completed compensable steps unwind in strict reverse, and " +
        "control never reaches the next step in the block. Steps after one are therefore " +
        "dropped rather than published — a manifest listing work the flow cannot do is a " +
        "contract that lies. Move them before the '.Fail(...)', or into the branch that " +
        "does not fail.",
        DiagnosticSeverity.Warning);

    /// <summary>FLOWX1028 — a step's input mapping produces a type its capability cannot accept.</summary>
    /// <remarks>
    /// <para>
    /// <c>.Step&lt;TCapability, TStepIn&gt;(map)</c> infers <c>TStepIn</c> from the lambda
    /// and constrains it to nothing: <c>.Step&lt;CapturePayment, string&gt;(ctx =&gt;
    /// "x")</c> is legal C# at the call site even though <c>CapturePayment</c> consumes a
    /// <c>Reservation</c>. The generated dispatcher passes the mapping's result straight to
    /// the capability, so without this rule the mistake surfaces as a <c>CS1503</c> inside
    /// generated source — the same shape of failure <c>ctx.Input</c> had, and the reason
    /// generated code is now compiled by the test harness rather than merely parsed.
    /// </para>
    /// <para>
    /// An <strong>error</strong>, because no degenerate form is honest. Ignoring the mapping
    /// is what this overload did while it was unimplemented, and it is the fix FLOWX1020
    /// recommends; falling back to binding from the state bag would make that remedy
    /// silently do something else again.
    /// </para>
    /// <para>
    /// Assignability rather than exact identity, unlike FLOWX1020. That rule asks what a
    /// <c>Dictionary&lt;Type, object&gt;</c> lookup finds, and a lookup is exact; this one
    /// asks what a C# argument accepts, and an argument takes anything implicitly
    /// convertible to it.
    /// </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor StepInputMappingHasWrongType = Create(
        "FLOWX1028",
        "Step input mapping produces the wrong contract",
        "Step '{0}' in flow '{1}' maps its input to '{2}', which the capability cannot " +
        "accept — it consumes '{3}'",
        "'.Step<TCapability, TStepIn>(map)' hands the mapping's result straight to the " +
        "capability, so TStepIn must be the capability's declared input contract or " +
        "something implicitly convertible to it. C# infers TStepIn from the lambda and " +
        "constrains it to nothing, which is why this is checked here rather than by the " +
        "language. Build the contract the capability declares, or name that contract " +
        "explicitly as the second type argument so the C# compiler reports the mismatch " +
        "on the lambda body itself.",
        DiagnosticSeverity.Error);

    /// <summary>Every descriptor, for the fitness function and for documentation generation.</summary>
    public static ImmutableArray<DiagnosticDescriptor> All { get; } = ImmutableArray.Create(
        FlowMustBePartial,
        StepIsNotACapability,
        CapabilityReferencesTransport,
        CapabilityInvokesCapability,
        FlowInheritsFlow,
        CapabilityMissingAuthorization,
        PredicateMustBePure,
        ParallelBranchesMustWriteDisjointSlots,
        RetryRequiresIdempotency,
        CapabilityHasMultipleContracts,
        ExpectedFailureIsThrown,
        AwaitSignalRequiresDurable,
        CacheRequiresNoSideEffects,
        DeadlineCannotFitSteps,
        StepInputIsNeverProduced,
        SubFlowCycle,
        SubFlowCannotBeComposed,
        FlowHasNoSteps,
        EmitIsNotYetPublished,
        TriggerCannotBeRead,
        StepIsUnreachableAfterFail,
        StepInputMappingHasWrongType);

    private static DiagnosticDescriptor Create(
        string id,
        string title,
        string messageFormat,
        string description,
        DiagnosticSeverity severity = DiagnosticSeverity.Error)
    {
        return new DiagnosticDescriptor(
            id,
            title,
            messageFormat,
            Category,
            severity,
            isEnabledByDefault: true,
            description: description,
            helpLinkUri: HelpRoot + id + ".md");
    }
}
