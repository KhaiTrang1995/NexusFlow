using System.Globalization;

namespace FlowX.Runtime;

/// <summary>
/// A five-field cron expression and the zone it is read in, as a function from an instant to
/// the instants that follow it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Stateless, and that is the whole of the multi-node design.</strong> Nothing here
/// records what fired, holds a timer or knows a node exists. Every node evaluates the same
/// expression against the same clock and arrives at the same set of instants, so agreement
/// costs no coordination — and an occurrence, being a value both nodes computed, can name the
/// instance it starts
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0026-an-occurrence-names-the-instance-it-starts.md">ADR-0026</a>).
/// A scheduler built on a leader and a timer would have to agree about who the leader is; this
/// one has nothing to agree about.
/// </para>
/// <para>
/// <strong>The expression names wall-clock fields, so the zone is not decoration.</strong>
/// <c>0 2 * * *</c> in <c>Europe/Berlin</c> is 01:00 UTC in January and 00:00 UTC in July, and
/// on two days a year the local time it names either does not exist or exists twice. Both are
/// answered here rather than left to the caller, because a nightly job that silently does not
/// run once a year is the defect a time zone field exists to prevent:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <strong>A local time the spring transition skips fires at the transition.</strong> 02:30
/// does not exist in Berlin on the last Sunday in March, so the occurrence lands on the first
/// instant that does — 03:00 local, which is the same instant 02:00 would have been. Late by
/// up to the size of the gap, and never skipped.
/// </description></item>
/// <item><description>
/// <strong>A local time the autumn transition repeats fires once, on the first pass.</strong>
/// 02:30 happens twice in Berlin on the last Sunday in October. Firing twice would run a
/// nightly job twice a year with no declaration anywhere saying so; firing on the first pass
/// is the reading that keeps "once a day" true.
/// </description></item>
/// </list>
/// <para>
/// <strong>What this parser does not read.</strong> Three-letter month and day names
/// (<c>JAN</c>, <c>MON</c>), the non-standard <c>@yearly</c> macros, seconds as a sixth field,
/// and Quartz's <c>L</c>, <c>W</c> and <c>#</c>. Each is refused rather than approximated:
/// <see cref="Parse"/> answers <c>schedule.cron_unreadable</c>, the registration that carries
/// it throws at startup, and a deployment finds out from a pod that never becomes ready rather
/// than from a job that never runs.
/// </para>
/// </remarks>
public sealed class CronSchedule
{
    /// <summary>An expression this parser cannot read.</summary>
    public const string UnreadableCode = "schedule.cron_unreadable";

    /// <summary>A time zone this machine does not know.</summary>
    public const string UnknownTimeZoneCode = "schedule.time_zone_unknown";

    /// <summary>
    /// How far ahead a search gives up. Five years and a leap day: <c>0 3 29 2 *</c> is the
    /// sparsest expression the five fields can write, and it fires at most every four years.
    /// </summary>
    private const int SearchDays = 5 * 366;

    private readonly ulong _minutes;
    private readonly uint _hours;
    private readonly uint _daysOfMonth;
    private readonly ushort _months;
    private readonly byte _daysOfWeek;
    private readonly bool _dayOfMonthRestricted;
    private readonly bool _dayOfWeekRestricted;
    private readonly TimeZoneInfo _zone;

    private CronSchedule(
        string expression,
        string timeZoneId,
        TimeZoneInfo zone,
        ulong minutes,
        uint hours,
        uint daysOfMonth,
        ushort months,
        byte daysOfWeek,
        bool dayOfMonthRestricted,
        bool dayOfWeekRestricted)
    {
        Expression = expression;
        TimeZoneId = timeZoneId;
        _zone = zone;
        _minutes = minutes;
        _hours = hours;
        _daysOfMonth = daysOfMonth;
        _months = months;
        _daysOfWeek = daysOfWeek;
        _dayOfMonthRestricted = dayOfMonthRestricted;
        _dayOfWeekRestricted = dayOfWeekRestricted;
    }

    /// <summary>The expression, exactly as it was declared.</summary>
    /// <remarks>
    /// Kept verbatim rather than normalised, because it is one of the five values the
    /// instance id is derived from and it is the string the manifest published. A schedule
    /// that normalised <c>*/60</c> to <c>0</c> would derive a different id from the one an
    /// operator can reconstruct by reading <c>flowx.manifest.json</c>.
    /// </remarks>
    public string Expression { get; }

    /// <summary>The IANA zone id, as it was declared.</summary>
    public string TimeZoneId { get; }

    /// <summary>Reads an expression and a zone, or says which of the two it could not.</summary>
    /// <param name="expression">A five-field cron expression.</param>
    /// <param name="timeZoneId">An IANA time zone id. <c>UTC</c> is always available.</param>
    /// <returns>The schedule, or the refusal.</returns>
    public static Result<CronSchedule> Parse(string expression, string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return Result.Fail<CronSchedule>(Unreadable(expression ?? string.Empty, "it is empty"));
        }

        var fields = expression.Split(
            [' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (fields.Length != 5)
        {
            return Result.Fail<CronSchedule>(Unreadable(
                expression,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"it has {fields.Length} fields and a cron expression has five: minute, " +
                    $"hour, day-of-month, month, day-of-week")));
        }

        if (!TryZone(timeZoneId, out var zone))
        {
            return Result.Fail<CronSchedule>(new Error(
                UnknownTimeZoneCode,
                $"'{timeZoneId}' is not a time zone this machine knows. Use an IANA id such as " +
                "'Europe/Berlin', or 'UTC'.",
                ErrorCategory.Validation));
        }

        if (!TryField(fields[0], 0, 59, out var minutes, out var restrictedMinutes) ||
            !TryField(fields[1], 0, 23, out var hours, out _) ||
            !TryField(fields[2], 1, 31, out var daysOfMonth, out var restrictedDom) ||
            !TryField(fields[3], 1, 12, out var months, out _) ||
            !TryField(fields[4], 0, 7, out var daysOfWeek, out var restrictedDow))
        {
            return Result.Fail<CronSchedule>(Unreadable(
                expression,
                "one of its fields is not a number, a range, a step or a comma-separated list " +
                "of those, or names a value outside the field's range"));
        }

        _ = restrictedMinutes;

        // Sunday is both 0 and 7, which every cron since Vixie accepts. Folded here so that
        // nothing downstream carries a seven-day week with eight members in it.
        if ((daysOfWeek & (1UL << 7)) != 0)
        {
            daysOfWeek = (daysOfWeek & ~(1UL << 7)) | 1UL;
        }

        return Result.Ok(new CronSchedule(
            expression,
            timeZoneId,
            zone,
            minutes,
            (uint)hours,
            (uint)daysOfMonth,
            (ushort)months,
            (byte)daysOfWeek,
            restrictedDom,
            restrictedDow));
    }

    /// <summary>The first occurrence strictly after an instant, or null if there is none in range.</summary>
    /// <param name="instant">The instant to search from. Not itself a candidate.</param>
    /// <remarks>
    /// <strong>Strict, and it has to be.</strong> A sweep asks "what has fallen due since the
    /// one I last fired"; a non-strict answer would hand it the same occurrence for ever.
    /// </remarks>
    public DateTimeOffset? NextAfter(DateTimeOffset instant)
    {
        var local = ToLocal(instant);
        var day = local.Date;
        var fromMinute = (local.Hour * 60) + local.Minute;

        for (var scanned = 0; scanned <= SearchDays; scanned++, day = day.AddDays(1), fromMinute = -1)
        {
            if (!MatchesDay(day))
            {
                continue;
            }

            foreach (var minuteOfDay in MinutesOfDay(after: fromMinute, ascending: true))
            {
                if (ToInstant(day.AddMinutes(minuteOfDay)) is { } candidate && candidate > instant)
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>The last occurrence at or before an instant, or null if there is none in range.</summary>
    /// <param name="instant">The instant to search back from. Itself a candidate.</param>
    public DateTimeOffset? LatestAtOrBefore(DateTimeOffset instant)
    {
        var local = ToLocal(instant);
        var day = local.Date;
        var beforeMinute = (local.Hour * 60) + local.Minute + 1;

        for (var scanned = 0; scanned <= SearchDays; scanned++, day = day.AddDays(-1), beforeMinute = 24 * 60)
        {
            if (!MatchesDay(day))
            {
                continue;
            }

            foreach (var minuteOfDay in MinutesOfDay(after: beforeMinute, ascending: false))
            {
                if (ToInstant(day.AddMinutes(minuteOfDay)) is { } candidate && candidate <= instant)
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>Every occurrence in <c>(from, to]</c>, ascending.</summary>
    /// <param name="from">The lower bound, excluded — the last occurrence already accounted for.</param>
    /// <param name="to">The upper bound, included — usually now.</param>
    /// <remarks>
    /// Half-open at the bottom and closed at the top, so that walking a sweep forward with
    /// <c>from</c> = the last occurrence handled produces every occurrence exactly once and
    /// never the same one twice.
    /// </remarks>
    public IEnumerable<DateTimeOffset> Between(DateTimeOffset from, DateTimeOffset to)
    {
        for (var occurrence = NextAfter(from); occurrence <= to; occurrence = NextAfter(occurrence.Value))
        {
            yield return occurrence.Value;
        }
    }

    /// <inheritdoc />
    public override string ToString() => Expression + " (" + TimeZoneId + ")";

    private static Error Unreadable(string expression, string why) => new(
        UnreadableCode,
        $"'{expression}' is not a cron expression this build can read: {why}. Five " +
        "space-separated fields are supported — minute, hour, day-of-month, month, " +
        "day-of-week — each a number, a range (1-5), a step (*/15 or 1-5/2), a comma-separated " +
        "list of those, or '*'. Names (JAN, MON), macros (@daily), a seconds field and " +
        "Quartz's L, W and # are not read.",
        ErrorCategory.Validation);

    private static bool TryZone(string timeZoneId, out TimeZoneInfo zone)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            zone = TimeZoneInfo.Utc;
            return false;
        }

        if (string.Equals(timeZoneId, "UTC", StringComparison.OrdinalIgnoreCase))
        {
            zone = TimeZoneInfo.Utc;
            return true;
        }

        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            zone = TimeZoneInfo.Utc;
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            zone = TimeZoneInfo.Utc;
            return false;
        }
    }

    /// <summary>Reads one field into a bitmask, and says whether it restricts anything.</summary>
    /// <remarks>
    /// "Restricts" means "is not <c>*</c>", and it is a separate answer from the mask because
    /// day-of-month and day-of-week are unioned when both restrict and intersected otherwise —
    /// a distinction the mask alone cannot carry, since <c>*</c> and <c>0-31</c> produce the
    /// same bits.
    /// </remarks>
    private static bool TryField(string field, int min, int max, out ulong mask, out bool restricted)
    {
        mask = 0;
        restricted = field != "*";

        foreach (var term in field.Split(','))
        {
            if (!TryTerm(term, min, max, ref mask))
            {
                return false;
            }
        }

        return mask != 0;
    }

    private static bool TryTerm(string term, int min, int max, ref ulong mask)
    {
        var step = 1;
        var body = term;
        var slash = term.IndexOf('/', StringComparison.Ordinal);

        if (slash >= 0)
        {
            body = term[..slash];

            if (!int.TryParse(
                    term[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out step) ||
                step <= 0)
            {
                return false;
            }
        }

        int first;
        int last;

        if (body == "*")
        {
            first = min;
            last = max;
        }
        else
        {
            var dash = body.IndexOf('-', StringComparison.Ordinal);

            if (dash < 0)
            {
                if (!int.TryParse(body, NumberStyles.None, CultureInfo.InvariantCulture, out first))
                {
                    return false;
                }

                // `5/2` means "from 5 to the end of the field, every 2", which is what every
                // cron does with a bare value in front of a step. Without a step it is one value.
                last = slash >= 0 ? max : first;
            }
            else if (!int.TryParse(body[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out first) ||
                     !int.TryParse(body[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out last))
            {
                return false;
            }
        }

        if (first < min || last > max || first > last)
        {
            return false;
        }

        for (var value = first; value <= last; value += step)
        {
            mask |= 1UL << value;
        }

        return true;
    }

    /// <summary>Whether the date's month and day fields match, with cron's day union.</summary>
    /// <remarks>
    /// <strong>Every other pair of fields is intersected; these two are unioned.</strong>
    /// <c>0 0 1 * 1</c> fires on the first of the month <em>and</em> on every Monday, which is
    /// what every cron implementation since Vixie does. A schedule that intersected them would
    /// fire on some months and not others, and its author would have written what looks like a
    /// weekly job.
    /// </remarks>
    private bool MatchesDay(DateTime day)
    {
        if ((_months & (1 << day.Month)) == 0)
        {
            return false;
        }

        var byDayOfMonth = (_daysOfMonth & (1U << day.Day)) != 0;
        var byDayOfWeek = (_daysOfWeek & (1 << (int)day.DayOfWeek)) != 0;

        return (_dayOfMonthRestricted, _dayOfWeekRestricted) switch
        {
            (true, true) => byDayOfMonth || byDayOfWeek,
            (true, false) => byDayOfMonth,
            (false, true) => byDayOfWeek,
            _ => true,
        };
    }

    /// <summary>The matching minutes of one day, walking away from a bound.</summary>
    private IEnumerable<int> MinutesOfDay(int after, bool ascending)
    {
        if (ascending)
        {
            for (var hour = 0; hour < 24; hour++)
            {
                if ((_hours & (1U << hour)) == 0)
                {
                    continue;
                }

                for (var minute = 0; minute < 60; minute++)
                {
                    var minuteOfDay = (hour * 60) + minute;

                    if (minuteOfDay > after && (_minutes & (1UL << minute)) != 0)
                    {
                        yield return minuteOfDay;
                    }
                }
            }

            yield break;
        }

        for (var hour = 23; hour >= 0; hour--)
        {
            if ((_hours & (1U << hour)) == 0)
            {
                continue;
            }

            for (var minute = 59; minute >= 0; minute--)
            {
                var minuteOfDay = (hour * 60) + minute;

                if (minuteOfDay < after && (_minutes & (1UL << minute)) != 0)
                {
                    yield return minuteOfDay;
                }
            }
        }
    }

    private DateTime ToLocal(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, _zone).DateTime;

    /// <summary>
    /// The instant one wall-clock time names, answering for the two days a year on which it
    /// names none or two.
    /// </summary>
    /// <returns>
    /// The instant, or <c>null</c> when the gap is wider than an hour and the search for the
    /// first valid local time ran out — which no real zone has, and which is answered rather
    /// than assumed away.
    /// </returns>
    private DateTimeOffset? ToInstant(DateTime local)
    {
        var candidate = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        // The spring gap: this local time does not exist. Walk forward to the first that does,
        // which is the transition instant itself. Bounded at three hours — the widest DST jump
        // any zone has ever made is two.
        for (var minute = 0; minute <= 180 && _zone.IsInvalidTime(candidate); minute++)
        {
            candidate = candidate.AddMinutes(1);
        }

        if (_zone.IsInvalidTime(candidate))
        {
            return null;
        }

        // The autumn fold: this local time happens twice. GetAmbiguousTimeOffsets returns both
        // offsets; the larger offset is the earlier instant, which is the first pass.
        if (_zone.IsAmbiguousTime(candidate))
        {
            var offsets = _zone.GetAmbiguousTimeOffsets(candidate);
            var earliest = offsets[0];

            foreach (var offset in offsets)
            {
                if (offset > earliest)
                {
                    earliest = offset;
                }
            }

            return new DateTimeOffset(candidate, earliest);
        }

        return new DateTimeOffset(candidate, _zone.GetUtcOffset(candidate));
    }
}
