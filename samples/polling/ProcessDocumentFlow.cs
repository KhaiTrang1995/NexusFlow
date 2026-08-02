using FlowX;

namespace Polling;

/// <summary>
/// Hands a document to an OCR provider, <strong>waits for it</strong> — for anything between
/// thirty seconds and four hours — and reads the fields out when it is done.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The claim this flow exists to prove is about what the waiting costs.</strong>
/// Between two attempts the instance is <see cref="FlowInstanceState.Suspended"/>: one row
/// carrying the instant it is next due, no lease, no pooled context, no thread anywhere in the
/// cluster. A hundred thousand documents in flight are a hundred thousand rows and no compute.
/// The three ordinary answers all cost more than that — a <c>while (!done) await
/// Task.Delay(5s)</c> is a thread and a container per document and loses every one on the next
/// deployment; a recurring job scanning a table polls everything on one schedule; a webhook
/// alone has no fallback for the day the provider's webhook fails.
/// </para>
/// <para>
/// <strong>Nothing in <c>ocr.status</c> knows it is being polled.</strong> It is a function of
/// a job id with no loop, no delay and no attempt counter in it. The looping is the engine's:
/// <c>PollUntil</c> is a node in the compiled plan whose body is that one capability, re-entered
/// once per attempt with the attempt number as its journal scope — the same mechanism a
/// <c>ForEach</c> uses for its elements, which is why the plan has no backward target in it and
/// the flow still terminates for the reason every other flow does.
/// </para>
/// <para>
/// <strong>The escalation ends in a <c>.Fail</c>, and that is the part worth reading
/// twice.</strong> Both paths out of a poll rejoin at the step after the block, exactly as the
/// two arms of a <c>.When</c> do — so a block that merely escalated and fell through would run
/// <c>document.extract</c> against a job that never produced anything. Ending it with a failure
/// is also what cancels the OCR job: <c>ocr.cancel</c> went on the compensation stack four
/// hours and fifty attempts earlier, on a node with no memory of it by then, and the resumed
/// flow rebuilds the stack from the journal's committed rows.
/// </para>
/// <para>
/// <strong><c>Durable</c> is not decoration and <c>FLOWX1017</c> says so.</strong> A poll parks
/// between attempts and reads which attempt it is on out of the journal that parked it; outside
/// one it has neither anywhere to record when the next call is due nor any way to count the ones
/// already made, which leaves a hot loop. Change the profile and the build fails naming the
/// construct.
/// </para>
/// </remarks>
[Flow("document.process", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "documents")]
[HttpTrigger("POST", "/api/v1/documents", Idempotent = true)]
[FlowDeadline("PT6H")]
public sealed partial class ProcessDocumentFlow : Flow<ProcessDocument, DocumentResult>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<ProcessDocument, DocumentResult> flow)
    {
        // CA1062, answered the way every other flow in this repository answers it: `Define` is
        // a protected override the framework calls, so the parameter cannot be null in
        // practice, and a guard is cheaper than an exemption to justify again next time.
        ArgumentNullException.ThrowIfNull(flow);

        flow
            // The one expensive effect, and therefore the one compensable step. Its inverse
            // has to survive four hours and every deployment that happens in them.
            .Step<UploadToOcr>().CompensateWith<CancelOcrJob>()

            // The poll. `ocr.status` runs, `until:` is asked about what it produced, and if the
            // answer is no the instance parks until the next attempt is due — which is where
            // the invocation returns and where the flow costs nothing.
            .PollUntil<CheckOcrStatus>(
                until: ctx => ctx.Get<OcrStatus>().IsTerminal,
                interval: Waits.OcrPolling,
                timeout: Waits.OcrBudget)

                // And this is what happens when four hours go by. It runs at the index after
                // the attempt — the escalation is the contiguous block, because the other path
                // out of a poll is the rest of the flow — and the failure it ends with is what
                // closes the OCR job.
                .OnTimeout(f => f
                    .Step<EscalateToManualReview>()
                    .Fail(DocumentErrors.NotReadInTime()))

            // Reached only on the satisfied path, and it binds the last attempt's status: the
            // one the predicate said was terminal.
            .Step<ExtractFields>()

            .Emit<DocumentProcessed>(ctx => new DocumentProcessed(
                ctx.Input.DocumentId,
                ctx.Get<ExtractedFields>().Values.Count))

            .Return(ctx => new DocumentResult(
                ctx.Input.DocumentId,
                ctx.Get<ExtractedFields>().Values.Count));
    }
}
