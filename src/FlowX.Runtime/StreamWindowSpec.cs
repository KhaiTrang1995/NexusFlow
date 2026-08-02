using System.Globalization;
using System.Xml;

namespace FlowX.Runtime;

/// <summary>
/// The window shape a <c>[StreamTrigger]</c> declared, once it has been read — and the refusal
/// of every shape this engine does not implement.
/// </summary>
/// <param name="Size">The tumbling window's width, in event time.</param>
/// <param name="Lateness">
/// How far behind the highest observed event time the watermark trails. A window closes when the
/// watermark reaches its upper bound, so this is exactly how long a window is held open for
/// records that arrive out of order.
/// </param>
/// <param name="CheckpointInterval">
/// The shortest gap between two checkpoint commits. Progress is still <em>computed</em> after
/// every window; this only throttles the write, because the checkpoint is a row per subscription
/// and a stream that closes a window a second does not need a row rewritten a second.
/// </param>
/// <param name="Parallelism">How many closed windows may have flows running at once.</param>
/// <remarks>
/// <para>
/// <strong>Tumbling only, and the refusal is the interesting part.</strong> docs/09 §9's table
/// names four shapes. Sliding and session windows assign one record to several windows or to a
/// window whose bounds move as records arrive, and in both cases a window's identity is no
/// longer a function of the event time alone — which is what
/// <see cref="StreamIdentity.InstanceIdFor"/> derives from, and therefore what makes a rebuilt
/// window deduplicate rather than double-count
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0055-a-window-names-the-instance-it-starts.md">ADR-0055</a>).
/// A global window has no upper bound, so nothing closes it and this engine's whole progress
/// rule — checkpoint the prefix whose windows have closed — never advances. Implementing them by
/// relaxing the identity would take the guarantee away from the shape that does work, so they
/// are refused here and by <c>FLOWX1042</c> at compile time.
/// </para>
/// </remarks>
public sealed record StreamWindowSpec(
    TimeSpan Size, TimeSpan Lateness, TimeSpan CheckpointInterval, int Parallelism)
{
    /// <summary>The prefix of the only window shape this engine implements.</summary>
    public const string TumblingPrefix = "tumbling:";

    /// <summary>The window an event time falls in.</summary>
    /// <param name="eventTime">The record's event time.</param>
    /// <returns>The window's inclusive lower bound, in UTC.</returns>
    /// <remarks>
    /// <strong>Aligned to the epoch, not to the first record.</strong> Two nodes, and one node
    /// before and after a restart, must agree about which window a record is in — and "the first
    /// record I happened to see" is the one origin they cannot agree on. Aligning to
    /// <see cref="DateTimeOffset.UnixEpoch"/> makes the assignment a pure function of the event
    /// time and the declared width, which is what lets a rebuilt window derive the id the
    /// original did.
    /// </remarks>
    public DateTimeOffset WindowStartFor(DateTimeOffset eventTime)
    {
        var ticks = eventTime.ToUniversalTime().Ticks - DateTimeOffset.UnixEpoch.Ticks;
        var aligned = ticks - Modulo(ticks, Size.Ticks);

        return DateTimeOffset.UnixEpoch.AddTicks(aligned);
    }

    /// <summary>The window's exclusive upper bound.</summary>
    /// <param name="windowStart">The window's inclusive lower bound.</param>
    /// <returns>The bound.</returns>
    public DateTimeOffset WindowEndFor(DateTimeOffset windowStart) => windowStart + Size;

    /// <summary>Reads a declaration, or says which part of it this engine cannot serve.</summary>
    /// <param name="window">The <c>Window</c> argument, e.g. <c>tumbling:1m</c>.</param>
    /// <param name="lateness">The <c>Lateness</c> argument, an ISO-8601 duration.</param>
    /// <param name="checkpoint">The <c>Checkpoint</c> argument, an ISO-8601 duration.</param>
    /// <param name="parallelism">The <c>Parallelism</c> argument.</param>
    /// <returns>The specification, or the refusal.</returns>
    /// <remarks>
    /// Returns a <c>Result</c> rather than throwing, because every caller is a registration or a
    /// pass that has somewhere better to put a refusal than a stack trace (ADR-0007).
    /// </remarks>
    public static Result<StreamWindowSpec> Read(
        string? window, string? lateness, string? checkpoint, int parallelism)
    {
        if (window is null || !window.StartsWith(TumblingPrefix, StringComparison.Ordinal))
        {
            return StreamErrors.UnsupportedWindow(window ?? string.Empty);
        }

        if (ShortDuration(window[TumblingPrefix.Length..]) is not { } size || size <= TimeSpan.Zero)
        {
            return StreamErrors.UnsupportedWindow(window);
        }

        if (Iso8601(lateness ?? "PT0S") is not { } allowed || allowed < TimeSpan.Zero)
        {
            return new Error(
                StreamErrors.UnsupportedWindowCode,
                $"Lateness '{lateness}' is not a non-negative ISO-8601 duration, e.g. PT10S.",
                ErrorCategory.Validation);
        }

        if (Iso8601(checkpoint ?? "PT5S") is not { } interval || interval < TimeSpan.Zero)
        {
            return new Error(
                StreamErrors.UnsupportedWindowCode,
                $"Checkpoint '{checkpoint}' is not a non-negative ISO-8601 duration, e.g. PT5S.",
                ErrorCategory.Validation);
        }

        if (parallelism < 1)
        {
            return new Error(
                StreamErrors.UnsupportedWindowCode,
                $"Parallelism is {parallelism}; it must be at least one. Zero is not 'the engine " +
                "decides' — it is a subscription that reads a stream and never runs a flow.",
                ErrorCategory.Validation);
        }

        return new StreamWindowSpec(size, allowed, interval, parallelism);
    }

    /// <summary>
    /// The short duration form docs/09 §9 writes a window in: <c>500ms</c>, <c>30s</c>,
    /// <c>15m</c>, <c>1h</c>, <c>1d</c>.
    /// </summary>
    /// <remarks>
    /// Not ISO-8601, unlike <c>Lateness</c> and <c>Checkpoint</c>, because those two say
    /// ISO-8601 on the attribute and this one prints <c>tumbling:1m</c> in the only example
    /// anyone has read. Accepting both spellings everywhere would be the kindest-looking choice
    /// and the worst one: two ways to write one value is two ways for a manifest diff to report
    /// a change nobody made.
    /// </remarks>
    private static TimeSpan? ShortDuration(string value)
    {
        var (suffix, unit) = value switch
        {
            _ when value.EndsWith("ms", StringComparison.Ordinal) => (2, TimeSpan.FromMilliseconds(1)),
            _ when value.EndsWith('s') => (1, TimeSpan.FromSeconds(1)),
            _ when value.EndsWith('m') => (1, TimeSpan.FromMinutes(1)),
            _ when value.EndsWith('h') => (1, TimeSpan.FromHours(1)),
            _ when value.EndsWith('d') => (1, TimeSpan.FromDays(1)),
            _ => (0, TimeSpan.Zero),
        };

        if (suffix == 0
            || !long.TryParse(
                value[..^suffix], NumberStyles.None, CultureInfo.InvariantCulture, out var count))
        {
            return null;
        }

        return unit * count;
    }

    private static TimeSpan? Iso8601(string value)
    {
        try
        {
            return XmlConvert.ToTimeSpan(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>A remainder that is never negative, so a pre-epoch event time aligns downwards.</summary>
    private static long Modulo(long value, long divisor)
    {
        var remainder = value % divisor;

        return remainder < 0 ? remainder + divisor : remainder;
    }
}
