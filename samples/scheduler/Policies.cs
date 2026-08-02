using FlowX;

namespace Scheduler;

/// <summary>The one policy set this application declares.</summary>
public static class Policies
{
    /// <summary>
    /// The stance for reading the bank: bounded, retried, and given up on rather than hammered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The retry is legal only because <c>reconciliation.statement.read</c> declares
    /// <c>Idempotent = true</c></strong>, and <c>FLOWX1014</c> is an error otherwise. Reading a
    /// statement twice is genuinely free; a policy set is not a place to assert that about a
    /// capability that writes.
    /// </para>
    /// <para>
    /// <strong>The timeout is well inside the flow's own deadline</strong>, and that ordering is
    /// checked: <c>FLOWX1019</c> reports a <c>[FlowDeadline]</c> shorter than the step timeouts
    /// it has to contain. A nightly job whose external read can outlast its deadline is a job
    /// that produces a <c>TimedOut</c> instance instead of a report, every night, once the bank
    /// gets slower.
    /// </para>
    /// </remarks>
    public static PolicySet ExternalRead { get; } = PolicySet
        .Named("external-read")
        .Timeout(TimeSpan.FromSeconds(30))
        .Retry(attempts: 3)
        .CircuitBreaker(failureRatio: 0.5, breakDuration: TimeSpan.FromMinutes(1));
}
