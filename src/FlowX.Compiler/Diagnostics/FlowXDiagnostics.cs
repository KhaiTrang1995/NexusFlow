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

    /// <summary>FLOWX1007 — time is read from the ambient clock rather than the context.</summary>
    /// <remarks>
    /// <para>
    /// Rule 7 of <c>07-Capability-Model.md §3</c> and the first row of <c>06 §5</c>'s
    /// determinism table, both of which named this id for a year while
    /// <c>FlowXDiagnostics</c> contained no descriptor for it. The engine journals
    /// <c>ctx.UtcNow</c> on first read in a step and reproduces it on replay
    /// (<c>NondeterminismCapture.UtcNow</c>); <c>DateTime.UtcNow</c> is read again at
    /// replay time and answers differently, so the step that recorded one instant replays
    /// against another.
    /// </para>
    /// <para>
    /// <strong>Warning by default, error on a durable replay path</strong> — the severity
    /// the whole determinism set takes, and the reasoning is on
    /// <c>docs/diagnostics/README.md</c> rather than repeated on each of the three. In
    /// short: Info is what ADR-0003 asked for and is invisible in a build log, so it would
    /// ship a rule that does nothing anywhere; a Warning still stops <em>this</em> build
    /// under <c>TreatWarningsAsErrors</c> while staying downgradable in a consumer's
    /// <c>.editorconfig</c>; and the escalation follows FLOWX1011's and FLOWX1025's
    /// precedent of choosing severity by what the compilation can prove about who is
    /// affected.
    /// </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor ClockIsReadAmbiently = Create(
        "FLOWX1007",
        "Time is read from the ambient clock rather than the context",
        "'{0}' reads '{1}', which is the ambient clock; a replay reproduces only what the " +
        "journal captured, and what it captures is ctx.UtcNow",
        "A durable flow is replayed, and the only clock reading a replay can reproduce is " +
        "the one the journal recorded: the engine captures ctx.UtcNow the first time a step " +
        "reads it and hands the same instant back on the way through again. DateTime.UtcNow " +
        "is read afresh every time, so the branch, the step input or the stored value that " +
        "depended on it differs between the run and its replay. Read the clock through the " +
        "context — ctx.UtcNow in a capability, ctx.UtcNow in a flow delegate — which is also " +
        "what lets a test pin the time instead of waiting for midnight. If what you need is " +
        "a duration rather than an instant, measure it inside the capability and keep it in " +
        "telemetry rather than in a value the flow carries.",
        DiagnosticSeverity.Warning);

    /// <summary>FLOWX1008 — an identifier or a random value is taken outside the context.</summary>
    /// <remarks>
    /// <para>
    /// The second row of <c>06 §5</c>'s table, and the other half of rule 7 in
    /// <c>07-Capability-Model.md §3</c>. Separate from FLOWX1007 because the two have
    /// different consequences and different fixes: a clock that moves changes a decision,
    /// while an identifier that moves duplicates an <em>effect</em> — the same capture
    /// retried or replayed under a new id is a second charge, not a second reading.
    /// <c>06 §5</c> is the document that splits them; <c>07 §3</c> and
    /// <c>ICapability</c>'s remarks state them as one rule with two ids.
    /// </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor IdentityIsTakenAmbiently = Create(
        "FLOWX1008",
        "Identity or randomness is taken outside the context",
        "'{0}' reads '{1}', which mints an identifier or a random value outside the " +
        "context; the journal captures ctx.NewId() and ctx.Random's seed, and reproduces those",
        "Guid.NewGuid() and Random.Shared answer differently on every call — including the " +
        "retry of a step, which the engine performs whenever a Retry policy is attached, and " +
        "the replay of a durable instance. An identifier minted this way is therefore not " +
        "the one the journal recorded, and a downstream system deduplicating on it sees two " +
        "operations where the flow performed one. Use ctx.NewId() and ctx.Random, whose " +
        "values and seed the journal captures, or ctx.IdempotencyKey, which is deliberately " +
        "stable across both retries and replays and is what a remote system should be given.",
        DiagnosticSeverity.Warning);

    /// <summary>FLOWX1009 — a capability or a flow declares state that can change after construction.</summary>
    /// <remarks>
    /// <para>
    /// Rule 6 of <c>07-Capability-Model.md §3</c> — "stateless: no mutable instance or
    /// static fields" — whose <em>Enforced by</em> cell said "— , FLOWX1009 does not exist"
    /// until this descriptor did. <c>06 §5</c> words the same row as "no mutable static
    /// state reachable from a flow", which is the wider claim; this rule proves the
    /// narrower one it can prove, at the declaration, and its page says which part of the
    /// wider claim is left uncovered.
    /// </para>
    /// <para>
    /// <strong>Its harm is not only a replay concern, and the severity says so on its
    /// page.</strong> A capability is registered once and invoked concurrently by every
    /// flow that names it, so a mutable field is a race under <em>either</em> profile —
    /// which is why the ephemeral severity is a warning rather than the Info ADR-0003
    /// specified, and why the ephemeral warning is not a statement that the state is
    /// acceptable there.
    /// </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor MutableStateIsHeld = Create(
        "FLOWX1009",
        "Capability or flow holds mutable state",
        "'{0}' holds mutable state: the {1} '{2}' can be assigned after construction",
        "A capability is resolved once and invoked concurrently by every flow that names it, " +
        "so a field or property something can assign later is shared across in-flight " +
        "invocations: the value one reads is whatever another wrote, which is right in a test " +
        "and wrong under load. On a durable flow it is worse than a race — the journal " +
        "records the step's inputs and result and knows nothing about the field, so a replay " +
        "runs against whatever the process happens to hold rather than against what was " +
        "recorded. Keep per-invocation state in the input contract, the result, or the flow's " +
        "context; mark anything genuinely fixed 'readonly' or 'const'; and put anything that " +
        "must outlive one invocation behind an injected dependency, where its lifetime is a " +
        "decision somebody made rather than an accident of where the field was declared.",
        DiagnosticSeverity.Warning);

    /// <summary>FLOWX1010 — a capability does not declare an authorisation stance.</summary>
    public static readonly DiagnosticDescriptor CapabilityMissingAuthorization = Create(
        "FLOWX1010",
        "Capability does not declare an authorisation stance",
        "Capability '{0}' does not declare Authorization",
        "There is no permissive default. Declare Authorization explicitly — including " +
        "Authorization.Public, which is a reviewable statement rather than an omission.");

    /// <summary>FLOWX1030 — a Permission or Policy stance names no permission or policy.</summary>
    /// <remarks>
    /// <para>
    /// <c>FLOWX1010</c>'s rule one level down. That rule refuses a capability with no
    /// stance because an omission is not a decision; this one refuses a stance that
    /// decides nothing checkable. <c>Authorization.Permission</c> with no
    /// <c>Permission = "…"</c> compiles, reads as enforced, and reaches
    /// <c>flowx.manifest.json</c> as <c>{"mode": "Permission"}</c> — a published claim
    /// that some grant is required, naming none. A reviewer sees the capability
    /// protected; an agent's tool descriptor says the same; nothing can act on either.
    /// </para>
    /// <para>
    /// <strong>An error, on FLOWX1010's argument rather than a new one.</strong> The
    /// remedy is one string the author owns and nobody else can supply — the whole reason
    /// FLOWX1010's quick action offers <c>Authenticated</c> and <c>Internal</c> and
    /// withholds these two. A warning would be a rule nobody has to obey guarding the
    /// thing this catalogue treats as least negotiable, and
    /// <c>SafetyDiagnosticsAreErrorsRatherThanWarnings</c> holds the line for the rest of
    /// the security set.
    /// </para>
    /// <para>
    /// <c>{1}</c> is the mode, and it names the property to add: <c>Permission</c> wants
    /// <c>Permission = "…"</c> and <c>Policy</c> wants <c>Policy = "…"</c>. A message
    /// hard-coding one would send half the readers to the wrong property.
    /// </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor AuthorizationStanceNamesNothing = Create(
        "FLOWX1030",
        "Authorisation stance names no permission or policy",
        "Capability '{0}' declares Authorization.{1} but no {1} name",
        "Authorization.Permission and Authorization.Policy each claim that a named grant " +
        "is required, so each needs the name: add Permission = \"…\" or Policy = \"…\" to " +
        "the [Capability] attribute. Without it the manifest publishes an authorisation " +
        "stance that nothing can be checked against, and `flowx diff` has no value to " +
        "compare when the grant later moves. If no named grant is actually required, the " +
        "honest stance is Authenticated or Internal — both are complete in themselves.");

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

    /// <summary>FLOWX1012 — compensation declared on a flow whose profile is not <c>Durable</c>.</summary>
    /// <remarks>
    /// <para>
    /// Specified alongside <see cref="AwaitSignalRequiresDurable"/> in ADR-0003's negative
    /// bullet — "a wrong profile is a real bug class" — and the half of that pair that was
    /// deliberately not written for two phases. The check was never the obstacle. The
    /// <em>remedy</em> was: <c>Profile = Durable</c> changed nothing while every profile ran
    /// in memory, and once WP-52 made the runtime read the profile it changed something
    /// worse than nothing — a durable flow with no journal is refused outright. Both halves
    /// expired. WP-53 and WP-55 give a host a journal and a lease store to register, so the
    /// recommendation this descriptor makes is one a reader can actually carry out.
    /// </para>
    /// <para>
    /// <strong>What the profile actually buys, stated exactly, because overstating it is how
    /// this rule would become the next thing nobody believes.</strong> A durable flow commits
    /// a row per step boundary; an instance whose node dies is found by the recovery scan and
    /// re-entered on the same step loop; the loop replays the committed rows, and a completed
    /// step that declared a compensation goes back onto the unwind stack as it is skipped. So
    /// a compensation pending across a crash survives, which is the whole claim. Two things
    /// are still true under <c>Durable</c> and are named in the description rather than
    /// discovered later: the unwind itself is not journaled, so a crash <em>during</em>
    /// compensation still loses it (<c>06 §7</c> rule 4), and a resumed parent does not
    /// rebuild a skipped sub-flow's compensations (WP-57).
    /// </para>
    /// <para>
    /// <strong>A warning, and not by inheritance from the determinism set.</strong> That set
    /// escalates to an error where the compilation can prove the code is on a durable flow's
    /// replay path; this rule reports precisely because the flow is <em>not</em> durable, so
    /// the escalation condition and the trigger are mutually exclusive and there is nothing
    /// to inherit. An error is wrong on its own terms as well: the source is not incorrect —
    /// a compensable ephemeral flow unwinds correctly on every failure that is not a crash,
    /// which is the trade ADR-0003 ratified and <c>docs/DEBT.md</c> cites as a decision
    /// rather than debt — and the remedy depends on a host registration no analyzer can see.
    /// Info is what ADR-0003 already got wrong twice: it never reaches a build log, and this
    /// rule fires only on flows that declined durability, which is nearly all of them.
    /// </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor CompensationIsNotDurable = Create(
        "FLOWX1012",
        "Compensation is declared on a flow that is not durable",
        "Flow '{0}' declares compensation ({1}) but its profile is {2}, so an instance that " +
        "dies between the compensable step and the end of the flow takes the pending " +
        "compensation with it",
        "An ephemeral instance lives entirely in the memory of the process that started it, " +
        "and so does its compensation stack. A crash, a deploy or a scale-in after a " +
        "compensable step has completed leaves that step's effect standing with nothing left " +
        "to undo it and no record that it happened — a reservation, a hold or an " +
        "authorisation that no operator has a way to find. Set " +
        "Profile = ExecutionProfile.Durable, which journals each step boundary and rebuilds " +
        "the unwind stack when a recovered instance replays them, and register a journal and " +
        "a lease store on the host: a durable flow started without them is refused with " +
        "flow.durability_not_configured rather than run ephemerally. That costs a store round " +
        "trip per step, so it is a decision and not a formality — if the effect is cheap to " +
        "leak, or something already sweeps it, keep the profile and record the choice. Two " +
        "limits remain under Durable and are not fixed by this change: the unwind is not " +
        "itself journaled, so a crash during compensation still loses it, and a resumed " +
        "parent does not rebuild a skipped sub-flow's compensations.",
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
    /// <para>
    /// A warning, not an error, and the only one in the set. The step is real: it is in
    /// the compiled plan and in the manifest, and a consumer reading the manifest will
    /// believe the event is published. It is not, and the gap between what the manifest
    /// promises and what the process does is exactly the kind of thing that is discovered
    /// in production. Saying so at build time is the cheapest place to find out.
    /// </para>
    /// <para>
    /// <strong>Revisited at WP-56, and kept.</strong> This used to say "until the outbox
    /// exists (design principle P8)", and that is no longer the gap: the table stages
    /// events atomically with the step that emitted them (WP-53) and
    /// <c>PostgresOutboxPublisher</c> drains them to an <c>IEventPublisher</c>
    /// at-least-once (WP-56). What is still missing is one link earlier —
    /// <c>FlowEngine.CommitStepAsync</c> never sets <c>StepCommit.Outbox</c>, and
    /// <c>IStepDispatcher.DescribeStep</c> returns no event for a generated dispatcher to
    /// have serialised — so an <c>Emit</c> step stages nothing and a publisher has nothing
    /// to publish. Narrower reason, same warning; the severity stance is argued on
    /// <c>docs/diagnostics/FLOWX1024.md</c>.
    /// </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor EmitIsNotYetPublished = Create(
        "FLOWX1024",
        "Emit step is recorded but not published",
        "Flow step '.Emit<{0}>' is compiled into the plan but no event is published yet",
        "The transactional outbox and its publisher both exist, and nothing connects an " +
        "Emit step to them: the engine stages no outbox row for one, so no event is " +
        "written and none is published. The step appears in the plan and the manifest, so " +
        "downstream consumers will expect the event — suppress this warning only once you " +
        "have confirmed nothing depends on it being delivered.",
        DiagnosticSeverity.Warning);

    /// <summary>FLOWX1025 — a trigger attribute that declares no <c>[TriggerKind]</c>.</summary>
    /// <remarks>
    /// <para>
    /// <strong>The rule changed shape when the fix arrived.</strong> It used to say a
    /// trigger attribute FlowX did not ship "cannot be read", and its advice was to declare
    /// a built-in attribute instead — advice the author of a transport plugin cannot act
    /// on, since it amounts to not shipping the attribute. <c>[TriggerKind(...)]</c> makes
    /// the kind attribute <em>data</em>, readable from a compiled reference, so the rule now
    /// reports a missing declaration with a one-line fix rather than a structural
    /// impossibility.
    /// </para>
    /// <para>
    /// <strong>Warning by default, error where the fix is in reach.</strong> The default is
    /// a warning for the reason FLOWX1024 is: the source is not wrong, the artifact is
    /// incomplete — the build succeeds, and <c>flowx.manifest.json</c> has no entry for the
    /// trigger, which <c>flowx diff</c> cannot tell apart from "this flow has no trigger",
    /// so the gate that classifies a removed trigger as breaking silently loses its input.
    /// Making that an error for everyone would break the build of a team whose only mistake
    /// was referencing a plugin that has not added the marker yet, and
    /// <c>17-Plugin-System.md §1</c> commits to the opposite of a platform where using a
    /// third-party transport fails your build.
    /// </para>
    /// <para>
    /// But when the attribute is declared in the compilation being built, the person seeing
    /// the diagnostic owns the file that fixes it, and a rule nobody has to obey is not a
    /// rule. <c>TriggerDeclarationAnalyzer</c> therefore raises it as an <strong>error</strong>
    /// in that case, the same escalation FLOWX1011 makes for a <c>Durable</c> flow. The
    /// descriptor's default stays <c>Warning</c> because that is what a consumer configures
    /// against in <c>.editorconfig</c>, and what the release-tracking table records.
    /// </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor TriggerDeclaresNoKind = Create(
        "FLOWX1025",
        "Trigger attribute declares no [TriggerKind]",
        "Trigger '{0}' on flow '{1}' declares no [TriggerKind], so this flow publishes no " +
        "trigger in the manifest",
        "A trigger's Kind property is an abstract property each attribute overrides — " +
        "executable code, not attribute data — so the compiler cannot read it. The kind " +
        "must therefore also be declared as data, with [TriggerKind(TriggerKind.Bus)] on " +
        "the attribute class, which the compiler can read out of a referenced assembly " +
        "without running it. Without the marker the flow's triggers are absent from the " +
        "manifest, and 'flowx diff' reads that absence as 'this flow has no trigger' " +
        "rather than as 'the compiler could not tell', so removing the trigger later is " +
        "not reported as breaking. Add [TriggerKind(...)] to the trigger attribute, " +
        "matching the value its Kind property returns. If the attribute belongs to a " +
        "package you do not own, ask its author to add the marker, and until then either " +
        "declare a built-in trigger attribute as well or downgrade this rule in " +
        ".editorconfig, knowing the manifest does not describe how this flow is reached.",
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

    /// <summary>FLOWX1029 — a step's input mapping produces a type its capability cannot accept.</summary>
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
        "FLOWX1029",
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
    /// <summary>FLOWX1028 — a declared execution profile the runtime does not implement.</summary>
    /// <remarks>
    /// <para>
    /// <strong>A scaffold for a missing phase, not a rule about the source.</strong>
    /// <c>Streaming</c> has no engine at all: a flow that declares it executes on the
    /// ephemeral path, with no checkpointed offsets, no windowing, no watermarks and no
    /// backpressure. The profile reaches an <c>ExecutionPlan</c> validation and the
    /// <c>profile</c> field of <c>flowx.manifest.json</c>, and stops there — so the
    /// declaration produces a fact in a published contract and no behaviour, and without this
    /// rule nothing would say so.
    /// </para>
    /// <para>
    /// <strong>This rule was narrowed rather than deleted, and the distinction matters.</strong>
    /// It covered <c>Durable</c> until WP-52, when the engine began journaling a durable
    /// flow's step boundaries and refusing to run one that has no journal. Removing the whole
    /// rule on that day would have handed <c>Streaming</c> exactly the silence <c>Durable</c>
    /// had just been rescued from, and P7 would have had to write it again to say the same
    /// thing.
    /// </para>
    /// <para>
    /// <strong>A warning, and the alternative is worse than lax — it is harmful.</strong>
    /// The only edit that would silence an error is a different profile, which deletes the
    /// author's design decision to buy back a build. ADR-0003 calls the profile the single
    /// most consequential decision a flow author makes, and lists its greppability —
    /// <c>Profile = Streaming</c> visible in the code, the manifest and the diagram — as a
    /// positive consequence of the design. An error would systematically erase exactly that
    /// record, and P7 would arrive to find no flow declaring the profile it needs.
    /// </para>
    /// <para>
    /// Info was the other candidate and is the option ADR-0003 already rejected once, for
    /// <c>FLOWX1011</c>: an Info diagnostic never appears in a build log, so the rule
    /// would ship doing nothing — the precise failure this package exists to correct.
    /// This repository builds with <c>TreatWarningsAsErrors</c>, so it stops the build
    /// <em>here</em>; a consumer who has read the page and accepted the gap downgrades it
    /// in <c>.editorconfig</c>, which records the decision in the repository that took it.
    /// </para>
    /// <para>
    /// <strong>Delete this descriptor when P7 lands the stream engine.</strong> A rule that
    /// outlives the gap it describes is noise, and noise is what teaches people to suppress
    /// the catalogue. The <c>Durable</c> half was taken down by ADR-0015's take-down list on
    /// the day it stopped being true; the deletion table on
    /// <c>docs/diagnostics/FLOWX1028.md</c> carries the remaining row.
    /// </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor ProfileIsNotHonouredByTheRuntime = Create(
        "FLOWX1028",
        "Execution profile is declared but not honoured by the runtime",
        "Flow '{0}' declares Profile = ExecutionProfile.{1}, which the runtime does not " +
        "implement: this flow executes on the ephemeral engine",
        "Streaming has no engine at all, so a flow declared Streaming gets the ephemeral one " +
        "with a different word in the manifest — no checkpointed offsets, no windowing, no " +
        "watermarks, no backpressure. Keep the declaration: it is the design decision " +
        "ADR-0003 asks you to make, it is what P7 will honour, and changing it to Ephemeral " +
        "to silence this warning would delete the record of what this flow needs while " +
        "changing nothing about how it runs. Instead, confirm that running this flow on the " +
        "ephemeral engine is survivable until the stream engine ships, and if it is, " +
        "downgrade this rule in .editorconfig with a FLOWX-DEBT marker. If it is not, this " +
        "flow cannot ship on this release. Durable no longer reports here: WP-52 made the " +
        "runtime journal a durable flow's step boundaries, so the declaration is honoured. " +
        "This rule is deleted, not fixed: it goes away when the runtime implements the " +
        "remaining profile.",
        DiagnosticSeverity.Warning);

    /// <summary>Every descriptor, for the fitness function and for documentation generation.</summary>
    public static ImmutableArray<DiagnosticDescriptor> All { get; } = ImmutableArray.Create(
        FlowMustBePartial,
        StepIsNotACapability,
        CapabilityReferencesTransport,
        CapabilityInvokesCapability,
        FlowInheritsFlow,
        ClockIsReadAmbiently,
        IdentityIsTakenAmbiently,
        MutableStateIsHeld,
        CapabilityMissingAuthorization,
        AuthorizationStanceNamesNothing,
        PredicateMustBePure,
        CompensationIsNotDurable,
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
        TriggerDeclaresNoKind,
        StepIsUnreachableAfterFail,
        StepInputMappingHasWrongType,
        ProfileIsNotHonouredByTheRuntime);

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
