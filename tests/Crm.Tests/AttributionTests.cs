using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// Who gets the credit, and the two things that make the answer worth reading.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The credit sums to the deal, to the penny.</strong> The quiet way an attribution report
/// stops being read is rounding: three campaigns each given a third of a deal worth one hundred
/// leave a penny nobody has, the campaign totals do not add to the pipeline total, and the first
/// person to notice stops trusting every other number on the page.
/// </para>
/// <para>
/// <strong>A touch after the deal closed did not win the deal.</strong> A campaign that emailed a
/// customer a month after they bought is claiming credit for a decision already made, and
/// last touch with no cutoff will give it all of one.
/// </para>
/// </remarks>
public sealed class AttributionTests
{
    private static readonly Guid Web = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Webinar = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Event = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Retention = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static readonly DateTimeOffset Decided =
        new(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);

    /// <summary>First touch gives it all to whoever got there first.</summary>
    [Fact]
    public void FirstTouchGivesItAllToWhoeverGotThereFirst()
    {
        var credits = Attribution.Split(AttributionModel.FirstTouch, Three(), 90_000m, Decided);

        credits.Count.ShouldBe(3);
        credits[0].Campaign.ShouldBe("web");
        credits[0].Amount.ShouldBe(90_000m);
        credits.Where(credit => credit.Campaign != "web")
            .ShouldAllBe(credit => credit.Amount == 0m);
    }

    /// <summary>Last touch gives it all to whoever got there last.</summary>
    [Fact]
    public void LastTouchGivesItAllToWhoeverGotThereLast()
    {
        var credits = Attribution.Split(AttributionModel.LastTouch, Three(), 90_000m, Decided);

        credits[0].Campaign.ShouldBe("event");
        credits[0].Amount.ShouldBe(90_000m);
    }

    /// <summary>Linear shares it evenly.</summary>
    [Fact]
    public void LinearSharesItEvenly()
    {
        var credits = Attribution.Split(AttributionModel.Linear, Three(), 90_000m, Decided);

        credits.ShouldAllBe(credit => credit.Amount == 30_000m);
    }

    /// <summary>Position-based pays the ends more than the middle.</summary>
    [Fact]
    public void PositionBasedPaysTheEndsMoreThanTheMiddle()
    {
        var credits = Attribution.Split(AttributionModel.PositionBased, Three(), 100_000m, Decided);

        var byName = credits.ToDictionary(credit => credit.Campaign, StringComparer.Ordinal);

        byName["web"].Amount.ShouldBe(40_000m);
        byName["event"].Amount.ShouldBe(40_000m);
        byName["webinar"].Amount.ShouldBe(20_000m);
    }

    /// <summary>Two campaigns share it evenly under position-based.</summary>
    /// <remarks>
    /// With nothing in between there is no middle to pay, and a build that kept the two fifths
    /// would leave a fifth of every two-touch deal attributed to nobody.
    /// </remarks>
    [Fact]
    public void PositionBasedWithNoMiddleSharesItEvenly()
    {
        IReadOnlyList<Touch> two =
        [
            new(Web, "web", Decided.AddDays(-30)),
            new(Event, "event", Decided.AddDays(-2)),
        ];

        var credits = Attribution.Split(AttributionModel.PositionBased, two, 100_000m, Decided);

        credits.ShouldAllBe(credit => credit.Amount == 50_000m);
    }

    /// <summary>The credit sums to the deal exactly, whatever the model and however many.</summary>
    /// <remarks>
    /// <strong>The property this whole file exists for.</strong> A third of one hundred is not
    /// representable, and a build that gave each campaign its own rounded share would leave the
    /// campaign totals disagreeing with the pipeline total by a penny — which is enough for
    /// somebody to stop reading the page.
    /// </remarks>
    [Theory]
    [InlineData(AttributionModel.FirstTouch, 3)]
    [InlineData(AttributionModel.LastTouch, 3)]
    [InlineData(AttributionModel.Linear, 3)]
    [InlineData(AttributionModel.PositionBased, 3)]
    [InlineData(AttributionModel.Linear, 7)]
    [InlineData(AttributionModel.PositionBased, 7)]
    [InlineData(AttributionModel.Linear, 11)]
    [InlineData(AttributionModel.PositionBased, 11)]
    public void TheCreditSumsToTheDealExactly(AttributionModel model, int campaigns)
    {
        var touches = Enumerable.Range(0, campaigns)
            .Select(index => new Touch(
                Guid.Parse($"{index:D8}-0000-0000-0000-000000000000"),
                "campaign_" + index,
                Decided.AddDays(-100 + index)))
            .ToList();

        // 100.01 over three ways is the shape that breaks a naive split: no share is exact and the
        // rounding goes in different directions.
        var credits = Attribution.Split(model, touches, 100.01m, Decided);

        credits.Sum(credit => credit.Amount).ShouldBe(100.01m);
    }

    /// <summary>A touch after the deal was decided is not credit for the deal.</summary>
    /// <remarks>
    /// The control this mechanism needs to be worth anything. A retention campaign emailing a
    /// customer a month after they bought is the most recent thing that touched them, and last
    /// touch with no cutoff would give it every penny.
    /// </remarks>
    [Fact]
    public void ATouchAfterTheDecisionIsNotCreditForTheDeal()
    {
        IReadOnlyList<Touch> touches =
        [
            .. Three(),
            new(Retention, "retention", Decided.AddDays(30)),
        ];

        var credits = Attribution.Split(AttributionModel.LastTouch, touches, 90_000m, Decided);

        credits.ShouldNotContain(credit => credit.Campaign == "retention");
        credits[0].Campaign.ShouldBe("event", "the last touch before the decision, not after it.");
    }

    /// <summary>One campaign that touched somebody five times is one campaign.</summary>
    /// <remarks>
    /// Otherwise "first touch" means the first email rather than the first campaign, and a
    /// campaign that sends a weekly newsletter takes a linear model's whole deal.
    /// </remarks>
    [Fact]
    public void ACampaignThatTouchedSomebodyFiveTimesIsOneCampaign()
    {
        IReadOnlyList<Touch> touches =
        [
            new(Web, "web", Decided.AddDays(-60)),
            new(Web, "web", Decided.AddDays(-50)),
            new(Web, "web", Decided.AddDays(-40)),
            new(Event, "event", Decided.AddDays(-2)),
        ];

        var credits = Attribution.Split(AttributionModel.Linear, touches, 100m, Decided);

        credits.Count.ShouldBe(2);
        credits.ShouldAllBe(credit => credit.Amount == 50m);
    }

    /// <summary>
    /// A campaign's first touch is the one that orders it, not its most recent.
    /// </summary>
    /// <remarks>
    /// A newsletter that reached somebody two years ago and again yesterday found them two years
    /// ago. Ordering by the latest touch would let anything that sends often claim to have got
    /// there first.
    /// </remarks>
    [Fact]
    public void ACampaignIsOrderedByWhenItFirstReachedThem()
    {
        IReadOnlyList<Touch> touches =
        [
            new(Web, "web", Decided.AddDays(-700)),
            new(Web, "web", Decided.AddDays(-1)),
            new(Event, "event", Decided.AddDays(-30)),
        ];

        Attribution.Split(AttributionModel.FirstTouch, touches, 100m, Decided)[0]
            .Campaign.ShouldBe("web");
    }

    /// <summary>A deal nothing touched is attributed to nobody, and that is an answer.</summary>
    /// <remarks>
    /// Empty rather than a share for everybody or a refusal. Most deals in most organisations are
    /// not influenced by a campaign, and a report that invented influence to close the gap between
    /// what was considered and what was attributed would be worse than one that showed the gap.
    /// </remarks>
    [Fact]
    public void ADealNothingTouchedIsAttributedToNobody()
    {
        Attribution.Split(AttributionModel.Linear, [], 90_000m, Decided).ShouldBeEmpty();
    }

    /// <summary>Everything before the cutoff being after it is the same as nothing.</summary>
    [Fact]
    public void ADealWhoseEveryTouchCameLaterIsAttributedToNobody()
    {
        IReadOnlyList<Touch> touches = [new(Retention, "retention", Decided.AddDays(1))];

        Attribution.Split(AttributionModel.FirstTouch, touches, 90_000m, Decided).ShouldBeEmpty();
    }

    private static IReadOnlyList<Touch> Three() =>
    [
        new(Web, "web", Decided.AddDays(-90)),
        new(Webinar, "webinar", Decided.AddDays(-30)),
        new(Event, "event", Decided.AddDays(-2)),
    ];
}
