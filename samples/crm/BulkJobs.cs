using FlowX;

namespace Crm;

// -------------------------------------------------------------------------------- what is asked

/// <summary>Submits rows to be imported.</summary>
/// <param name="Target">Which object they become records of.</param>
/// <param name="Rows">The rows, in the order they are to be numbered.</param>
/// <remarks>
/// <strong>Submitted, not performed.</strong> Ten thousand rows is not a request: doing it inline
/// holds one connection and one transaction for as long as the slowest row takes, and a client
/// that times out cannot tell a slow import from a lost one.
/// </remarks>
public sealed record SubmitImport(
    Guid Target,
    IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows);

/// <summary>Submits a request for an object's records as a document.</summary>
/// <param name="Target">Which object.</param>
/// <param name="Filter">Which records, or null for all of them.</param>
public sealed record SubmitExport(Guid Target, RecordFilter? Filter);

/// <summary>Asks how a job is getting on.</summary>
/// <param name="JobId">Which job.</param>
public sealed record ReadJob(Guid JobId);

/// <summary>What a bulk capability is given, once the flow has read the caller.</summary>
/// <param name="Import">The import, when that is what was submitted.</param>
/// <param name="Export">The export, when that is what was submitted.</param>
/// <param name="Scopes">
/// The grants the caller holds. <strong>Frozen onto the job</strong>, because the sweep that runs
/// it later must decide on the submitter's authority and never on its own — a sweeper running as
/// itself would let anyone export a field they cannot read by asking a background process to
/// read it.
/// </param>
public sealed record SubmitJob(
    SubmitImport? Import,
    SubmitExport? Export,
    IReadOnlyList<string> Scopes);

/// <summary>What the job-reading capability is given, once the flow has read the caller.</summary>
/// <param name="Request">Which job.</param>
/// <param name="Scopes">The grants the caller holds, for what an export may show them.</param>
public sealed record ReadJobFor(ReadJob Request, IReadOnlyList<string> Scopes);

// ------------------------------------------------------------------------------- what comes back

/// <summary>The job was accepted.</summary>
/// <param name="JobId">What to ask about it by.</param>
/// <param name="Total">How much work it is.</param>
public sealed record JobSubmitted(Guid JobId, int Total);

/// <summary>One row an import refused, and why.</summary>
/// <param name="Ordinal">Its position in what was submitted, counting from zero.</param>
/// <param name="Message">What was wrong with it.</param>
/// <remarks>
/// <strong>The ordinal is the point.</strong> An import that reports "3 rows failed" and not which
/// three is an import somebody redoes by hand.
/// </remarks>
public sealed record JobRowError(int Ordinal, string Message);

/// <summary>How a job is getting on.</summary>
/// <param name="JobId">Which job.</param>
/// <param name="Kind"><c>Import</c> or <c>Export</c>.</param>
/// <param name="Status"><c>Pending</c>, <c>Succeeded</c> or <c>Failed</c>.</param>
/// <param name="Total">How much work it is.</param>
/// <param name="Processed">How much is dealt with. Durable, so a resumed job does not go back.</param>
/// <param name="Failed">
/// How many rows were refused. <strong>A job with refused rows still succeeds:</strong> ten
/// thousand rows with three bad ones is a spreadsheet with three bad lines, not a failed import,
/// and refusing the batch would make the caller resubmit the 9 997 that were fine.
/// </param>
/// <param name="Errors">Which rows were refused, and why.</param>
/// <param name="Rows">An export's records, redacted for the submitter. Null until it has run.</param>
public sealed record JobStatus(
    Guid JobId,
    string Kind,
    string Status,
    int Total,
    int Processed,
    int Failed,
    IReadOnlyList<JobRowError> Errors,
    IReadOnlyList<RecordView>? Rows);

/// <summary>What one pass of the job sweep did.</summary>
/// <param name="Advanced">How many rows it dealt with.</param>
/// <param name="Finished">How many jobs reached a terminal status.</param>
/// <param name="Abandoned">How many were given up on, having failed too many times.</param>
/// <param name="OccurrenceAt">The occurrence this pass was.</param>
public sealed record JobsSwept(int Advanced, int Finished, int Abandoned, DateTimeOffset OccurrenceAt);

// ------------------------------------------------------------------------------- what can go wrong

/// <summary>Refusals the bulk surface can produce.</summary>
public static class BulkErrors
{
    /// <summary>The submission named an import and an export, or neither.</summary>
    public static Error AskForOneOrTheOther() =>
        new(
            "crm.bulk_ambiguous",
            "A submission is an import or an export, and this was neither or both.",
            ErrorCategory.Validation);

    /// <summary>More rows than one job carries.</summary>
    /// <param name="rows">How many were sent.</param>
    /// <returns>The refusal.</returns>
    public static Error TooManyRows(int rows) =>
        new(
            "crm.bulk_too_many_rows",
            $"A job carries at most {BulkLimits.MaxRows} rows, and {rows} were sent. " +
            "Submit them as more than one job.",
            ErrorCategory.Validation);

    /// <summary>No rows at all.</summary>
    public static Error NoRows() =>
        new(
            "crm.bulk_no_rows",
            "An import of no rows is a job that would succeed having done nothing.",
            ErrorCategory.Validation);

    /// <summary>The job is not one this tenant has.</summary>
    /// <param name="jobId">What was asked for.</param>
    /// <returns>The refusal.</returns>
    public static Error JobNotFound(Guid jobId) =>
        new(
            "crm.bulk_job_not_found",
            $"Job '{jobId}' is not one this tenant submitted.",
            ErrorCategory.NotFound);
}

/// <summary>What the bulk surface will carry.</summary>
public static class BulkLimits
{
    /// <summary>The most rows one import carries.</summary>
    /// <remarks>
    /// The whole submission is one <c>jsonb</c> value and one request body, so this is bounded by
    /// what is reasonable to hold in memory twice rather than by the sweep, which never reads more
    /// than a chunk.
    /// </remarks>
    public const int MaxRows = 5_000;

    /// <summary>The most records one export returns.</summary>
    public const int MaxExported = 5_000;

    /// <summary>How many rows one pass of the sweep deals with.</summary>
    /// <remarks>
    /// Bounded so that a large job yields between chunks rather than holding a sweep against every
    /// other tenant's queue — the same argument the connector sweep's batch makes.
    /// </remarks>
    public const int Chunk = 100;

    /// <summary>How many times a chunk may fail before the job is given up on.</summary>
    /// <remarks>
    /// A job that cannot make progress stops rather than being retried until somebody notices the
    /// queue. The counter is reset by a chunk that succeeds, so a long job is not condemned by
    /// having once been unlucky.
    /// </remarks>
    public const int MaxAttempts = 5;
}
