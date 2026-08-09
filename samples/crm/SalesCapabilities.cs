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
/// Re-prices a quote by writing a revision and retiring the quote it replaces.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It does not touch the quote the customer holds, and that is the whole design.</strong>
/// A quote is a document that was sent: its lines, its total and whoever approved its discount
/// are the record of what was offered on the day it was offered, and an order joins to it.
/// Rewriting that row in place is not a re-price, it is a denial that the first price was ever
/// quoted. So the revision is a new row that names the one it replaces, and the replaced one
/// moves to <see cref="QuoteStatus.Superseded"/> — terminal, which is what stops the old price
/// from being ordered at.
/// </para>
/// <para>
/// <strong>Refused on a quote that has been ordered against or already replaced.</strong> An
/// accepted quote has an order hanging off it and superseding it would leave that order pointing
/// at a retired document; a second revision of one quote is two current prices and nothing saying
/// which the customer should read, which is also what the unique index on <c>supersedes</c> says.
/// The status is checked here for the sentence and again in the <c>UPDATE</c>'s own <c>WHERE</c>
/// for the race.
/// </para>
/// <para>
/// <strong>The revision is priced from scratch, approval and all.</strong> A discount a manager
/// signed off on the old lines is not a discount they signed off on these ones, so a revision
/// past the threshold is a <c>Draft</c> even when the quote it replaces was approved.
/// </para>
/// </remarks>
[Capability("crm.quote.reprice", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.write",
    Idempotent = true,
    SideEffects = ["crm.quote.written"])]
public sealed class RepriceQuoteAsARevision : ICapability<RepriceQuote, QuoteSuperseded>
{
    private readonly SalesStore _store;

    /// <summary>Creates the capability.</summary>
    /// <param name="store">Reads the quote, writes the revision and retires the original.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public RepriceQuoteAsARevision(SalesStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <inheritdoc />
    public async ValueTask<Result<QuoteSuperseded>> ExecuteAsync(
        RepriceQuote input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var priced = Pricing.Price(input.Lines, input.Discount);

        if (!priced.IsSuccess)
        {
            return Result.Fail<QuoteSuperseded>(priced.Error!);
        }

        if (await _store.ReadQuoteAsync(ctx.TenantId, input.QuoteId, ct).ConfigureAwait(false)
            is not { } replaced)
        {
            return Result.Fail<QuoteSuperseded>(SalesErrors.QuoteNotFound(input.QuoteId));
        }

        // Draft and Issued are the two states a quote is still live in. Accepted has an order
        // against it, and the other three are already terminal — Superseded among them, which is
        // what makes a second revision of one quote a refusal rather than a fork.
        if (replaced.Status is not (QuoteStatus.Draft or QuoteStatus.Issued))
        {
            return Result.Fail<QuoteSuperseded>(
                SalesErrors.QuoteIsNotIn(replaced.Id, replaced.Status, QuoteStatus.Issued));
        }

        var quoteId = ctx.NewId();

        var status = await _store.SupersedeAsync(
            ctx.TenantId,
            quoteId,
            replaced,
            priced.Value!,
            input.Lines,
            ctx.UtcNow.AddDays(input.ValidForDays),
            ct).ConfigureAwait(false);

        // Null means the row moved between the read and the write. The same refusal as above,
        // read off the status the quote has now.
        if (status is not { } written)
        {
            var now = await _store.ReadQuoteAsync(ctx.TenantId, input.QuoteId, ct).ConfigureAwait(false);

            return Result.Fail<QuoteSuperseded>(SalesErrors.QuoteIsNotIn(
                replaced.Id, now?.Status ?? replaced.Status, QuoteStatus.Issued));
        }

        return Result.Ok(new QuoteSuperseded(
            quoteId, replaced.Id, priced.Value!.Total, written, priced.Value.NeedsApproval));
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
/// <para>
/// <strong>It moves nothing.</strong> Where the opportunity goes is
/// <see cref="RunConfiguredTransition"/>'s answer, read out of the definition an administrator
/// wrote. What this does is check the opportunity is one this tenant has and record that
/// somebody applied the trigger; the event <see cref="AdvanceOpportunityFlow"/> emits is what
/// the change feed hands to §7's engine.
/// </para>
/// <para>
/// <strong>What it now leaves behind is a row, and that is the only thing that changed.</strong>
/// Until it did, a caller had a 200 and nothing else: the deal was in the stage it started in,
/// and there was no way to tell "the feed has not reached it" from "the engine ran and declined
/// to move it" — both of which are successes, so neither raised anything to catch. The engine
/// stamps its answer onto this row when it decides, and <see cref="ReadOpportunityTriggerOutcome"/>
/// hands it back. Nothing here waits for that, and nothing about when the transition runs moved.
/// </para>
/// </remarks>
[Capability("crm.opportunity.advance", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.write",
    Idempotent = true,
    SideEffects = ["crm.trigger.applied"])]
public sealed class ApplyOpportunityTrigger : ICapability<AdvanceOpportunity, OpportunityAdvanced>
{
    private readonly SalesStore _store;
    private readonly TriggerLogStore _applications;

    /// <summary>Creates the capability.</summary>
    /// <param name="store">Reads the opportunity.</param>
    /// <param name="applications">Records the application the engine will answer.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public ApplyOpportunityTrigger(SalesStore store, TriggerLogStore applications)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(applications);

        _store = store;
        _applications = applications;
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

        // Journaled, so a replay of this step records the same application rather than a second
        // one. `ctx.NewId()` is the only id in a Durable flow that survives a replay saying the
        // same thing, and this id is the handle the caller polls on.
        var applicationId = ctx.NewId();

        await _applications
            .ApplyAsync(ctx.TenantId, applicationId, input.OpportunityId, input.Trigger, ctx.UtcNow, ct)
            .ConfigureAwait(false);

        return Result.Ok(new OpportunityAdvanced(applicationId, input.OpportunityId, input.Trigger));
    }
}

/// <summary>
/// What the configured process did with a trigger somebody applied.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The read that replaced a guess.</strong> The web client used to poll the entity page
/// six times over three seconds and then announce "the process left this deal in X" — true when
/// the engine had declined, and a fabrication when it simply had not run yet. The row this reads
/// tells the two apart because the engine writes to it, and the answer is a state rather than an
/// inference from a stage that did not change.
/// </para>
/// <para>
/// <c>crm.read</c> and not <c>crm.write</c>: asking what happened is not applying anything, and a
/// read only the applier could make would be a board nobody else can follow.
/// </para>
/// </remarks>
[Capability("crm.opportunity.trigger_outcome", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.read",
    Idempotent = true)]
public sealed class ReadOpportunityTriggerOutcome : ICapability<ReadTriggerOutcome, TriggerOutcomeView>
{
    private readonly TriggerLogStore _applications;

    /// <summary>Creates the capability.</summary>
    /// <param name="applications">Reads the register.</param>
    /// <exception cref="ArgumentNullException"><paramref name="applications"/> is null.</exception>
    public ReadOpportunityTriggerOutcome(TriggerLogStore applications)
    {
        ArgumentNullException.ThrowIfNull(applications);

        _applications = applications;
    }

    /// <inheritdoc />
    public async ValueTask<Result<TriggerOutcomeView>> ExecuteAsync(
        ReadTriggerOutcome input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        return await _applications.ReadAsync(ctx.TenantId, input.ApplicationId, ct).ConfigureAwait(false)
            is { } outcome
            ? Result.Ok(outcome)
            : Result.Fail<TriggerOutcomeView>(
                SalesErrors.TriggerApplicationNotFound(input.ApplicationId));
    }
}
