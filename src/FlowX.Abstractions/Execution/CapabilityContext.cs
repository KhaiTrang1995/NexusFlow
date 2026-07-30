namespace FlowX;

/// <summary>
/// Ambient execution state visible to a capability. Pooled and reset by the runtime,
/// never allocated per step (principle P5).
/// </summary>
/// <remarks>
/// Everything a capability might otherwise reach for ambiently — the clock, new
/// identifiers, randomness — is here instead, because durable replay must reproduce
/// those values exactly. Reaching past this type is a build error in a durable flow
/// (FLOWX1007/1008).
/// </remarks>
public abstract class CapabilityContext
{
    /// <summary>Correlates every step, log record and span of one logical operation.</summary>
    public abstract string CorrelationId { get; }

    /// <summary>The durable flow instance this step belongs to; <c>null</c> for ephemeral flows.</summary>
    public abstract string? FlowInstanceId { get; }

    /// <summary>Identity of the capability being executed, e.g. <c>payment.capture</c>.</summary>
    public abstract string CapabilityId { get; }

    /// <summary>
    /// Resolved from validated claims only, never from a payload or header
    /// (docs/16-Multi-Tenant.md §3).
    /// </summary>
    public abstract string? TenantId { get; }

    /// <summary>
    /// Stable across retries <em>and</em> across replays. Pass it to downstream systems
    /// so they can deduplicate — this is what makes at-least-once delivery produce
    /// effectively-once effects.
    /// </summary>
    public abstract string IdempotencyKey { get; }

    /// <summary>
    /// The flow's absolute deadline. Set once at trigger time and never reset by a retry,
    /// so a step cannot extend the operation's budget.
    /// </summary>
    public abstract DateTimeOffset Deadline { get; }

    /// <summary>
    /// Journaled on first read so replay reproduces it. Use this, never
    /// <see cref="DateTimeOffset.UtcNow"/>.
    /// </summary>
    public abstract DateTimeOffset UtcNow { get; }

    /// <summary>
    /// Journaled on first call so replay reproduces it. Use this, never
    /// <see cref="Guid.NewGuid"/>.
    /// </summary>
    public abstract Guid NewId();

    /// <summary>
    /// Deterministic under replay: the seed is journaled on first use. Use this, never
    /// <see cref="Random.Shared"/>.
    /// </summary>
    public abstract Random Random { get; }

    /// <summary>Remaining budget before <see cref="Deadline"/>; zero when already exceeded.</summary>
    public TimeSpan TimeRemaining
    {
        get
        {
            var remaining = Deadline - UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }
}
