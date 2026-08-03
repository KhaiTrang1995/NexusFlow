using FlowX;

namespace Crm;

/// <summary>
/// Prices a set of lines and writes the quote.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It never refuses a discount, and that is the point.</strong> A representative may ask
/// for any discount at all; what the threshold decides is whether the quote is <c>Issued</c> or
/// sits as a <c>Draft</c> until somebody who holds <c>crm.discount.approve</c> signs it. Refusing
/// here would make the rule a validation, and a validation is not an authorisation — the caller
/// would be told "no" for a thing that is allowed, just not by them.
/// </para>
/// <para>
/// <strong>The arithmetic is <see cref="Pricing"/>'s and it is pure.</strong> This capability
/// reads one row, calls a function, and writes what it returned.
/// </para>
/// </remarks>
[Capability("crm.quote.issue", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.write",
    Idempotent = true,
    SideEffects = ["crm.quote.written"])]
public sealed class IssueQuoteForOpportunity : ICapability<IssueQuote, QuoteIssued>
{
    private readonly SalesStore _store;

    /// <summary>Creates the capability.</summary>
    /// <param name="store">Writes the quote and its lines.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public IssueQuoteForOpportunity(SalesStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <inheritdoc />
    public async ValueTask<Result<QuoteIssued>> ExecuteAsync(
        IssueQuote input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var priced = Pricing.Price(input.Lines, input.Discount);

        if (!priced.IsSuccess)
        {
            return Result.Fail<QuoteIssued>(priced.Error!);
        }

        if (await _store.ReadAccountAsync(ctx.TenantId, input.OpportunityId, ct).ConfigureAwait(false) is null)
        {
            return Result.Fail<QuoteIssued>(SalesErrors.OpportunityNotFound(input.OpportunityId));
        }

        var quoteId = ctx.NewId();

        var status = await _store.InsertQuoteAsync(
            ctx.TenantId,
            quoteId,
            input.OpportunityId,
            priced.Value!,
            input.Lines,
            ctx.UtcNow.AddDays(input.ValidForDays),
            ct).ConfigureAwait(false);

        return Result.Ok(new QuoteIssued(
            quoteId, priced.Value!.Total, status, priced.Value.NeedsApproval));
    }
}

/// <summary>
/// Signs off a discount somebody asked for.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the sample's second authorisation stance and the whole of package 9's
/// "done when".</strong> <c>crm.discount.approve</c> is a grant a manager's token carries and a
/// representative's does not. Nothing in this class checks it: the engine refuses the step
/// before <see cref="ExecuteAsync"/> is entered, from the claims the trigger authenticated. A
/// permission checked inside a capability is a permission somebody can forget to check.
/// </para>
/// <para>
/// <strong>Who approved comes from the flow, which read it off the caller.</strong> The request
/// over HTTP carries a quote id and nothing else; <see cref="ApproveQuoteDiscount.ApprovedBy"/>
/// is put there by <see cref="ApproveDiscountFlow"/> from
/// <see cref="FlowContext.Principal"/>. A body field would let a representative name a manager.
/// </para>
/// </remarks>
[Capability("crm.quote.approve_discount", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.discount.approve",
    Idempotent = true,
    SideEffects = ["crm.quote.written"])]
public sealed class ApproveQuoteDiscountCapability : ICapability<ApproveQuoteDiscount, DiscountApproved>
{
    private readonly SalesStore _store;

    /// <summary>Creates the capability.</summary>
    /// <param name="store">Reads the quote and stamps the approver.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public ApproveQuoteDiscountCapability(SalesStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <inheritdoc />
    public async ValueTask<Result<DiscountApproved>> ExecuteAsync(
        ApproveQuoteDiscount input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (await _store.ReadQuoteAsync(ctx.TenantId, input.QuoteId, ct).ConfigureAwait(false)
            is not { } quote)
        {
            return Result.Fail<DiscountApproved>(SalesErrors.QuoteNotFound(input.QuoteId));
        }

        if (quote.ApprovedBy is { } already)
        {
            // A second approval of the same quote is the first one's answer again. Two managers
            // pressing approve is a race, not a conflict, and the row says who won it.
            return Result.Ok(new DiscountApproved(quote.Id, already, ctx.UtcNow));
        }

        if (quote.Status != QuoteStatus.Draft)
        {
            return Result.Fail<DiscountApproved>(
                SalesErrors.QuoteIsNotIn(quote.Id, quote.Status, QuoteStatus.Draft));
        }

        await _store.ApproveAsync(ctx.TenantId, quote.Id, input.ApprovedBy, ct).ConfigureAwait(false);

        return Result.Ok(new DiscountApproved(quote.Id, input.ApprovedBy, ctx.UtcNow));
    }
}

/// <summary>
/// Turns an issued quote into an order.
/// </summary>
/// <remarks>
/// <strong>It re-asks whether the discount needed approving.</strong> The threshold is a
/// constant this build carries, and a quote issued by an older build — or before the constant
/// moved — could be sitting at <c>Issued</c> with a discount this build would have held back.
/// Reading the stored subtotal and discount and asking <see cref="DiscountPolicy"/> again is
/// cheap, and it is the moment the money is actually committed.
/// </remarks>
[Capability("crm.order.place", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.write",
    Idempotent = true,
    SideEffects = ["crm.order.written", "crm.quote.written"])]
public sealed class PlaceOrderForQuote : ICapability<PlaceOrder, OrderPlaced>
{
    private readonly SalesStore _store;

    /// <summary>Creates the capability.</summary>
    /// <param name="store">Reads the quote and writes the order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public PlaceOrderForQuote(SalesStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <inheritdoc />
    public async ValueTask<Result<OrderPlaced>> ExecuteAsync(
        PlaceOrder input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (await _store.ReadQuoteAsync(ctx.TenantId, input.QuoteId, ct).ConfigureAwait(false)
            is not { } quote)
        {
            return Result.Fail<OrderPlaced>(SalesErrors.QuoteNotFound(input.QuoteId));
        }

        var orderId = SalesIds.Order(quote.Id);

        if (quote.Status == QuoteStatus.Accepted)
        {
            // Already ordered. The order's id is derived from the quote, so the answer to a
            // repeat is the same order rather than a conflict.
            return Result.Ok(new OrderPlaced(orderId, quote.Id, new Money(quote.Total, quote.Currency)));
        }

        if (quote.Status != QuoteStatus.Issued)
        {
            return Result.Fail<OrderPlaced>(
                SalesErrors.QuoteIsNotIn(quote.Id, quote.Status, QuoteStatus.Issued));
        }

        if (quote.ValidUntil <= ctx.UtcNow)
        {
            return Result.Fail<OrderPlaced>(SalesErrors.QuoteHasExpired(quote.Id, quote.ValidUntil));
        }

        if (DiscountPolicy.NeedsApproval(quote.Subtotal, quote.Discount) && quote.ApprovedBy is null)
        {
            return Result.Fail<OrderPlaced>(SalesErrors.DiscountIsNotApproved(quote.Id));
        }

        await _store.PlaceOrderAsync(ctx.TenantId, quote, orderId, ctx.UtcNow, ct).ConfigureAwait(false);

        return Result.Ok(new OrderPlaced(orderId, quote.Id, new Money(quote.Total, quote.Currency)));
    }
}

/// <summary>
/// Applies a trigger to an opportunity, and lets the configured process decide what it means.
/// </summary>
/// <remarks>
/// <strong>It moves nothing.</strong> Where the opportunity goes is
/// <see cref="RunConfiguredTransition"/>'s answer, read out of the definition an administrator
/// wrote. What this does is check the opportunity is one this tenant has and record that
/// somebody applied the trigger; the event <see cref="AdvanceOpportunityFlow"/> emits is what
/// the change feed hands to §7's engine.
/// </remarks>
[Capability("crm.opportunity.advance", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.write",
    Idempotent = true)]
public sealed class ApplyOpportunityTrigger : ICapability<AdvanceOpportunity, OpportunityAdvanced>
{
    private readonly SalesStore _store;

    /// <summary>Creates the capability.</summary>
    /// <param name="store">Reads the opportunity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public ApplyOpportunityTrigger(SalesStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <inheritdoc />
    public async ValueTask<Result<OpportunityAdvanced>> ExecuteAsync(
        AdvanceOpportunity input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (await _store.ReadAccountAsync(ctx.TenantId, input.OpportunityId, ct).ConfigureAwait(false) is null)
        {
            return Result.Fail<OpportunityAdvanced>(
                SalesErrors.OpportunityNotFound(input.OpportunityId));
        }

        return Result.Ok(new OpportunityAdvanced(input.OpportunityId, input.Trigger));
    }
}
