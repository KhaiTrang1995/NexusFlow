namespace FlowX;

/// <summary>
/// The cost/reliability trade-off of a flow — the single most consequential decision
/// a flow author makes (ADR-0003).
/// </summary>
/// <remarks>
/// The default is <see cref="Ephemeral"/>: you opt <em>into</em> cost, never out of it.
/// A journaled step commit is roughly 1&#8211;15&#160;ms against ~1&#160;µs in memory —
/// three to four orders of magnitude. Making every flow durable, as always-durable
/// engines do, charges every flow for a guarantee most of them do not need.
/// </remarks>
public enum ExecutionProfile
{
    /// <summary>
    /// State in memory, lost on crash, caller retries. ~1&#160;µs per step, zero
    /// allocations. For queries, validation APIs and CRUD.
    /// </summary>
    Ephemeral = 0,

    /// <summary>
    /// Every step journaled; resumes on another node after a crash; supports signals
    /// and timers. ~1&#160;ms per step. Determinism rules are <em>errors</em> under this
    /// profile. For payments, sagas and long-running processes.
    /// </summary>
    Durable = 1,

    /// <summary>
    /// Checkpointed offsets with windowing, watermarks and backpressure. For continuous
    /// ingestion, enrichment and aggregation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Journaled, exactly as <see cref="Durable"/> is.</strong> A stream's checkpoint is
    /// committed after a closed window's flow has run, so a crash in between rebuilds that window
    /// from the source — and what turns the rebuild into a refusal rather than a second
    /// aggregation is the derived instance id meeting the journal's primary key
    /// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0055-a-window-names-the-instance-it-starts.md">ADR-0055</a>).
    /// So this profile costs what <see cref="Durable"/> costs, per <em>window</em> rather than
    /// per record, and the determinism rules are errors under it for the same reason.
    /// </para>
    /// <para>
    /// <strong>What it adds is upstream of the flow.</strong> A flow declaring this must be
    /// bound by a <c>[StreamTrigger]</c> and take a <see cref="StreamWindowBatch"/>; declaring it
    /// without one buys nothing at all, and <c>FLOWX1028</c> says so.
    /// </para>
    /// </remarks>
    Streaming = 2,
}

/// <summary>What each execution profile costs the runtime, asked in one place.</summary>
/// <remarks>
/// <strong>A predicate rather than five comparisons against <see cref="ExecutionProfile.Durable"/>.</strong>
/// Before P7 the runtime asked "is this Durable?" in five places and meant "does this journal?",
/// and the two questions had the same answer only because the third profile ran nowhere. Making
/// <see cref="ExecutionProfile.Streaming"/> journal turned that into five places to remember; this
/// is the one place instead, and a profile added later is a row here rather than a silent
/// omission on whichever of the five the author did not find.
/// </remarks>
public static class ExecutionProfiles
{
    /// <summary>Whether a flow on this profile commits a journal row per step boundary.</summary>
    /// <param name="profile">The declared profile.</param>
    /// <returns><see langword="true"/> for every profile but <see cref="ExecutionProfile.Ephemeral"/>.</returns>
    /// <remarks>
    /// Expressed as "not <see cref="ExecutionProfile.Ephemeral"/>" rather than as a list of the
    /// profiles that journal, which is the safe direction for this to be wrong in: a profile
    /// added later and forgotten here gets a journal it does not need rather than silently losing
    /// one it does. B2's hard zero is unaffected either way — this is one integer comparison on a
    /// path that already made it.
    /// </remarks>
    public static bool IsJournaled(ExecutionProfile profile) => profile != ExecutionProfile.Ephemeral;
}
