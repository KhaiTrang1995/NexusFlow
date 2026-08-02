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
    private FlowSignal? _pending;

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
    /// The instant recorded for the wait at this <c>(scope, step)</c>, or <c>null</c> when the
    /// instance was not found parked there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Identity is what makes the recorded instant readable.</strong> One row carries
    /// one wait, and a flow may declare several: an instance whose first wait was satisfied
    /// late, reaching its second, would read an instant already in the past and walk straight
    /// through a wait that had not started. Matching the step and the iteration is what makes
    /// that unrepresentable rather than unlikely.
    /// </para>
    /// <para>
    /// A miss is not an error — it is "this wait is being reached for the first time", which is
    /// what a fresh instance and a resumed one past its first wait both are — so the loop arms
    /// it from the clock.
    /// </para>
    /// </remarks>
    internal DateTimeOffset? RecordedWake(StepScope scope, int stepId) =>
        Frontier?.Instance.Wake is { } wake && wake.Scope == scope && wake.StepId == stepId
            ? wake.At
            : null;

    /// <summary>
    /// The signal this invocation carries into the instance, or <c>null</c> when it carries
    /// none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>One signal per invocation, and it is consumed by the first suspension point
    /// that asked for it.</strong> A flow that waits twice for the same identity is resumed
    /// twice, once per delivery — which is the only reading under which "the signal that
    /// arrived satisfied the wait that was open" stays true. Delivering a batch would need the
    /// engine to decide which wait each one belongs to, and the journal already answers that:
    /// the open wait is the first <c>AwaitSignal</c> with no committed row.
    /// </para>
    /// <para>
    /// A resume with no signal is exactly what <c>FlowRecoveryScan</c> issues. Such an
    /// invocation runs the instance forward to its next suspension point and stops there
    /// again, which is why picking up a suspended instance is harmless rather than a way of
    /// skipping the wait.
    /// </para>
    /// </remarks>
    public FlowSignal? PendingSignal => _pending;

    /// <summary>
    /// Attaches a signal to this invocation.
    /// </summary>
    /// <param name="signal">The signal being delivered.</param>
    /// <returns>The same session, so a caller can chain it onto <c>ResumeAsync</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="signal"/> is null.</exception>
    /// <remarks>
    /// On the session rather than on <c>ExecuteAsync</c>'s parameter list, so that delivering a
    /// signal reaches the engine through the <em>same</em> overload a recovery scan reaches it
    /// through. A second entry point taking a signal would be the second resume path
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0015-journal-schema-and-durable-execution.md">ADR-0015</a>
    /// rejected a second engine to avoid.
    /// </remarks>
    public DurableExecution WithSignal(FlowSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);

        _pending = signal;

        return this;
    }

    /// <summary>
    /// Hands over the pending signal if it is the one this suspension point waits for.
    /// </summary>
    /// <param name="signalType">The identity the plan's node carries.</param>
    /// <returns>The signal, or <c>null</c> when none was delivered or it is a different one.</returns>
    /// <remarks>
    /// Taken rather than read, so that a flow declaring the same signal at two suspension
    /// points does not satisfy both from one delivery — the second wait is still open, and the
    /// instance suspends again at it.
    /// </remarks>
    internal FlowSignal? TakeSignal(string? signalType)
    {
        if (_pending is not { } pending ||
            !string.Equals(pending.SignalType, signalType, StringComparison.Ordinal))
        {
            return null;
        }

        _pending = null;

        return pending;
    }

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
    /// The lowest attempt of a poll whose body has not committed, and when the first one did.
    /// </summary>
    /// <param name="scope">The scope the poll node itself runs in.</param>
    /// <param name="bodyStepId">The polled capability's flat index.</param>
    /// <param name="firstAt">
    /// When the first attempt <em>ran</em>, or <c>null</c> when none has — which is the same
    /// fact as a return of zero, and is returned beside it because both come from one scan.
    /// </param>
    /// <returns>How many attempts have completed, which is also the number of the next one.</returns>
    /// <remarks>
    /// <para>
    /// <strong>This is what makes a poll's bound survive the node that started it.</strong> The
    /// attempt number and the instant the polling began are both facts on committed rows —
    /// <c>flow_step</c> keyed by the iteration scope, exactly as a <c>ForEach</c> element's row
    /// is — so a poll resumed on a different node an hour later knows which attempt it is on
    /// and how much of its budget is left without anything having been remembered.
    /// </para>
    /// <para>
    /// <strong>The instant is the one the step itself read, and not the one the store stamped
    /// on the row.</strong> <see cref="NondeterminismCapture.UtcNow"/> is
    /// <c>FlowContext.UtcNow</c> captured for replay — the engine's own <c>IClock</c>, the same
    /// one that decides whether the next attempt is due — and every journaled capability step
    /// has one, because the loop's deadline check reads the clock before the step is
    /// dispatched. <see cref="JournalStep.CommittedAt"/> comes from the store: PostgreSQL's
    /// <c>now()</c>, a second clock on a second machine. Bounding a loop by the difference
    /// between two clocks is the ambient read <c>FLOWX1007</c> forbids a capability, performed
    /// by the engine instead — and under a test clock, or a host whose <c>IClock</c> is
    /// deliberately not wall-clock, the difference is not a small one.
    /// </para>
    /// <para>
    /// The store's stamp is the fallback and nothing more: it is the only other instant on the
    /// row, and a poll whose first attempt somehow recorded no capture is better bounded
    /// approximately than not at all.
    /// </para>
    /// <para>
    /// Linear and from zero, for <see cref="Completed"/>'s reason: the frontier is a list in
    /// commit order, an index over it would cost every resumed instance an allocation, and the
    /// attempts a capped exponential makes inside any tolerable timeout are tens rather than
    /// thousands.
    /// </para>
    /// </remarks>
    internal int PollAttemptsMade(StepScope scope, int bodyStepId, out DateTimeOffset? firstAt)
    {
        firstAt = null;

        if (Frontier is null)
        {
            return 0;
        }

        var made = 0;

        while (Completed(scope.Element(made), bodyStepId) is { } attempt)
        {
            if (made == 0)
            {
                firstAt = attempt.Nondeterminism.UtcNow ?? attempt.CommittedAt;
            }

            made++;
        }

        return made;
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
