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
    private string _correlationId = string.Empty;
    private string _idempotencyKey = string.Empty;
    private string? _tenantId;
    private DateTimeOffset _deadline;
    private IClock _clock = SystemClock.Instance;
    private Random? _random;
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

    /// <summary>How many sub-flow boundaries lie between this execution and the outermost one.</summary>
    private int _depth;

    /// <inheritdoc />
    public override string CorrelationId => _correlationId;

    /// <inheritdoc />
    public override string? FlowInstanceId => null;

    /// <inheritdoc />
    public override string CapabilityId => _capabilityId;

    /// <inheritdoc />
    public override string? TenantId => _tenantId;

    /// <inheritdoc />
    public override string IdempotencyKey => _idempotencyKey;

    /// <inheritdoc />
    public override DateTimeOffset Deadline => _deadline;

    /// <inheritdoc />
    public override DateTimeOffset UtcNow => _clock.UtcNow;

    /// <inheritdoc />
    /// <remarks>
    /// Created on first access, not on reset. Most flows never touch it, and building
    /// a <see cref="System.Random"/> eagerly cost an allocation on every execution —
    /// which measurement caught and review did not. Lazy creation also matches the
    /// durability contract: the seed is journaled on first use (P2), so a flow that
    /// never asks for randomness journals nothing.
    /// </remarks>
    public override Random Random => _random ??= new Random();

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
    public override Guid NewId() => Guid.NewGuid();

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
        _flowId = plan.Flow.Id;
        _flowVersion = plan.Flow.Version;
        _correlationId = invocation.CorrelationId;
        _idempotencyKey = invocation.IdempotencyKey;
        _tenantId = invocation.TenantId;
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
    internal void RecordCompleted(StepNode step, FlowContext? scope = null)
    {
        if (_guarded)
        {
            lock (_state)
            {
                _compensations.RecordCompleted(step, scope);
            }

            return;
        }

        _compensations.RecordCompleted(step, scope);
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
    /// </remarks>
    internal string EnterStep(StepNode step)
    {
        var id = step.Capability?.Id ?? step.EventType ?? step.SignalType ?? step.SubFlowId ?? string.Empty;
        _capabilityId = id;
        return id;
    }

    /// <summary>Records the error that ended the flow, so compensations can read it.</summary>
    internal void SetError(Error? error) => _error = error;

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
        _correlationId = string.Empty;
        _idempotencyKey = string.Empty;
        _tenantId = null;
        _deadline = default;
        _clock = SystemClock.Instance;
        _random = null;
        _error = null;
        _dispatcher = null;
        _depth = 0;
    }
}
