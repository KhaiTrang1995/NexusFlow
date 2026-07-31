namespace FlowX.Conformance.InMemory;

/// <summary>
/// The reference <see cref="IRecoveryIndex"/>: the one query a recovery scan needs, over the
/// reference journal's instances.
/// </summary>
/// <remarks>
/// <para>
/// A separate class over <see cref="InMemoryFlowJournal"/> rather than a member on it, for the
/// reason ADR-0016 gives for <c>PostgresRecoveryIndex</c> being separate from
/// <c>PostgresFlowJournal</c>: the index was split out of <see cref="IFlowJournal"/> because a
/// scan is not part of executing an instance, and a reference implementation that folded them
/// back together would be a reference for a shape the contract does not have.
/// </para>
/// <para>
/// It reads the journal's instances the way the PostgreSQL adapter reads
/// <c>flow_instance</c> — the same one table, written by the same writes, with no second store
/// to fall out of step with.
/// </para>
/// </remarks>
public sealed class InMemoryRecoveryIndex : IRecoveryIndex
{
    private readonly InMemoryFlowJournal _journal;

    /// <summary>Creates an index over a journal's instances.</summary>
    /// <param name="journal">The journal whose rows this answers from.</param>
    /// <exception cref="ArgumentNullException"><paramref name="journal"/> is null.</exception>
    public InMemoryRecoveryIndex(InMemoryFlowJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);

        _journal = journal;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Answers "looks abandoned", and cannot answer more: leases live in
    /// <see cref="InMemoryLeaseStore"/>, which this does not read and which a deployment could
    /// site in another technology altogether. <see cref="ILeaseStore.AcquireAsync"/> is the
    /// arbiter, and a candidate returned here that another node then wins is a skip rather than
    /// a wasted query.
    /// </remarks>
    public ValueTask<Result<IReadOnlyList<AbandonedInstance>>> ListAbandonedAsync(
        AbandonedInstanceQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        if (query.Limit <= 0)
        {
            // "At most none" is a question with an answer, and it is this one. It reaches a
            // store as a node's remaining recovery capacity, computed by arithmetic that can
            // slip. Letting it through to a slice — or, in the PostgreSQL adapter, to a
            // negative LIMIT — turns that slip into an exception, which is the one thing an
            // exception from a store is supposed to mean: it could not be reached (ADR-0007).
            return new(Result.Ok<IReadOnlyList<AbandonedInstance>>(Array.Empty<AbandonedInstance>()));
        }

        var candidates = new List<AbandonedInstance>();

        foreach (var record in _journal.Instances)
        {
            if (!IsAbandonable(record.State) || record.UpdatedAt >= query.IdleBefore)
            {
                continue;
            }

            if (query.TenantId is not null && query.TenantId != record.TenantId)
            {
                continue;
            }

            candidates.Add(new AbandonedInstance(
                record.InstanceId,
                record.FlowId,
                record.FlowVersion,
                record.TenantId,
                record.State,
                record.UpdatedAt));
        }

        // Oldest first, as the contract asks: a page that advances rather than one that
        // returns the same head for ever.
        candidates.Sort(static (left, right) => left.UpdatedAt.CompareTo(right.UpdatedAt));

        return new(Result.Ok<IReadOnlyList<AbandonedInstance>>(
            candidates.Count <= query.Limit ? candidates : candidates[..query.Limit]));
    }

    /// <summary>
    /// The three states a node can die holding, and not the fourth that is unfinished.
    /// </summary>
    /// <remarks>
    /// <c>Suspended</c> is unfinished and is deliberately absent: it means "waiting for a
    /// signal, a timer or a child flow", so no node holds it and none died holding it. A sweep
    /// that took it over would resume a flow that is parked by design, fence out whatever
    /// eventually delivers the signal, and — because a parked instance is stale by definition —
    /// do it again on every sweep for as long as the instance waits.
    /// </remarks>
    private static bool IsAbandonable(FlowInstanceState state) => state
        is FlowInstanceState.Pending
        or FlowInstanceState.Running
        or FlowInstanceState.Compensating;
}

/// <summary>
/// The reference journal, the reference index over it, and the writes that arrange a candidate.
/// </summary>
/// <remarks>
/// The arrangement goes through the journal's own contract — a commit for the states a running
/// flow passes through, a completion for the states it ends in — so nothing the suite asserts
/// rests on a record shape <see cref="InMemoryFlowJournal"/> would not produce. Only the
/// coldness is applied out of band, which is the exception <see cref="RecoveryStore"/>
/// describes and the PostgreSQL fixture takes as well.
/// </remarks>
public sealed class InMemoryRecoveryStore : RecoveryStore
{
    private readonly InMemoryFlowJournal _journal = new();
    private readonly IRecoveryIndex _index;

    /// <summary>Creates a store served by the reference index.</summary>
    public InMemoryRecoveryStore() => _index = new InMemoryRecoveryIndex(_journal);

    /// <summary>Creates a store served by some other index over the same journal.</summary>
    /// <param name="index">Builds the index under test from the journal it reads.</param>
    /// <remarks>
    /// The seam <c>TheSuiteRejectsAStoreThatIsWrongTests</c> uses to run a deliberately broken
    /// index through this same arrangement. Keeping the arrangement in one place is what makes
    /// that comparison mean something: the naive index differs from the reference one in what
    /// it answers, and in nothing else.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="index"/> is null.</exception>
    public InMemoryRecoveryStore(Func<InMemoryFlowJournal, IRecoveryIndex> index)
    {
        ArgumentNullException.ThrowIfNull(index);

        _index = index(_journal);
    }

    /// <inheritdoc />
    public override IRecoveryIndex Index => _index;

    /// <summary>The journal the index reads, for a test that needs to write more than a state.</summary>
    public InMemoryFlowJournal Journal => _journal;

    /// <inheritdoc />
    public override async ValueTask<Guid> AbandonAsync(
        FlowInstanceState state,
        TimeSpan idleFor,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        var instance = Guid.NewGuid();

        var started = await _journal.StartAsync(
            new FlowInstanceStart
            {
                InstanceId = instance,
                FlowId = FlowId,
                FlowVersion = FlowVersion,
                TenantId = tenantId,
                Token = new FencingToken(1),
            },
            cancellationToken);

        Ensure(started.IsSuccess, started.IsFailure ? started.Error.ToString() : string.Empty);

        await MoveAsync(instance, state, cancellationToken);

        if (idleFor > TimeSpan.Zero)
        {
            Ensure(
                _journal.Backdate(instance, DateTimeOffset.UtcNow - idleFor),
                "the instance was just opened, so it is there to be aged.");
        }

        return instance;
    }

    /// <summary>Moves a freshly opened instance to the state under test.</summary>
    private async ValueTask MoveAsync(
        Guid instance,
        FlowInstanceState state,
        CancellationToken cancellationToken)
    {
        if (state is FlowInstanceState.Pending)
        {
            return;
        }

        if (IsTerminal(state))
        {
            var completed = await _journal.CompleteAsync(
                instance, new FencingToken(1), state, JournalPayload.Empty, cancellationToken);

            Ensure(completed.IsSuccess, completed.IsFailure ? completed.Error.ToString() : string.Empty);

            return;
        }

        var committed = await _journal.CommitAsync(
            new StepCommit
            {
                Key = StepKey.First(instance, 0),
                Token = new FencingToken(1),
                CapabilityId = "order.validate",
                CapabilityVersion = "1.0.0",
                Outcome = JournalOutcome.Success,
                State = state,
            },
            cancellationToken);

        Ensure(committed.IsSuccess, committed.IsFailure ? committed.Error.ToString() : string.Empty);
    }

    private static bool IsTerminal(FlowInstanceState state) => state
        is FlowInstanceState.Completed
        or FlowInstanceState.Failed
        or FlowInstanceState.TimedOut
        or FlowInstanceState.CompensationFailed;

    /// <summary>
    /// Fails the arrangement rather than the assertion, so a fixture that could not set up says
    /// so instead of being reported as a store that answered wrongly.
    /// </summary>
    private static void Ensure(bool condition, string because)
    {
        if (!condition)
        {
            throw new InvalidOperationException(
                $"The reference store could not arrange the fixture: {because}");
        }
    }
}
