namespace FlowX;

/// <summary>Which of the host's row-driven sweeps a wake belongs to.</summary>
/// <remarks>
/// One enum rather than one signal per sweep, because a store that can wake any of them can
/// usually wake all of them from the same place — the PostgreSQL listener holds one session and
/// three channels — and a contract that had to be registered once per sweep would make waking
/// only one of them the accident rather than the decision.
/// </remarks>
public enum SweepKind
{
    /// <summary>The pass a change subscription makes over the outbox.</summary>
    Change,

    /// <summary>The pass the timer sweep makes over parked instances.</summary>
    Timer,

    /// <summary>
    /// The pass the recovery sweep makes over instances a dead node left running.
    /// </summary>
    /// <remarks>
    /// What this one waits for is not a row appearing but a lease <em>lapsing</em>, which is an
    /// instant a store knows the moment it writes the lease. It is the same wake as the other
    /// two and carries no more than they do: the sweep re-reads its own candidates, and what a
    /// wake means is "run the pass now".
    /// </remarks>
    Recovery,
}

/// <summary>
/// Something that can tell a sweep there is work, so the sweep does not have to wait out its
/// interval to find out.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It accelerates a sweep and never replaces one.</strong> The interval a host is
/// configured with stays exactly what it was and stays the correctness backstop: a wake that is
/// never raised — a listener that is disconnected, a deployment that registered no signal at all,
/// a notification the transport dropped — costs latency and nothing else, because the pass that
/// would have happened anyway still happens. That is the whole reason this is a wait rather than
/// a queue, and it is why an implementation is allowed to be lossy but never allowed to throw.
/// </para>
/// <para>
/// <strong>The wake carries nothing.</strong> There is no change on it, no instance id and no
/// count — the sweep re-reads its own cursor and its own rows when it runs, which it has to do
/// regardless, so anything carried here would be a second source of truth that could disagree
/// with the first. What a caller may conclude from <see cref="WaitAsync"/> returning is only
/// "run the pass now", which is what it concludes from the interval elapsing too.
/// </para>
/// <para>
/// <strong>A wake raised while nobody is waiting is remembered.</strong> The moment a sweep is
/// most likely to be told about work is the moment it is busy doing some, so an implementation
/// latches: the next <see cref="WaitAsync"/> for that <see cref="SweepKind"/> returns at once and
/// consumes the latch. One latch, not a count — two wakes and one wake both mean "there is
/// work", and the pass that answers them reads everything either way.
/// </para>
/// <para>
/// <strong>One waiter per <see cref="SweepKind"/>.</strong> A host runs one loop per sweep, so
/// an implementation is not required to fan a wake out to several waiters at once.
/// </para>
/// <para>
/// Optional, like <see cref="IRecoveryIndex"/> and <see cref="ITimerIndex"/>. Nothing registered
/// is the ordinary configuration and is the behaviour every release before this one had: the
/// services fall back to waiting out the interval, and no part of the pass changes.
/// </para>
/// </remarks>
public interface ISweepSignal
{
    /// <summary>
    /// Waits until this sweep should run: as soon as a wake is raised for it, or when
    /// <paramref name="interval"/> has elapsed, whichever is first.
    /// </summary>
    /// <param name="sweep">Which sweep is waiting.</param>
    /// <param name="interval">
    /// The longest this call may wait. The caller's configured interval, which is what makes a
    /// missed wake a latency cost rather than a lost event.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes when the caller should run its pass.</returns>
    /// <remarks>
    /// <strong>Cancellation behaves as <see cref="Task.Delay(TimeSpan, CancellationToken)"/>
    /// does</strong>, throwing <see cref="OperationCanceledException"/>, because this call stands
    /// exactly where that one stood and a host's shutdown path is written against it.
    /// </remarks>
    Task WaitAsync(SweepKind sweep, TimeSpan interval, CancellationToken cancellationToken);
}
