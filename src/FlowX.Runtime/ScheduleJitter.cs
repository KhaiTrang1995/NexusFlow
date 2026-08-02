using System.Globalization;
using System.Xml;

namespace FlowX.Runtime;

/// <summary>
/// How long after its occurrence one firing is released, and the reason the answer is derived
/// rather than drawn.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A random delay per node is not jitter — it is a race won by the smallest
/// draw.</strong> Every node computes the same occurrence and derives the same instance id from
/// it
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0031-an-occurrence-names-the-instance-it-starts.md">ADR-0031</a>),
/// so a fleet of <em>n</em> nodes each sleeping a random slice of the window fires at
/// min(<em>n</em> draws) — which converges on the occurrence itself as the fleet grows. The
/// spread an author asked for disappears exactly when the fleet is big enough to need it.
/// Deriving the offset from the firing means every node computes the <em>same</em> instant, so
/// the firing moves as a unit and the spread is across schedules and tenants, which is what the
/// downstream at 02:00:00 is actually being protected from
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0059-schedule-jitter-is-derived-from-the-firing.md">ADR-0059</a>).
/// </para>
/// <para>
/// <strong>The offset moves when the firing is noticed, never what the firing is.</strong> The
/// instance id, the journal row and the <see cref="ScheduledFire.OccurrenceAt"/> the flow binds
/// are all the un-jittered occurrence. So jitter cannot make two nodes disagree about identity,
/// cannot make a late firing do a different night's work, and is invisible to
/// <c>MissedFirePolicy</c> — which is what keeps it a scheduling concern rather than a second
/// definition of when the schedule ran.
/// </para>
/// </remarks>
public static class ScheduleJitter
{
    /// <summary>The code a jitter this platform cannot read is refused with.</summary>
    /// <remarks>
    /// <c>CronSchedule.UnreadableCode</c>'s sibling, and separate from it because the two are
    /// repaired in different places: one is the expression, one is the window beside it.
    /// <c>FLOWX1045</c> is the compile-time half of the same rule.
    /// </remarks>
    public const string UnreadableCode = "schedule.jitter_unreadable";

    /// <summary>Reads a declared jitter, or says why it is not one.</summary>
    /// <param name="jitter">
    /// An ISO-8601 duration such as <c>PT120S</c>, or null / empty for a schedule that declares
    /// none.
    /// </param>
    /// <returns>
    /// The window, <see cref="TimeSpan.Zero"/> when none was declared, or the refusal.
    /// </returns>
    /// <remarks>
    /// <strong><c>null</c> is the only value that means "no jitter"; <c>""</c> and <c>PT0S</c>
    /// are both refused.</strong> Writing a jitter of zero, or of nothing, is asking for a
    /// spread and getting none — the shape of defect that survives review because the
    /// declaration reads as though it works. Omitting the property is the ordinary way to say a
    /// schedule fires on its occurrence, and it is the only way that is not reported.
    /// </remarks>
    public static Result<TimeSpan> Read(string? jitter)
    {
        if (jitter is null)
        {
            return Result.Ok(TimeSpan.Zero);
        }

        TimeSpan window;

        try
        {
            window = XmlConvert.ToTimeSpan(jitter);
        }
        catch (FormatException)
        {
            return Result.Fail<TimeSpan>(new Error(
                UnreadableCode,
                $"Jitter '{jitter}' is not an ISO-8601 duration. Write it as PT120S, PT2M or " +
                "PT1H, or omit the property for a schedule that fires on its occurrence.",
                ErrorCategory.Validation));
        }
        catch (OverflowException)
        {
            return Result.Fail<TimeSpan>(new Error(
                UnreadableCode,
                $"Jitter '{jitter}' does not fit in a TimeSpan.",
                ErrorCategory.Validation));
        }

        if (window <= TimeSpan.Zero)
        {
            return Result.Fail<TimeSpan>(new Error(
                UnreadableCode,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Jitter '{jitter}' is {window}, and a spread has to be positive. Omit the " +
                    $"property rather than declaring a window nothing is spread over."),
                ErrorCategory.Validation));
        }

        return Result.Ok(window);
    }

    /// <summary>How long after its occurrence one firing is released.</summary>
    /// <param name="instanceId">
    /// The id the firing runs under — the occurrence, the schedule and the tenant, already
    /// folded together by <see cref="ScheduleOccurrence.InstanceIdFor"/>.
    /// </param>
    /// <param name="window">The declared spread. Zero or less means no delay.</param>
    /// <returns>An offset in <c>[0, window)</c>, the same one on every node, for ever.</returns>
    /// <remarks>
    /// <para>
    /// <strong>The id is the source of the offset because it is already every term that makes
    /// this firing different from the next one.</strong> Two tenants of one schedule have
    /// different ids and therefore different offsets, which is the fan-out spread; two
    /// consecutive occurrences of one schedule have different ids and therefore different
    /// offsets, so a fleet does not simply run five minutes late every night.
    /// </para>
    /// <para>
    /// The first eight bytes are read big-endian, matching the order
    /// <see cref="DerivedIdentity"/> lays a digest out in, so the offset is a function of the
    /// leading digest bits an operator can recompute from the same material.
    /// </para>
    /// </remarks>
    public static TimeSpan OffsetFor(Guid instanceId, TimeSpan window)
    {
        if (window <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        Span<byte> bytes = stackalloc byte[16];

        instanceId.TryWriteBytes(bytes, bigEndian: true, out _);

        // 53 bits, which is every bit a double can hold exactly. Taking all 64 and dividing
        // would round to 1.0 for the largest draws and produce an offset of exactly the window,
        // which is the one value the interval is supposed to exclude.
        var draw = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(bytes) >> 11;

        return new TimeSpan((long)(window.Ticks * ((double)draw / (1UL << 53))));
    }
}
