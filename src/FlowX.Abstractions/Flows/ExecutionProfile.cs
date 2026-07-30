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
    Streaming = 2,
}
