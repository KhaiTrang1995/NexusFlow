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
    /// <remarks>
    /// <para>
    /// <strong>The capability that is running, never the one it is running on behalf
    /// of.</strong> Inside a compensation this is the <em>compensating</em> capability —
    /// <c>payment.refund</c>, not the <c>payment.capture</c> being reversed. What is being
    /// undone is <see cref="CompensatingFor"/>, and the two are deliberately separate
    /// values because they are read for opposite purposes.
    /// </para>
    /// <para>
    /// <strong>This is what to derive an idempotency key from</strong>, together with
    /// <see cref="IdempotencyKey"/>. The pair is stable across a retry and across a replay
    /// and distinct between a step and its undo — which is the property that makes a contra
    /// write land instead of being deduplicated against the write it reverses. It is also
    /// what the journal row records, what a span is named after, and what an unhandled
    /// throw is attributed to, so a capability reading it here sees the same string an
    /// operator reading the audit trail does.
    /// </para>
    /// <para>
    /// Inside a parallel branch this is best-effort: one pooled context is shared by every
    /// branch, so a sibling entering its own step overwrites the field. Per-branch identity
    /// needs a per-branch context.
    /// </para>
    /// </remarks>
    public abstract string CapabilityId { get; }

    /// <summary>
    /// The capability this execution is undoing, or <c>null</c> when it is running forward.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Both facts are on the type because both have readers, and one value cannot
    /// serve both.</strong> A compensator writing to a store needs to know what
    /// <em>it</em> is, so its write is keyed differently from the one it reverses; an
    /// operator reading a trace, and a log line explaining why an undo is happening at all,
    /// need to know which step is being reversed. Collapsing them into one member is the
    /// defect this pair replaced: <see cref="CapabilityId"/> answered the second question
    /// while every caller read it for the first, and an undo keyed on it produced the
    /// forward step's key exactly.
    /// </para>
    /// <para>
    /// <c>null</c> is the whole of the forward path, so <c>ctx.CompensatingFor is not
    /// null</c> — spelled <see cref="IsCompensating"/> — is how a capability that is both a
    /// step and somebody else's compensation tells which way round it is being run.
    /// </para>
    /// <para>
    /// A step whose undo is a nested unwind rather than a capability — an inline sub-flow —
    /// reports its own identity here, because the composition is what is being undone and
    /// there is no separate compensating capability to name.
    /// </para>
    /// </remarks>
    public abstract string? CompensatingFor { get; }

    /// <summary>True when this execution is undoing a completed step rather than running one.</summary>
    /// <remarks>
    /// Not virtual: it is <see cref="CompensatingFor"/> being non-null, and an override that
    /// could disagree with that would be a third answer to a question that already had one
    /// too many.
    /// </remarks>
    public bool IsCompensating => CompensatingFor is not null;

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
