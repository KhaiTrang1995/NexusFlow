namespace FlowX;

/// <summary>
/// What a journaled flow instance is doing, as recorded on its <c>flow_instance</c> row.
/// </summary>
/// <remarks>
/// The set is closed and matches the <c>CHECK</c> constraint in
/// <c>docs/11-Distributed-Runtime.md §2</c>. Adding a member changes what a recovery scan
/// has to understand and what a retention policy has to keep, so it is a schema change
/// rather than an enum edit.
/// </remarks>
public enum FlowInstanceState
{
    /// <summary>Recorded, not yet started. The zero value: an instance exists before it runs.</summary>
    Pending = 0,

    /// <summary>A node holds the lease and is executing steps.</summary>
    Running = 1,

    /// <summary>Waiting for a signal, a timer or a child flow. Retained until it completes or its deadline passes.</summary>
    Suspended = 2,

    /// <summary>A step failed and the recorded compensations are unwinding.</summary>
    Compensating = 3,

    /// <summary>Finished successfully.</summary>
    Completed = 4,

    /// <summary>Finished with a business or technical failure, after compensating cleanly.</summary>
    Failed = 5,

    /// <summary>The flow deadline passed before it finished.</summary>
    TimedOut = 6,

    /// <summary>
    /// A compensation itself failed and exhausted its retries.
    /// </summary>
    /// <remarks>
    /// The one terminal state with no automatic resolution: two systems now disagree about
    /// the same business fact and an operator has to decide. FlowX makes it visible rather
    /// than pretending otherwise (<c>docs/11-Distributed-Runtime.md §8</c>).
    /// </remarks>
    CompensationFailed = 7,
}

/// <summary>
/// How one attempt at one step ended, as recorded on its <c>flow_step</c> row.
/// </summary>
public enum JournalOutcome
{
    /// <summary>The step produced a value.</summary>
    Success = 0,

    /// <summary>The step produced an error.</summary>
    Failure = 1,

    /// <summary>The step's compensation ran and undid it.</summary>
    Compensated = 2,
}
