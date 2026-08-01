namespace FlowX.Testing;

/// <summary>
/// The fixed values both test contexts expose, and the logic that produces them.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CapabilityContext"/> and <see cref="FlowContext"/> are a base and a
/// derived class, so a test double for each cannot share a base of its own —
/// <c>TestFlowContext</c> must inherit <see cref="FlowContext"/>, which rules out
/// inheriting <see cref="TestCapabilityContext"/>. Composition instead: each context is
/// ten forwarding properties over one of these, and the defaults, the validation and
/// the identifier sequence exist once.
/// </para>
/// <para>
/// Duplicating the property list is the accepted cost; duplicating the behaviour is not.
/// If a default changes, it changes here.
/// </para>
/// </remarks>
internal sealed class ContextValues
{
    private readonly Guid _firstId;

    private int _idsIssued;

    internal ContextValues(
        string idempotencyKey,
        string correlationId,
        string? tenantId,
        string capabilityId,
        string? compensatingFor,
        string? flowInstanceId,
        DateTimeOffset? utcNow,
        TimeSpan? budget,
        Guid? firstId,
        int randomSeed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityId);

        IdempotencyKey = idempotencyKey;
        CorrelationId = correlationId;
        TenantId = tenantId;
        CapabilityId = capabilityId;
        CompensatingFor = compensatingFor;
        FlowInstanceId = flowInstanceId;

        // A fixed instant, not UtcNow: a test asserting on a timestamp must not pass
        // today and fail tomorrow.
        UtcNow = utcNow ?? DateTimeOffset.UnixEpoch;
        _firstId = firstId ?? Guid.Empty;

        // Long enough that a capability under test never trips the deadline by accident,
        // short enough that a test asserting deadline behaviour can shorten it usefully.
        Deadline = UtcNow + (budget ?? TimeSpan.FromMinutes(1));
        Random = new Random(randomSeed);
    }

    internal string IdempotencyKey { get; }

    internal string CorrelationId { get; }

    internal string? TenantId { get; }

    internal string CapabilityId { get; }

    internal string? CompensatingFor { get; }

    internal string? FlowInstanceId { get; }

    internal DateTimeOffset UtcNow { get; }

    internal DateTimeOffset Deadline { get; }

    internal Random Random { get; }

    internal int IdsIssued => _idsIssued;

    /// <summary>
    /// A distinct identifier on every call, reproducible across runs.
    /// </summary>
    /// <remarks>
    /// Returning one constant would be reproducible too, and would hide a capability
    /// that used a single id where it needed two.
    /// </remarks>
    internal Guid NewId()
    {
        var ordinal = _idsIssued++;

        if (ordinal == 0)
        {
            return _firstId;
        }

        Span<byte> bytes = stackalloc byte[16];

        if (!_firstId.TryWriteBytes(bytes))
        {
            // A Guid is exactly 16 bytes, so this cannot happen. Failing loudly beats
            // silently returning a duplicate id.
            throw new InvalidOperationException("Could not read the seed identifier.");
        }

        var tail = BitConverter.ToInt32(bytes[12..]) + ordinal;
        BitConverter.TryWriteBytes(bytes[12..], tail);

        return new Guid(bytes);
    }
}
