namespace Crm;

/// <summary>One day the desk is open, and for how long.</summary>
/// <param name="Day">Which day. Sunday is 0, matching <see cref="DayOfWeek"/>.</param>
/// <param name="Opens">When it opens.</param>
/// <param name="Closes">When it closes. Must be later than <paramref name="Opens"/>.</param>
public sealed record OpeningHours(DayOfWeek Day, TimeOnly Opens, TimeOnly Closes);

/// <summary>
/// Works out when a promise falls due, counting only the hours the desk is open.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is a pure function and not a query.</strong> Every service desk gets the
/// arithmetic here wrong at least once, and the symptom is a report nobody trusts: a case raised
/// at half past four on a Friday breaches a four-hour target at half past eight the same evening,
/// when the desk was shut. Written here, over a week and an instant and nothing else, it can be
/// tested at a hundred boundaries in a second. Written in SQL it can be tested by opening a case
/// and waiting until Friday.
/// </para>
/// <para>
/// <strong>A week with no open days means the clock does not run.</strong> That is a
/// configuration mistake rather than a state to interpolate, and it is reported by returning null
/// rather than by inventing a due date the desk has no chance of meeting.
/// </para>
/// </remarks>
public static class BusinessCalendar
{
    /// <summary>How far ahead the walk will look before it gives up.</summary>
    /// <remarks>
    /// A budget bigger than the open hours in eight weeks is a promise measured in months, and the
    /// walk stopping is a better answer than the walk running. Also the only thing standing
    /// between a badly configured week and an unbounded loop.
    /// </remarks>
    public const int MaxDaysWalked = 56;

    /// <summary>When a minute budget started at an instant runs out.</summary>
    /// <param name="from">When the clock starts.</param>
    /// <param name="minutes">How many open minutes are promised.</param>
    /// <param name="week">The days the desk is open. Empty means it never is.</param>
    /// <returns>
    /// When the promise falls due, or null when the week is empty or the budget outruns
    /// <see cref="MaxDaysWalked"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="week"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minutes"/> is not positive.</exception>
    public static DateTimeOffset? Due(
        DateTimeOffset from,
        int minutes,
        IReadOnlyList<OpeningHours> week)
    {
        ArgumentNullException.ThrowIfNull(week);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minutes);

        if (week.Count == 0)
        {
            return null;
        }

        var remaining = (double)minutes;
        var cursor = from;

        for (var walked = 0; walked <= MaxDaysWalked; walked++)
        {
            var day = week.FirstOrDefault(open => open.Day == cursor.DayOfWeek);

            if (day is not null)
            {
                var opens = On(cursor, day.Opens);
                var closes = On(cursor, day.Closes);

                // A case raised before the desk opens does not start burning its budget at the
                // moment it arrived. It starts when somebody could first have looked at it, which
                // is what makes an overnight email and a nine-o'clock telephone call the same
                // promise.
                var start = cursor < opens ? opens : cursor;

                if (start < closes)
                {
                    var available = (closes - start).TotalMinutes;

                    if (available >= remaining)
                    {
                        return start.AddMinutes(remaining);
                    }

                    remaining -= available;
                }
            }

            // The next day at midnight. Not `cursor.AddDays(1)`, which would carry the time of day
            // and skip the morning of every day after the first.
            cursor = Midnight(cursor).AddDays(1);
        }

        return null;
    }

    /// <summary>How many open minutes elapsed between two instants.</summary>
    /// <param name="from">The earlier instant.</param>
    /// <param name="to">The later one.</param>
    /// <param name="week">The days the desk is open.</param>
    /// <returns>
    /// The open minutes, or null when the span is longer than <see cref="MaxDaysWalked"/>. Zero
    /// when <paramref name="to"/> is not after <paramref name="from"/>, which is what a reply
    /// written before the case was raised means: a clock error, not a negative duration.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="week"/> is null.</exception>
    public static double? Elapsed(
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyList<OpeningHours> week)
    {
        ArgumentNullException.ThrowIfNull(week);

        if (to <= from)
        {
            return 0;
        }

        var elapsed = 0d;
        var cursor = from;

        for (var walked = 0; walked <= MaxDaysWalked; walked++)
        {
            if (cursor >= to)
            {
                return elapsed;
            }

            if (week.FirstOrDefault(open => open.Day == cursor.DayOfWeek) is { } day)
            {
                var opens = On(cursor, day.Opens);
                var closes = On(cursor, day.Closes);

                var start = cursor < opens ? opens : cursor;
                var end = to < closes ? to : closes;

                if (end > start)
                {
                    elapsed += (end - start).TotalMinutes;
                }
            }

            cursor = Midnight(cursor).AddDays(1);
        }

        return null;
    }

    private static DateTimeOffset On(DateTimeOffset day, TimeOnly time) =>
        new(day.Year, day.Month, day.Day, time.Hour, time.Minute, 0, day.Offset);

    private static DateTimeOffset Midnight(DateTimeOffset day) =>
        new(day.Year, day.Month, day.Day, 0, 0, 0, day.Offset);
}
