using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;

namespace FlowX.Testing;

/// <summary>
/// A <see cref="FlowContext"/> for tests: a working typed bag over
/// <see cref="TestCapabilityContext"/>'s fixed values.
/// </summary>
/// <remarks>
/// <para>
/// What this makes testable is the code the generator emits. A step dispatcher reads
/// <c>ctx.Get&lt;TInput&gt;()</c> and writes <c>ctx.Set(result.Value)</c>; a
/// <c>.Return(...)</c> projection reads the finished context. Both are ordinary methods
/// that take a <see cref="FlowContext"/>, and with one of these they can be called
/// directly — no engine, no plan, no host.
/// </para>
/// <para>
/// The bag is keyed by type, exactly as the real context is, because that is what makes
/// a step bind to the contract it declared rather than to a position in a list. A test
/// that seeds two values of the same type gets the second, and so would production.
/// </para>
/// </remarks>
public sealed class TestFlowContext : FlowContext
{
    private readonly Dictionary<Type, object> _bag = [];
    private readonly ContextValues _values;

    private Error? _error;

    /// <summary>Creates a flow context.</summary>
    /// <param name="flowId">Business identity of the flow under test.</param>
    /// <param name="flowVersion">Contract version of the flow.</param>
    /// <param name="principal">The authenticated caller, or <c>null</c>.</param>
    /// <param name="trigger">
    /// How the flow was activated. Defaults to a <see cref="TriggerKind.Manual"/>
    /// envelope, which is what a test invocation actually is.
    /// </param>
    /// <param name="idempotencyKey">See <see cref="TestCapabilityContext"/>.</param>
    /// <param name="correlationId">See <see cref="TestCapabilityContext"/>.</param>
    /// <param name="tenantId">See <see cref="TestCapabilityContext"/>.</param>
    /// <param name="capabilityId">See <see cref="TestCapabilityContext"/>.</param>
    /// <param name="flowInstanceId">See <see cref="TestCapabilityContext"/>.</param>
    /// <param name="utcNow">See <see cref="TestCapabilityContext"/>.</param>
    /// <param name="budget">See <see cref="TestCapabilityContext"/>.</param>
    /// <param name="firstId">See <see cref="TestCapabilityContext"/>.</param>
    /// <param name="randomSeed">See <see cref="TestCapabilityContext"/>.</param>
    public TestFlowContext(
        string flowId = "test.flow",
        string flowVersion = "1.0.0",
        ClaimsPrincipal? principal = null,
        TriggerEnvelope? trigger = null,
        string idempotencyKey = "test-idempotency-key",
        string correlationId = "test-correlation-id",
        string? tenantId = null,
        string capabilityId = "test.capability",
        string? flowInstanceId = null,
        DateTimeOffset? utcNow = null,
        TimeSpan? budget = null,
        Guid? firstId = null,
        int randomSeed = 0)
    {
        _values = new ContextValues(
            idempotencyKey,
            correlationId,
            tenantId,
            capabilityId,
            flowInstanceId,
            utcNow,
            budget,
            firstId,
            randomSeed);

        ArgumentException.ThrowIfNullOrWhiteSpace(flowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowVersion);

        FlowId = flowId;
        FlowVersion = flowVersion;
        Principal = principal;

        Trigger = trigger ?? new TriggerEnvelope(
            TriggerKind.Manual,
            "test",
            ReadOnlyMemory<byte>.Empty,
            new TriggerHeaders(correlationId, tenantId, principal, idempotencyKey),
            _values.UtcNow);
    }

    /// <inheritdoc />
    public override string CorrelationId => _values.CorrelationId;

    /// <inheritdoc />
    public override string? FlowInstanceId => _values.FlowInstanceId;

    /// <inheritdoc />
    public override string CapabilityId => _values.CapabilityId;

    /// <inheritdoc />
    public override string? TenantId => _values.TenantId;

    /// <inheritdoc />
    public override string IdempotencyKey => _values.IdempotencyKey;

    /// <inheritdoc />
    public override DateTimeOffset Deadline => _values.Deadline;

    /// <inheritdoc />
    public override DateTimeOffset UtcNow => _values.UtcNow;

    /// <inheritdoc />
    public override Random Random => _values.Random;

    /// <summary>How many identifiers this context has issued.</summary>
    public int IdsIssued => _values.IdsIssued;

    /// <inheritdoc />
    /// <remarks>Distinct on every call and reproducible across runs.</remarks>
    public override Guid NewId() => _values.NewId();

    /// <inheritdoc />
    public override string FlowId { get; }

    /// <inheritdoc />
    public override string FlowVersion { get; }

    /// <inheritdoc />
    public override ClaimsPrincipal? Principal { get; }

    /// <inheritdoc />
    public override TriggerEnvelope Trigger { get; }

    /// <inheritdoc />
    /// <remarks><c>null</c> until <see cref="Fail"/> is called.</remarks>
    public override Error? Error => _error;

    /// <summary>Marks the flow as failed, as the engine does before it compensates.</summary>
    /// <param name="error">Why the flow failed.</param>
    /// <returns>This context.</returns>
    /// <remarks>
    /// A compensation runs <em>after</em> a failure and may read <see cref="Error"/> to
    /// decide how much to undo. Without this there was no way to put a context into the
    /// state a compensation actually meets, and the property would have been permanently
    /// <c>null</c> — present in the API and useless.
    /// </remarks>
    public TestFlowContext Fail(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        _error = error;
        return this;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// No value of that type has been set. Throws rather than returning
    /// <c>default</c>, because the real context does: a step reading a contract no
    /// earlier step produced is a defect in the flow, and a silent <c>null</c> turns it
    /// into a mystery two steps later.
    /// </exception>
    public override T Get<T>() => _bag.TryGetValue(typeof(T), out var value)
        ? (T)value
        : throw new InvalidOperationException(
            $"No value of type {typeof(T).Name} is in the context. Seed it with Set<T>(), " +
            "or check that the step you are testing runs after the one that produces it.");

    /// <inheritdoc />
    public override bool TryGet<T>([MaybeNullWhen(false)] out T value)
    {
        if (_bag.TryGetValue(typeof(T), out var found))
        {
            value = (T)found;
            return true;
        }

        value = default;
        return false;
    }

    /// <inheritdoc />
    public override void Set<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _bag[typeof(T)] = value;
    }

    /// <summary>Seeds a value and returns the context, for one-expression setup.</summary>
    /// <typeparam name="T">The contract type, which is also its key.</typeparam>
    /// <param name="value">The value a prior step would have produced.</param>
    /// <returns>This context.</returns>
    /// <example>
    /// <code>
    /// var ctx = new TestFlowContext()
    ///     .With(new ValidatedOrder("SKU-1", 2, 40m))
    ///     .With(new Reservation("SKU-1", 2, "key-1"));
    /// </code>
    /// </example>
    public TestFlowContext With<T>(T value)
        where T : notnull
    {
        Set(value);
        return this;
    }

    /// <summary>How many values the context currently holds.</summary>
    public int Count => _bag.Count;
}
