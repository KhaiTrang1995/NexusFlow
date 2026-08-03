using Crm;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// Quote arithmetic and the discount threshold, of <c>docs/26-CRM-Sample.md</c> §5.2.
/// </summary>
/// <remarks>
/// <strong>No database, and none of these need one.</strong> What a quote costs and whether its
/// discount needs a manager are functions of the lines and one constant. The half of package 9
/// that does need a server is the half that decides who may act, and that is
/// <see cref="DiscountApprovalTests"/>.
/// </remarks>
public sealed class PricingTests
{
    // ---------------------------------------------------------------------------- arithmetic

    [Fact]
    public void ASubtotalIsTheSumOfTheLines()
    {
        var priced = Pricing.Price(
            [Line("SEAT", 3, 250m), Line("SUPPORT", 1, 1_000m)],
            discount: 0m);

        priced.IsSuccess.ShouldBeTrue();
        priced.Value!.Subtotal.Amount.ShouldBe(1_750m);
        priced.Value.Total.Amount.ShouldBe(1_750m);
        priced.Value.Discount.Amount.ShouldBe(0m);
    }

    [Fact]
    public void TheTotalIsTheSubtotalLessTheDiscount()
    {
        var priced = Pricing.Price([Line("SEAT", 10, 100m)], discount: 150m);

        priced.Value!.Total.Amount.ShouldBe(850m);
    }

    [Fact]
    public void TheQuoteCarriesTheLinesCurrency()
    {
        var priced = Pricing.Price([Line("SEAT", 1, 100m, "SEK")], discount: 0m);

        priced.Value!.Subtotal.Currency.ShouldBe("SEK");
        priced.Value.Discount.Currency.ShouldBe("SEK", "a discount of nothing is still in a currency.");
        priced.Value.Total.Currency.ShouldBe("SEK");
    }

    /// <summary>
    /// A half-unit at the column's scale rounds the way <c>numeric</c> rounds it, not the way
    /// .NET rounds it by default.
    /// </summary>
    [Fact]
    public void AMidpointRoundsAwayFromZeroAsTheColumnWould()
    {
        Pricing.Round(0.00005m).ShouldBe(0.0001m);
        Pricing.Round(0.00015m).ShouldBe(0.0002m, "to even would give 0.0002 here and 0.0000 above.");
    }

    [Fact]
    public void ALineIsRoundedBeforeItIsSummed()
    {
        // 3 × 0.33335 is 1.00005, which is a half-unit past the column's scale.
        var priced = Pricing.Price([Line("MICRO", 3, 0.33335m)], discount: 0m);

        priced.Value!.Subtotal.Amount.ShouldBe(1.0001m,
            "the line's extended total is rounded once, not its unit price three times.");
    }

    // ----------------------------------------------------------------------------- refusals

    [Fact]
    public void AQuoteWithNoLinesIsRefused()
    {
        Pricing.Price([], discount: 0m).Error!.Code.ShouldBe("crm.quote_has_no_lines");
    }

    [Fact]
    public void TwoCurrenciesInOneQuoteAreRefusedRatherThanConverted()
    {
        var priced = Pricing.Price([Line("SEAT", 1, 100m, "EUR"), Line("SUPPORT", 1, 90m, "USD")], 0m);

        priced.Error!.Code.ShouldBe("crm.quote_mixes_currencies");
        priced.Error.Message.ShouldContain("EUR");
        priced.Error.Message.ShouldContain("USD");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ALineSellingNothingIsRefused(int quantity)
    {
        Pricing.Price([Line("SEAT", quantity, 100m)], 0m)
            .Error!.Code.ShouldBe("crm.quote_line_not_sellable");
    }

    [Fact]
    public void ALinePricedBelowZeroIsRefused()
    {
        Pricing.Price([Line("SEAT", 1, -1m)], 0m).Error!.Code.ShouldBe("crm.quote_line_not_sellable");
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1000.01)]
    public void ADiscountOutsideTheSubtotalIsRefused(double discount)
    {
        // Ten at a hundred is a thousand, so the second of these is a penny too much.
        Pricing.Price([Line("SEAT", 10, 100m)], (decimal)discount)
            .Error!.Code.ShouldBe("crm.discount_not_applicable");
    }

    [Fact]
    public void ADiscountOfTheWholeSubtotalIsAllowed()
    {
        var priced = Pricing.Price([Line("SEAT", 10, 100m)], discount: 1_000m);

        priced.IsSuccess.ShouldBeTrue("giving it away is a business decision, not an arithmetic error.");
        priced.Value!.Total.Amount.ShouldBe(0m);
        priced.Value.NeedsApproval.ShouldBeTrue();
    }

    // ---------------------------------------------------------------------------- the threshold

    /// <summary>
    /// The boundary, and which side of it a representative is on.
    /// </summary>
    [Theory]
    [InlineData(149.99, false)]
    [InlineData(150.00, false)]
    [InlineData(150.01, true)]
    public void FifteenPerCentIsAllowedAndAPennyMoreIsNot(double discount, bool needsApproval)
    {
        var priced = Pricing.Price([Line("SEAT", 10, 100m)], (decimal)discount);

        priced.Value!.NeedsApproval.ShouldBe(needsApproval);
    }

    [Fact]
    public void AQuoteWorthNothingNeedsNoApproval()
    {
        var priced = Pricing.Price([Line("FREE", 1, 0m)], discount: 0m);

        priced.Value!.NeedsApproval.ShouldBeFalse(
            "nothing off nothing is not a discount anybody has to approve.");
    }

    [Fact]
    public void TheThresholdIsAFractionOfWhateverTheSubtotalIs()
    {
        // The same money is under the threshold on a big quote and over it on a small one.
        Pricing.Price([Line("SEAT", 100, 100m)], 1_000m).Value!.NeedsApproval.ShouldBeFalse();
        Pricing.Price([Line("SEAT", 10, 100m)], 1_000m).Value!.NeedsApproval.ShouldBeTrue();
    }

    // ------------------------------------------------------------------------------- fixtures

    private static QuoteRequestLine Line(string sku, int quantity, decimal price, string currency = "EUR") =>
        new(sku, quantity, new Money(price, currency));
}
