using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// The arithmetic every service desk gets wrong at least once.
/// </summary>
/// <remarks>
/// A promise measured in calendar hours breaches a Friday-afternoon case on the Friday evening,
/// when the desk was shut and nobody had a chance to meet it. The number is wrong, everybody knows
/// it is wrong, and the report stops being read. These are the boundaries that arithmetic gets
/// wrong, tested without a database because the walk is a pure function over a week.
/// </remarks>
public sealed class BusinessCalendarTests
{
    /// <summary>Monday to Friday, nine to five.</summary>
    private static readonly OpeningHours[] OfficeWeek =
    [
        new(DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(17, 0)),
        new(DayOfWeek.Tuesday, new TimeOnly(9, 0), new TimeOnly(17, 0)),
        new(DayOfWeek.Wednesday, new TimeOnly(9, 0), new TimeOnly(17, 0)),
        new(DayOfWeek.Thursday, new TimeOnly(9, 0), new TimeOnly(17, 0)),
        new(DayOfWeek.Friday, new TimeOnly(9, 0), new TimeOnly(17, 0)),
    ];

    /// <summary>A budget that fits inside one day falls due inside that day.</summary>
    [Fact]
    public void APromiseThatFitsInTheDayFallsDueInTheDay()
    {
        // Wednesday 2026-08-05, ten in the morning.
        var raised = new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero);

        BusinessCalendar.Due(raised, 240, OfficeWeek)
            .ShouldBe(new DateTimeOffset(2026, 8, 5, 14, 0, 0, TimeSpan.Zero));
    }

    /// <summary>The clock stops overnight and picks up when the desk opens.</summary>
    /// <remarks>
    /// The case this whole file exists for. A four-hour promise made at half past four in the
    /// afternoon is met by half past twelve the next day, not by half past eight the same evening.
    /// </remarks>
    [Fact]
    public void ThePromiseCarriesOvernightRatherThanBreachingWhileTheDeskIsShut()
    {
        // Wednesday, half past four — thirty minutes of the day left.
        var raised = new DateTimeOffset(2026, 8, 5, 16, 30, 0, TimeSpan.Zero);

        BusinessCalendar.Due(raised, 240, OfficeWeek)
            .ShouldBe(
                new DateTimeOffset(2026, 8, 6, 12, 30, 0, TimeSpan.Zero),
                "thirty minutes on Wednesday and three and a half hours on Thursday.");
    }

    /// <summary>The weekend is not time the desk had.</summary>
    [Fact]
    public void TheWeekendDoesNotCount()
    {
        // Friday, half past four.
        var raised = new DateTimeOffset(2026, 8, 7, 16, 30, 0, TimeSpan.Zero);

        BusinessCalendar.Due(raised, 240, OfficeWeek)
            .ShouldBe(
                new DateTimeOffset(2026, 8, 10, 12, 30, 0, TimeSpan.Zero),
                "Saturday and Sunday are days the desk did not have.");
    }

    /// <summary>A case that arrives overnight starts its clock when the desk opens.</summary>
    /// <remarks>
    /// What makes an email sent at three in the morning and a telephone call at nine the same
    /// promise. Without it the overnight queue starts every day already six hours down.
    /// </remarks>
    [Fact]
    public void ACaseThatArrivesBeforeTheDeskOpensStartsWhenItOpens()
    {
        var raised = new DateTimeOffset(2026, 8, 5, 3, 0, 0, TimeSpan.Zero);

        BusinessCalendar.Due(raised, 60, OfficeWeek)
            .ShouldBe(new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero));
    }

    /// <summary>A case that arrives after the desk shuts starts the next morning.</summary>
    [Fact]
    public void ACaseThatArrivesAfterTheDeskShutsStartsTheNextMorning()
    {
        var raised = new DateTimeOffset(2026, 8, 5, 22, 0, 0, TimeSpan.Zero);

        BusinessCalendar.Due(raised, 60, OfficeWeek)
            .ShouldBe(new DateTimeOffset(2026, 8, 6, 10, 0, 0, TimeSpan.Zero));
    }

    /// <summary>A desk with no week has no clock, and says so.</summary>
    /// <remarks>
    /// Null rather than a default of around-the-clock. A desk that configured no week and got a
    /// twenty-four-hour promise would breach everything by Tuesday and blame the report.
    /// </remarks>
    [Fact]
    public void ADeskThatIsNeverOpenHasNoDueDate()
    {
        BusinessCalendar.Due(DateTimeOffset.UnixEpoch, 60, []).ShouldBeNull();
    }

    /// <summary>A budget longer than the walk looks is refused rather than approximated.</summary>
    [Fact]
    public void ABudgetLongerThanTheWalkIsRefused()
    {
        // Eight weeks of an eight-hour day is 26,880 minutes. One more than the walk can find.
        BusinessCalendar
            .Due(new DateTimeOffset(2026, 8, 5, 9, 0, 0, TimeSpan.Zero), 1_000_000, OfficeWeek)
            .ShouldBeNull();
    }

    /// <summary>Elapsed counts only the hours the desk was open.</summary>
    [Fact]
    public void ElapsedSkipsTheHoursTheDeskWasShut()
    {
        var raised = new DateTimeOffset(2026, 8, 5, 16, 30, 0, TimeSpan.Zero);
        var answered = new DateTimeOffset(2026, 8, 6, 9, 30, 0, TimeSpan.Zero);

        BusinessCalendar.Elapsed(raised, answered, OfficeWeek)
            .ShouldBe(60, "thirty minutes on Wednesday and thirty on Thursday.");
    }

    /// <summary>A reply before the case is a clock error, not a negative duration.</summary>
    [Fact]
    public void AReplyBeforeTheCaseIsZeroAndNotNegative()
    {
        var raised = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

        BusinessCalendar.Elapsed(raised, raised.AddHours(-1), OfficeWeek).ShouldBe(0);
    }

    /// <summary>What Due promises and what Elapsed measures are the same walk.</summary>
    /// <remarks>
    /// The property that makes a breach report mean anything: a reply written exactly when it was
    /// due has used exactly the promised minutes. Two separate walks that disagreed here would put
    /// every case answered at the wire on the wrong side of the line.
    /// </remarks>
    [Theory]
    [InlineData(30)]
    [InlineData(240)]
    [InlineData(480)]
    [InlineData(1_500)]
    [InlineData(4_800)]
    public void ElapsedAtTheDueInstantIsExactlyThePromise(int minutes)
    {
        var raised = new DateTimeOffset(2026, 8, 7, 16, 30, 0, TimeSpan.Zero);
        var due = BusinessCalendar.Due(raised, minutes, OfficeWeek);

        due.ShouldNotBeNull();

        BusinessCalendar.Elapsed(raised, due.Value, OfficeWeek)
            .ShouldNotBeNull()
            .ShouldBe(minutes, 0.001);
    }
}
