namespace FlowX.Testing;

/// <summary>
/// A <see cref="CapabilityContext"/> for tests: fixed values, every one overridable.
/// </summary>
/// <remarks>
/// <para>
/// Quality goal Q2 says a capability is testable by constructing it and calling it.
/// It is — but <see cref="CapabilityContext"/> is abstract with nine members, so the
/// first thing every consumer wrote was the same thirty-line stub. Ceremony that every
/// user pays is a platform defect, not a user problem.
/// </para>
/// <para>
/// <strong>Every value is fixed, and that is the point.</strong> The clock, the
/// identifiers and the randomness a capability is allowed to reach for all come from
/// this type, so pinning them is what makes a capability test deterministic. It is the
/// same property durable replay depends on, which is why the abstraction exists at all
/// — a capability that reads <see cref="DateTimeOffset.UtcNow"/> instead is untestable
/// here and unreplayable there, for one reason.
/// </para>
/// <para>
/// Constructor parameters rather than object-initialiser syntax, because an override of
/// a get-only abstract property cannot add an <c>init</c> accessor. Named arguments give
/// the same call site: <c>new TestCapabilityContext(idempotencyKey: "key-1")</c>.
/// </para>
/// </remarks>
public sealed class TestCapabilityContext : CapabilityContext
{
    private readonly ContextValues _values;

    /// <summary>Creates a context. Every parameter has a usable default.</summary>
    /// <param name="idempotencyKey">
    /// Stable across retries and replays. Pass your own when a test asserts that a
    /// capability hands it downstream — which is the behaviour that makes a retry safe.
    /// </param>
    /// <param name="correlationId">Correlates the operation's records.</param>
    /// <param name="tenantId">Resolved from validated claims in production; here, whatever you say.</param>
    /// <param name="capabilityId">Identity of the capability under test.</param>
    /// <param name="flowInstanceId">Durable instance id, or <c>null</c> for an ephemeral flow.</param>
    /// <param name="utcNow">The clock. Defaults to <see cref="DateTimeOffset.UnixEpoch"/>.</param>
    /// <param name="budget">Time until the deadline. Defaults to one minute.</param>
    /// <param name="firstId">The first value <see cref="NewId"/> returns.</param>
    /// <param name="randomSeed">Seed for <see cref="Random"/>. Fixed, so draws repeat.</param>
    public TestCapabilityContext(
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
    /// <remarks>
    /// Does not advance. A capability that needs time to move should be given a context
    /// that says so, rather than one that changes underneath the assertion.
    /// </remarks>
    public override DateTimeOffset UtcNow => _values.UtcNow;

    /// <inheritdoc />
    public override Random Random => _values.Random;

    /// <summary>How many identifiers this context has issued.</summary>
    /// <remarks>
    /// Exposed so a test can assert a capability asked for one — or, more usefully, that
    /// it did not, because an id minted mid-flow is a value replay cannot reproduce.
    /// </remarks>
    public int IdsIssued => _values.IdsIssued;

    /// <inheritdoc />
    /// <remarks>Distinct on every call and reproducible across runs.</remarks>
    public override Guid NewId() => _values.NewId();
}
