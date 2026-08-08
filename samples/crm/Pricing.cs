using System.Globalization;
using FlowX;

namespace Crm;

/// <summary>One line a caller asks to be quoted for.</summary>
/// <param name="Sku">What is being sold.</param>
/// <param name="Quantity">How many.</param>
/// <param name="UnitPrice">What one costs, in its own currency.</param>
public sealed record QuoteRequestLine(string Sku, int Quantity, Money UnitPrice);

/// <summary>The three numbers a quote is stored with, and whether it may be issued unapproved.</summary>
/// <param name="Subtotal">The sum of the lines.</param>
/// <param name="Discount">What is being taken off.</param>
/// <param name="Total">What is being asked for.</param>
/// <param name="NeedsApproval">Whether the discount is over <see cref="DiscountPolicy.Threshold"/>.</param>
public sealed record PricedQuote(Money Subtotal, Money Discount, Money Total, bool NeedsApproval);

/// <summary>Refusals pricing and the sales flows can produce.</summary>
public static class SalesErrors
{
    /// <summary>A quote with no lines has no subtotal to discount.</summary>
    public static Error QuoteHasNoLines() =>
        new(
            "crm.quote_has_no_lines",
            "A quote needs at least one line.",
            ErrorCategory.Validation);

    /// <summary>Two lines priced in different currencies.</summary>
    /// <param name="expected">The currency the first line set.</param>
    /// <param name="found">The currency a later line used.</param>
    /// <remarks>
    /// <strong>Refused rather than converted.</strong> Converting needs a rate, a rate needs an
    /// instant, and a quote that silently picked one would be wrong at every other instant. The
    /// <c>event-driven</c> sample is where this sample learnt that a hard-coded currency is
    /// agreed with by every transport and is still wrong.
    /// </remarks>
    public static Error QuoteMixesCurrencies(string expected, string found) =>
        new Error(
            "crm.quote_mixes_currencies",
            $"This quote is priced in {expected} and a line is priced in {found}. " +
            "A quote carries one currency; converting would need a rate this application does not hold.",
            ErrorCategory.Validation)
            .With("expected", expected)
            .With("found", found);

    /// <summary>A line's quantity or price is not a thing that can be sold.</summary>
    /// <param name="sku">Which line.</param>
    /// <param name="reason">What is wrong with it.</param>
    public static Error QuoteLineIsNotSellable(string sku, string reason) =>
        new Error(
            "crm.quote_line_not_sellable",
            $"Line '{sku}' {reason}.",
            ErrorCategory.Validation)
            .With("sku", sku);

    /// <summary>The discount is negative, or larger than what is being discounted.</summary>
    /// <param name="discount">What was asked for.</param>
    /// <param name="subtotal">What there was to take it off.</param>
    public static Error DiscountIsNotApplicable(decimal discount, decimal subtotal) =>
        new Error(
            "crm.discount_not_applicable",
            $"A discount of {discount.ToString(CultureInfo.InvariantCulture)} cannot be taken off a " +
            $"subtotal of {subtotal.ToString(CultureInfo.InvariantCulture)}.",
            ErrorCategory.Validation)
            .With("discount", discount)
            .With("subtotal", subtotal);

    /// <summary>That quote is not in this tenant, or is gone.</summary>
    /// <param name="quoteId">What was named.</param>
    public static Error QuoteNotFound(Guid quoteId) =>
        new Error(
            "crm.quote_not_found",
            "That quote is not in this tenant.",
            ErrorCategory.NotFound)
            .With("quoteId", quoteId);

    /// <summary>The quote is not in a state this transition can be made from.</summary>
    /// <param name="quoteId">The quote.</param>
    /// <param name="status">Where it actually is.</param>
    /// <param name="expected">Where it would have to be.</param>
    public static Error QuoteIsNotIn(Guid quoteId, QuoteStatus status, QuoteStatus expected) =>
        new Error(
            "crm.quote_wrong_status",
            $"That quote is {status} and this needs it to be {expected}.",
            ErrorCategory.Conflict)
            .With("quoteId", quoteId)
            .With("status", status.ToString())
            .With("expected", expected.ToString());

    /// <summary>An order was asked for on a quote whose discount nobody approved.</summary>
    /// <param name="quoteId">The quote.</param>
    /// <remarks>
    /// <strong>Checked again here, and not only where the discount was applied.</strong> The
    /// approval is a grant a manager holds; the order is the moment the money is committed. A
    /// deployment that only checked at issue time would let a quote issued before a threshold
    /// changed be ordered after it.
    /// </remarks>
    public static Error DiscountIsNotApproved(Guid quoteId) =>
        new Error(
            "crm.discount_not_approved",
            "That quote's discount is over the threshold and no manager has approved it.",
            ErrorCategory.Conflict)
            .With("quoteId", quoteId);

    /// <summary>The quote has run out.</summary>
    /// <param name="quoteId">The quote.</param>
    /// <param name="validUntil">When it ran out.</param>
    public static Error QuoteHasExpired(Guid quoteId, DateTimeOffset validUntil) =>
        new Error(
            "crm.quote_expired",
            $"That quote was valid until {validUntil.ToString("O", CultureInfo.InvariantCulture)}.",
            ErrorCategory.Conflict)
            .With("quoteId", quoteId);

    /// <summary>That opportunity is not in this tenant, or is gone.</summary>
    /// <param name="opportunityId">What was named.</param>
    public static Error OpportunityNotFound(Guid opportunityId) =>
        new Error(
            "crm.opportunity_not_found",
            "That opportunity is not in this tenant.",
            ErrorCategory.NotFound)
            .With("opportunityId", opportunityId);

    /// <summary>Nobody in this tenant applied a trigger under that handle.</summary>
    /// <param name="applicationId">What was named.</param>
    /// <remarks>
    /// A <c>404</c> and not a <see cref="TriggerOutcome.Pending"/>: "we have no record of you
    /// applying this" and "the engine has not answered yet" are the two mistakes this whole read
    /// exists to keep apart, and answering the first with the second would put one of them back.
    /// </remarks>
    public static Error TriggerApplicationNotFound(Guid applicationId) =>
        new Error(
            "crm.trigger_application_not_found",
            "No trigger was applied under that handle in this tenant.",
            ErrorCategory.NotFound)
            .With("applicationId", applicationId);
}

/// <summary>
/// When a discount stops being a representative's decision.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A constant, and §5.2 says why it is not a column.</strong> The threshold decides who
/// may act, and a rule about who may act is not a rule about what may be stored. Storing it per
/// tenant would make it configuration — and configuration deciding an authorisation boundary is
/// the thing §7.3 draws a line under.
/// </para>
/// <para>
/// <strong>Strictly over, so a discount exactly at the threshold is a representative's to
/// make.</strong> A boundary has to fall on one side and this is the side that does not make
/// "up to fifteen per cent" mean fourteen.
/// </para>
/// </remarks>
public static class DiscountPolicy
{
    /// <summary>The fraction of the subtotal a representative may discount unaided.</summary>
    public const decimal Threshold = 0.15m;

    /// <summary>Whether a discount needs <c>crm.discount.approve</c>.</summary>
    /// <param name="subtotal">What is being discounted.</param>
    /// <param name="discount">What is being taken off.</param>
    /// <returns>True when the discount is over the threshold.</returns>
    public static bool NeedsApproval(decimal subtotal, decimal discount) =>
        subtotal > 0m && discount > subtotal * Threshold;
}

/// <summary>
/// Turns lines and a discount into the numbers a quote is stored with.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Pure, and that is what lets the sales rules be tested without a server.</strong>
/// Nothing here reads a database, a clock or a caller. The same lines produce the same quote on
/// a replay, which is what an idempotent issue over HTTP needs to be true.
/// </para>
/// <para>
/// <strong>It rounds where the column does.</strong> <c>quote.subtotal</c> is
/// <c>numeric(19,4)</c>, so an unrounded product would be rounded on the way in and the
/// application's <c>total</c> would stop equalling the database's <c>subtotal - discount</c>.
/// Rounding here rather than there means the arithmetic the caller is shown is the arithmetic
/// that was stored.
/// </para>
/// </remarks>
public static class Pricing
{
    /// <summary>The scale of <c>numeric(19,4)</c>, which is what the money columns are.</summary>
    public const int Scale = 4;

    /// <summary>Prices a set of lines.</summary>
    /// <param name="lines">What is being sold.</param>
    /// <param name="discount">What is being taken off the subtotal, in the lines' currency.</param>
    /// <returns>The priced quote, or the first refusal.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="lines"/> is null.</exception>
    public static Result<PricedQuote> Price(IReadOnlyList<QuoteRequestLine> lines, decimal discount)
    {
        ArgumentNullException.ThrowIfNull(lines);

        if (lines.Count == 0)
        {
            return Result.Fail<PricedQuote>(SalesErrors.QuoteHasNoLines());
        }

        var currency = lines[0].UnitPrice.Currency;
        var subtotal = 0m;

        foreach (var line in lines)
        {
            if (!string.Equals(line.UnitPrice.Currency, currency, StringComparison.Ordinal))
            {
                return Result.Fail<PricedQuote>(
                    SalesErrors.QuoteMixesCurrencies(currency, line.UnitPrice.Currency));
            }

            if (line.Quantity <= 0)
            {
                return Result.Fail<PricedQuote>(
                    SalesErrors.QuoteLineIsNotSellable(line.Sku, "has a quantity of nothing or less"));
            }

            if (line.UnitPrice.Amount < 0m)
            {
                return Result.Fail<PricedQuote>(
                    SalesErrors.QuoteLineIsNotSellable(line.Sku, "is priced below zero"));
            }

            subtotal += Round(line.UnitPrice.Amount * line.Quantity);
        }

        subtotal = Round(subtotal);

        if (discount < 0m || discount > subtotal)
        {
            return Result.Fail<PricedQuote>(SalesErrors.DiscountIsNotApplicable(discount, subtotal));
        }

        var taken = Round(discount);

        return Result.Ok(new PricedQuote(
            new Money(subtotal, currency),
            new Money(taken, currency),
            new Money(subtotal - taken, currency),
            DiscountPolicy.NeedsApproval(subtotal, taken)));
    }

    /// <summary>Rounds to the scale the money columns hold.</summary>
    /// <param name="amount">What to round.</param>
    /// <returns>The amount at <see cref="Scale"/> decimal places.</returns>
    /// <remarks>
    /// <strong>Away from zero, because that is what PostgreSQL's <c>numeric</c> does.</strong>
    /// .NET rounds a midpoint to even by default and <c>numeric</c> rounds it away from zero, so
    /// the default would disagree with the column on exactly the values a half-cent lands on.
    /// Two roundings that disagree are worse than either.
    /// </remarks>
    public static decimal Round(decimal amount) =>
        Math.Round(amount, Scale, MidpointRounding.AwayFromZero);
}
