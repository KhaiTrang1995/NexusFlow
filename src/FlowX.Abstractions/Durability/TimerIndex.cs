namespace FlowX;

/// <summary>
/// A wait an instance is parked at: which step, in which iteration, and when it is due.
/// </summary>
/// <param name="Scope">The iteration the waiting step is running in.</param>
/// <param name="StepId">The waiting step's flat index.</param>
/// <param name="At">When the instance must be picked up again.</param>
/// <remarks>
/// <para>
/// <strong>The step is in here, and not because a sweep needs it.</strong> A sweep asks only
/// "is this due". The engine asks a harder question: it walks the plan from the frontier and
/// arrives at a suspension point, and it has to know whether the instant on the row belongs to
/// <em>that</em> wait or to one the instance has since finished. Without an identity the
/// answer is a guess, and the guess is wrong in exactly the case that matters — a flow whose
/// first wait was satisfied late reaching its second, which would read a stale instant already
/// in the past and walk straight through a wait that had not started.
/// </para>
/// <para>
/// <strong>One wait per instance, which is one per row.</strong> An instance is parked at one
/// point at a time in every shape but a fork, and a fork whose branches both wait records the
/// earlier of the two: it is resumed then, the branch that is due proceeds, and the other
/// finds its own wait still running and parks again. The cost is that the sibling's wait is
/// re-armed from the clock rather than continued, so it is <em>lengthened</em> by however long
/// its sibling had left. Never shortened, and only in a fork around a wait.
/// </para>
/// </remarks>
public readonly record struct FlowWake(StepScope Scope, int StepId, DateTimeOffset At);

/// <summary>
/// The one query a timer sweep needs: which suspended instances are due to be woken.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Separate from <see cref="IFlowJournal"/> for the reason
/// <see cref="IRecoveryIndex"/> is, and separate from <see cref="IRecoveryIndex"/> because it
/// is a different question about a different set of rows.</strong> A recovery scan looks for
/// instances a node <em>died</em> holding — <c>Pending</c>, <c>Running</c>,
/// <c>Compensating</c>, stale — and deliberately excludes <c>Suspended</c>, because an
/// instance parked by design was not abandoned. This looks for exactly the rows that one
/// excludes, and it filters on an instant the instance chose rather than on staleness.
/// Folding them together would mean one query with a disjunction no partial index can serve.
/// </para>
/// <para>
/// <strong>It answers "is due", not "should run", and that is not a hedge.</strong> The row
/// says when the instance asked to be woken; whether the wait it is parked at is actually over
/// is a question about the compiled plan, and the step loop is what holds one. So a candidate
/// here is resumed through the same <c>FlowHost.ResumeAsync</c> a recovery scan and a signal
/// use, and the engine decides. A sweep that decided would be a second place that knows what a
/// suspension point means.
/// </para>
/// <para>
/// Optional, like <see cref="IRecoveryIndex"/>. A store implements it when it can serve the
/// query cheaply; a host whose journal does not is a host whose <c>.Delay</c> and
/// <c>.OnTimeout</c> never fire, which is worth knowing about rather than worth pretending
/// away — <c>FlowTimerScan.IsEnabled</c> is how a deployment finds out.
/// </para>
/// </remarks>
public interface ITimerIndex
{
    /// <summary>Lists suspended instances whose recorded wake instant has passed.</summary>
    /// <param name="query">Which instances to consider, and how many to return.</param>
    /// <param name="cancellationToken">Cancels the store call.</param>
    /// <returns>
    /// The candidates, longest overdue first, or the store's error. An empty list is the
    /// ordinary answer and is not an error.
    /// </returns>
    /// <remarks>
    /// Ordered by wake instant for the reason <see cref="IRecoveryIndex.ListAbandonedAsync"/>
    /// is ordered by staleness: an instance no deployed node can run must not hold the head of
    /// every page for ever. Everything behind an overdue instance is overdue too, and one that
    /// is woken has its row rewritten and moves to the back.
    /// </remarks>
    ValueTask<Result<IReadOnlyList<DueInstance>>> ListDueAsync(
        DueInstanceQuery query,
        CancellationToken cancellationToken);
}

/// <summary>Which parked instances a timer sweep is asking about.</summary>
/// <remarks>
/// A record for the reason <see cref="AbandonedInstanceQuery"/> is one: the shape will grow —
/// a shard predicate is foreseeable — and adding a property is a smaller change to every store
/// than adding a parameter.
/// </remarks>
public sealed record DueInstanceQuery
{
    /// <summary>Only instances whose recorded wake instant is at or before this.</summary>
    /// <remarks>
    /// <strong>Now, and nothing clever.</strong> There is no equivalent of
    /// <see cref="AbandonedInstanceQuery.IdleBefore"/>'s lease-TTL grace here, because there is
    /// nothing to be graceful about: a parked instance has no owner to wait for. The instant
    /// comes from the runtime's <c>IClock</c> rather than from the database's <c>now()</c>, so
    /// a suite can wind a timer forward without waiting for one.
    /// </remarks>
    public required DateTimeOffset DueBefore { get; init; }

    /// <summary>How many candidates to return at most.</summary>
    /// <remarks>
    /// Bounded with no way to ask for everything, for the reason
    /// <see cref="AbandonedInstanceQuery.Limit"/> is: a backlog of timers that all came due
    /// during an outage is unbounded, and what one node pulls from it must not be.
    /// </remarks>
    public int Limit { get; init; } = 64;

    /// <summary>Restrict the sweep to one tenant, or null for every tenant this node serves.</summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// How many of <see cref="Limit"/> one tenant may occupy, or zero for no cap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong><see cref="Limit"/> plus an ordering is a first-in-first-out queue, and a
    /// first-in-first-out queue starves.</strong> The page is the oldest work in the table, so a
    /// tenant with a backlog longer than <see cref="Limit"/> owns every row of every page and a
    /// quieter tenant's instance is not merely served late — it is never fetched, and no amount
    /// of fairness applied to the page can select a candidate that is not in it. That is
    /// <c>docs/16 §4</c>'s noisy neighbour arriving through the one place the sweep cannot see
    /// it, and it is why this cap belongs to the query rather than to the caller.
    /// </para>
    /// <para>
    /// <strong>Zero is the default and means the page this contract always returned.</strong> A
    /// single-tenant deployment sets nothing, the store issues the statement it always issued,
    /// and no window function is planned. A store that ignored a non-zero value would be a
    /// deployment believing it had bought fairness it does not have, so
    /// <c>RecoveryIndexConformance</c> asserts it rather than describing it.
    /// </para>
    /// </remarks>
    public int PerTenantLimit { get; init; }
}

/// <summary>A parked instance whose wake instant has passed.</summary>
/// <param name="InstanceId">The instance to wake.</param>
/// <param name="FlowId">The flow it is an instance of.</param>
/// <param name="FlowVersion">
/// The version it is pinned to for its whole life. A node that does not carry this exact
/// version must not resume it, for the reason <see cref="AbandonedInstance"/> gives.
/// </param>
/// <param name="TenantId">The partition key, for a sweep that is scoped to one tenant.</param>
/// <param name="WakeAt">The instant the row recorded — how overdue this candidate is.</param>
/// <remarks>
/// Deliberately not <see cref="AbandonedInstance"/>, though the two carry nearly the same
/// columns. That one has a <c>State</c>, because a scan finds instances in three of them and
/// an operator reading a candidate list wants to know which; every instance here is
/// <see cref="FlowInstanceState.Suspended"/> by construction, so a field saying so on every row
/// would be a constant pretending to be data. And its <c>UpdatedAt</c> means "how long since
/// anybody touched this", which is the opposite of what <see cref="WakeAt"/> means.
/// </remarks>
public sealed record DueInstance(
    Guid InstanceId,
    string FlowId,
    string FlowVersion,
    string? TenantId,
    DateTimeOffset WakeAt);
