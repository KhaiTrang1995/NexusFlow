using Shouldly;
using Xunit;

namespace FlowX.Conformance;

/// <summary>
/// Proves the conformance suite can fail, by running it against stores that are wrong in the
/// ways a first implementation is wrong.
/// </summary>
/// <remarks>
/// <para>
/// A suite that only ever runs against an implementation written by the same person, on the
/// same day, from the same reading of the contract, is a suite whose green is uninformative.
/// This repository has shipped a race test that could not fail and a generator harness that
/// never compiled its output; the cost of that is not the missing coverage, it is that
/// nobody could tell.
/// </para>
/// <para>
/// The stores below are naive rather than absurd. Each defect is one a store author reaches
/// by reading <see cref="IFlowJournal"/> or <see cref="ILeaseStore"/> and implementing the
/// obvious thing: key on the step, upsert on conflict, trust the writer's token, answer
/// "where was I" from the column named <c>resume_from_step</c>, restart a counter when a
/// lease is given back. Each is caught, and the test below names the assertion that catches
/// it — because a suite that fails without saying which guarantee broke is a suite people
/// re-run until it goes green.
/// </para>
/// <para>
/// The last two tests are the control: the naive stores pass the assertions they get right.
/// A suite that rejected everything would be as uninformative as one that accepted
/// everything, and the difference matters exactly here.
/// </para>
/// </remarks>
public sealed class TheSuiteRejectsAStoreThatIsWrongTests
{
    /// <summary>
    /// A journal that trusts the writer's fencing token is rejected by name.
    /// </summary>
    /// <remarks>
    /// This is the store WP-51 names: "a deliberately broken store — one that accepts a stale
    /// fencing token — which the suite must reject by name". It is also the defect with the
    /// worst consequence, because nothing else in the system will notice it: the flow runs,
    /// the rows appear, and two nodes write the same instance.
    /// </remarks>
    [Fact]
    public async Task AJournalThatAcceptsAStaleFencingTokenFailsAStaleFencingTokenIsRejected()
    {
        var suite = new NaiveJournalUnderTest();

        var failure = await CaughtByAsync(
            nameof(suite.AStaleFencingTokenIsRejected), suite.AStaleFencingTokenIsRejected);

        failure.Message
            .Contains("node-1 lost the lease while it was paused", StringComparison.Ordinal)
            .ShouldBeTrue(
                "the failure must say which guarantee broke, not merely that something did. " +
                $"It said: {failure.Message}");
    }

    /// <summary>A journal keyed on (instance, step, attempt) is rejected by name.</summary>
    /// <remarks>
    /// The schema as drawn in <c>docs/11-Distributed-Runtime.md §2</c>, which is exactly what
    /// a store author implementing from that document would build. It was correct when a flow
    /// was a straight line and stopped being correct when <c>ForEach</c> shipped.
    /// </remarks>
    [Fact]
    public async Task AScopeBlindJournalFailsTheStepKeyIncludesTheScope()
    {
        var suite = new NaiveJournalUnderTest();

        var failure = await CaughtByAsync(
            nameof(suite.TheStepKeyIncludesTheScope), suite.TheStepKeyIncludesTheScope);

        failure.Message.ShouldContain("four scopes committed step 7");
    }

    /// <summary>A journal that upserts is rejected by name.</summary>
    [Fact]
    public async Task AnUpsertingJournalFailsARepeatedKeyIsRefusedRatherThanOverwritten()
    {
        var suite = new NaiveJournalUnderTest();

        var failure = await CaughtByAsync(
            nameof(suite.ARepeatedKeyIsRefusedRatherThanOverwritten),
            suite.ARepeatedKeyIsRefusedRatherThanOverwritten);

        failure.Message.ShouldContain("accepted the call instead");
    }

    /// <summary>A journal that answers the frontier from the hint is rejected by name.</summary>
    /// <remarks>
    /// The most sympathetic defect of the set: the column is called <c>resume_from_step</c>,
    /// so answering "where do I resume" from it is the reading the name invites. It is also
    /// the one that produces a wrong answer only for flows that fork, which is to say only in
    /// production.
    /// </remarks>
    [Fact]
    public async Task AJournalThatTrustsTheHintFailsTheResumeHintIsNotTheResumeCursor()
    {
        var suite = new NaiveJournalUnderTest();

        var failure = await CaughtByAsync(
            nameof(suite.TheResumeHintIsNotTheResumeCursor), suite.TheResumeHintIsNotTheResumeCursor);

        failure.Message.ShouldContain("derived from rows");
    }

    /// <summary>A lease store that restarts its token counter is rejected by name.</summary>
    /// <remarks>
    /// Passes every exclusivity test and every expiry test. The only thing it gets wrong is
    /// the number, and the number is the whole of ADR-0006's safety argument.
    /// </remarks>
    [Fact]
    public async Task ALeaseStoreThatResetsItsCounterFailsEveryAcquisitionIssuesAStrictlyGreaterToken()
    {
        var suite = new NaiveLeaseStoreUnderTest();

        var failure = await CaughtByAsync(
            nameof(suite.EveryAcquisitionIssuesAStrictlyGreaterToken),
            suite.EveryAcquisitionIssuesAStrictlyGreaterToken);

        failure.Message.ShouldContain("never restarts");
    }

    /// <summary>A lease store that lets any token release is rejected by name.</summary>
    [Fact]
    public async Task ALeaseStoreThatIgnoresTheTokenOnReleaseFailsASupersededTokenCannotRelease()
    {
        var suite = new NaiveLeaseStoreUnderTest();

        var failure = await CaughtByAsync(
            nameof(suite.ASupersededTokenCannotRelease), suite.ASupersededTokenCannotRelease);

        failure.Message.ShouldContain("no longer owns anything to release");
    }

    /// <summary>The control: the naive journal passes what it gets right.</summary>
    /// <remarks>
    /// Without this, the six tests above would be satisfied by a store that threw on every
    /// call, and the suite would be shown to reject rather than to discriminate.
    /// </remarks>
    [Fact]
    public async Task TheNaiveJournalStillPassesTheAssertionsItSatisfies()
    {
        var suite = new NaiveJournalUnderTest();

        await suite.APayloadIsStoredAsTheGeneratedContextWroteIt();
        await suite.ASensitiveMemberIsNotWrittenToTheJournal();
        await suite.HistoryIsReadInCommitOrder();
    }

    /// <summary>The control, for the lease half.</summary>
    [Fact]
    public async Task TheNaiveLeaseStoreStillPassesTheAssertionsItSatisfies()
    {
        var suite = new NaiveLeaseStoreUnderTest();

        await suite.AcquiringAnUnheldInstanceGrantsIt();
        await suite.ALiveLeaseCannotBeAcquiredAgain();
        await suite.AnExpiredLeaseIsAcquirableByAnotherNode();
    }

    /// <summary>
    /// Runs one conformance assertion and returns the failure it produced.
    /// </summary>
    /// <remarks>
    /// A conformance test signals a violation by throwing <see cref="ShouldAssertException"/>,
    /// so catching exactly that type — and nothing else — is what distinguishes "the suite
    /// caught the defect" from "the store threw". A store that crashes is a different finding
    /// and must not be reported as a guarantee being enforced.
    /// </remarks>
    private static async Task<ShouldAssertException> CaughtByAsync(string assertion, Func<Task> run)
    {
        ShouldAssertException? failure = null;

        try
        {
            await run();
        }
        catch (ShouldAssertException caught)
        {
            failure = caught;
        }

        failure.ShouldNotBeNull(
            $"{assertion} passed against a store that violates the guarantee it describes. " +
            "A suite that accepts a store it was written to reject is not a gate.");

        return failure;
    }

    private sealed class NaiveJournalUnderTest : JournalConformance
    {
        protected override ValueTask<IFlowJournal> CreateJournalAsync() => new(new NaiveJournal());
    }

    private sealed class NaiveLeaseStoreUnderTest : LeaseStoreConformance
    {
        protected override ValueTask<ILeaseStore> CreateStoreAsync() => new(new NaiveLeaseStore());
    }

    /// <summary>
    /// A journal built from the interface alone, and from the schema as drawn before
    /// <c>ForEach</c>, <c>Parallel</c> and <c>SubFlow</c> shipped.
    /// </summary>
    /// <remarks>
    /// Four defects, all of them plausible: the key ignores the scope, a repeated key upserts,
    /// the fencing token is recorded rather than checked, and the resume frontier is derived
    /// from <c>resume_from_step</c> when that column has a value.
    /// </remarks>
    private sealed class NaiveJournal : IFlowJournal
    {
        private readonly Dictionary<Guid, FlowInstanceRecord> _instances = [];
        private readonly Dictionary<Guid, List<JournalStep>> _steps = [];
        private readonly Dictionary<Guid, List<OutboxRecord>> _outbox = [];
        private long _sequence;

        public ValueTask<Result<FlowInstanceRecord>> StartAsync(
            FlowInstanceStart start,
            CancellationToken cancellationToken)
        {
            if (_instances.ContainsKey(start.InstanceId))
            {
                return new(Result.Fail<FlowInstanceRecord>(DurabilityErrors.InstanceExists(start.InstanceId)));
            }

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
                ParentInstanceId = start.ParentInstanceId,
                ParentScope = start.ParentScope,
                ParentStepId = start.ParentStepId,
            };

            _instances[start.InstanceId] = record;
            _steps[start.InstanceId] = [];
            _outbox[start.InstanceId] = [];

            return new(Result.Ok(record));
        }

        // Defect: the token is recorded, never compared.
        public ValueTask<Result<FencingToken>> FenceAsync(
            Guid instanceId,
            FencingToken token,
            CancellationToken cancellationToken)
        {
            if (!_instances.TryGetValue(instanceId, out var record))
            {
                return new(Result.Fail<FencingToken>(DurabilityErrors.InstanceNotFound(instanceId)));
            }

            _instances[instanceId] = record with { Fence = token };

            return new(Result.Ok(token));
        }

        public ValueTask<Result<JournalStep>> CommitAsync(
            StepCommit commit,
            CancellationToken cancellationToken)
        {
            if (!_instances.TryGetValue(commit.Key.InstanceId, out var record))
            {
                return new(Result.Fail<JournalStep>(DurabilityErrors.InstanceNotFound(commit.Key.InstanceId)));
            }

            var steps = _steps[commit.Key.InstanceId];

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
                CommittedAt = DateTimeOffset.UtcNow,
            };

            // Defect: (instance, step, attempt) is the key, and a repeat replaces the row.
            var existing = steps.FindIndex(s =>
                s.Key.StepId == commit.Key.StepId && s.Key.Attempt == commit.Key.Attempt);

            if (existing >= 0)
            {
                steps[existing] = step;
            }
            else
            {
                steps.Add(step);
            }

            foreach (var write in commit.Outbox)
            {
                _outbox[commit.Key.InstanceId].Add(new OutboxRecord
                {
                    EventId = Guid.NewGuid(),
                    InstanceId = commit.Key.InstanceId,
                    Type = write.Type,
                    SchemaVersion = write.SchemaVersion,
                    PartitionKey = write.PartitionKey,
                    PayloadJson = write.Payload.ToJson(),
                });
            }

            _instances[commit.Key.InstanceId] = record with
            {
                Fence = commit.Token,
                State = commit.State ?? FlowInstanceState.Running,
                StateBagJson = commit.StateBag.IsEmpty ? record.StateBagJson : commit.StateBag.ToJson(),
                ResumeHint = commit.ResumeHint ?? record.ResumeHint,
            };

            return new(Result.Ok(step));
        }

        public ValueTask<Result<FlowInstanceRecord>> CompleteAsync(
            Guid instanceId,
            FencingToken token,
            FlowInstanceState state,
            JournalPayload stateBag,
            CancellationToken cancellationToken)
        {
            if (!_instances.TryGetValue(instanceId, out var record))
            {
                return new(Result.Fail<FlowInstanceRecord>(DurabilityErrors.InstanceNotFound(instanceId)));
            }

            var completed = record with
            {
                Fence = token,
                State = state,
                StateBagJson = stateBag.IsEmpty ? record.StateBagJson : stateBag.ToJson(),
            };

            _instances[instanceId] = completed;

            return new(Result.Ok(completed));
        }

        public ValueTask<Result<FlowInstanceRecord>> ReadInstanceAsync(
            Guid instanceId,
            CancellationToken cancellationToken) =>
            new(_instances.TryGetValue(instanceId, out var record)
                ? Result.Ok(record)
                : Result.Fail<FlowInstanceRecord>(DurabilityErrors.InstanceNotFound(instanceId)));

        public ValueTask<Result<ResumeFrontier>> ReadResumeFrontierAsync(
            Guid instanceId,
            CancellationToken cancellationToken)
        {
            if (!_instances.TryGetValue(instanceId, out var record))
            {
                return new(Result.Fail<ResumeFrontier>(DurabilityErrors.InstanceNotFound(instanceId)));
            }

            // Defect: the column is called resume_from_step, so it is read as the cursor.
            var committed = record.ResumeHint is { } hint
                ? Enumerable.Range(0, hint + 1)
                    .Select(stepId => new JournalStep
                    {
                        Key = StepKey.First(instanceId, stepId),
                        Sequence = stepId,
                        CapabilityId = "unknown",
                        CapabilityVersion = "0.0.0",
                        Outcome = JournalOutcome.Success,
                    })
                    .ToList()
                : _steps[instanceId];

            return new(Result.Ok(new ResumeFrontier { Instance = record, Committed = committed }));
        }

        public ValueTask<Result<IReadOnlyList<OutboxRecord>>> ReadOutboxAsync(
            Guid instanceId,
            CancellationToken cancellationToken) =>
            new(_outbox.TryGetValue(instanceId, out var rows)
                ? Result.Ok<IReadOnlyList<OutboxRecord>>(rows)
                : Result.Fail<IReadOnlyList<OutboxRecord>>(DurabilityErrors.InstanceNotFound(instanceId)));
    }

    /// <summary>
    /// A lease store that is exclusive and expires correctly, and gets the token wrong.
    /// </summary>
    /// <remarks>
    /// Two defects: the token counter restarts when a lease is released, and a release is
    /// accepted from any caller. Neither is visible in a single-node test.
    /// </remarks>
    private sealed class NaiveLeaseStore : ILeaseStore
    {
        private readonly Dictionary<Guid, FlowLease> _leases = [];
        private readonly Dictionary<Guid, long> _issued = [];

        public ValueTask<Result<FlowLease>> AcquireAsync(
            Guid instanceId,
            string ownerNode,
            TimeSpan ttl,
            CancellationToken cancellationToken)
        {
            if (Live(instanceId) is { } live)
            {
                return new(Result.Fail<FlowLease>(DurabilityErrors.LeaseHeld(instanceId, live.OwnerNode)));
            }

            var next = _issued.TryGetValue(instanceId, out var last) ? last + 1 : 1;
            _issued[instanceId] = next;

            var lease = new FlowLease(instanceId, ownerNode, new FencingToken(next), DateTimeOffset.UtcNow + ttl);
            _leases[instanceId] = lease;

            return new(Result.Ok(lease));
        }

        public ValueTask<Result<FlowLease>> RenewAsync(
            FlowLease lease,
            TimeSpan ttl,
            CancellationToken cancellationToken)
        {
            if (Live(lease.InstanceId) is not { } live || live.Token != lease.Token)
            {
                return new(Result.Fail<FlowLease>(DurabilityErrors.LeaseLost(lease.InstanceId, lease.Token)));
            }

            var renewed = live with { ExpiresAt = DateTimeOffset.UtcNow + ttl };
            _leases[lease.InstanceId] = renewed;

            return new(Result.Ok(renewed));
        }

        // Defect: any caller may release, and releasing restarts the token sequence.
        public ValueTask<Result<bool>> ReleaseAsync(FlowLease lease, CancellationToken cancellationToken)
        {
            _leases.Remove(lease.InstanceId);
            _issued.Remove(lease.InstanceId);

            return new(Result.Ok(true));
        }

        public ValueTask<Result<FlowLease>> ReadAsync(Guid instanceId, CancellationToken cancellationToken) =>
            new(Live(instanceId) is { } live
                ? Result.Ok(live)
                : Result.Fail<FlowLease>(DurabilityErrors.LeaseNotHeld(instanceId)));

        private FlowLease? Live(Guid instanceId) =>
            _leases.TryGetValue(instanceId, out var lease) && lease.ExpiresAt > DateTimeOffset.UtcNow
                ? lease
                : null;
    }
}
