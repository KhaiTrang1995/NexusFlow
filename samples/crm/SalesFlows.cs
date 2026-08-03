using FlowX;

namespace Crm;

/// <summary>Prices an opportunity and writes the quote.</summary>
/// <remarks>
/// <strong><c>Idempotent</c> because the same request must not leave two quotes.</strong> A
/// browser that retried a slow POST would otherwise double a pipeline's value, and the number
/// somebody forecasts on is the number this writes.
/// </remarks>
[Flow("crm.quote.issue", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-sales")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/quotes", Idempotent = true)]
public sealed partial class IssueQuoteFlow : Flow<IssueQuote, QuoteIssued>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<IssueQuote, QuoteIssued> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<IssueQuoteForOpportunity>()
            .Return(ctx => ctx.Get<QuoteIssued>());
    }
}

/// <summary>
/// Approves a discount a representative could ask for but not grant.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The step's input is built from the caller, and that is the only reason this flow has
/// a projection.</strong> <see cref="ApproveDiscount"/> is what a manager sends — a quote id.
/// <see cref="ApproveQuoteDiscount"/> is what the capability is given, and it carries the
/// approver derived from <see cref="FlowContext.Principal"/>. Between the two sits the rule that
/// the body cannot say who is approving.
/// </para>
/// <para>
/// <strong>Who may reach it at all is <c>crm.discount.approve</c> on the step.</strong> The route
/// is authenticated and the step is what refuses a representative — so the same refusal applies
/// however the flow is reached, including from an agent.
/// </para>
/// </remarks>
[Flow("crm.quote.approve", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-sales")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/quotes/approvals", Idempotent = true)]
public sealed partial class ApproveDiscountFlow : Flow<ApproveDiscount, DiscountApproved>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<ApproveDiscount, DiscountApproved> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<ApproveQuoteDiscountCapability, ApproveQuoteDiscount>(
                ctx => new ApproveQuoteDiscount(ctx.Input.QuoteId, Approvers.Of(ctx.Principal)))
            .Return(ctx => ctx.Get<DiscountApproved>());
    }
}

/// <summary>Accepts an issued quote into an order.</summary>
/// <remarks>
/// <strong>Where the discount rule is enforced a second time.</strong> The approval decides who
/// may sign; this decides whether an unsigned quote may be ordered. They are the same threshold
/// asked at two moments, and the second is the one that commits the money.
/// </remarks>
[Flow("crm.order.place", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-sales")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/orders", Idempotent = true)]
public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlaced>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<PlaceOrder, OrderPlaced> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<PlaceOrderForQuote>()
            .Return(ctx => ctx.Get<OrderPlaced>());
    }
}

/// <summary>
/// Applies a trigger to an opportunity and announces it for the configured process.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the seam between §9's pipeline and §7's engine.</strong> A representative
/// presses "advance"; this flow says the opportunity is theirs and emits
/// <c>opportunity.stage.changed</c>. What the stage becomes — and what tasks, approvals and
/// notifications go with it — is read out of the definition by
/// <see cref="RunWorkflowTransitionFlow"/>, which no part of this flow knows about.
/// </para>
/// <para>
/// <strong><c>Durable</c> because it emits.</strong> The event is staged in the step's own
/// transaction, so an opportunity a caller was told had advanced cannot exist without the change
/// that advances it.
/// </para>
/// </remarks>
[Flow("crm.opportunity.advance", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-sales")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/opportunities/triggers", Idempotent = true)]
public sealed partial class AdvanceOpportunityFlow : Flow<AdvanceOpportunity, OpportunityAdvanced>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<AdvanceOpportunity, OpportunityAdvanced> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<ApplyOpportunityTrigger>()
            .Emit<OpportunityStageChanged>(ctx => new OpportunityStageChanged(
                ctx.Input.OpportunityId,
                ctx.Input.Trigger))
            .Return(ctx => ctx.Get<OpportunityAdvanced>());
    }
}
