using FlowX;

namespace AiAgent;

/// <summary>
/// Issues a refund against a ticket: find it, then reverse the charge.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The two attributes below are the whole of this flow's agent integration, and the
/// second one is one line.</strong> The tool's name is the flow id with <c>.</c> replaced by
/// <c>_</c>; its description is the attribute's; its declared consequences are
/// <c>payment.refund</c>'s <c>SideEffects</c>; its required permission is that capability's
/// stance; and <c>confirmationRequired</c> is the mode below resolved against those consequences.
/// All five are read out of <c>flowx.manifest.json</c> at run time, so what a model is told and
/// what the build published are one document.
/// </para>
/// <para>
/// <strong>The <c>[HttpTrigger]</c> is here so that the equality can be asserted rather than
/// asserted about.</strong> <c>tests/AiAgent.Tests</c> sends the same under-privileged token down
/// both routes and compares the refusal's code, in one process, against one plan. That is
/// docs/13 §6's first safety property — "an agent cannot exceed a human's permissions" — checked
/// instead of restated.
/// </para>
/// <para>
/// <strong>Ephemeral, and this sample is the case where that is not a compromise.</strong> The
/// flow has one effectful step and nothing after it, so there is nothing to unwind and no
/// compensation to lose on a crash: FLOWX1012 does not fire, and <c>dotnet run</c> needs no
/// database.
/// </para>
/// </remarks>
[Flow("ticket.refund", Version = "1.0.0", Profile = ExecutionProfile.Ephemeral, Owner = "support")]
[FlowDeadline("PT30S")]
[HttpTrigger("POST", "/api/v1/refunds", Idempotent = true)]
[AgentTrigger(
    Description =
        "Issue a refund against a support ticket. Reverses the full amount charged on the " +
        "ticket at the payment provider and writes the reversal to the ledger. Not reversible " +
        "and not safe to repeat.",
    Confirmation = ConfirmationMode.RequiredForSideEffects)]
public sealed partial class IssueRefundFlow : Flow<IssueRefund, RefundIssued>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<IssueRefund, RefundIssued> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<LoadTicket>()
            .Step<RefundPayment>()
            .Return(ctx => new RefundIssued(
                ctx.Get<Refund>().TicketId,
                ctx.Get<Refund>().Amount,
                ctx.Get<Refund>().RefundReference));
    }
}

/// <summary>
/// Finds tickets. The read half of the desk, and the tool an agent may call freely.
/// </summary>
/// <remarks>
/// <para>
/// <strong><c>ConfirmationMode.Never</c> is written out rather than left to the default, and it
/// is legal here for exactly one reason: no capability this flow runs declares a side effect.</strong>
/// Writing it on a flow that reached <c>payment.refund</c> would be <c>FLOWX1046</c>, because the
/// descriptor would then publish <c>confirmationRequired: false</c> for a call that moves money
/// and no client would prompt. The rule is what makes this line safe to read as a decision rather
/// than as an oversight.
/// </para>
/// <para>
/// It is also what a deployment enforcing confirmation needs in order to be usable: under
/// <c>ConfirmationPolicy.Elicit</c> this tool still runs on the first call, because the
/// requirement is computed from the flow's declared consequences and this flow has none.
/// </para>
/// </remarks>
[Flow("ticket.search", Version = "1.0.0", Profile = ExecutionProfile.Ephemeral, Owner = "support")]
[FlowDeadline("PT10S")]
[AgentTrigger(
    Description =
        "Find support tickets whose subject matches a query. Returns matching ticket ids and " +
        "nothing else. Reads only.",
    Confirmation = ConfirmationMode.Never)]
public sealed partial class SearchTicketsFlow : Flow<SearchTickets, TicketMatches>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<SearchTickets, TicketMatches> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<SearchTicketDesk>()
            .Return(ctx => ctx.Get<TicketMatches>());
    }
}

/// <summary>
/// Reviews this application from its own manifest, and can ask the caller's model to write it up.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A tool because it is a flow, which is the only way anything becomes a tool here.</strong>
/// <c>McpServer</c> refuses at start-up any binding the manifest does not publish, so a
/// review capability bolted onto the transport as a built-in tool could not exist — it would be
/// a name reachable by an agent that <c>flowx diff</c> never sees. Making it a flow means the
/// review is authorised by <c>ops.read</c> like anything else, traced like anything else, and
/// visible in the manifest like anything else.
/// </para>
/// <para>
/// <strong>And it is what carries the sampling claim.</strong> With <c>narrate: true</c> the
/// capability asks the calling agent's own model over <c>sampling/createMessage</c> — so
/// <c>FlowX.Ai</c> holds no API key, this deployment makes no egress to a model vendor, and
/// docs/13 §5's <c>IAiProvider</c> does not need to exist
/// (<a href="../../docs/adr/ADR-0060-the-server-asks-the-caller-for-what-it-does-not-have.md">ADR-0060</a>).
/// </para>
/// </remarks>
[Flow("ops.review", Version = "1.0.0", Profile = ExecutionProfile.Ephemeral, Owner = "platform")]
[FlowDeadline("PT60S")]
[AgentTrigger(
    Description =
        "Review this application's architecture from its build manifest and report what the " +
        "document proves about it: orphaned events, unauthorised side effects, partially " +
        "compensated sagas, and agent tools that ask for no confirmation. Reads no source code " +
        "and no business data. Set narrate to have your own model write the report up.")]
public sealed partial class ReviewApplicationFlow : Flow<ReviewApplicationRequest, ApplicationReview>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<ReviewApplicationRequest, ApplicationReview> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<ReviewApplication>()
            .Return(ctx => ctx.Get<ApplicationReview>());
    }
}
