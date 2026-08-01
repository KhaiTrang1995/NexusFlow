namespace FlowX.Conformance.InMemory;

/// <summary>
/// The reference <see cref="ITimerIndex"/>: the one query a timer sweep needs, over the
/// reference journal's instances.
/// </summary>
/// <remarks>
/// <para>
/// A separate class over <see cref="InMemoryFlowJournal"/> for the reason
/// <see cref="InMemoryRecoveryIndex"/> is one, and separate from <em>that</em> for the reason
/// <c>PostgresTimerIndex</c> is separate from <c>PostgresRecoveryIndex</c>: the two ask
/// opposite questions of disjoint sets of rows. One looks for instances a node died holding
/// and excludes <see cref="FlowInstanceState.Suspended"/> on purpose; this looks for exactly
/// the state that one excludes.
/// </para>
/// <para>
/// It reads the journal's instances the way the PostgreSQL adapter reads
/// <c>flow_instance</c> — the same one table, written by the same writes, with no second store
/// to fall out of step with. A durable timer is a value on the instance row, so there is
/// nothing else for it to read.
/// </para>
/// </remarks>
public sealed class InMemoryTimerIndex : ITimerIndex
{
    private readonly InMemoryFlowJournal _journal;

    /// <summary>Creates an index over a journal's instances.</summary>
    /// <param name="journal">The journal whose rows this answers from.</param>
    /// <exception cref="ArgumentNullException"><paramref name="journal"/> is null.</exception>
    public InMemoryTimerIndex(InMemoryFlowJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);

        _journal = journal;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Answers "is due", and deliberately not "should run". Whether the wait an instance is
    /// parked at is actually over is a question about the compiled plan, and the step loop is
    /// what holds one — so every candidate here is resumed through the same
    /// <c>FlowHost.ResumeAsync</c> a signal and a recovery scan use, and the engine decides.
    /// </remarks>
    public ValueTask<Result<IReadOnlyList<DueInstance>>> ListDueAsync(
        DueInstanceQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        if (query.Limit <= 0)
        {
            // "At most none" is a question with an answer, and it is this one — the same
            // answer InMemoryRecoveryIndex gives, for the same reason: the value reaches a
            // store as a node's remaining capacity, computed by arithmetic that can slip.
            return new(Result.Ok<IReadOnlyList<DueInstance>>(Array.Empty<DueInstance>()));
        }

        var candidates = new List<DueInstance>();

        foreach (var record in _journal.Instances)
        {
            if (record.State != FlowInstanceState.Suspended || record.Wake is not { } wake)
            {
                continue;
            }

            // At or before, not strictly before. A wait is over at the instant it is due and
            // the engine compares the same way; a strict inequality here would leave an
            // instance whose instant is exactly now unfetched until the next sweep, and would
            // disagree with the loop about a boundary a suite can hit exactly with a fake
            // clock.
            if (wake.At > query.DueBefore)
            {
                continue;
            }

            if (query.TenantId is not null && query.TenantId != record.TenantId)
            {
                continue;
            }

            candidates.Add(new DueInstance(
                record.InstanceId,
                record.FlowId,
                record.FlowVersion,
                record.TenantId,
                wake.At));
        }

        // Longest overdue first, as the contract asks: a page that advances rather than one
        // that returns the same head for ever.
        candidates.Sort(static (left, right) => left.WakeAt.CompareTo(right.WakeAt));

        return new(Result.Ok<IReadOnlyList<DueInstance>>(
            candidates.Count <= query.Limit ? candidates : candidates[..query.Limit]));
    }
}
