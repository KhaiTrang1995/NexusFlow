namespace FlowX.Conformance.InMemory;

/// <summary>
/// The reference <see cref="IFlowJournal"/>: everything the conformance suite requires and
/// nothing a durable deployment needs.
/// </summary>
/// <remarks>
/// <para>
/// Its job is to prove the suite is passable — a suite nothing has ever passed is a
/// specification, not a gate — and to be the thing a store author reads when a conformance
/// failure message is not enough. It is deliberately the simplest implementation that is
/// correct: one lock, one dictionary, no batching, no retention, no partitioning.
/// </para>
/// <para>
/// Everything here is in a process's memory, so it survives nothing. That is the honest
/// shape for a reference: a store that persisted would invite being used, and the first
/// question of P2 is whether the contract is right, not whether this file is fast.
/// </para>
/// </remarks>
public sealed class InMemoryFlowJournal : IFlowJournal
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _instances = [];
    private readonly List<Guid> _opened = [];
    private long _sequence;

    /// <summary>
    /// Every instance this journal holds, in the order it was asked to open them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not part of <see cref="IFlowJournal"/>, and deliberately so: "list the instances" is
    /// the query <see cref="IRecoveryIndex"/> was split out of the journal to serve, and a
    /// store that could not serve it cheaply is a store that implements no index rather than
    /// a journal with a missing member. This is what <see cref="InMemoryRecoveryIndex"/> reads
    /// — the same one table the PostgreSQL adapter's query reads, expressed the only way a
    /// dictionary can express it.
    /// </para>
    /// <para>
    /// Insertion order, rather than the dictionary's, because it is order the caller can
    /// predict. It is emphatically not staleness order — an index that returned this list as
    /// it stands would fail
    /// <c>RecoveryIndexConformance.CandidatesComeBackOldestFirst</c>, which is exactly what a
    /// reference implementation should let a suite catch.
    /// </para>
    /// </remarks>
    public IReadOnlyList<FlowInstanceRecord> Instances
    {
        get
        {
            lock (_gate)
            {
                return [.. _opened.Select(id => _instances[id].Record)];
            }
        }
    }

    /// <summary>
    /// Moves an instance's last-written instant into the past, so a suite can arrange a cold
    /// row without waiting for one.
    /// </summary>
    /// <param name="instanceId">The instance to age.</param>
    /// <param name="updatedAt">When the row should claim it was last written.</param>
    /// <returns>Whether the instance was there to be aged.</returns>
    /// <remarks>
    /// The counterpart of the <c>UPDATE flow_instance SET updated_at = now() - interval …</c>
    /// the PostgreSQL fixture issues, and it exists for the same reason: staleness is the
    /// premise of every recovery-index assertion, and the alternative is a test that sleeps for
    /// a lease TTL. It is not on <see cref="IFlowJournal"/> and no contract member exposes it —
    /// nothing but a test fixture can reach it.
    /// </remarks>
    public bool Backdate(Guid instanceId, DateTimeOffset updatedAt)
    {
        lock (_gate)
        {
            if (!_instances.TryGetValue(instanceId, out var entry))
            {
                return false;
            }

            entry.Record = entry.Record with { UpdatedAt = updatedAt };

            return true;
        }
    }

    /// <inheritdoc />
    public ValueTask<Result<FlowInstanceRecord>> StartAsync(
        FlowInstanceStart start,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_instances.ContainsKey(start.InstanceId))
            {
                return Fail<FlowInstanceRecord>(DurabilityErrors.InstanceExists(start.InstanceId));
            }

            var now = DateTimeOffset.UtcNow;

            var record = new FlowInstanceRecord
            {
                InstanceId = start.InstanceId,
                FlowId = start.FlowId,
                FlowVersion = start.FlowVersion,
                State = FlowInstanceState.Pending,
                Fence = start.Token,
                TenantId = start.TenantId,
                InputJson = start.Input.ToJson(),
                CorrelationId = start.CorrelationId,
                TraceId = start.TraceId,
                DeadlineAt = start.DeadlineAt,
                ParentInstanceId = start.ParentInstanceId,
                ParentScope = start.ParentScope,
                ParentStepId = start.ParentStepId,
                CreatedAt = now,
                UpdatedAt = now,
            };

            _instances[start.InstanceId] = new Entry(record);
            _opened.Add(start.InstanceId);

            return Ok(record);
        }
    }

    /// <inheritdoc />
    public ValueTask<Result<FencingToken>> FenceAsync(
        Guid instanceId,
        FencingToken token,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_instances.TryGetValue(instanceId, out var entry))
            {
                return Fail<FencingToken>(DurabilityErrors.InstanceNotFound(instanceId));
            }

            if (token < entry.Record.Fence)
            {
                return Fail<FencingToken>(
                    DurabilityErrors.FencedOut(instanceId, token, entry.Record.Fence));
            }

            entry.Record = entry.Record with { Fence = token, UpdatedAt = DateTimeOffset.UtcNow };

            return Ok(entry.Record.Fence);
        }
    }

    /// <inheritdoc />
    public ValueTask<Result<JournalStep>> CommitAsync(
        StepCommit commit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_instances.TryGetValue(commit.Key.InstanceId, out var entry))
            {
                return Fail<JournalStep>(DurabilityErrors.InstanceNotFound(commit.Key.InstanceId));
            }

            // Every refusal is decided before anything is written, so a refused commit
            // leaves the step list, the state bag and the outbox exactly as they were.
            if (commit.Token < entry.Record.Fence)
            {
                return Fail<JournalStep>(
                    DurabilityErrors.FencedOut(commit.Key.InstanceId, commit.Token, entry.Record.Fence));
            }

            if (IsTerminal(entry.Record.State))
            {
                return Fail<JournalStep>(
                    DurabilityErrors.InstanceTerminal(commit.Key.InstanceId, entry.Record.State));
            }

            if (entry.Keys.Contains(commit.Key))
            {
                return Fail<JournalStep>(DurabilityErrors.DuplicateStep(commit.Key));
            }

            var now = DateTimeOffset.UtcNow;

            var step = new JournalStep
            {
                Key = commit.Key,
                Sequence = ++_sequence,
                CapabilityId = commit.CapabilityId,
                CapabilityVersion = commit.CapabilityVersion,
                Outcome = commit.Outcome,
                ResultJson = commit.Result.ToJson(),
                Nondeterminism = commit.Nondeterminism,
                Duration = commit.Duration,
                CommittedAt = now,
            };

            entry.Keys.Add(commit.Key);
            entry.Steps.Add(step);

            foreach (var write in commit.Outbox)
            {
                entry.Outbox.Add(new OutboxRecord
                {
                    EventId = Guid.NewGuid(),
                    InstanceId = commit.Key.InstanceId,
                    Type = write.Type,
                    SchemaVersion = write.SchemaVersion,
                    PartitionKey = write.PartitionKey,
                    PayloadJson = write.Payload.ToJson(),
                });
            }

            entry.Record = entry.Record with
            {
                Fence = commit.Token,
                State = commit.State ?? (entry.Record.State == FlowInstanceState.Pending
                    ? FlowInstanceState.Running
                    : entry.Record.State),
                StateBagJson = commit.StateBag.IsEmpty
                    ? entry.Record.StateBagJson
                    : commit.StateBag.ToJson(),
                ResumeHint = commit.ResumeHint ?? entry.Record.ResumeHint,
                UpdatedAt = now,
            };

            return Ok(step);
        }
    }

    /// <inheritdoc />
    public ValueTask<Result<FlowInstanceRecord>> CompleteAsync(
        Guid instanceId,
        FencingToken token,
        FlowInstanceState state,
        JournalPayload stateBag,
        FlowWake? wake,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stateBag);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_instances.TryGetValue(instanceId, out var entry))
            {
                return Fail<FlowInstanceRecord>(DurabilityErrors.InstanceNotFound(instanceId));
            }

            if (token < entry.Record.Fence)
            {
                return Fail<FlowInstanceRecord>(
                    DurabilityErrors.FencedOut(instanceId, token, entry.Record.Fence));
            }

            entry.Record = entry.Record with
            {
                Fence = token,
                State = state,
                StateBagJson = stateBag.IsEmpty ? entry.Record.StateBagJson : stateBag.ToJson(),

                // Assigned, not merged, and the asymmetry with the state bag above is the
                // contract rather than an oversight: an absent bag means "unchanged", an
                // absent wake means "nothing is due to wake this instance". A completed
                // instance that kept the instant it was parked at would be found by every
                // timer sweep for ever.
                Wake = wake,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            return Ok(entry.Record);
        }
    }

    /// <inheritdoc />
    public ValueTask<Result<FlowInstanceRecord>> ReadInstanceAsync(
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return _instances.TryGetValue(instanceId, out var entry)
                ? Ok(entry.Record)
                : Fail<FlowInstanceRecord>(DurabilityErrors.InstanceNotFound(instanceId));
        }
    }

    /// <inheritdoc />
    public ValueTask<Result<ResumeFrontier>> ReadResumeFrontierAsync(
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_instances.TryGetValue(instanceId, out var entry))
            {
                return Fail<ResumeFrontier>(DurabilityErrors.InstanceNotFound(instanceId));
            }

            var frontier = new ResumeFrontier
            {
                Instance = entry.Record,
                Committed = entry.Steps.OrderBy(static step => step.Sequence).ToList(),
            };

            return Ok(frontier);
        }
    }

    /// <inheritdoc />
    public ValueTask<Result<IReadOnlyList<OutboxRecord>>> ReadOutboxAsync(
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_instances.TryGetValue(instanceId, out var entry))
            {
                return Fail<IReadOnlyList<OutboxRecord>>(DurabilityErrors.InstanceNotFound(instanceId));
            }

            return Ok<IReadOnlyList<OutboxRecord>>(entry.Outbox.ToList());
        }
    }

    private static bool IsTerminal(FlowInstanceState state) => state
        is FlowInstanceState.Completed
        or FlowInstanceState.Failed
        or FlowInstanceState.TimedOut
        or FlowInstanceState.CompensationFailed;

    private static ValueTask<Result<T>> Ok<T>(T value) => new(Result.Ok(value));

    private static ValueTask<Result<T>> Fail<T>(Error error) => new(Result.Fail<T>(error));

    private sealed class Entry(FlowInstanceRecord record)
    {
        public FlowInstanceRecord Record { get; set; } = record;

        public List<JournalStep> Steps { get; } = [];

        public List<OutboxRecord> Outbox { get; } = [];

        public HashSet<StepKey> Keys { get; } = [];
    }
}
