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

    /// <summary>FLOWX1037 — a declared stance is one the runtime cannot decide.</summary>
    /// <remarks>
    /// <para>
    /// The third rule of the authorisation chain, and it presupposes the first two passed.
    /// <c>FLOWX1010</c> asks whether a stance was declared; <c>FLOWX1030</c> asks whether a
    /// stance that needs a name has one; this asks whether the stance that was declared and
    /// named is one the engine can reach a decision for. Four of the five are —
    /// <c>StepAuthorization.Decide</c> settles <c>Public</c>, <c>Authenticated</c>,
    /// <c>Permission</c> and <c>Internal</c> against the invocation's <c>ClaimsPrincipal</c>.
    /// </para>
    /// <para>
    /// <c>Authorization.Policy</c> is not. It names an ASP.NET Core authorisation policy,
    /// which only <c>IAuthorizationService</c> can evaluate, and <c>FlowX.Runtime</c> may not
    /// reference ASP.NET Core — <c>RuntimeIsolationTests</c> is the gate, and it exists so a
    /// flow behaves identically whichever transport activated it (ADR-0004).
    /// </para>
    /// <para>
    /// <strong>An error, and not <c>FLOWX1032</c>'s warning.</strong> That rule's argument is
    /// that an error would delete the inventory the fixing phase needs, and that a rate limit
    /// enforced at the gateway is a correct program. Neither transfers: the declaration is a
    /// choice among five of which four work, and an authorisation stance that checks nothing
    /// is the control failing open. ADR-0030 carries it in full.
    /// </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor AuthorizationStanceNotEnforceable = Create(
        "FLOWX1037",
        "Authorisation stance is not enforced by the runtime",
        "Capability '{0}' declares Authorization.{1}, which the runtime cannot enforce",
        "Authorization.Policy names an ASP.NET Core authorisation policy, and only " +
        "IAuthorizationService can evaluate one — which FlowX.Runtime may not reference, so " +
        "the stance reaches the manifest and `flowx diff` and is then checked by nothing. If " +
        "the policy is a single claim requirement, which most are, declare " +
        "Authorization.Permission with that claim's value and the runtime enforces it. If it " +
        "genuinely needs a handler, keep the policy on the transport endpoint and declare the " +
        "stance the capability is left with — accepting that the rule then holds over HTTP " +
        "only, and not for a bus or agent invocation of the same flow.");

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
    /// <remarks>
    /// <para>
    /// <strong>Two policies, one rule, and <c>{1}</c> is which of them.</strong> <c>Retry</c>
    /// wraps the step and is judged by the step's declaration; <c>CompensationRetry</c> wraps
    /// the step's <em>undo</em> and is judged by the compensating capability's. One
    /// <c>.WithPolicy(...)</c> may carry both, against two capabilities with two different
    /// answers — a <c>payment.capture</c> that is not idempotent and a reversal that is — so
    /// the message has to name which policy and which capability it means. Naming only the
    /// capability would send the reader of a compensation report to the wrong declaration.
    /// </para>
    /// <para>
    /// One id rather than two, because it is one rule reaching the case it always covered:
    /// the argument the page makes is "what would run twice has to be safe to run twice", and
    /// a compensation retry re-dispatches the compensation. A second id would let a team
    /// suppress half a safety rule while believing they had suppressed a different one.
    /// </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor RetryRequiresIdempotency = Create(
        "FLOWX1014",
        "Retry requires an idempotent capability",
        "Capability '{0}' declares Idempotent = false, so a {1} policy cannot be attached",
        "Retrying a non-idempotent operation duplicates its effect; for a payment capture " +
        "that is a duplicate charge, and for a reversal run twice it is a second reversal. " +
        "The capability judged is the one the policy would re-dispatch: the step for Retry, " +
        "the compensation for CompensationRetry. Make that capability idempotent and declare " +
        "it, or handle the failure in the flow.");

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

    /// <summary>FLOWX1017 — a wait in a non-durable flow.</summary>
    /// <remarks>
    /// <para>
    /// <strong>Both kinds of wait, since WP-63's timer half.</strong> The rule was written for
    /// <c>AwaitSignal</c> because that was the only wait a plan could carry; <c>.Delay</c>
    /// compiled to nothing at all, so there was nothing to report about it. Now that it is a
    /// step, it needs the same profile for the same reason — there is nowhere outside a journal
    /// to record when a timer is due, so the only way to honour one in memory is to hold the
    /// process for the duration, which is a <c>Task.Delay</c> wearing a plan node.
    /// </para>
    /// <para>
    /// The title still names <c>AwaitSignal</c> alone, and the message names whichever
    /// construct the flow declared. That is deliberate: the id and the title are what an
    /// <c>.editorconfig</c> line and a build log carry, and renaming a shipped rule's title to
    /// cover a second construct would break every search anybody has saved for the first.
    /// </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor AwaitSignalRequiresDurable = Create(
        "FLOWX1017",
        "AwaitSignal requires the Durable profile",
        "Flow '{0}' uses {2} but runs under the {1} profile",
        "An in-memory wait does not survive a deployment, a crash or a scale-in, and a timer " +
        "outside a journal has nowhere to record when it is due — so the only way to honour " +
        "one in memory is to hold the process for the duration. Set " +
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

    /// <summary>FLOWX1024 — an <c>.Emit&lt;T&gt;()</c> step this build cannot stage.</summary>
    /// <remarks>
    /// <para>
    /// A warning, not an error, and the only one in the set. The step is real: it is in
    /// the compiled plan and in the manifest, and a consumer reading the manifest will
    /// believe the event is published. Where one of the two conditions below holds it is
    /// not, and the gap between what the manifest promises and what the process does is
    /// exactly the kind of thing that is discovered in production. Saying so at build time
    /// is the cheapest place to find out.
    /// </para>
    /// <para>
    /// <strong>Re-scoped once the chain was connected.</strong> It used to report the whole
    /// pipeline: nothing anywhere turned an <c>Emit</c> step into an event. That is no longer
    /// true — <c>FlowEngine.CommitStepAsync</c> stages what a generated <c>DescribeStep</c>
    /// describes, in the same transaction as the step row, and <c>PostgresOutboxPublisher</c>
    /// drains it at-least-once. What survives is two narrow cases where the chain still
    /// cannot start, both named in <see cref="EmitReasons"/> and both with a fix in user
    /// code: an <c>Ephemeral</c> flow has no transaction to stage into, and a contract
    /// outside every source-generated <c>JsonSerializerContext</c> has no body that can be
    /// written without reflection.
    /// </para>
    /// <para>
    /// <strong>The severity is unchanged and that is still a decision.</strong> The source is
    /// not wrong; the deployment is incomplete. An error would fail builds of code that is
    /// one attribute or one profile away from correct, and <c>Info</c> is where
    /// <c>FLOWX1007</c>–<c>1009</c> sat unraised for two phases.
    /// </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor EmitIsNotYetPublished = Create(
        "FLOWX1024",
        "Emit step stages no event to publish",
        "Flow step '.Emit<{0}>' is compiled into the plan and stages no event: {1}",
        "The transactional outbox, its publisher and the engine's staging all exist, so an " +
        "Emit step normally reaches a broker. This one does not, for the reason the message " +
        "names. The step still appears in the plan and the manifest, so downstream consumers " +
        "will expect the event — suppress this warning only once you have confirmed nothing " +
        "depends on it being delivered.",
        DiagnosticSeverity.Warning);

    /// <summary>FLOWX1006 — a state-bag contract no generated JSON context declares.</summary>
    /// <remarks>
    /// <para>
    /// <strong>Reserved since P0, and unraisable until WP-59.</strong> The rule checks
    /// membership of the source-generated <c>System.Text.Json</c> context ADR-0008 chose, and
    /// until a generated payload writer needed a <c>JsonTypeInfo&lt;T&gt;</c> for a state-bag
    /// contract there was no membership to check — the shipped dispatchers described no
    /// payloads, so the requirement was one nothing had.
    /// </para>
    /// <para>
    /// <strong>An error, uniformly, and that is the determinism set's own rule applied rather
    /// than an exception to it.</strong> That set is Warning by default and Error where the
    /// compilation can prove the code is on a durable flow's replay path. This rule reports
    /// only on a <c>Durable</c> flow, so its trigger <em>is</em> the proof and no case is left
    /// to warn about. <c>FLOWX1012</c> reaches the opposite conclusion from the same rule for
    /// the mirror-image reason: it fires because a flow is <em>not</em> durable.
    /// </para>
    /// <para>
    /// What the message names is the contract, because the fix is one
    /// <c>[JsonSerializable]</c> attribute and the only hard part of writing it is knowing
    /// which type goes inside the parentheses.
    /// </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor StateIsNotSerialisable = Create(
        "FLOWX1006",
        "State-bag contract is outside every generated JSON context",
        "'{0}' is written into the state bag of durable flow '{1}', and {2}",
        "A Durable flow journals its state bag and every step result, and a value reaches " +
        "the journal only through JournalPayload, whose Of<T> requires the source-generated " +
        "JsonTypeInfo<T> — there is no overload that reflects over a type, which is what " +
        "keeps the write path NativeAOT- and trim-safe (constraint C2). A contract no " +
        "JsonSerializerContext in this compilation declares is one the generated payload " +
        "writer cannot name metadata for, so the journal would record nothing for it and a " +
        "resumed instance would run the rest of the flow against values no step produced.");

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

    /// <summary>FLOWX1032 — a declared policy the runtime applies to nothing.</summary>
    /// <remarks>
    /// <para>
    /// <strong>Four of the nine kinds <c>PolicySet</c> offers are executed by no code:</strong>
    /// <c>RateLimit</c>, <c>Idempotency</c>, <c>Cache</c> and <c>Audit</c>. The rule was
    /// written over eight of the nine and narrowed when the policy engine landed
    /// <c>PolicyStage.Resilience</c> — <c>Timeout</c>, <c>Retry</c>, <c>CircuitBreaker</c> and
    /// <c>Bulkhead</c> are now applied around every step that declares them, and
    /// <c>CompensationRetry</c> has been applied to the unwind since WP-57.
    /// </para>
    /// <para>
    /// <strong>The cut is by stage now, and it was not before.</strong> The four that remain
    /// are stage 1, stage 3, stage 5 and half of stage 7 — but the half of stage 7 is the
    /// reason the rule cannot be written as a stage range. <c>Audit</c> shares
    /// <c>PolicyStage.Consistency</c> with <c>CompensationRetry</c>, which executes, so
    /// "stages 1, 3, 5 and 7 do not run" would be silent on nothing and wrong about the undo.
    /// The list is a list, pinned against the runtime by
    /// <c>PolicyStageFitnessTests</c>.
    /// </para>
    /// <para>
    /// <strong>A warning, on <see cref="ProfileIsNotHonouredByTheRuntime"/>'s argument one
    /// level down.</strong> Both of that rule's halves transfer, and unlike the deleted rule
    /// over the suspension constructs, this one may use the second as well as the first. An error's only repair is deleting the <c>.WithPolicy(...)</c> call, which
    /// erases the inventory P4 needs to find; and the source is not wrong — a great many
    /// flows are correct with a <c>RateLimit</c> enforced by the gateway in front of the
    /// process or a <c>Timeout</c> subsumed by a shorter <c>[FlowDeadline]</c>, so "confirm
    /// the flow is correct as it is, and record that" is a real remedy here where it was
    /// not for a seven-day wait compiled to no wait. Nothing is falsified either: the plan
    /// carries exactly the declared set, in exactly ADR-0011's stage order.
    /// </para>
    /// <para>
    /// Info was the other candidate and is rejected for the reason the rest of this
    /// catalogue rejects it: it never reaches a build log, so the rule would ship doing
    /// nothing — which is the state it exists to end.
    /// </para>
    /// <para>
    /// <strong>Deleted, not fixed, when P4 lands the policy engine</strong> — or narrowed to
    /// the kinds that still do not execute, exactly as WP-52 narrowed
    /// <see cref="ProfileIsNotHonouredByTheRuntime"/> rather than deleting it. The deletion
    /// table is on <c>docs/diagnostics/FLOWX1032.md</c>.
    /// </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor PolicyIsNotExecutedByTheRuntime = Create(
        "FLOWX1032",
        "Declared policy is not executed by the runtime",
        "'{0}' declares policies this release does not execute: {1}. The compiled plan and " +
        "flowx.manifest.json carry them; no code applies them.",
        "Two of the nine kinds PolicySet offers are executed by nothing: Cache and Audit. So a " +
        "declared Cache is never consulted and a declared Audit writes no record. The other " +
        "seven do run — Timeout, Retry, CircuitBreaker and Bulkhead are applied around the " +
        "step, RateLimit admits or refuses the caller before it, an Idempotency window records " +
        "and replays the step's result, and CompensationRetry wraps the undo — so this rule " +
        "names only what is left. Keep the declaration: it is the published statement of what " +
        "this step needs, it reaches flowx.manifest.json where a reviewer and a 'flowx diff' " +
        "can read it, it is what the stage that implements it will execute, and deleting it to " +
        "silence this warning would remove the record while changing nothing about how the " +
        "step runs. Instead, confirm the step is survivable with the policy unenforced — a " +
        "cache the capability can hold itself, an audit record the capability writes — and " +
        "if it is, downgrade this rule in .editorconfig with a FLOWX-DEBT marker; if it is " +
        "not, move the control into the capability, where it is real. FLOWX1014 and FLOWX1018 " +
        "are unaffected and still errors: whether a declared policy is safe is a different " +
        "question from whether it is applied. This rule is deleted, not fixed, and only when " +
        "the last two kinds execute.",
        DiagnosticSeverity.Warning);

    /// <summary>
    /// FLOWX1039 — an <c>Idempotency</c> window on a flow whose result cannot be recorded
    /// without redaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>An error, where <see cref="PolicyIsNotExecutedByTheRuntime"/> is a
    /// warning</strong>, and every argument that makes that rule a warning fails here. That
    /// rule's central claim is "the source is not wrong; it is written correctly for a platform
    /// that has the feature" — a rate limit the gateway applies is a correct program. Here the
    /// platform *has* the feature and cannot serve this flow with it, and no release changes
    /// that except one giving <c>[Sensitive]</c> a read path, at which point the rule is deleted
    /// rather than downgraded.
    /// </para>
    /// <para>
    /// <strong>Flow-wide, because the redaction is.</strong> <c>SensitiveMembers</c> is read off
    /// the flow's input and output contracts and matched by name, case-insensitively, at every
    /// depth, against every document the flow writes. A per-step rule would have to traverse a
    /// contract graph reaching referenced assemblies, generics and collections, and a traversal
    /// wrong in the permissive direction ships a silently fabricated replay. See ADR-0038 §1.4.
    /// </para>
    /// <para>
    /// The build-time rule is the report and not the guarantee: a policy set the compiler cannot
    /// read (FLOWX1036) leaves it silent, and a hand-built plan never meets an analyzer at all.
    /// <c>JournalPayload.TryToReplayableJson</c> is the guarantee, and it refuses at run time.
    /// </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor IdempotencyCannotRecordARedactedResult = Create(
        "FLOWX1039",
        "Idempotency is declared on a flow whose result cannot be recorded without redaction",
        "'{0}' declares an Idempotency window on '{1}', which declares '{2}' [Sensitive]. The " +
        "recorded result would carry '[redacted]' where that value was, and replaying it would " +
        "answer a later caller with the placeholder.",
        "Stage 3 records the flow's state bag through JournalPayload, whose only exit replaces " +
        "every member named in the flow's SensitiveMembers — matched case-insensitively, at " +
        "every depth — with the literal '[redacted]'. SensitiveMembers is read off the flow's " +
        "input and output contracts, so a flow that marks one member records a document that is " +
        "not what it produced. Replaying that document hands a later step the placeholder as if " +
        "somebody had computed it: the second caller gets a plausible wrong answer with a " +
        "success beside it, every step reports success, and nothing is logged or counted. That " +
        "is worse than having no idempotency at all, because without it the step would simply " +
        "be dispatched again — the capability is idempotent, which is what makes the window " +
        "declarable — and would return the real value. Fix it by removing the Idempotency from " +
        "the set (the duplicate you were worried about is already held shut by FLOWX1014 and by " +
        "the stable ctx.IdempotencyKey), by taking the [Sensitive] marker off if the member is " +
        "not actually sensitive (check what else the marker is doing first: it strips the " +
        "member from RFC 7807 bodies, journal rows and emitted events), or by moving the marked " +
        "member off the flow's input and output contracts. Suppressing this does not make it " +
        "safe: JournalPayload.TryToReplayableJson refuses the recording at run time, so the " +
        "step fails on its first execution after its capability has already been dispatched. " +
        "This rule is deleted, not fixed, and only when a read path for [Sensitive] values " +
        "exists.",
        DiagnosticSeverity.Error);

    /// <summary>FLOWX1033 — a compensation retry attached to a step with no compensation.</summary>
    /// <remarks>
    /// <para>
    /// <strong>The one policy that runs, dropped in silence.</strong>
    /// <c>FlowEmitter.PolicyArguments</c> emits <c>compensationPolicies:</c> only when the
    /// step <c>IsCompensable</c>; on any other step the declaration reaches no plan node,
    /// while <c>ManifestWriter</c> publishes the kind and its stage regardless. So the
    /// published contract says this step's undo is retried and the compiled plan says the
    /// step has no undo.
    /// </para>
    /// <para>
    /// <strong>An error, where <see cref="PolicyIsNotExecutedByTheRuntime"/> is a
    /// warning.</strong> Every argument that makes that rule a warning fails here. There is
    /// no fixing phase — P4 implements the eight inert kinds; it does not give a
    /// non-compensable step an undo. There is no legitimate program — a retry over an undo
    /// that does not exist is not a design decision anyone defends. And something *is*
    /// falsified, which is the property that made the deleted suspension rule's
    /// <c>AwaitSignal</c> half an error: the manifest and the plan disagree about the same
    /// source line.
    /// </para>
    /// <para>
    /// <strong>The runtime already refuses the shape.</strong> <c>StepNode.ForCapability</c>
    /// throws <c>InvalidFlowPlanException</c> — "a policy chain that wraps nothing is a
    /// promise the unwind cannot keep" — and can never see it, because the emitter drops the
    /// argument before the node is constructed. That is exactly FLOWX1014's recorded history,
    /// and the two analyzer rules that mirror <c>PolicyChain</c>'s own rejections —
    /// <see cref="RetryRequiresIdempotency"/> and <see cref="CacheRequiresNoSideEffects"/> —
    /// are both errors.
    /// </para>
    /// <para>
    /// <strong>Not deleted when P4 lands.</strong> Unlike its neighbour this describes a
    /// mistake in the source rather than a gap in the platform, and it becomes more
    /// load-bearing afterwards, not less: once every other policy in the set is running, a
    /// reader is likelier to assume this one is too.
    /// </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor CompensationRetryHasNoCompensation = Create(
        "FLOWX1033",
        "CompensationRetry is declared on a step with no compensation",
        "Step '{0}' declares CompensationRetry in '{1}' and no compensation, so the one " +
        "policy this runtime executes is dropped: the retry wraps the step's undo, and this " +
        "step has none",
        "CompensationRetry bounds the dispatch of a step's compensation, so a step with no " +
        "'.CompensateWith<T>()' gives it nothing to wrap. The compiler emits the compensation " +
        "chain only for a compensable step, so the declaration reaches no plan node at all — " +
        "while the manifest publishes it, leaving the published contract promising a retried " +
        "undo the plan has no undo for. Either the step does have an inverse and it was not " +
        "declared, in which case add '.CompensateWith<T>()'; or it genuinely has none, in " +
        "which case the set naming its policies should not promise one — split the set, and " +
        "give the steps that do have an undo a set that declares the retry. Not a second " +
        ".WithPolicy(PolicySet.CompensationDefault) beside the first: a step carries one " +
        "policy set and the later call discards the earlier, which is FLOWX1034. " +
        "There is no suppression that makes the declaration work: the emitter still drops it, " +
        "so what a suppression buys is a manifest and a plan that disagree with no message " +
        "saying which is true.",
        DiagnosticSeverity.Error);

    /// <summary>FLOWX1034 — a step declaring more than one policy set.</summary>
    /// <remarks>
    /// <para>
    /// <strong>A declared control deleted, not merely unapplied.</strong>
    /// <c>StepModel.WithPolicy</c> assigns <c>PolicySetName</c> and <c>PolicyKinds</c> rather
    /// than adding to them, and <c>FlowAnalyzer.AttachPolicy</c> calls it once per
    /// <c>.WithPolicy(...)</c>. So the last call on a step wins outright: everything the
    /// earlier sets declared is gone before <c>FlowEmitter</c> and <c>ManifestWriter</c> run,
    /// and a step that declared a five-second timeout compiles to a plan with no timeout and
    /// publishes a contract with no timeout in it.
    /// </para>
    /// <para>
    /// <strong>An error, where <see cref="PolicyIsNotExecutedByTheRuntime"/> is a
    /// warning</strong>, on <see cref="CompensationRetryHasNoCompensation"/>'s line exactly.
    /// That rule's warning neighbour keeps the declaration somewhere P4 can find it; here
    /// there is nothing to keep. No release makes a discarded set apply, no author means to
    /// write two sets and use one, and the repair is mechanical and local: merge them.
    /// </para>
    /// <para>
    /// <strong>It was written down one rule over and not reported.</strong>
    /// <c>docs/diagnostics/FLOWX1019.md</c> declines to count a second <c>.WithPolicy</c>
    /// because "which set wins is a resolution question this rule has no answer to". The
    /// answer is the later one, and a fact about the compiler belongs in a diagnostic rather
    /// than in the stated limits of a rule about deadlines.
    /// </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor StepDeclaresMoreThanOnePolicySet = Create(
        "FLOWX1034",
        "Step declares more than one policy set",
        "'{0}' is discarded: this step's policy set is '{1}', because a second " +
        ".WithPolicy(...) replaces the first rather than adding to it",
        "A step carries one policy set. FlowAnalyzer writes it with StepModel.WithPolicy, " +
        "which assigns the set and its kinds rather than accumulating them, so every " +
        ".WithPolicy(...) but the last one on a step is discarded before the compiled plan " +
        "and flowx.manifest.json are written. Nothing else says so: the discarded set has no " +
        "plan node, no manifest entry and no FLOWX1014 or FLOWX1018 check, so a declared " +
        "timeout, breaker, rate limit or audit disappears in silence. Merge the sets into " +
        "one and name the merged set for the step — a PolicySet is a fluent chain, so the " +
        "merge is textual and the result is one declaration a reviewer can read. Do not " +
        "apply PolicySet.CompensationDefault as a second set: that is what this rule " +
        "reports, and until it existed FLOWX1033 recommended it. There is no suppression " +
        "that makes both sets apply; policy-set composition is a language feature this " +
        "release does not have.",
        DiagnosticSeverity.Error);

    /// <summary>FLOWX1035 — a compensation retry that retries nothing.</summary>
    /// <remarks>
    /// <para>
    /// <strong>The number is honest and the kind is not.</strong>
    /// <c>CompensationPolicy.IsRetrying</c> is <c>Attempts &gt; 1</c>;
    /// <c>ExecutionPlan.Create</c> ORs it across the graph into
    /// <c>HasCompensationPolicies</c>; <c>FlowEngine.CompensateAsync</c> reads that one flag
    /// and takes <c>CompensationPolicy.None</c> when it is false. So a single attempt is the
    /// dispatch a step with no declared chain already gets, because <c>None</c> is one
    /// attempt — and <c>ManifestWriter</c> publishes the kind and its stage with no
    /// parameters, so the published contract cannot be told apart from five attempts.
    /// </para>
    /// <para>
    /// <strong>A warning, not <see cref="CompensationRetryHasNoCompensation"/>'s error</strong>,
    /// and the line is that rule's own. It is an error because the plan and the manifest
    /// disagree about one source line — the declaration reaches no plan node while the
    /// manifest publishes it. Here they agree: the chain is built, the descriptor is in it at
    /// stage <c>Consistency</c>, and <c>PolicyChain.ForCompensation</c> has already checked
    /// the compensating capability's idempotency. What is false is the inference a reader
    /// draws from the kind's name, which is <see cref="PolicyIsNotExecutedByTheRuntime"/>'s
    /// category and its severity.
    /// </para>
    /// <para>
    /// <strong>Not fixed by redefining the parameter.</strong> <c>attempts</c> is documented
    /// as "how many times the undo may be dispatched, including the first"; making one mean
    /// two would silently double a reversal for every author who wrote the honest thing.
    /// </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor CompensationRetryRetriesNothing = Create(
        "FLOWX1035",
        "CompensationRetry declares a single attempt",
        "Step '{0}' declares CompensationRetry({1}) in '{2}', which retries nothing: the " +
        "undo is dispatched once, and the manifest publishes it as retried",
        "CompensationPolicy.IsRetrying is Attempts > 1, so an attempt count below two leaves " +
        "ExecutionPlan.HasCompensationPolicies false and FlowEngine.CompensateAsync takes " +
        "CompensationPolicy.None — one dispatch, which is exactly what a step with no " +
        "declared chain gets. A count of zero or less behaves identically, because " +
        "CompensationPolicy.From clamps it. Meanwhile ManifestWriter publishes " +
        "{\"kind\":\"CompensationRetry\",\"stage\":\"Consistency\"} and no parameters, so a " +
        "reviewer, a flowx diff and an agent all read a retried undo out of the published " +
        "contract. Either raise the count — docs/06-Execution-Engine.md §7 rule 2 makes " +
        "compensation retry more aggressive than forward retry at five attempts, which is " +
        "what PolicySet.CompensationDefault declares — or delete the call, which stops the " +
        "manifest promising a retry and changes nothing about how the undo is dispatched. " +
        "There is no configuration, deployment or later release under which one attempt " +
        "becomes a retry.",
        DiagnosticSeverity.Warning);

    /// <summary>FLOWX1036 — a policy set the compiler cannot read.</summary>
    /// <remarks>
    /// <para>
    /// <strong>Five rules and two artifacts being quiet together.</strong>
    /// <c>PolicySetReader</c> resolves a set by walking the fluent chain that built it, and a
    /// symbol from a referenced assembly has no <c>DeclaringSyntaxReferences</c> — the
    /// initialiser was compiled to IL in another build. So <c>FlowEmitter</c> emits no chain,
    /// <c>ManifestWriter</c> writes no <c>policies</c> array, and FLOWX1014, FLOWX1018,
    /// FLOWX1019, <see cref="PolicyIsNotExecutedByTheRuntime"/> and
    /// <see cref="CompensationRetryHasNoCompensation"/> all decline to speak. That is not an
    /// unchecked policy; it is an absent one, and a <c>CompensationRetry</c> inside such a set
    /// — the one policy an undo can carry — does not run.
    /// </para>
    /// <para>
    /// <strong>Silence was the deliberate choice, and it was the wrong one.</strong> The
    /// reader returns nothing rather than guessing, which is right: a report naming kinds the
    /// compiler inferred would name policies the author cannot find. But "I cannot read this
    /// set" is itself a fact worth reporting, and it is the fact the author needs — it is not
    /// a claim about the contents at all.
    /// </para>
    /// <para>
    /// <strong>A warning, on <see cref="PolicyIsNotExecutedByTheRuntime"/>'s argument.</strong>
    /// The source is not wrong: a shared policy library is a reasonable design that this
    /// compiler cannot see into, and the repairs are structural rather than a token. An error
    /// would fail builds over a program that needs no change to be correct on the day the
    /// compiler can read a metadata set.
    /// </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor PolicySetCannotBeRead = Create(
        "FLOWX1036",
        "Policy set cannot be read at compile time",
        "'{0}' cannot be read at compile time, so none of the policies it declares reaches " +
        "the compiled plan or flowx.manifest.json",
        "PolicySetReader resolves a .WithPolicy(...) argument by walking the fluent chain " +
        "that built the set. A set declared in a referenced assembly has no syntax to walk — " +
        "its initialiser was compiled to IL, and Roslyn does not read IL — and a set " +
        "returned by a method, held in a local or chosen by a conditional has no single " +
        "initialiser either. Everything downstream then agrees, quietly: no PolicyChain is " +
        "emitted, so a CompensationRetry in the set does not run and " +
        "ExecutionPlan.HasCompensationPolicies stays false; no policies array is published, " +
        "so flowx diff compares nothing; and FLOWX1014, which is what prevents a duplicate " +
        "charge, has no set to inspect. Move the declaration into the assembly that declares " +
        "the flow — a linked source file or a source-only package where several projects " +
        "need one set — or use PolicySet.CompensationDefault, which FlowX declares and this " +
        "compiler therefore knows the composition of. Do not assemble a set at run time: " +
        "PolicySet composition is resolved at compile time by design, and only parameter " +
        "values are runtime-configurable.",
        DiagnosticSeverity.Warning);

    /// <summary>FLOWX1038 — a scheduled flow nothing can fire.</summary>
    /// <remarks>
    /// <para>
    /// <strong>This rule exists because the alternative is the defect the schedule trigger was
    /// bound to remove.</strong> A <c>[CronTrigger]</c> the generator cannot turn into a
    /// registration is a flow declaring an address nothing serves — which is what
    /// <c>TriggerReader</c>'s remarks said about <c>Schedule</c> as a whole until this release.
    /// Skipping it silently, the way an <c>[HttpTrigger]</c> on a flow with no <c>.Return</c>
    /// is skipped, would leave the same hole one layer down and with no message at all.
    /// </para>
    /// <para>
    /// <strong>Two reasons, and they fail in opposite directions.</strong> A flow whose input
    /// is not <c>ScheduledFire</c> cannot be started at all: a cron firing has no body, and the
    /// occurrence is the only fact there is to hand it — which it has to be handed, because
    /// <see cref="ClockIsReadAmbiently"/> forbids it asking
    /// (<a href="../adr/ADR-0033-a-scheduled-flows-input-is-its-occurrence.md">ADR-0028</a>).
    /// An <c>Ephemeral</c> flow, by contrast, would start perfectly well — and would start on
    /// every node in the fleet, every occurrence, because nothing journals an ephemeral instance
    /// and the duplicate refusal that makes a schedule fire once is a primary key it never
    /// writes
    /// (<a href="../adr/ADR-0031-an-occurrence-names-the-instance-it-starts.md">ADR-0026</a>).
    /// </para>
    /// <para>
    /// <strong>An error, not a warning.</strong> Neither case has a deployment, configuration
    /// or later release under which it becomes correct, and both present as work that silently
    /// does not happen or silently happens <em>n</em> times. That is
    /// <see cref="CompensationRetryHasNoCompensation"/>'s bar rather than
    /// <see cref="PolicyIsNotExecutedByTheRuntime"/>'s: the source is wrong, not merely ahead of
    /// the runtime.
    /// </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor ScheduledFlowCannotBeFired = Create(
        "FLOWX1038",
        "Scheduled flow cannot be fired",
        "Flow '{0}' declares a [CronTrigger] and no schedule is registered for it: {1}",
        "A [CronTrigger] is turned into a registration by the same reading of the attribute " +
        "that produces the manifest's triggers block, so a declared schedule and a fired one " +
        "cannot disagree — but only for a flow the host can actually start. Two things stop " +
        "it. A flow whose input contract is not FlowX.ScheduledFire has nothing to bind: a " +
        "cron firing carries no body, and the occurrence is the only fact a schedule has to " +
        "give — which it must give, because FLOWX1007 and FLOWX1011 forbid the flow reading a " +
        "clock, so an instance that had to work out which occurrence it was could not. Declare " +
        "the flow as Flow<ScheduledFire, TOut> and take whatever else it needs from a " +
        "capability. And a flow that does not declare ExecutionProfile.Durable journals no " +
        "instance, so there is no primary key to refuse a second node's firing: every node in " +
        "the fleet runs every occurrence, with no error, no duplicate row and nothing anywhere " +
        "to count. Declare Profile = ExecutionProfile.Durable. There is no suppression that " +
        "makes either work — the generator emits no registration either way, so what a " +
        "suppression buys is a manifest publishing a schedule and a host that fires nothing.",
        DiagnosticSeverity.Error);

    /// <summary>Every descriptor, for the fitness function and for documentation generation.</summary>
    public static ImmutableArray<DiagnosticDescriptor> All { get; } = ImmutableArray.Create(
        FlowMustBePartial,
        StepIsNotACapability,
        CapabilityReferencesTransport,
        CapabilityInvokesCapability,
        FlowInheritsFlow,
        StateIsNotSerialisable,
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
        ProfileIsNotHonouredByTheRuntime,
        PolicyIsNotExecutedByTheRuntime,
        CompensationRetryHasNoCompensation,
        StepDeclaresMoreThanOnePolicySet,
        CompensationRetryRetriesNothing,
        PolicySetCannotBeRead,
        ScheduledFlowCannotBeFired);

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
