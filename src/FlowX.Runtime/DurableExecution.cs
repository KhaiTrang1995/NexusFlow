namespace FlowX.Runtime;

/// <summary>
/// One durable flow instance as the step loop sees it: the journal it commits to, the
/// fencing token every write is guarded by, and — when the instance is being picked up
/// again — the committed history the loop derives its cursor from.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the whole of the seam's input, and it is deliberately small.</strong>
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0015-journal-schema-and-durable-execution.md">ADR-0015</a>
/// implements <c>Durable</c> as "the ephemeral step loop with a journaled step boundary",
/// and resumption as "that same loop re-entered from a cursor derived by replaying the
/// journal". So there is nothing here that describes <em>how</em> to run a flow — no second
/// engine, no recovery plan, no alternate entry point. There is a journal, an instance, a
/// token, and optionally a history.
/// </para>
/// <para>
/// <strong>The token comes from outside.</strong> Acquiring and renewing a lease is
/// <c>ILeaseStore</c>'s job and the host's decision (WP-55): a lease has a TTL that has to
/// be renewed while a long step runs, and the engine is the wrong place to own a timer.
/// What the engine owns is the obligation the token creates — every commit carries it, and a
/// commit refused because the token is below the instance's fence ends the flow rather than
/// being retried, because a node that lost the lease must discard its work.
/// </para>
/// <para>
/// <strong>Why <see cref="ResumeFrontier"/> and not an integer.</strong> A scalar cursor
/// cannot describe a half-completed <c>Parallel</c> fork, and a fork is a shape the DSL
/// shipped in P1. The engine asks "has this <c>(scope, step)</c> committed" against the plan
/// it already has, which is compile-time truth, rather than believing a position field a
/// crashing node was in the middle of writing.
/// </para>
/// </remarks>
public sealed class DurableExecution
{
    private DurableExecution(
        IFlowJournal journal,
        Guid instanceId,
        FencingToken token,
        ResumeFrontier? frontier)
    {
        Journal = journal;
        InstanceId = instanceId;
        Token = token;
        Frontier = frontier;
    }

    /// <summary>The journal this instance's step boundaries are committed to.</summary>
    public IFlowJournal Journal { get; }

    /// <summary>The instance being executed.</summary>
    public Guid InstanceId { get; }

    /// <summary>The lease token every write is guarded by.</summary>
    public FencingToken Token { get; }

    /// <summary>
    /// The committed history, when this execution is resuming one; <c>null</c> for a fresh
    /// instance.
    /// </summary>
    public ResumeFrontier? Frontier { get; }

    /// <summary>Whether this execution is re-entering an instance that already ran.</summary>
    public bool IsResumed => Frontier is not null;

    /// <summary>
    /// Opens the <c>flow_instance</c> row for a fresh instance and returns the session the
    /// engine runs it under.
    /// </summary>
    /// <param name="journal">Where the step boundaries go.</param>
    /// <param name="plan">The flow being started — its id and version pin the instance.</param>
    /// <param name="invocation">Correlation, tenant and the caller's remaining budget.</param>
    /// <param name="instanceId">
    /// The instance's identity, chosen by the caller so that a retried trigger is
    /// idempotent: starting the same id twice is refused rather than merged.
    /// </param>
    /// <param name="token">
    /// The token of the lease held while starting. It becomes the instance's opening fence.
    /// </param>
    /// <param name="input">The trigger input, recorded immutably and redacted on the way in.</param>
    /// <param name="cancellationToken">Cancels the store call.</param>
    /// <returns>The session, or the journal's refusal unchanged.</returns>
    public static async ValueTask<Result<DurableExecution>> BeginAsync(
        IFlowJournal journal,
        ExecutionPlan plan,
        FlowInvocation invocation,
        Guid instanceId,
        FencingToken token,
        JournalPayload? input = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(plan);

        var start = new FlowInstanceStart
        {
            InstanceId = instanceId,
            FlowId = plan.Flow.Id,
            FlowVersion = plan.Flow.Version,
            Token = token,
            TenantId = invocation.TenantId,
            Input = input ?? JournalPayload.Empty,
            CorrelationId = invocation.CorrelationId,
            DeadlineAt = invocation.Deadline,
        };

        var started = await journal.StartAsync(start, cancellationToken).ConfigureAwait(false);

        return started.IsSuccess
            ? Result.Ok(new DurableExecution(journal, instanceId, token, null))
            : Result.Fail<DurableExecution>(started.Error);
    }

    /// <summary>
    /// Raises the instance's fence to a freshly acquired token and reads the history the
    /// loop resumes from.
    /// </summary>
    /// <param name="journal">The journal holding the instance.</param>
    /// <param name="instanceId">The instance being taken over.</param>
    /// <param name="token">The token the lease store just issued.</param>
    /// <param name="cancellationToken">Cancels the store calls.</param>
    /// <returns>The session, carrying the committed steps, or the journal's refusal.</returns>
    /// <remarks>
    /// <para>
    /// <strong>The fence is raised before the history is read, and that order is the whole
    /// point of <see cref="IFlowJournal.FenceAsync"/>.</strong> If the journal only learned a
    /// token from a write, a zombie holding the previous token could still commit in the
    /// window between this node acquiring the lease and its first commit — the journal's
    /// highest seen token would still be the old one, and the write would be accepted.
    /// </para>
    /// <para>
    /// There is no separate recovery routine after this. What comes back is handed to the
    /// same <c>ExecuteAsync</c> a fresh instance uses, and the same step loop skips what has
    /// committed and runs what has not.
    /// </para>
    /// </remarks>
    public static async ValueTask<Result<DurableExecution>> ResumeAsync(
        IFlowJournal journal,
        Guid instanceId,
        FencingToken token,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journal);

        var fenced = await journal.FenceAsync(instanceId, token, cancellationToken).ConfigureAwait(false);

        if (fenced.IsFailure)
        {
            return Result.Fail<DurableExecution>(fenced.Error);
        }

        var frontier = await journal
            .ReadResumeFrontierAsync(instanceId, cancellationToken)
            .ConfigureAwait(false);

        return frontier.IsSuccess
            ? Result.Ok(new DurableExecution(journal, instanceId, token, frontier.Value))
            : Result.Fail<DurableExecution>(frontier.Error);
    }

    /// <summary>
    /// The successful row already committed for a step in a scope, or <c>null</c> if this
    /// instance has never completed it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Success, not merely presence.</strong> <see cref="ResumeFrontier.IsCommitted"/>
    /// answers "has any attempt at this key committed", which is the right question for the
    /// journal to answer and the wrong one for the loop to act on: a committed
    /// <see cref="JournalOutcome.Failure"/> row records an attempt that did <em>not</em>
    /// complete the step, and skipping it would treat a failure as work already done.
    /// </para>
    /// <para>
    /// Linear rather than indexed, because the frontier is a list a store handed back in
    /// commit order and building a dictionary over it would cost a resumed instance an
    /// allocation to save a scan a fresh instance never performs. B8 bounds this scan by the
    /// state-bag snapshot, which is why the snapshot is carried on the instance row.
    /// </para>
    /// </remarks>
    internal JournalStep? Completed(StepScope scope, int stepId)
    {
        if (Frontier is null)
        {
            return null;
        }

        var committed = Frontier.Committed;

        for (var i = 0; i < committed.Count; i++)
        {
            var step = committed[i];

            if (step.Key.StepId == stepId &&
                step.Key.Scope == scope &&
                step.Outcome == JournalOutcome.Success)
            {
                return step;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether this instance has already committed a row saying this step's compensation ran.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>docs/06-Execution-Engine.md §7</c> rule 4: compensation is itself journaled, so a
    /// crash during compensation resumes compensation rather than repeating it. This is the
    /// read half. A step whose undo committed is not put back on the rebuilt stack, so the
    /// resumed unwind continues from where the dead node stopped instead of refunding the same
    /// payment twice.
    /// </para>
    /// <para>
    /// <see cref="JournalOutcome.Compensated"/> only. A failed compensation attempt commits a
    /// <see cref="JournalOutcome.Failure"/> row, and treating that as done would be the
    /// mirror of the mistake <see cref="Completed"/> avoids — an attempt that did not undo the
    /// step recorded as though it had.
    /// </para>
    /// </remarks>
    internal bool Compensated(StepScope scope, int stepId)
    {
        if (Frontier is null)
        {
            return false;
        }

        var committed = Frontier.Committed;

        for (var i = 0; i < committed.Count; i++)
        {
            var step = committed[i];

            if (step.Key.StepId == stepId &&
                step.Key.Scope == scope &&
                step.Outcome == JournalOutcome.Compensated)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Which attempt at this step in this scope the next commit is: one more than the number
    /// already recorded.
    /// </summary>
    /// <remarks>
    /// Derived rather than remembered, for the same reason the resume position is. A retry
    /// writes a new row instead of replacing the failed one, so the attempt history survives
    /// — and the count of rows already in the journal is the only thing that can say what
    /// number the next one carries after the node that wrote them has gone away.
    /// </remarks>
    internal int NextAttempt(StepScope scope, int stepId)
    {
        if (Frontier is null)
        {
            return 1;
        }

        var committed = Frontier.Committed;
        var attempts = 0;

        for (var i = 0; i < committed.Count; i++)
        {
            var key = committed[i].Key;

            if (key.StepId == stepId && key.Scope == scope)
            {
                attempts++;
            }
        }

        return attempts + 1;
    }

    /// <summary>
    /// The session a composed sub-flow runs under: its own instance, the same journal, the
    /// same lease token, and no history of its own.
    /// </summary>
    /// <remarks>
    /// ADR-0015's third schema commitment. A sub-flow child is its own <c>flow_instance</c>
    /// row rather than steps spliced into the parent's history — WP-33 refused splicing at
    /// compile time because it would make the parent's manifest claim the child's
    /// capabilities and discard the child's deadline and profile, and splicing in the journal
    /// would reintroduce that untruth one layer down. It is also the only shape in which a
    /// detached child, which outlives the step that started it, has anywhere to live.
    /// </remarks>
    internal DurableExecution ForChild(Guid childInstanceId) =>
        new(Journal, childInstanceId, Token, null);
}
