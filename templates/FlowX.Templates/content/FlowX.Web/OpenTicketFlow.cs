using FlowX;

namespace FlowXStarter;

/// <summary>Opens a support ticket: validate it, then record it.</summary>
/// <remarks>
/// <para>
/// This is the application's control flow. It expresses <em>order, condition and
/// recovery</em> and nothing else — no business rule, and no mention of HTTP, which is
/// why the same flow would run behind another transport by changing the attribute above
/// it rather than this method.
/// </para>
/// <para>
/// The class is <c>partial</c> because the compiled plan, the step dispatcher and the
/// projection are generated into its other half at build time, under
/// <c>obj/generated</c>, with line directives back to this file.
/// </para>
/// <para>
/// <strong>The <c>[HttpTrigger]</c> is read, not yet acted on.</strong> It reaches the
/// manifest, so the published contract states the address this flow answers on; the
/// registration that serves it is in <c>Program.cs</c> until the endpoint generator
/// lands. Keep the two in step.
/// </para>
/// </remarks>
[Flow("ticket.open", Version = "1.0.0", Profile = ExecutionProfile.Ephemeral, Owner = "support")]
[FlowDeadline("PT10S")]
[HttpTrigger("POST", "/api/v1/tickets", Idempotent = true)]
public sealed partial class OpenTicketFlow : Flow<OpenTicket, TicketOpened>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<OpenTicket, TicketOpened> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        // Add a third step and this becomes a saga: .Step<Notify>() after a
        // .Step<RecordTicket>().CompensateWith<DeleteTicket>() unwinds the write when
        // the notification fails.
        flow
            .Step<ValidateTicket>()
            .Step<RecordTicket>()
            .Return(ctx => ctx.Get<TicketOpened>());
    }
}
