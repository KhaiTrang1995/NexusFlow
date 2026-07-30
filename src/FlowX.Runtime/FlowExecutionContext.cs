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
    public override T Get<T>() => _state.TryGetValue(typeof(T), out var value)
        ? (T)value
        : throw new InvalidOperationException(
            $"No step in flow '{_flowId}' produced a {typeof(T).Name}. Step bindings are " +
            "resolved at build time (FLOWX1020), so reaching this at run time means the " +
            "value was written dynamically rather than returned by a step.");

    /// <inheritdoc />
    public override bool TryGet<T>([MaybeNullWhen(false)] out T value)
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
        _state[typeof(T)] = value;
    }

    /// <summary>Prepares a pooled instance for one execution.</summary>
    internal void Initialise(ExecutionPlan plan, in FlowInvocation invocation, IClock clock)
    {
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

    /// <summary>Records the identity of the step currently running, for diagnostics.</summary>
    internal void EnterStep(StepNode step) =>
        _capabilityId = step.Capability?.Id ?? step.EventType ?? step.SignalType ?? string.Empty;

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
    }
}
