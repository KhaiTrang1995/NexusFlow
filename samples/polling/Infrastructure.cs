using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Serialization;

namespace Polling;

/// <summary>Source-generated serialisation for the contracts that cross a boundary.</summary>
/// <remarks>
/// <para>
/// <strong><see cref="DocumentProcessed"/> is here for a reason the others are not.</strong> An
/// <c>Emit</c> step's body is written through a source-generated <c>JsonSerializerContext</c>
/// and never by reflection, so membership is a build requirement rather than a convention —
/// <c>FLOWX1024</c> otherwise.
/// </para>
/// <para>
/// <strong>The rest are here for <c>FLOWX1006</c>, and <see cref="OcrStatus"/> is the one that
/// makes the poll work.</strong> This flow is <c>Durable</c>, so every attempt commits a row
/// and the state bag beside it; the value a resumed instance asks <c>until:</c> about is that
/// snapshot restored. Without this line the last attempt's answer would live in one
/// invocation's memory, and the poll would ask the predicate about a bag that has no
/// <c>OcrStatus</c> in it — which is <c>flow.selector_failed</c> on the first resume rather
/// than an unbounded loop, but is a defect either way.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ProcessDocument))]
[JsonSerializable(typeof(DocumentResult))]
[JsonSerializable(typeof(DocumentProcessed))]
[JsonSerializable(typeof(OcrJob))]
[JsonSerializable(typeof(OcrCancellation))]
[JsonSerializable(typeof(OcrStatus))]
[JsonSerializable(typeof(ExtractedFields))]
[JsonSerializable(typeof(ManualReview))]
internal sealed partial class PollingJsonContext : JsonSerializerContext;

/// <summary>What the fields of a finished job look like when they come back.</summary>
/// <param name="DocumentId">Which document was read.</param>
/// <param name="Values">What was read out of it.</param>
public sealed record OcrRead(string DocumentId, IReadOnlyList<string> Values);

/// <summary>
/// The OCR provider, as the capabilities see it: submit, ask, read, cancel.
/// </summary>
/// <remarks>
/// Four methods and no notion of waiting, because waiting is not the provider's problem and it
/// is not the capabilities'. A job id and a status endpoint is what a real third-party OCR
/// service gives you, and it is the whole reason this sample exists.
/// </remarks>
public interface IOcrService
{
    /// <summary>Submits a document and returns the job id the provider assigned.</summary>
    /// <param name="documentId">Which document.</param>
    /// <param name="pages">How many pages, which is what decides how long it takes.</param>
    /// <param name="idempotencyKey">
    /// What makes a re-dispatched upload join the job it already started rather than starting
    /// a second one.
    /// </param>
    string Submit(string documentId, int pages, string idempotencyKey);

    /// <summary>Where a job has got to, as of an instant the caller supplies.</summary>
    /// <param name="jobId">Which job.</param>
    /// <param name="asOf">
    /// The instant to answer for, read from <c>CapabilityContext.UtcNow</c> — the seam that
    /// journals a clock reading rather than taking one ambiently (<c>FLOWX1007</c>). It is what
    /// lets a test move a virtual clock and have this stand-in answer accordingly.
    /// </param>
    OcrState StateOf(string jobId, DateTimeOffset asOf);

    /// <summary>The fields of a finished job.</summary>
    OcrRead Fields(string jobId);

    /// <summary>Closes a job the flow gave up on. A no-op on a job that has already finished.</summary>
    void Cancel(string documentId);

    /// <summary>Asks a person to look at a document nobody could read.</summary>
    void SendForReview(string documentId, string reason);
}

/// <summary>
/// An OCR provider that finishes a job a fixed time after it was submitted.
/// </summary>
/// <remarks>
/// <para>
/// The capabilities depend on <see cref="IOcrService"/>, not on this. It answers from the
/// instant it is handed rather than from a clock of its own, so a test that moves a virtual
/// clock four hours forward sees exactly what an application that waited four hours would —
/// which is what makes "waiting costs nothing" measurable in milliseconds.
/// </para>
/// <para>
/// How long a job takes is a function of its page count, which is arbitrary and is meant to be:
/// what matters is that it is a duration the flow does not know and cannot shorten.
/// </para>
/// </remarks>
internal sealed class InMemoryOcrService : IOcrService
{
    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _byDocument = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _reviews = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly TimeSpan _perPage;

    /// <summary>Creates a provider that takes <paramref name="perPage"/> for each page.</summary>
    /// <param name="time">Where a submission's own instant comes from.</param>
    /// <param name="perPage">How long one page takes.</param>
    public InMemoryOcrService(TimeProvider time, TimeSpan perPage)
    {
        _time = time;
        _perPage = perPage;
    }

    /// <summary>Every document a person has been asked to look at, and why.</summary>
    public IReadOnlyDictionary<string, string> Reviews => _reviews;

    /// <summary>Whether a job is still open — that is, submitted and neither finished nor cancelled.</summary>
    public bool IsOpen(string documentId) =>
        _byDocument.TryGetValue(documentId, out var jobId) &&
        _jobs.TryGetValue(jobId, out var job) &&
        !job.Cancelled;

    /// <inheritdoc />
    public string Submit(string documentId, int pages, string idempotencyKey)
    {
        // Keyed on the idempotency key, which is what `Idempotent = true` on `ocr.upload`
        // promises: a step re-dispatched after a crash joins the job it already started.
        var jobId = "job-" + idempotencyKey;

        _jobs.GetOrAdd(jobId, _ => new Job(documentId, _time.GetUtcNow() + (_perPage * Math.Max(pages, 1))));
        _byDocument[documentId] = jobId;

        return jobId;
    }

    /// <inheritdoc />
    public OcrState StateOf(string jobId, DateTimeOffset asOf)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            return OcrState.Failed;
        }

        if (job.Cancelled)
        {
            return OcrState.Failed;
        }

        return asOf >= job.DueAt ? OcrState.Completed : OcrState.Running;
    }

    /// <inheritdoc />
    public OcrRead Fields(string jobId) =>
        _jobs.TryGetValue(jobId, out var job)
            ? new OcrRead(job.DocumentId, Read(job.DocumentId))
            : new OcrRead(jobId, []);

    /// <inheritdoc />
    public void Cancel(string documentId)
    {
        if (_byDocument.TryGetValue(documentId, out var jobId) &&
            _jobs.TryGetValue(jobId, out var job))
        {
            _jobs[jobId] = job with { Cancelled = true };
        }
    }

    /// <inheritdoc />
    public void SendForReview(string documentId, string reason) => _reviews[documentId] = reason;

    private static string[] Read(string documentId) =>
    [
        "document=" + documentId,
        "read-at=" + DateTimeOffset.UnixEpoch.ToString("O", CultureInfo.InvariantCulture),
        "confidence=0.97",
    ];

    private sealed record Job(string DocumentId, DateTimeOffset DueAt)
    {
        public bool Cancelled { get; init; }
    }
}
