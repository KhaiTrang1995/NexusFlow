namespace FlowX;

/// <summary>
/// The values a step read that it could not have computed — the clock, the ids it minted and
/// the seed its random generator was built from — captured on first use and replayed
/// thereafter.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the envelope that makes replay a guarantee rather than a hope.</strong>
/// A flow is deterministic in everything except what it reads from outside itself; recording
/// exactly those reads is what lets a resumed instance, or <c>flowx replay</c>, reconstruct a
/// run that has already happened.
/// </para>
/// <para>
/// <strong><see cref="RandomSeed"/> is why <c>FlowExecutionContext.RandomSeed</c> is
/// public.</strong> <c>new Random()</c> picks a seed and shows it to nobody, so a generator
/// built that way is reproducible by nothing at all. The runtime now draws the seed itself
/// and keeps it; this is the field it was drawn for. It is an <c>int?</c> rather than a
/// sentinel because "this run never asked for randomness" and "the seed happened to be zero"
/// are different facts, and a journal that confused them would replay a run that drew no
/// number as one that did.
/// </para>
/// </remarks>
public sealed record NondeterminismCapture
{
    /// <summary>Nothing was read from outside the flow.</summary>
    public static NondeterminismCapture None { get; } = new();

    /// <summary>What <c>ctx.UtcNow</c> returned the first time the step read it.</summary>
    public DateTimeOffset? UtcNow { get; init; }

    /// <summary>The ids <c>ctx.NewId()</c> produced, in the order it produced them.</summary>
    public IReadOnlyList<Guid> NewIds { get; init; } = [];

    /// <summary>
    /// The seed <c>ctx.Random</c> was built from, or null if the step never asked for
    /// randomness.
    /// </summary>
    public int? RandomSeed { get; init; }

    /// <summary>Whether nothing was captured, so the column can be left null.</summary>
    public bool IsEmpty => UtcNow is null && RandomSeed is null && NewIds.Count == 0;
}
