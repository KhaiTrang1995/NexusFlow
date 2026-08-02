namespace FlowX.Runtime;

/// <summary>The result of one step. A struct, so the step loop allocates nothing.</summary>
public readonly struct StepOutcome
{
    private StepOutcome(Error? error) => Error = error;

    /// <summary>A step that completed.</summary>
    public static StepOutcome Success => default;

    /// <summary>A step that produced a business error.</summary>
    public static StepOutcome Failed(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new StepOutcome(error);
    }

    /// <summary>The error, or <c>null</c> on success.</summary>
    public Error? Error { get; }

    /// <summary>True when the step completed.</summary>
    public bool IsSuccess => Error is null;
}

/// <summary>
/// Invokes the capability behind a step index. <strong>This is the contract the
/// source generator implements</strong>, and the reason the engine can be
/// reflection-free.
/// </summary>
/// <remarks>
/// <para>
/// The engine's loop cannot know each step's input and output types — a flow's steps
/// have different ones, and there is exactly one loop. The obvious workaround, boxing
/// them into <c>object</c>, allocates per step and would lose budget B2 on the first
/// commit.
/// </para>
/// <para>
/// So the split is: <em>the engine owns control flow, the dispatcher owns types.</em>
/// A generated implementation switches on the step index and holds each
/// step's typed input and output in its own fields, which is why nothing here is
/// generic and nothing here is boxed. The engine sees only whether the step worked.
/// </para>
/// <para>
/// Until WP-5 exists, hand-written implementations stand in. That is deliberate: if
/// this interface is awkward to implement by hand, the generated version would have
/// been awkward to debug.
/// </para>
/// </remarks>
public interface IStepDispatcher
{
    /// <summary>Runs the step at <paramref name="stepIndex"/>.</summary>
    /// <remarks>
    /// A <see cref="StepKind.Fail"/> arrives here too, and answers
    /// <see cref="StepOutcome.Failed"/> with the error its flow declared. That is
    /// deliberate rather than convenient: it means the engine cannot tell a deliberate
    /// rejection from a declined payment, so both take the failure path and both unwind
    /// what completed — which is what a saga needs them to be.
    /// </remarks>
    /// <param name="stepIndex">Position in the plan's step graph.</param>
    /// <param name="ctx">The flow's pooled context.</param>
    /// <param name="ct">Cancellation linked to the caller's token.</param>
    ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct);

    /// <summary>
    /// Runs the compensation registered for the step at <paramref name="stepIndex"/>.
    /// Only called for steps that both completed and declared one.
    /// </summary>
    ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct);

    /// <summary>
    /// Evaluates the predicate of the <see cref="StepKind.Branch"/> step at
    /// <paramref name="stepIndex"/>.
    /// </summary>
    /// <param name="stepIndex">
    /// Position in the plan's step graph. Always a branch — the engine calls this for no
    /// other kind, so an implementation is free to treat any other index as a defect.
    /// </param>
    /// <param name="ctx">The flow's pooled context.</param>
    /// <returns>
    /// <c>true</c> to continue at the next step, <c>false</c> to continue at the branch's
    /// <see cref="StepNode.Target"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Synchronous, returning <c>bool</c> rather than
    /// <c>ValueTask&lt;bool&gt;</c>.</strong> Both halves of that are load-bearing.
    /// </para>
    /// <para>
    /// An awaitable predicate would put an async state machine on the hot path. The
    /// engine's loop completes synchronously whenever its steps do — that is what
    /// <c>EngineAllocationTests</c> asserts and what makes budget B2 a hard zero — so a
    /// single awaited predicate would cost an allocation on every execution of every
    /// flow that branches, whether or not the predicate ever actually waits for
    /// anything.
    /// </para>
    /// <para>
    /// An awaitable predicate is also an invitation to do IO in one, and the determinism
    /// rules forbid it: a condition may read only the context, the flow input and prior
    /// step results (FLOWX1011), so that a durable replay takes the branch it took the
    /// first time. A signature that cannot express IO costs nothing to enforce; a
    /// diagnostic that reports it has to be written, kept accurate, and can be
    /// suppressed.
    /// </para>
    /// <para>
    /// There is no cancellation token for the same reason — a pure predicate has nothing
    /// to cancel, and the engine checks the deadline at the step the branch lands on.
    /// </para>
    /// </remarks>
    bool Evaluate(int stepIndex, FlowContext ctx);

    /// <summary>
    /// Runs the selector of the <see cref="StepKind.Switch"/> step at
    /// <paramref name="stepIndex"/> and reports which case matched.
    /// </summary>
    /// <param name="stepIndex">
    /// Position in the plan's step graph. Always a switch — the engine calls this for no
    /// other kind, so an implementation is free to treat any other index as a defect.
    /// </param>
    /// <param name="ctx">The flow's pooled context.</param>
    /// <returns>
    /// The zero-based position of the matching case in <see cref="StepNode.CaseTargets"/>,
    /// or <c>-1</c> when none matched, which sends control to
    /// <see cref="StepNode.Target"/> — the <c>Default</c> block, or the join when the
    /// author declared none.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>An <c>int</c>, not the value that was selected.</strong> Returning the
    /// value would mean returning it as <c>object</c>, which boxes an <c>enum</c> or an
    /// <c>int</c> on every switch a flow takes and loses budget B2 on the first commit —
    /// or making this method generic, which the engine cannot call because it does not
    /// know the type. An arm number is the one shape that carries the answer and no type.
    /// </para>
    /// <para>
    /// Synchronous and cancellation-free for exactly the reasons given on
    /// <see cref="Evaluate"/>: a selector may read only the context, the flow input and
    /// prior step results, so a durable replay selects the arm it selected before.
    /// </para>
    /// </remarks>
    int Select(int stepIndex, FlowContext ctx);

    /// <summary>
    /// Evaluates the collection selector of the <see cref="StepKind.ForEach"/> step at
    /// <paramref name="stepIndex"/>, once, and reports how many elements it produced.
    /// </summary>
    /// <param name="stepIndex">
    /// Position in the plan's step graph. Always an iteration — the engine calls this for
    /// no other kind.
    /// </param>
    /// <param name="ctx">
    /// The context the loop itself runs under. Inside a nested loop that is the enclosing
    /// iteration's scope, which is what lets an inner <c>ForEach</c> select over the outer
    /// element.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>Once, before the first iteration, and never again.</strong> The count is
    /// what bounds the loop, so re-reading it mid-flight would mean a collection that grows
    /// under the engine could iterate forever — inside a step loop that has no iteration cap
    /// by design. It is also the same determinism rule every other delegate obeys: a
    /// selector may read only the context, the flow input and prior step results
    /// (FLOWX1011), so a durable replay walks the collection it walked before.
    /// </para>
    /// <para>
    /// Synchronous and cancellation-free for the reasons given on <see cref="Evaluate"/>.
    /// </para>
    /// </remarks>
    IterationSource BeginIteration(int stepIndex, FlowContext ctx);

    /// <summary>
    /// Produces the context one iteration's body runs under: the loop's own context, plus
    /// the element at <paramref name="iteration"/>.
    /// </summary>
    /// <param name="stepIndex">Position in the plan's step graph. Always an iteration.</param>
    /// <param name="source">What <see cref="BeginIteration"/> returned for this step.</param>
    /// <param name="iteration">Zero-based position in the collection.</param>
    /// <param name="ctx">The context the loop itself runs under.</param>
    /// <returns>
    /// A view of <paramref name="ctx"/> in which the element resolves by its own type.
    /// Build it with <see cref="IterationScope.For{TItem}"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>The dispatcher builds the scope, not the engine</strong>, for the same
    /// reason it holds every other typed thing: only the generated code knows what a
    /// <c>TItem</c> is, and an engine that had to know would need either reflection or a
    /// boxed element. This is the one call that turns an opaque
    /// <see cref="IterationSource"/> back into a typed element, and it is a single indexed
    /// read.
    /// </para>
    /// <para>
    /// Called once per element, on the thread that is about to run that element's body —
    /// so with a concurrency bound above one, several scopes exist at the same time and
    /// each iteration sees only its own.
    /// </para>
    /// </remarks>
    FlowContext EnterIteration(int stepIndex, in IterationSource source, int iteration, FlowContext ctx);

    /// <summary>
    /// Names the child a <see cref="StepKind.SubFlow"/> step composes, and evaluates its
    /// input mapping.
    /// </summary>
    /// <param name="stepIndex">
    /// Position in the plan's step graph. Always a sub-flow — the engine calls this for no
    /// other kind.
    /// </param>
    /// <param name="ctx">
    /// The context the composing step runs under. Inside a <c>ForEach</c> body that is the
    /// iteration's scope, which is what lets a sub-flow be composed per element.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>Returns the child's plan and dispatcher rather than running them.</strong>
    /// Renting the child's context, deriving its deadline from the parent's, running its
    /// graph and unwinding its compensation are all the engine's job, and they are the same
    /// job for every flow; a dispatcher that ran the child itself would reimplement them
    /// once per composing flow, in generated code, where a mistake is hardest to see.
    /// </para>
    /// <para>
    /// Synchronous, for the reasons given on <see cref="Evaluate"/>. The mapping obeys the
    /// same determinism rule as a predicate — context, flow input and prior step results
    /// only (FLOWX1011) — so a durable replay composes the child it composed before, with
    /// the input it had before.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// <para>
    /// <strong>Defaulted to a throw, unlike every other member here, and the reason is
    /// worth stating.</strong> The engine calls this only for a
    /// <see cref="StepKind.SubFlow"/> node, so a dispatcher for a flow that composes
    /// nothing can never receive it — and the generated dispatcher for such a flow emits
    /// exactly this throw, word for word. Making it required would therefore have meant
    /// every hand-written and third-party dispatcher copying eight lines of unreachable
    /// code to satisfy the compiler. The generator always emits both members explicitly, so
    /// nothing that ships depends on the default.
    /// </para>
    /// </remarks>
    SubFlowSource BeginSubFlow(int stepIndex, FlowContext ctx) =>
        throw new ArgumentOutOfRangeException(
            nameof(stepIndex),
            stepIndex,
            "This flow composes no sub-flow, so the engine never asks it to compose one. " +
            "Reaching this means the plan and this dispatcher came from different builds.");

    /// <summary>
    /// Seeds the child's input into the child's own context, under its own type.
    /// </summary>
    /// <param name="stepIndex">Position in the plan's step graph. Always a sub-flow.</param>
    /// <param name="source">What <see cref="BeginSubFlow"/> returned for this step.</param>
    /// <param name="child">
    /// The child's freshly initialised context. Not the parent's — the two never meet,
    /// which is what makes a detached child safe against a pooled context being reused.
    /// </param>
    /// <remarks>
    /// The mirror of <see cref="EnterIteration"/>: the one call that turns an opaque
    /// <see cref="SubFlowSource.Input"/> back into a typed value, in the only code that
    /// knows the type. It is a cast and a <c>ctx.Set</c>.
    /// <para>
    /// Defaulted to a throw for the reason given on <see cref="BeginSubFlow"/>: the engine
    /// cannot reach it without having reached that one first.
    /// </para>
    /// </remarks>
    void EnterSubFlow(int stepIndex, in SubFlowSource source, FlowContext child) =>
        throw new ArgumentOutOfRangeException(
            nameof(stepIndex),
            stepIndex,
            "This flow composes no sub-flow, so the engine never asks it to seed one. " +
            "Reaching this means the plan and this dispatcher came from different builds.");

    /// <summary>
    /// Describes the step that just completed for the journal: what it produced, and the
    /// flow's state bag as it now stands.
    /// </summary>
    /// <param name="stepIndex">Position in the plan's step graph.</param>
    /// <param name="ctx">
    /// The scope the step ran under — the iteration's view inside a <c>ForEach</c> body, so
    /// that what is described is what the step actually saw.
    /// </param>
    /// <returns>
    /// The payloads to commit, or <see cref="StepJournalEntry.Nothing"/> when there is
    /// nothing to record.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Called at the step boundary by two callers, and this sentence used to name
    /// one.</strong> It read "called only for a <c>Durable</c> flow, at the step boundary,
    /// before the commit", which was a statement about the journal rather than about this
    /// member — and <c>PLAN §6a</c> and
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md">ADR-0025</a>
    /// both read it as a constraint and concluded that stage 3 had no per-step result seam. It
    /// does: this member and <see cref="RestoreState"/> are one, and a stage-3 idempotency
    /// window uses them under either profile.
    /// </para>
    /// <para>
    /// <strong>Budget B2's hard zero is untouched, structurally.</strong> The journal asks only
    /// for a <c>Durable</c> flow; the idempotency window asks only when a step's resolved
    /// <c>StepPolicy</c> declares one, which is gated by <c>ExecutionPlan.HasStepPolicies</c>
    /// exactly as stage 4 is. An ephemeral flow that declares no policy reaches neither caller.
    /// </para>
    /// <para>
    /// <strong>Here rather than on the engine, for the reason nothing else typed is on the
    /// engine either.</strong> <c>JournalPayload.Of</c> requires the generated
    /// <c>JsonTypeInfo&lt;T&gt;</c> — there is no overload that reflects over a type — and
    /// only generated code can name one. That requirement is what makes membership of the
    /// generated JSON context a compile error rather than a convention (ADR-0008,
    /// ADR-0015 commitment 5) and what keeps the write path trim- and NativeAOT-safe.
    /// </para>
    /// <para>
    /// <strong>The payloads must carry the flow's <c>SensitiveMembers</c>.</strong> The
    /// generator already emits that array onto every flow's partial class, so nothing new has
    /// to be discovered to keep a marked member out of a table retained for months — only
    /// remembered. <see cref="JournalPayload"/> is shaped so that a store cannot get at the
    /// value any other way.
    /// </para>
    /// <para>
    /// Defaulted to <see cref="StepJournalEntry.Nothing"/> rather than to a throw, unlike
    /// <see cref="BeginSubFlow"/>. A dispatcher that describes nothing produces a journal
    /// with the step boundaries and without the payloads, which is a truthful record and a
    /// resumable one only in the weak sense — the resumed loop skips what committed and
    /// re-enters with an empty bag. WP-59 made the generated dispatcher describe both
    /// payloads at every step boundary, so that is no longer what a compiled flow does; the
    /// default stays because a hand-written dispatcher is entitled to run under
    /// <c>Durable</c>, and a default that threw would make it unusable.
    /// </para>
    /// </remarks>
    StepJournalEntry DescribeStep(int stepIndex, FlowContext ctx) => StepJournalEntry.Nothing;

    /// <summary>
    /// Describes the trigger input for the instance row, before the first step runs.
    /// </summary>
    /// <param name="input">
    /// The value the flow was started with, or <c>null</c> when it was started without one.
    /// </param>
    /// <returns>
    /// The payload to record on <c>flow_instance.input</c>, or
    /// <see cref="JournalPayload.Empty"/> when there is nothing to record.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>This exists because <c>FlowHost</c> could not write an input and the defect was
    /// invisible.</strong> <c>OpenAsync</c> passed the literal <c>input: null</c> to
    /// <c>DurableExecution.BeginAsync</c>, so <c>flow_instance.input</c> was NULL on every row
    /// ever written: a replay could not reconstruct what was requested, the audit trail had no
    /// record of it, and <c>[Sensitive]</c> on an input contract protected nothing, because
    /// nothing was stored. It was not fixable in the host — journaling an input needs a
    /// <c>JsonTypeInfo&lt;TIn&gt;</c> and only generated code can name one, which is the same
    /// reason <see cref="DescribeStep"/> is here.
    /// </para>
    /// <para>
    /// <strong>The stored input is redacted, like every other payload.</strong> What comes
    /// back is a <see cref="JournalPayload"/> carrying the flow's <c>SensitiveMembers</c>, so a
    /// marked member of the input contract is <see cref="JournalPayload.Redacted"/> in the row
    /// — which is the point of storing the input at all rather than an argument against it.
    /// </para>
    /// <para>
    /// <c>object?</c> rather than a generic, because the host holds the dispatcher through this
    /// interface and the interface cannot carry the flow's input type. The boxing is on the
    /// durable start path, which is already taking a lease and a store round trip; the
    /// ephemeral loop budget B2 measures never reaches this call.
    /// </para>
    /// <para>
    /// Defaulted to <see cref="JournalPayload.Empty"/> rather than to a throw, for
    /// <see cref="DescribeStep"/>'s reason: a hand-written dispatcher is entitled to run under
    /// <c>Durable</c>, and an instance row without an input is the record this release wrote
    /// for every flow until WP-59.
    /// </para>
    /// </remarks>
    JournalPayload DescribeInput(object? input) => JournalPayload.Empty;

    /// <summary>
    /// Names what this step's result depends on, for a <c>Cache</c> key.
    /// </summary>
    /// <param name="stepIndex">Position in the plan's step graph.</param>
    /// <param name="ctx">The scope the step is about to run under.</param>
    /// <returns>
    /// The step's input, or <see cref="JournalPayload.Empty"/> when this dispatcher cannot key
    /// the step — which the engine reads as "not cacheable" and dispatches.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Here rather than on the engine, for <see cref="DescribeStep"/>'s reason.</strong>
    /// <c>docs/10 §8</c> keys a cache entry on "capability id + input hash + tenant + principal
    /// permission set". The engine holds three of those four; the input is a contract value in a
    /// <c>Dictionary&lt;Type, object&gt;</c>, so only generated code can name the
    /// <c>JsonTypeInfo&lt;T&gt;</c> that writes it.
    /// </para>
    /// <para>
    /// <strong>A <see cref="JournalPayload"/>, not a string, and the difference is the whole
    /// design.</strong> The engine hashes what <see cref="JournalPayload.ToJson"/> produced —
    /// the one exit, which redacts. A step whose input carries a <c>[Sensitive]</c> member
    /// therefore keys on a document containing <see cref="JournalPayload.Redacted"/> where the
    /// distinguishing value should be, and two different inputs would collide on one key. The
    /// engine refuses such a key outright rather than serving one caller another's result;
    /// see <c>FlowEngine</c>'s cache path and
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0044-a-cache-is-a-plugin-store-keyed-by-the-redacted-input.md">ADR-0044</a>.
    /// </para>
    /// <para>
    /// Called before the dispatch, and only for a step whose resolved <c>StepPolicy.HasCache</c>
    /// is true. Defaulted to <see cref="JournalPayload.Empty"/> so a hand-written dispatcher
    /// caches nothing rather than caching wrongly.
    /// </para>
    /// </remarks>
    JournalPayload DescribeCacheKey(int stepIndex, FlowContext ctx) => JournalPayload.Empty;

    /// <summary>
    /// Describes what a finished step produced, as the cache should hold it.
    /// </summary>
    /// <param name="stepIndex">Position in the plan's step graph.</param>
    /// <param name="ctx">The scope the step ran under.</param>
    /// <returns>
    /// A one-member document naming the result by its contract's simple name, or
    /// <see cref="JournalPayload.Empty"/> when there is nothing to hold.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Composed the way the state bag is, so it comes back the way the state bag
    /// does.</strong> The document is built by <c>JournalPayload.OfState</c> under the
    /// contract's simple name, which is exactly the shape <see cref="RestoreState"/> reads — so
    /// a cache hit is put back into the bag by the method that already exists, and there is no
    /// second deserialiser to keep in step with the first. A dispatcher that can write a state
    /// bag can read this back by construction.
    /// </para>
    /// <para>
    /// The payload carries the flow's <c>SensitiveMembers</c>, like every other payload, so a
    /// marked member reaches the store as <see cref="JournalPayload.Redacted"/>. That makes it
    /// unusable as a cached result, which is why the engine declines to store a document
    /// containing the placeholder: a hit that returned <c>[redacted]</c> would hand the flow a
    /// value no capability produced.
    /// </para>
    /// </remarks>
    JournalPayload DescribeCacheEntry(int stepIndex, FlowContext ctx) => JournalPayload.Empty;

    /// <summary>
    /// Describes what an audited step carried, for the record's payload.
    /// </summary>
    /// <param name="stepIndex">Position in the plan's step graph.</param>
    /// <param name="ctx">The scope the step ran under.</param>
    /// <param name="redact">
    /// The member names the <c>Audit</c> policy declared. Passed to
    /// <c>JournalPayload.OfState</c> alongside the flow's <c>SensitiveMembers</c>, so the
    /// record's redaction is the same pass with a longer list.
    /// </param>
    /// <returns>
    /// A composed <c>request</c> / <c>result</c> document, or <see cref="JournalPayload.Empty"/>
    /// when this dispatcher has nothing to describe.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>This is what makes <c>redact</c> mean something.</strong> An audit record the
    /// engine could write alone would carry no contract value at all, and a redact list over an
    /// empty record names nothing — the objection that had stage 7 declined twice. The list
    /// reaches the one redaction pass FlowX has, on the one type a value can leave through, so
    /// a record is never more revealing than the journal row beside it and can be made less so.
    /// </para>
    /// <para>
    /// Called after the step succeeded and after its commit, and only for a step whose resolved
    /// <c>StepAudit.IsAudited</c> is true. Defaulted to <see cref="JournalPayload.Empty"/>: a
    /// record with no payload is still a record of what ran and on whose authority, which is the
    /// half <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0028-identity-arrives-on-the-invocation.md">ADR-0028</a>
    /// asked for.
    /// </para>
    /// </remarks>
    JournalPayload DescribeAudit(int stepIndex, FlowContext ctx, IReadOnlyList<string> redact) =>
        JournalPayload.Empty;

    /// <summary>
    /// Rehydrates a resumed flow's state bag from the snapshot the journal committed.
    /// </summary>
    /// <param name="ctx">The freshly rented context the resumed loop will run under.</param>
    /// <param name="stateBagJson">
    /// The last committed state bag, exactly as it was stored — sensitive members already
    /// redacted, because there is no read path that could put them back.
    /// </param>
    /// <remarks>
    /// <para>
    /// The mirror of <see cref="DescribeStep"/>, and the only call that turns stored JSON
    /// back into the typed values the steps after the frontier bind to. The engine cannot do
    /// it for the same reason it cannot write it.
    /// </para>
    /// <para>
    /// <strong>The redaction in the parameter's description is a loss the journal accepts and a
    /// stage-3 replay refuses.</strong> A resumed instance has no alternative — its effects have
    /// already happened and the node that held the real values is gone — so it takes the
    /// placeholder and <c>JournalState</c> says so. An idempotency replay does have one, which
    /// is to dispatch the capability again, so returning the placeholder to a caller as if it
    /// were the value would be choosing a fabricated answer over a second call. The engine
    /// therefore records through <c>JournalPayload.TryToReplayableJson</c>, which refuses a
    /// document the redaction pass had to change, and never reaches this method with one. See
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0042-a-recorded-result-is-replayed-only-when-recording-lost-nothing.md">ADR-0042</a>
    /// §1.3.
    /// </para>
    /// <para>
    /// Called once, before the first step of a resumed execution, and only when the instance
    /// actually recorded a state bag. A resumed flow whose journal carries no snapshot — a
    /// dispatcher that describes nothing — re-enters with an empty bag rather than reaching
    /// this method.
    /// </para>
    /// <para>
    /// Defaulted to a throw, like <see cref="BeginSubFlow"/> and for the same kind of reason:
    /// a dispatcher that never described a state bag can never be asked to restore one, so
    /// nothing that ships depends on the default — and a dispatcher that <em>did</em> write
    /// one and cannot read it back must fail loudly rather than resume a flow with an empty
    /// bag, which would rerun the rest of it against values no step produced.
    /// </para>
    /// </remarks>
    void RestoreState(FlowContext ctx, string stateBagJson) =>
        throw new NotSupportedException(
            "This dispatcher journals no state bag, so the engine never asks it to restore " +
            "one. Reaching this means a state bag was committed by a dispatcher that cannot " +
            "read it back — resuming would run the rest of the flow against values no step " +
            "produced.");
}
