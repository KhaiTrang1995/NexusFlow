using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;

namespace FlowX.Runtime;

/// <summary>
/// The concrete, pooled <see cref="FlowContext"/> the engine threads through a flow.
/// </summary>
/// <remarks>
/// <para>
/// Pooled because allocating one per execution is the single largest source of Gen0
/// pressure a runtime can inflict on its host, and budget B2 is a hard zero.
/// </para>
/// <para>
/// Pooling is also the most dangerous thing in this file. A field left over from the
/// previous execution is a cross-tenant data leak (OWASP A01), so
/// <see cref="Reset"/> clears <strong>every</strong> field, and
/// <c>ContextPoolingTests</c> asserts it for the ones that carry tenant data. When a
/// field is added here, it must be added to <see cref="Reset"/> in the same commit.
/// </para>
/// </remarks>
public sealed class FlowExecutionContext : FlowContext
{
    private readonly Dictionary<Type, object> _state = [];

    // Owned by the context so it is pooled with it. Constructing one per execution
    // cost 288 B, which was the last allocation between the engine and budget B2.
    private readonly CompensationStack _compensations = new();

    /// <summary>
    /// Child contexts this execution composed successfully and is still holding, because
    /// the parent may yet have to undo them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Owned by the context so it is pooled with it, for exactly the reason the
    /// compensation stack is. The alternative was to find them by walking that stack at the
    /// end of every successful flow — which works, and costs one iterator per nesting level
    /// per execution: 96 B measured, on the <em>success</em> path, for a flow that composes
    /// one child that composes nothing. A list the pool already owns is read with a plain
    /// <c>for</c> and costs nothing.
    /// </para>
    /// <para>
    /// Only the success path reads it. When the flow fails, the unwind visits the same
    /// children through the compensation stack — in the right order, which this list does
    /// not have — and returns them there.
    /// </para>
    /// </remarks>
    private readonly List<FlowExecutionContext> _retainedSubFlows = [];

    /// <summary>
    /// Whether more than one thread can reach this context, and therefore whether the
    /// state bag and the compensation stack have to be serialised.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set from <c>ExecutionPlan.HasParallel</c>, so it is a fact about the flow's compiled
    /// shape rather than a guess. A flow that never forks takes the unguarded path and
    /// costs exactly what it did before this field existed — one predictable, always-false
    /// branch. That is deliberate: budget B2 is a hard zero for the linear, conditional and
    /// switch paths, and a <c>ConcurrentDictionary</c> would have lost it, because
    /// <c>ConcurrentDictionary</c> allocates a node per entry written where a pooled
    /// <c>Dictionary</c> reuses the buckets it already has.
    /// </para>
    /// <para>
    /// <strong>What the lock does and does not buy.</strong> It makes concurrent writes
    /// <em>safe</em> — the dictionary cannot be corrupted, and a torn read is impossible.
    /// It does not make them <em>meaningful</em>: two branches writing the same contract
    /// type still race, and the winner is whichever finished last. That is a modelling
    /// mistake rather than a memory-safety one, and it is what FLOWX1013 refuses at build
    /// time. The runtime's job here is only to make sure the failure mode is a wrong value
    /// rather than a corrupted heap.
    /// </para>
    /// </remarks>
    private bool _guarded;

    private string _flowId = string.Empty;
    private string _flowVersion = string.Empty;
    private string _capabilityId = string.Empty;
    private string? _compensatingFor;
    private string _correlationId = string.Empty;
    private string _idempotencyKey = string.Empty;
    private string? _tenantId;

    /// <summary>
    /// <see cref="Run"/>'s instance id as text, computed the first time it is asked for.
    /// </summary>
    /// <remarks>
    /// Lazily, because the only caller on the hot path is a generated <c>DescribeStep</c>
    /// choosing an emitted event's partition key, and a flow that emits nothing must not pay
    /// a string for a question nobody asks. Two parallel branches racing here both compute
    /// the same text off the same immutable id, so the race is benign and a lock would cost
    /// every linear flow to make an equal value equal.
    /// </remarks>
    private string? _flowInstanceId;

    private DateTimeOffset _deadline;
    private IClock _clock = SystemClock.Instance;
    private Random? _random;

    /// <summary>
    /// The seed <see cref="_random"/> was built from, or <c>null</c> while this execution
    /// has not asked for randomness.
    /// </summary>
    /// <remarks>
    /// An <c>int?</c> rather than a sentinel because "no seed yet" and "the seed happened to
    /// be zero" are different facts, and a journal that confused them would replay a run
    /// that never drew a number as one that did.
    /// </remarks>
    private int? _randomSeed;

    /// <summary>
    /// Whether this execution's step boundaries are being journaled, and therefore whether
    /// what it reads from outside itself has to be captured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set from <c>ExecutionPlan.Flow.Profile</c>, so it is a fact about the flow's
    /// declaration rather than a guess — exactly as <see cref="_guarded"/> is set from
    /// <c>HasParallel</c>. An ephemeral flow takes the same path it always did and pays one
    /// predictable, always-false branch on <see cref="UtcNow"/> and <see cref="NewId"/>,
    /// which is what keeps budget B2 a hard zero for a feature it does not use.
    /// </para>
    /// <para>
    /// It is the runtime reading <c>ExecutionProfile</c>, which is the statement WP-52
    /// exists to make true and <c>FLOWX1028</c> existed to say was false.
    /// </para>
    /// </remarks>
    private bool _journaled;

    /// <summary>
    /// What <see cref="UtcNow"/> answered the first time the step now running read it.
    /// </summary>
    /// <remarks>
    /// Pinned rather than merely recorded. "Captured on first use and replayed thereafter"
    /// is only true if later reads inside the same step get the captured value — a step that
    /// read the clock twice and journaled the first answer would replay as a run that never
    /// happened. Cleared at every step boundary by <see cref="TakeNondeterminism"/>, so the
    /// pinning lasts exactly one step.
    /// </remarks>
    private DateTimeOffset? _capturedNow;

    /// <summary>
    /// The ids <see cref="NewId"/> minted during the step now running, in order.
    /// </summary>
    /// <remarks>
    /// Created on first use and then owned by the pooled context, for the reason the
    /// compensation stack is: a durable flow that mints ids should not build a list per step,
    /// and an ephemeral flow should not build one at all.
    /// </remarks>
    private List<Guid>? _newIds;

    /// <summary>Whether the random seed has already been written to a journal row.</summary>
    /// <remarks>
    /// The seed is drawn once per execution, not once per step, so it belongs on the row for
    /// the step that first asked for randomness and on no other. Without this flag every
    /// subsequent row would repeat it, and a reader could not tell one execution that drew a
    /// number from one that drew several.
    /// </remarks>
    private bool _seedRecorded;

    /// <summary>
    /// The ids the step now running is being <em>given</em>, because a previous execution of
    /// it minted them and the journal kept them; <c>null</c> when nothing is being replayed.
    /// </summary>
    /// <remarks>
    /// The read half of <see cref="_newIds"/>, and the direction that did not exist until
    /// WP-61. Recording a value nothing can be handed back makes replay describable and not
    /// deliverable, which is the state <see cref="RandomSeed"/> said this class was in.
    /// </remarks>
    private IReadOnlyList<Guid>? _replayIds;

    /// <summary>How many of <see cref="_replayIds"/> this step has already been given.</summary>
    /// <remarks>
    /// A cursor rather than a queue, so replaying a step costs no allocation and the capture
    /// stays the immutable record it is everywhere else. Once it passes the end, further reads
    /// mint fresh ids — deliberately, because a replay that reads <em>more</em> than the
    /// original did has diverged, and the honest way to report that is to let the extra id
    /// appear on the replayed row where a comparison can see it.
    /// </remarks>
    private int _replayIdCursor;

    /// <summary>The seed <see cref="Random"/> must be rebuilt from, when one is being replayed.</summary>
    private int? _replaySeed;

    private Error? _error;

    /// <summary>
    /// The dispatcher running this context's flow, or <c>null</c> for a context the engine
    /// was handed no dispatcher for.
    /// </summary>
    /// <remarks>
    /// Recorded for exactly one purpose: a sub-flow's compensation. When a child succeeds
    /// and its parent later fails, the parent's unwind reaches an entry whose scope is the
    /// child's context — and the undo has to be dispatched through the <em>child's</em>
    /// dispatcher, because the step indices on that stack are the child's. Carrying it here
    /// is what makes "the context of a running flow knows what is running it" true rather
    /// than something the engine has to thread through five signatures.
    /// </remarks>
    private IStepDispatcher? _dispatcher;

    /// <summary>
    /// The plan this context is executing, recorded for the reason
    /// <see cref="_dispatcher"/> is.
    /// </summary>
    /// <remarks>
    /// A sub-flow that succeeded is unwound long after the composition returned, through the
    /// child's own dispatcher and against the child's own context. What the unwind also needs
    /// by then is the child's <em>plan</em> — to know whether any of its compensations declare
    /// a policy at all — and this context is the only thing that still has it.
    /// </remarks>
    private ExecutionPlan? _plan;

    /// <summary>How many sub-flow boundaries lie between this execution and the outermost one.</summary>
    private int _depth;

    /// <inheritdoc />
    public override string CorrelationId => _correlationId;

    /// <inheritdoc />
    /// <remarks>
    /// The journaled instance's id, and <c>null</c> for an ephemeral execution — which is
    /// exactly what <see cref="CapabilityContext.FlowInstanceId"/> declares it to be. It read
    /// <c>null</c> unconditionally until an emitted event needed a partition key, and a
    /// per-instance key is the only ordering ADR-0018 offers.
    /// </remarks>
    public override string? FlowInstanceId =>
        _flowInstanceId ??= Run?.InstanceId.ToString();

    /// <inheritdoc />
    public override string CapabilityId => _capabilityId;

    /// <inheritdoc />
    public override string? CompensatingFor => _compensatingFor;

    /// <inheritdoc />
    public override string? TenantId => _tenantId;

    /// <inheritdoc />
    public override string IdempotencyKey => _idempotencyKey;

    /// <inheritdoc />
    public override DateTimeOffset Deadline => _deadline;

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// A plain clock read for an ephemeral flow, and one predictable always-false branch on
    /// top of it. Under <c>Durable</c> the first read of each step is captured into the
    /// step's <see cref="NondeterminismCapture"/> and every later read in that step returns
    /// the captured instant, because a replay that re-read the clock would reconstruct a run
    /// that never happened.
    /// </para>
    /// <para>
    /// The engine's own deadline check is usually the first read of a step, so the captured
    /// instant is the moment the step started — which is the honest thing for it to be.
    /// </para>
    /// </remarks>
    public override DateTimeOffset UtcNow => _journaled ? CaptureNow() : _clock.UtcNow;

    /// <summary>Reads the clock once per step and answers with the same instant thereafter.</summary>
    /// <remarks>
    /// Serialised under the state bag's lock when the flow forks, for the reason every other
    /// write on this class is: two branches can read the clock at the same instant, and a
    /// nullable <see cref="DateTimeOffset"/> is wider than a word.
    /// </remarks>
    private DateTimeOffset CaptureNow()
    {
        if (_guarded)
        {
            lock (_state)
            {
                return CaptureNowCore();
            }
        }

        return CaptureNowCore();
    }

    private DateTimeOffset CaptureNowCore()
    {
        if (_capturedNow is { } captured)
        {
            return captured;
        }

        var now = _clock.UtcNow;
        _capturedNow = now;

        return now;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Created on first access, not on reset. Most flows never touch it, and building
    /// a <see cref="System.Random"/> eagerly cost an allocation on every execution —
    /// which measurement caught and review did not. Lazy creation also matches the
    /// durability contract: a flow that never asks for randomness has no seed to journal.
    /// </para>
    /// <para>
    /// <strong>Built from a seed this context keeps, and that is the whole point.</strong>
    /// <c>new Random()</c> picks its own seed and exposes it to nobody — not to a caller,
    /// not to reflection, not to the type itself — so a generator built that way cannot be
    /// reproduced by anything, and the replay guarantee described here would have been
    /// undeliverable rather than merely unimplemented. Drawing the seed first and holding it
    /// in <see cref="RandomSeed"/> makes the guarantee <em>possible</em>: the journal that
    /// records it is P2 work and does not exist yet, but the value it has to record now does.
    /// </para>
    /// <para>
    /// The seed comes from <see cref="System.Random.Shared"/> rather than from the clock or a
    /// counter. Two executions starting in the same tick must not draw the same stream, and
    /// <c>Random.Shared</c> is thread-safe and allocation-free — which matters because this
    /// runs inside the property, on whichever thread first touched it, possibly a parallel
    /// branch.
    /// </para>
    /// <para>
    /// <strong>Budget B2 is untouched.</strong> The seed is drawn where the generator is
    /// built — on first use — so a linear, conditional or switch flow that never reads
    /// <c>ctx.Random</c> does exactly what it did before: nothing. The field costs a pooled
    /// object four bytes and <see cref="Reset"/> one assignment. <c>EngineAllocationTests</c>
    /// still measures 0 B on all four of those paths.
    /// </para>
    /// </remarks>
    public override Random Random => _random ??= CreateRandom();

    /// <summary>
    /// The seed <see cref="Random"/> was constructed from, or <c>null</c> if this execution
    /// never asked for randomness.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Public because a store, a replay tool and an operator's view of a stuck instance are
    /// all outside this assembly. Inside it, <see cref="TakeNondeterminism"/> is the one
    /// reader: a durable execution writes the seed onto the row for the step that first asked
    /// for randomness, which is what turns this property from a value nobody uses into the
    /// thing that makes the replay guarantee described above deliverable.
    /// </para>
    /// <para>
    /// Reading this does not create the generator. A caller that asks a flow which never drew
    /// a number gets <c>null</c>, which is the honest answer and the one a journal wants:
    /// there is nothing to record.
    /// </para>
    /// <para>
    /// <strong>The other direction exists now, and this paragraph used to say it did
    /// not.</strong> It read: "the context has no way to be <em>given</em> a seed, so a
    /// resumed instance re-draws rather than reproducing". <see cref="ReplayNondeterminism"/>
    /// is that way, added by WP-61 because a corpus cannot demonstrate replay against a
    /// capture nothing can read back. What <em>still</em> does not happen is the engine
    /// calling it on a resume: the step loop skips a committed step rather than re-running it,
    /// so no resumed execution reaches a step it has a capture for. Replaying the capture into
    /// execution is what <c>ReplayDeterminismTests</c> drives and what <c>flowx replay</c>
    /// (WP-64) will.
    /// </para>
    /// </remarks>
    public int? RandomSeed => _randomSeed;

    /// <summary>Draws this execution's seed and builds the generator from it.</summary>
    /// <remarks>
    /// Separate from the property so the lazy path stays a null check and a call, and so the
    /// two statements cannot drift apart — a generator built without its seed being recorded
    /// is exactly the defect this replaced.
    /// </remarks>
    private Random CreateRandom()
    {
        // The replayed seed when this execution has been given one, and a fresh draw
        // otherwise. Same statement, same field, so a replayed generator cannot end up
        // reproducible-looking and unrecorded.
        var seed = _replaySeed ?? System.Random.Shared.Next();
        _randomSeed = seed;

        return new Random(seed);
    }

    /// <inheritdoc />
    public override string FlowId => _flowId;

    /// <inheritdoc />
    public override string FlowVersion => _flowVersion;

    /// <inheritdoc />
    public override ClaimsPrincipal? Principal => null;

    /// <inheritdoc />
    /// <remarks>
    /// Empty until a transport plugin supplies one (WP-8). The engine never reads it:
    /// a flow that can observe how it was triggered is a flow that will branch on it,
    /// and quality goal Q4 is lost.
    /// </remarks>
    public override TriggerEnvelope Trigger { get; }

    /// <inheritdoc />
    public override Error? Error => _error;

    /// <inheritdoc />
    /// <remarks>
    /// An ephemeral flow mints an id and forgets it, exactly as before. A durable one keeps
    /// every id the step minted, in order, so that the row records what the step read that it
    /// could not have computed.
    /// </remarks>
    public override Guid NewId()
    {
        if (!_journaled)
        {
            return Guid.NewGuid();
        }

        return MintId();
    }

    /// <summary>
    /// Answers a journaled execution's request for an id: the one the capture holds, if this
    /// step is being replayed, and a fresh one otherwise — and records it either way.
    /// </summary>
    /// <remarks>
    /// The mint and the record are one operation under one lock. Splitting them let a sibling
    /// branch interleave between the two, which is how an id could be recorded in an order no
    /// step actually minted it in.
    /// </remarks>
    private Guid MintId()
    {
        if (_guarded)
        {
            lock (_state)
            {
                return MintIdCore();
            }
        }

        return MintIdCore();
    }

    private Guid MintIdCore()
    {
        var id = _replayIds is { } replayed && _replayIdCursor < replayed.Count
            ? replayed[_replayIdCursor++]
            : Guid.NewGuid();

        (_newIds ??= []).Add(id);

        return id;
    }

    /// <inheritdoc />
    public override T Get<T>() => TryGet<T>(out var value)
        ? value
        : throw new InvalidOperationException(
            $"No step in flow '{_flowId}' produced a {typeof(T).Name}. Step bindings are " +
            "resolved at build time (FLOWX1020), so reaching this at run time means the " +
            "value was written dynamically rather than returned by a step.");

    /// <inheritdoc />
    public override bool TryGet<T>([MaybeNullWhen(false)] out T value)
    {
        // Reads are guarded too, not only writes. A Dictionary being written on another
        // thread can be mid-resize, and a read that races a resize does not merely miss
        // the new entry — it can walk a bucket array that is being replaced.
        if (_guarded)
        {
            lock (_state)
            {
                return TryRead(out value);
            }
        }

        return TryRead(out value);
    }

    private bool TryRead<T>([MaybeNullWhen(false)] out T value)
    {
        if (_state.TryGetValue(typeof(T), out var stored))
        {
            value = (T)stored;
            return true;
        }

        value = default;
        return false;
    }

    /// <inheritdoc />
    public override void Set<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (_guarded)
        {
            lock (_state)
            {
                _state[typeof(T)] = value;
            }

            return;
        }

        _state[typeof(T)] = value;
    }

    /// <summary>Prepares a pooled instance for one execution.</summary>
    /// <param name="plan">The flow being executed.</param>
    /// <param name="invocation">Correlation, tenant and the caller's remaining budget.</param>
    /// <param name="clock">The time source.</param>
    /// <param name="dispatcher">
    /// What is executing this flow. Recorded so a sub-flow's compensation can be dispatched
    /// through the right one long after the child returned.
    /// </param>
    /// <param name="depth">
    /// How many sub-flow boundaries lie above this execution. Zero for a flow a trigger
    /// started.
    /// </param>
    internal void Initialise(
        ExecutionPlan plan,
        in FlowInvocation invocation,
        IClock clock,
        IStepDispatcher? dispatcher = null,
        int depth = 0)
    {
        _dispatcher = dispatcher;
        _depth = depth;
        _guarded = plan.HasParallel;
        _plan = plan;
        Run = null;

        // The runtime reading ExecutionProfile, in one line. Everything a durable execution
        // costs hangs off this field, and everything an ephemeral one does not pay is the
        // branches that read it being false.
        _journaled = plan.Flow.Profile == ExecutionProfile.Durable;
        _flowId = plan.Flow.Id;
        _flowVersion = plan.Flow.Version;
        _correlationId = invocation.CorrelationId;
        _idempotencyKey = invocation.IdempotencyKey;
        _tenantId = invocation.TenantId;
        _flowInstanceId = null;
        _clock = clock;

        // The flow's own budget, shortened by the caller's if the caller has less.
        // Never lengthened: a trigger must not be able to buy more time than the
        // flow's author allowed.
        var declared = clock.UtcNow + plan.Flow.Deadline;
        _deadline = invocation.Deadline is { } supplied && supplied < declared ? supplied : declared;
    }

    /// <summary>The compensations registered by this execution. Reused, never reallocated.</summary>
    internal CompensationStack Compensations => _compensations;

    /// <summary>What is executing this flow, for a sub-flow's deferred unwind.</summary>
    internal IStepDispatcher? Dispatcher => _dispatcher;

    /// <summary>The plan this context is executing, for a sub-flow's deferred unwind.</summary>
    internal ExecutionPlan? Plan => _plan;

    /// <summary>
    /// The journaled instance this execution's compensation rows belong to, or <c>null</c> for
    /// an ephemeral execution.
    /// </summary>
    /// <remarks>
    /// Set by the engine immediately after the instance is opened, and read only on the
    /// failure path. A deferred sub-flow unwind writes rows against the <em>child's</em>
    /// instance, which is the one its step indices mean something in.
    /// </remarks>
    internal DurableExecution? Run { get; set; }

    /// <summary>Child contexts still rented on this execution's behalf.</summary>
    internal List<FlowExecutionContext> RetainedSubFlows => _retainedSubFlows;

    /// <summary>
    /// Records a successfully composed child whose context this execution keeps.
    /// </summary>
    /// <remarks>
    /// Serialised under the same lock the state bag uses when the flow forks, for the same
    /// reason <see cref="RecordCompleted"/> is: two parallel branches can each compose a
    /// child at the same instant, and a lost entry here is a pooled context that is never
    /// given back.
    /// </remarks>
    internal void RetainSubFlow(FlowExecutionContext child)
    {
        if (_guarded)
        {
            lock (_state)
            {
                _retainedSubFlows.Add(child);
            }

            return;
        }

        _retainedSubFlows.Add(child);
    }

    /// <summary>How many sub-flow boundaries lie above this execution.</summary>
    internal int Depth => _depth;

    /// <summary>
    /// Pushes a completed compensable step onto the unwind stack, serialising the push
    /// when the flow forks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>CompensationStack</c> says of itself that it is "not thread-safe: one instance,
    /// one flow, one thread at a time". That was true until a fork could complete two
    /// compensable steps at once; a <c>Stack&lt;T&gt;.Push</c> racing another loses an
    /// entry or corrupts the backing array, and the entry it loses is an undo that will
    /// then never run. So the stack keeps its single-threaded contract and this is the
    /// one place that honours it.
    /// </para>
    /// <para>
    /// <strong>The order is completion order, and across concurrent branches that is not
    /// deterministic.</strong> "Strict reverse order" remains exact <em>within</em> a
    /// branch and between a branch and everything sequential around it — those are ordered
    /// by the happens-before the fork and the join establish. Two steps in two different
    /// branches have no such ordering, so their compensations may unwind in either order.
    /// That is not a defect being hidden: branches that write disjoint slots
    /// (FLOWX1013) have nothing to order against each other, and a saga that needed one
    /// undone before the other was never expressing concurrency in the first place.
    /// </para>
    /// </remarks>
    internal void RecordCompleted(StepNode step, FlowContext? scope = null, StepScope journalScope = default)
    {
        if (_guarded)
        {
            lock (_state)
            {
                _compensations.RecordCompleted(step, scope, journalScope);
            }

            return;
        }

        _compensations.RecordCompleted(step, scope, journalScope);
    }

    /// <summary>
    /// Records the identity of the step currently running, for diagnostics, and returns it.
    /// </summary>
    /// <remarks>
    /// The return value is what the engine reports a failure against. Reading the field
    /// back would be wrong inside a fork: a sibling branch entering its own step overwrites
    /// it between the throw and the catch, and the error would then name a capability that
    /// did not fail. The field is still written, because a capability may read
    /// <c>ctx.CapabilityId</c> — but inside a parallel branch what it reads is whichever
    /// step most recently started, which may be a sibling's. That is a fidelity limit of
    /// one shared field, stated rather than papered over; per-branch identity needs a
    /// per-branch context, which is a larger change than this work package.
    /// <para>
    /// <see cref="StepNode.Identity"/> rather than the expression spelled out here, so this
    /// and the journal row cannot drift about what the same step is called.
    /// </para>
    /// </remarks>
    internal string EnterStep(StepNode step)
    {
        ArgumentNullException.ThrowIfNull(step);

        var id = step.Identity;
        _capabilityId = id;

        // Cleared rather than left, although no forward step runs after an unwind starts and
        // Reset clears it again before the context is reused. A method that sets one of two
        // fields describing the same thing and leaves the other is the exact shape that made
        // this defect possible; both entry points state the whole identity, and it costs one
        // store of a null.
        _compensatingFor = null;

        return id;
    }

    /// <summary>
    /// Records that this execution is now undoing <paramref name="step"/>, and returns the
    /// identity of the capability doing the undoing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The counterpart of <see cref="EnterStep"/>, and it exists because reusing
    /// that one lost money.</strong> The unwind used to call <see cref="EnterStep"/> with
    /// the completed step, so <c>ctx.CapabilityId</c> named the capability being
    /// <em>reversed</em> while the capability actually running was its compensation. An undo
    /// keying an idempotency key on the context therefore produced the forward step's key
    /// byte for byte; a store honouring that key deduplicated the contra write away, the
    /// capability returned success, and the engine recorded
    /// <see cref="CompensationOutcome.Succeeded"/> over an effect that never happened. The
    /// journal row was right the whole time, which is what made the disagreement invisible.
    /// </para>
    /// <para>
    /// Both facts are recorded because both have readers, and neither can be derived from
    /// the other at the point of use: a compensator needs its own identity to key a write,
    /// and an operator reading a trace needs to know which step is being reversed.
    /// </para>
    /// </remarks>
    internal string EnterCompensation(StepNode step)
    {
        ArgumentNullException.ThrowIfNull(step);

        var id = step.CompensationIdentity;
        _capabilityId = id;
        _compensatingFor = step.Identity;

        return id;
    }

    /// <summary>Records the error that ended the flow, so compensations can read it.</summary>
    internal void SetError(Error? error) => _error = error;

    /// <summary>
    /// Takes what the step that just finished read from outside itself, and clears it ready
    /// for the next one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The envelope ADR-0015's fourth commitment describes: the clock instant the step
    /// pinned, the ids it minted, and the seed its generator was built from. Called once per
    /// journaled step boundary and never on the ephemeral path, so a flow that declared
    /// nothing pays nothing.
    /// </para>
    /// <para>
    /// <strong>Attribution inside a fork is best-effort, and it is the same fidelity limit
    /// <see cref="EnterStep"/> already states.</strong> One pooled context is shared by every
    /// branch, so an id minted by a sibling between this step finishing and its commit is
    /// attributed to this row. It is safe — the writes are serialised — and it is now
    /// <em>observably</em> wrong rather than merely latent: WP-61 replays a capture back into
    /// execution, and <c>ReplayDeterminismTests</c> pins both halves — the misattribution
    /// itself, and the divergence it causes when the misattributed row is replayed. Making it
    /// exact needs a per-branch context. WP-61 did not buy one; it measured what not having
    /// one costs, and the cost is that a fork whose branches genuinely overlap does not
    /// replay.
    /// </para>
    /// </remarks>
    internal NondeterminismCapture TakeNondeterminism()
    {
        if (_guarded)
        {
            lock (_state)
            {
                return TakeNondeterminismCore();
            }
        }

        return TakeNondeterminismCore();
    }

    /// <summary>
    /// Gives the step that is about to run the values a previous execution of it read from
    /// outside itself, so that it reads the journal rather than the world.
    /// </summary>
    /// <param name="captured">
    /// The envelope committed for this step: the instant it pinned, the ids it minted in
    /// order, and the seed its generator was built from.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="captured"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// This execution is not journaled, so there is no capture it could have come from.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <strong>This is the direction that did not exist, and its absence is why replay was a
    /// specification rather than a property under test.</strong> WP-52 made a durable step
    /// write a <see cref="NondeterminismCapture"/>; nothing could read one back, so
    /// <see cref="RandomSeed"/> could say what the seed had been and no execution could be
    /// told to use it. A capture that is written and never read is exactly the shape in which
    /// a determinism leak leaves no trace: it is recorded, it is wrong, and nothing compares
    /// it against anything. This method is what lets something compare.
    /// </para>
    /// <para>
    /// <strong>Public for the reason <see cref="RandomSeed"/> is public.</strong> A replay
    /// tool (<c>flowx replay</c>, WP-64) and a test harness that drives one execution against
    /// another's journal are both outside this assembly, and both need to hand a step its
    /// history. Internal visibility would have meant the only code able to prove the replay
    /// contract lived in the assembly the contract is about.
    /// </para>
    /// <para>
    /// <strong>It does not clear what the step has already read.</strong> Anything minted
    /// before this call still lands on the replayed row, so a replay that read ahead of its
    /// capture shows up as an extra id rather than being tidied away. The same is true in the
    /// other direction: when <see cref="NondeterminismCapture.UtcNow"/> is null this pins
    /// nothing and the step reads the live clock, because there is no captured instant to
    /// serve and inventing one would make a replay look faithful that is not.
    /// </para>
    /// <para>
    /// <strong>What it cannot buy is per-branch fidelity.</strong> One pooled context is
    /// shared by every branch of a <c>Parallel</c>, so a capture written under a fork may have
    /// been attributed to a sibling's row — and this method replays a row, faithfully,
    /// including that attribution. Exactness there needs a per-branch context, which
    /// <see cref="TakeNondeterminism"/> names and this does not deliver.
    /// </para>
    /// </remarks>
    public void ReplayNondeterminism(NondeterminismCapture captured)
    {
        ArgumentNullException.ThrowIfNull(captured);

        if (!_journaled)
        {
            throw new InvalidOperationException(
                $"Flow '{_flowId}' is not journaled, so it has no capture to be replayed " +
                "from. Replaying an ephemeral execution would be replaying a run that was " +
                "never recorded.");
        }

        if (_guarded)
        {
            lock (_state)
            {
                ReplayNondeterminismCore(captured);
            }

            return;
        }

        ReplayNondeterminismCore(captured);
    }

    private void ReplayNondeterminismCore(NondeterminismCapture captured)
    {
        _capturedNow = captured.UtcNow;
        _replaySeed = captured.RandomSeed;
        _replayIds = captured.NewIds.Count > 0 ? captured.NewIds : null;
        _replayIdCursor = 0;
    }

    private NondeterminismCapture TakeNondeterminismCore()
    {
        var now = _capturedNow;
        var seed = _seedRecorded ? null : _randomSeed;
        var ids = _newIds is { Count: > 0 } minted ? minted.ToArray() : [];

        _capturedNow = null;
        _newIds?.Clear();
        _seedRecorded |= seed is not null;

        // What the step was *given* is cleared with what it read, and for the same reason the
        // pinning lasts exactly one step: a capture belongs to one row. Leaving it would let
        // an unreplayed step be served the previous one's history, which would make a replay
        // that skipped a step look faithful.
        _replayIds = null;
        _replayIdCursor = 0;
        _replaySeed = null;

        return now is null && seed is null && ids.Length == 0
            ? NondeterminismCapture.None
            : new NondeterminismCapture { UtcNow = now, RandomSeed = seed, NewIds = ids };
    }

    /// <summary>
    /// Clears every field before the instance returns to the pool.
    /// </summary>
    /// <remarks>
    /// Anything missed here is visible to the <em>next</em> flow, which may belong to
    /// a different tenant. Adding a field above without adding it here is the defect
    /// this method exists to prevent.
    /// </remarks>
    internal void Reset()
    {
        _state.Clear();
        _compensations.Reset();
        _retainedSubFlows.Clear();
        _guarded = false;
        _flowId = string.Empty;
        _flowVersion = string.Empty;
        _capabilityId = string.Empty;
        _compensatingFor = null;
        _correlationId = string.Empty;
        _idempotencyKey = string.Empty;
        _tenantId = null;
        _flowInstanceId = null;
        _deadline = default;
        _clock = SystemClock.Instance;
        _random = null;
        _randomSeed = null;
        _journaled = false;
        _capturedNow = null;
        _newIds?.Clear();
        _seedRecorded = false;
        _replayIds = null;
        _replayIdCursor = 0;
        _replaySeed = null;
        _error = null;
        _dispatcher = null;
        _plan = null;
        Run = null;
        _depth = 0;
    }
}
