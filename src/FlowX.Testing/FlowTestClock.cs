using FlowX.Runtime;

namespace FlowX.Testing;

/// <summary>
/// A clock a test moves by hand. The default time source for <see cref="FlowTestHost"/>.
/// </summary>
/// <remarks>
/// <para>
/// Pinned rather than running, for the reason <see cref="TestCapabilityContext"/>'s clock
/// is: a flow's deadline is derived from the instant it started, so a clock that ticks
/// makes every deadline assertion a race against the test runner's scheduling. A test
/// that needs time to pass says so by calling <see cref="Advance"/>.
/// </para>
/// <para>
/// <strong>Reads and advances are atomic.</strong> A <see cref="DateTimeOffset"/> is
/// wider than a machine word, so a plain field written by the test thread and read by a
/// <c>Parallel</c> branch on the thread pool can be torn. The instant is therefore held
/// as UTC ticks and read with <see cref="Interlocked"/> — which matters precisely for
/// the shapes this host exists to exercise.
/// </para>
/// <para>
/// This is <em>not</em> virtual time. Nothing here makes a retry or a breaker window
/// elapse instantly, because P1 ships no policy executor for them to elapse in; see
/// <c>docs/23-Testing-Strategy.md §5</c>.
/// </para>
/// </remarks>
public sealed class FlowTestClock : IClock
{
    private long _ticks;

    /// <summary>Creates a clock pinned at <see cref="DateTimeOffset.UnixEpoch"/>.</summary>
    /// <remarks>
    /// The same instant <see cref="TestCapabilityContext"/> uses, so a capability tested
    /// directly and the same capability tested through a flow see the same clock.
    /// </remarks>
    public FlowTestClock()
        : this(DateTimeOffset.UnixEpoch)
    {
    }

    /// <summary>Creates a clock pinned at <paramref name="start"/>.</summary>
    /// <param name="start">The instant the clock reports until it is advanced.</param>
    public FlowTestClock(DateTimeOffset start) => _ticks = start.UtcTicks;

    /// <inheritdoc />
    public DateTimeOffset UtcNow => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    /// <summary>Moves the clock forward.</summary>
    /// <param name="by">How far forward. Must not be negative.</param>
    /// <returns>This clock, so a set-up reads as one expression.</returns>
    /// <remarks>
    /// Forward only. A clock that can go backwards can make a flow's deadline un-expire
    /// halfway through an unwind, which is a state no production clock can produce and
    /// therefore not a state worth being able to test.
    /// </remarks>
    public FlowTestClock Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);

        Interlocked.Add(ref _ticks, by.Ticks);

        return this;
    }
}
