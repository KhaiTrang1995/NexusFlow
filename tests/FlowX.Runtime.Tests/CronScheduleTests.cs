using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Runtime.Tests;

/// <summary>
/// The five fields, the time zone, and the occurrences between two instants.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A schedule is a function from an instant to a set of instants, and nothing
/// else.</strong> It holds no state, starts no timer and knows nothing about a node — which
/// is what lets every node in a fleet compute the same occurrence and agree without talking
/// (<a href="../../docs/adr/ADR-0031-an-occurrence-names-the-instance-it-starts.md">ADR-0026</a>).
/// Every test here is therefore a pure evaluation.
/// </para>
/// <para>
/// <strong>The zone is where a cron expression stops being obvious.</strong> A five-field
/// expression names wall-clock fields, so <c>0 2 * * *</c> in <c>Europe/Berlin</c> is a
/// different instant in January and July, and on two days a year it is either two instants or
/// none. Those two days are the reason <c>CronTriggerAttribute.TimeZone</c> exists and the
/// reason they are asserted rather than assumed.
/// </para>
/// </remarks>
public sealed class CronScheduleTests
{
    private static CronSchedule Parse(string expression, string zone = "UTC") =>
        CronSchedule.Parse(expression, zone).Value;

    /// <summary>Every minute is the densest expression the five fields can write.</summary>
    [Fact]
    public void EveryMinuteAdvancesByOneMinute()
    {
        var schedule = Parse("* * * * *");

        schedule.NextAfter(new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero))
            .ShouldBe(new DateTimeOffset(2026, 8, 1, 12, 1, 0, TimeSpan.Zero));
    }

    /// <summary>An instant that is itself an occurrence is not its own successor.</summary>
    /// <remarks>
    /// <c>NextAfter</c> is strict, and it has to be: the sweep asks "what has fallen due since
    /// the one I last fired", and a non-strict answer would hand it the same occurrence for
    /// ever.
    /// </remarks>
    [Fact]
    public void NextAfterIsStrict()
    {
        var schedule = Parse("0 * * * *");
        var onTheHour = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

        schedule.NextAfter(onTheHour).ShouldBe(onTheHour.AddHours(1));
        schedule.LatestAtOrBefore(onTheHour).ShouldBe(onTheHour);
    }

    /// <summary>Lists, ranges, steps and the wildcard, in the fields that take them.</summary>
    [Theory]
    [InlineData("30 2 * * *", "2026-08-01T03:00:00Z", "2026-08-02T02:30:00Z")]
    [InlineData("0 0 1 * *", "2026-08-01T00:00:01Z", "2026-09-01T00:00:00Z")]
    [InlineData("0 9-17 * * *", "2026-08-01T09:30:00Z", "2026-08-01T10:00:00Z")]
    [InlineData("*/15 * * * *", "2026-08-01T12:01:00Z", "2026-08-01T12:15:00Z")]
    [InlineData("0 0 * * 1", "2026-08-01T00:00:00Z", "2026-08-03T00:00:00Z")]
    [InlineData("0 0,12 * * *", "2026-08-01T01:00:00Z", "2026-08-01T12:00:00Z")]
    [InlineData("0 3 29 2 *", "2026-08-01T00:00:00Z", "2028-02-29T03:00:00Z")]
    public void TheFiveFieldsMeanWhatCronMeans(string expression, string from, string expected)
    {
        Parse(expression)
            .NextAfter(DateTimeOffset.Parse(from, System.Globalization.CultureInfo.InvariantCulture))
            .ShouldBe(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// A day-of-month and a day-of-week both restricted means either, which is cron's
    /// least obvious rule.
    /// </summary>
    /// <remarks>
    /// Every other pair of fields is intersected. These two are unioned, because that is what
    /// every cron implementation since Vixie does and a schedule that quietly disagreed would
    /// fire on days its author did not write.
    /// </remarks>
    [Fact]
    public void ARestrictedDayOfMonthAndDayOfWeekAreUnioned()
    {
        // The 1st of August 2026 is a Saturday. `1 * * 1 6` fires on the 1st and on Mondays.
        var schedule = Parse("0 0 1 * 1");

        schedule.NextAfter(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero))
            .ShouldBe(new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero), "the first Monday");

        schedule.NextAfter(new DateTimeOffset(2026, 8, 25, 0, 0, 0, TimeSpan.Zero))
            .ShouldBe(new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero), "a Monday");

        schedule.NextAfter(new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero))
            .ShouldBe(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), "the 1st, which is a Tuesday");
    }

    /// <summary>Sunday is both 0 and 7.</summary>
    [Fact]
    public void SundayIsBothZeroAndSeven()
    {
        var from = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

        Parse("0 0 * * 0").NextAfter(from).ShouldBe(Parse("0 0 * * 7").NextAfter(from));
    }

    /// <summary>A zone is wall clock, so the same expression is two instants a year apart.</summary>
    [Fact]
    public void AZonedScheduleIsWallClockAndNotAnOffset()
    {
        var schedule = Parse("0 2 * * *", "Europe/Berlin");

        schedule.NextAfter(new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero))
            .ShouldBe(new DateTimeOffset(2026, 1, 15, 1, 0, 0, TimeSpan.Zero), "CET is UTC+1");

        schedule.NextAfter(new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero))
            .ShouldBe(new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero),
                "CEST is UTC+2, so 02:00 local is midnight UTC");
    }

    /// <summary>
    /// A wall-clock time the spring transition skips fires once, at the transition.
    /// </summary>
    /// <remarks>
    /// 02:30 does not exist in <c>Europe/Berlin</c> on 29 March 2026. Skipping the day
    /// silently is the failure mode this asserts against: a nightly job that does not run once
    /// a year, with nothing anywhere saying so.
    /// </remarks>
    [Fact]
    public void AnOccurrenceInsideTheSpringGapStillFires()
    {
        var schedule = Parse("30 2 * * *", "Europe/Berlin");

        schedule.NextAfter(new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.Zero))
            .ShouldBe(new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero),
                "02:00 local does not exist that day, so the occurrence lands on the transition");
    }

    /// <summary>
    /// A wall-clock time the autumn transition repeats fires once, on the first pass.
    /// </summary>
    [Fact]
    public void AnOccurrenceInsideTheAutumnFoldFiresOnce()
    {
        var schedule = Parse("30 2 * * *", "Europe/Berlin");

        var occurrences = schedule
            .Between(
                new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero))
            .ToList();

        occurrences.Count.ShouldBe(2, "one occurrence on the 25th and one on the 26th, not three");
    }

    /// <summary>Occurrences between two instants are ascending, exclusive of the lower bound.</summary>
    [Fact]
    public void BetweenIsAscendingAndExcludesItsLowerBound()
    {
        var schedule = Parse("0 * * * *");
        var noon = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

        schedule.Between(noon, noon.AddHours(3))
            .ShouldBe([noon.AddHours(1), noon.AddHours(2), noon.AddHours(3)]);
    }

    /// <summary>An expression the parser cannot read is a refusal, not a guess.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("* * * *")]
    [InlineData("* * * * * *")]
    [InlineData("60 * * * *")]
    [InlineData("* 24 * * *")]
    [InlineData("* * 32 * *")]
    [InlineData("* * * 13 *")]
    [InlineData("* * * * 8")]
    [InlineData("*/0 * * * *")]
    [InlineData("5-1 * * * *")]
    [InlineData("banana * * * *")]
    public void AnUnreadableExpressionIsRefused(string expression)
    {
        var parsed = CronSchedule.Parse(expression, "UTC");

        parsed.IsFailure.ShouldBeTrue($"'{expression}' is not a five-field cron expression");
        parsed.Error.Code.ShouldBe("schedule.cron_unreadable");
    }

    /// <summary>An unknown time zone is refused for the same reason.</summary>
    [Fact]
    public void AnUnknownTimeZoneIsRefused()
    {
        var parsed = CronSchedule.Parse("0 * * * *", "Mars/Olympus_Mons");

        parsed.IsFailure.ShouldBeTrue();
        parsed.Error.Code.ShouldBe("schedule.time_zone_unknown");
    }

    /// <summary>
    /// Two nodes deriving the id for one occurrence derive the same id.
    /// </summary>
    /// <remarks>
    /// <strong>The whole of the multi-node answer is this equality.</strong> There is no
    /// coordination anywhere else: ten nodes compute one occurrence, derive one id from it, and
    /// the stores refuse nine of the ten starts.
    /// </remarks>
    [Fact]
    public void OneOccurrenceDerivesOneInstanceId()
    {
        var occurrence = new DateTimeOffset(2026, 8, 1, 2, 0, 0, TimeSpan.Zero);

        ScheduleOccurrence.InstanceIdFor("ledger.reconcile", "1.0.0", "0 2 * * *", "UTC", occurrence)
            .ShouldBe(ScheduleOccurrence.InstanceIdFor(
                "ledger.reconcile", "1.0.0", "0 2 * * *", "UTC", occurrence));
    }

    /// <summary>Everything the id is derived from changes it.</summary>
    /// <remarks>
    /// The version is in the derivation because an instance is pinned to the version it started
    /// with: two versions of one flow deployed side by side are two schedules, and folding them
    /// onto one id would let the older version's fire suppress the newer one's.
    /// </remarks>
    [Fact]
    public void EveryTermOfTheDerivationChangesTheId()
    {
        var occurrence = new DateTimeOffset(2026, 8, 1, 2, 0, 0, TimeSpan.Zero);
        var baseline = ScheduleOccurrence.InstanceIdFor("a.b", "1.0.0", "0 2 * * *", "UTC", occurrence);

        ScheduleOccurrence.InstanceIdFor("a.c", "1.0.0", "0 2 * * *", "UTC", occurrence).ShouldNotBe(baseline);
        ScheduleOccurrence.InstanceIdFor("a.b", "1.0.1", "0 2 * * *", "UTC", occurrence).ShouldNotBe(baseline);
        ScheduleOccurrence.InstanceIdFor("a.b", "1.0.0", "0 3 * * *", "UTC", occurrence).ShouldNotBe(baseline);
        ScheduleOccurrence.InstanceIdFor("a.b", "1.0.0", "0 2 * * *", "Europe/Berlin", occurrence).ShouldNotBe(baseline);
        ScheduleOccurrence.InstanceIdFor("a.b", "1.0.0", "0 2 * * *", "UTC", occurrence.AddDays(1)).ShouldNotBe(baseline);
    }

    /// <summary>The derived id is a well-formed UUID, version 8.</summary>
    /// <remarks>
    /// Version 8 is RFC 9562's slot for a custom derivation, which is exactly what this is. A
    /// version 4 would claim the bytes were random and a version 7 would claim the leading bits
    /// were a timestamp; neither is true, and an operator reading a journal row is entitled to
    /// know which kind of id is in front of them.
    /// </remarks>
    [Fact]
    public void TheDerivedIdIsAVersionEightUuid()
    {
        var id = ScheduleOccurrence.InstanceIdFor(
            "a.b", "1.0.0", "0 2 * * *", "UTC", DateTimeOffset.UnixEpoch);

        id.ShouldNotBe(Guid.Empty);
        id.ToString()[14].ShouldBe('8');
    }
}
