namespace FlowX.Runtime;

/// <summary>
/// The runtime's only source of wall-clock time.
/// </summary>
/// <remarks>
/// Exists so that deadline behaviour is testable without sleeping, and so that a
/// durable flow can replay against journaled time rather than against whatever the
/// machine says now. Code that reads <see cref="DateTimeOffset.UtcNow"/> directly is
/// code that cannot be replayed — which is why the determinism analyzers
/// (FLOWX1007) reject it inside a durable flow.
/// </remarks>
public interface IClock
{
    /// <summary>The current instant, in UTC.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>Waits for <paramref name="delay"/>.</summary>
    /// <param name="delay">How long to wait. Zero or negative returns immediately.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <remarks>
    /// <para>
    /// <strong>Here because "testable without sleeping" is what this interface is for</strong>,
    /// and a runtime that waits has to wait through something a test can move. The only thing
    /// that waits today is a compensation's backoff; a suite that genuinely slept through five
    /// full-jitter attempts would spend seconds proving arithmetic a fake clock can state
    /// exactly.
    /// </para>
    /// <para>
    /// <strong>Defaulted rather than required</strong>, for the reason
    /// <c>IStepDispatcher.DescribeStep</c> is: every clock that already exists — the host's,
    /// the test host's, the ones written inside test classes — is complete without it, and
    /// making it required would have meant every one of them copying the same three lines.
    /// The default is the real wait, so a clock that does not override it is correct and
    /// merely slow.
    /// </para>
    /// </remarks>
    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
        delay <= TimeSpan.Zero ? ValueTask.CompletedTask : new ValueTask(Task.Delay(delay, cancellationToken));
}

/// <summary>The clock a running host uses.</summary>
public sealed class SystemClock : IClock
{
    /// <summary>The shared instance. Stateless, so one is enough.</summary>
    public static SystemClock Instance { get; } = new();

    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
