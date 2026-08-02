using FlowX;

namespace Polling;

/// <summary>Hands the document to the OCR provider and keeps the job id it answers with.</summary>
/// <remarks>
/// <para>
/// The one step here with a real, expensive effect: it starts work on somebody else's
/// infrastructure that will run for hours. So it is the compensable one, and
/// <see cref="CancelOcrJob"/> is what closes the job when the flow later gives up — four hours
/// and fifty attempts after this returned, on a node that has no memory of it.
/// </para>
/// <para>
/// <c>Idempotent = true</c> is honest rather than decorative: the provider is keyed on the
/// idempotency key the invocation carries, so a re-dispatched upload joins the job it already
/// started instead of starting a second one. Two OCR jobs for one document is two bills and
/// two answers, and only one of them would ever be polled.
/// </para>
/// </remarks>
[Capability("ocr.upload", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "document.write",
    Idempotent = true, SideEffects = ["ocr-provider"])]
public sealed class UploadToOcr : ICapability<ProcessDocument, OcrJob>
{
    private readonly IOcrService _ocr;

    /// <summary>Creates the capability over the provider.</summary>
    public UploadToOcr(IOcrService ocr) => _ocr = ocr;

    /// <inheritdoc />
    public ValueTask<Result<OcrJob>> ExecuteAsync(
        ProcessDocument input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (string.IsNullOrWhiteSpace(input.DocumentId))
        {
            return ValueTask.FromResult(Result.Fail<OcrJob>(DocumentErrors.MissingDocument()));
        }

        var jobId = _ocr.Submit(input.DocumentId, input.Pages, ctx.IdempotencyKey);

        return ValueTask.FromResult(Result.Ok(new OcrJob(input.DocumentId, jobId)));
    }
}

/// <summary>Closes an OCR job the flow has given up on. The inverse of <see cref="UploadToOcr"/>.</summary>
/// <remarks>
/// Runs on the failure path, which for this flow means "the four hours ran out" far more often
/// than it means "something went wrong". It binds the <see cref="ProcessDocument"/> the upload
/// bound — a compensation is invoked with the input of the step it undoes — and finds the job
/// by document, because the id the upload returned is on the other side of a state bag that a
/// resumed unwind restores from the journal.
/// </remarks>
[Capability("ocr.cancel", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "document.write",
    Idempotent = true, SideEffects = ["ocr-provider"])]
public sealed class CancelOcrJob : ICapability<ProcessDocument, OcrCancellation>
{
    private readonly IOcrService _ocr;

    /// <summary>Creates the capability over the provider.</summary>
    public CancelOcrJob(IOcrService ocr) => _ocr = ocr;

    /// <inheritdoc />
    public ValueTask<Result<OcrCancellation>> ExecuteAsync(
        ProcessDocument input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        _ocr.Cancel(input.DocumentId);

        return ValueTask.FromResult(Result.Ok(new OcrCancellation(input.DocumentId)));
    }
}

/// <summary>Asks the provider where a job has got to. This is the step the flow polls.</summary>
/// <remarks>
/// <para>
/// <strong>A read, and <c>FLOWX1044</c> is why that matters here rather than merely being
/// tidy.</strong> A poll invokes this once per attempt, with one request's worth of input and
/// one idempotency key, until its condition holds — which is the repetition
/// <c>Idempotent = true</c> declares to be safe. The build refuses to poll a capability that
/// has not said so, on the argument <c>FLOWX1014</c> makes about a retry, only stronger: a
/// retry repeats after a failure, a poll repeats after every success.
/// </para>
/// <para>
/// It binds the <see cref="OcrJob"/> the upload produced and answers with an
/// <see cref="OcrStatus"/>, which is what the flow's <c>until:</c> reads. Nothing in it knows
/// it is being polled — no loop, no delay, no attempt counter. The waiting is the engine's,
/// and this is a function of a job id.
/// </para>
/// </remarks>
[Capability("ocr.status", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "document.read",
    Idempotent = true, SideEffects = ["ocr-provider"])]
public sealed class CheckOcrStatus : ICapability<OcrJob, OcrStatus>
{
    private readonly IOcrService _ocr;

    /// <summary>Creates the capability over the provider.</summary>
    public CheckOcrStatus(IOcrService ocr) => _ocr = ocr;

    /// <inheritdoc />
    public ValueTask<Result<OcrStatus>> ExecuteAsync(
        OcrJob input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        return ValueTask.FromResult(Result.Ok(
            new OcrStatus(input.JobId, _ocr.StateOf(input.JobId, ctx.UtcNow))));
    }
}

/// <summary>Reads the fields out of a finished job.</summary>
/// <remarks>
/// Runs only on the satisfied path, which is what the predicate buys: it binds an
/// <see cref="OcrStatus"/> that <em>is</em> terminal, and refuses one that finished without
/// text rather than extracting nothing and reporting success. A poll that fell through to this
/// step on a timeout — which is what an <c>.OnTimeout</c> block ending in anything but a
/// failure would cause — would run it against a job that never produced anything.
/// </remarks>
[Capability("document.extract", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "document.read",
    Idempotent = true, SideEffects = ["ocr-provider"])]
public sealed class ExtractFields : ICapability<OcrStatus, ExtractedFields>
{
    private readonly IOcrService _ocr;

    /// <summary>Creates the capability over the provider.</summary>
    public ExtractFields(IOcrService ocr) => _ocr = ocr;

    /// <inheritdoc />
    public ValueTask<Result<ExtractedFields>> ExecuteAsync(
        OcrStatus input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.State != OcrState.Completed)
        {
            return ValueTask.FromResult(
                Result.Fail<ExtractedFields>(DocumentErrors.OcrFailed(input.JobId)));
        }

        var read = _ocr.Fields(input.JobId);

        return ValueTask.FromResult(Result.Ok(new ExtractedFields(read.DocumentId, read.Values)));
    }
}

/// <summary>Puts a document nobody could read in front of a person.</summary>
/// <remarks>
/// <para>
/// The escalation, and it declares no compensation on purpose. Asking a human to look at
/// something is not an effect a later failure should reverse — the document is still one
/// nobody read, and withdrawing the request would leave it unprocessed with nothing pointing
/// at it. What <em>is</em> reversed when the block ends is the OCR job, by
/// <see cref="CancelOcrJob"/>, which is a different fact about a different system.
/// </para>
/// <para>
/// It binds the flow's own input rather than the last <see cref="OcrStatus"/>, because what a
/// person needs is the document — and because the last status on a timed-out poll says
/// <c>Running</c>, which is the least informative thing in the instance.
/// </para>
/// </remarks>
[Capability("document.escalate", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "document.write",
    Idempotent = true, SideEffects = ["review-queue"])]
public sealed class EscalateToManualReview : ICapability<ProcessDocument, ManualReview>
{
    private readonly IOcrService _ocr;

    /// <summary>Creates the capability over the review queue the provider stands in for.</summary>
    public EscalateToManualReview(IOcrService ocr) => _ocr = ocr;

    /// <inheritdoc />
    public ValueTask<Result<ManualReview>> ExecuteAsync(
        ProcessDocument input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var reason = $"OCR did not finish within {Waits.OcrBudget}.";

        _ocr.SendForReview(input.DocumentId, reason);

        return ValueTask.FromResult(Result.Ok(new ManualReview(input.DocumentId, reason)));
    }
}
