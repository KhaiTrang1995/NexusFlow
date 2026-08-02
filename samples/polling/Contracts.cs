using System.Text.Json.Serialization;
using FlowX;

namespace Polling;

/// <summary>The schedule and the budget this application polls on, named once.</summary>
/// <remarks>
/// <para>
/// Named properties rather than literals at the call site, for the reason
/// <c>samples/workflow</c>'s <c>Waits</c> gives: a duration is a business decision and belongs
/// where it can be read and changed without opening a flow. They also demonstrate the half of
/// the compiler's behaviour that matters — the generator copies the author's <em>expression</em>
/// into the plan rather than folding it, so <c>StepNode.PollInterval</c> and
/// <c>StepNode.PollTimeout</c> are whatever these properties are.
/// </para>
/// <para>
/// <strong><see cref="OcrBudget"/> is folded for the manifest and <see cref="OcrPolling"/> is
/// not, and that is not an omission.</strong> The timeout is structure — it says how long this
/// flow will keep trying before it escalates, which a consumer reading
/// <c>flowx.manifest.json</c> has a reason to compare between two versions. How often it asks
/// in the meantime is a tuning number, in exactly the sense
/// <c>ForEachOptions.MaxDegreeOfParallelism</c> is one, and the manifest has no field for
/// either.
/// </para>
/// </remarks>
public static class Waits
{
    /// <summary>
    /// How long to park between attempts: five seconds after the first, doubling to five
    /// minutes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Jittered, and that is the difference between this and a number picked for a demo. A
    /// hundred thousand documents accepted in the same minute and parked on the same
    /// undecorrelated schedule wake in the same second, and the provider that could not keep up
    /// with the originals gets the retries as a spike — so "waiting costs nothing" becomes a
    /// load test somebody else pays for.
    /// </para>
    /// <para>
    /// The shape is the point rather than the numbers: a job that finishes in thirty seconds is
    /// noticed within five, and one that takes four hours is asked about fifty times rather
    /// than two thousand.
    /// </para>
    /// </remarks>
    public static Backoff OcrPolling { get; } = Backoff.ExponentialJitter("PT5S", "PT5M");

    /// <summary>How long the polling goes on before the escalation runs. Four hours.</summary>
    /// <remarks>
    /// Measured from the instant the <em>first attempt committed</em>, not from this
    /// invocation's clock — so a document picked up by three different nodes over an afternoon
    /// has one budget rather than three fresh ones. That instant is a column on a committed
    /// row, which is what makes the bound survive the node that started the polling.
    /// </remarks>
    public static TimeSpan OcrBudget { get; } = TimeSpan.FromHours(4);
}

/// <summary>A document handed to the platform for processing.</summary>
/// <param name="DocumentId">Which document. Also what makes a re-posted request idempotent.</param>
/// <param name="ContentType">What the OCR provider is being asked to read.</param>
/// <param name="Pages">How many pages, which is roughly what decides how long it takes.</param>
public sealed record ProcessDocument(string DocumentId, string ContentType, int Pages);

/// <summary>The job the OCR provider accepted, and the id it answers about.</summary>
public sealed record OcrJob(string DocumentId, string JobId);

/// <summary>An OCR job this flow gave up on and closed.</summary>
/// <remarks>
/// A compensation returns a contract like any other capability — there is no <c>void</c> in
/// this model, because a step that reports nothing is a step whose journal row says only that
/// it happened.
/// </remarks>
public sealed record OcrCancellation(string DocumentId);

/// <summary>Where an OCR job has got to.</summary>
public enum OcrState
{
    /// <summary>Accepted and not started.</summary>
    Pending = 0,

    /// <summary>Being read.</summary>
    Running = 1,

    /// <summary>Finished, with text.</summary>
    Completed = 2,

    /// <summary>Finished, without text.</summary>
    Failed = 3,
}

/// <summary>One answer from the provider's status endpoint.</summary>
/// <param name="JobId">Which job was asked about.</param>
/// <param name="State">What it said.</param>
/// <remarks>
/// <strong>This contract is what the poll's <c>until:</c> reads, and it is journaled.</strong>
/// Every attempt commits a row and the state bag beside it, so the value a resumed instance
/// asks the predicate about is the last attempt's answer restored from the journal — which is
/// why it needs the same generated metadata a step's output does (<c>FLOWX1006</c>), and why
/// the flow does not have to go and look again to find out whether it is finished.
/// </remarks>
public sealed record OcrStatus(string JobId, OcrState State)
{
    /// <summary>Whether the provider has stopped working on this job, either way.</summary>
    /// <remarks>
    /// <para>
    /// <strong>Terminal, not successful.</strong> A failed job is one the polling should stop
    /// on: waiting four hours for a provider that has already given up is the failure the
    /// budget exists to bound, arrived at the slow way. What to do about the failure is a
    /// decision for the step after the poll, which is where it can be expressed.
    /// </para>
    /// <para>
    /// <c>[JsonIgnore]</c> because it is derived. Writing it into the journal would put a
    /// second copy of <see cref="State"/> in every row, and a restored instance would read the
    /// copy rather than recomputing it — which is a way for a redefinition of "terminal" to
    /// apply to new documents and not to the ones already in flight.
    /// </para>
    /// </remarks>
    [JsonIgnore]
    public bool IsTerminal => State is OcrState.Completed or OcrState.Failed;
}

/// <summary>The fields read out of a finished job.</summary>
public sealed record ExtractedFields(string DocumentId, IReadOnlyList<string> Values);

/// <summary>What a caller gets once the document has been processed.</summary>
public sealed record DocumentResult(string DocumentId, int FieldCount);

/// <summary>A document a human has been asked to look at.</summary>
public sealed record ManualReview(string DocumentId, string Reason);

/// <summary>Published once a document has been read and its fields extracted.</summary>
public sealed record DocumentProcessed(string DocumentId, int FieldCount);

/// <summary>Errors <c>document.process</c> can produce.</summary>
public static class DocumentErrors
{
    /// <summary>The request names no document.</summary>
    public static Error MissingDocument() =>
        new("document.missing_id", "The request names no document.", ErrorCategory.Validation);

    /// <summary>The OCR provider stopped without producing text.</summary>
    public static Error OcrFailed(string jobId) =>
        new Error(
            "document.ocr_failed",
            $"The OCR provider gave up on job '{jobId}'.",
            ErrorCategory.Unavailable)
            .With("jobId", jobId);

    /// <summary>The polling budget ran out with the job still unfinished.</summary>
    /// <remarks>
    /// <para>
    /// Raised by the flow's own <c>.OnTimeout</c> block rather than by the engine. The
    /// engine's <c>flow.poll_not_satisfied</c> is what a poll with no block declared ends with;
    /// a flow that declares one is saying "this is what happens instead", and what happens here
    /// is a human being asked to look, followed by a business failure.
    /// </para>
    /// <para>
    /// <strong>Ending the block with a failure is what cancels the OCR job.</strong> The unwind
    /// runs <c>ocr.cancel</c>, which was put on the compensation stack four hours and fifty
    /// attempts ago, on a node with no memory of it by then — rebuilt from the journal's
    /// committed rows by the same frontier scan that decides which attempts to skip. Falling
    /// through instead would run <c>document.extract</c> against a job that never produced
    /// anything.
    /// </para>
    /// </remarks>
    public static Error NotReadInTime() =>
        new Error(
            "document.not_read_in_time",
            $"The OCR job did not finish within {Waits.OcrBudget}; it has been sent for manual review.",
            ErrorCategory.Unavailable)
            .With("budget", Waits.OcrBudget);
}
