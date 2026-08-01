using Shouldly;
using Xunit;

namespace FlowX.Conformance;

/// <summary>
/// What an <see cref="IFlowJournal"/> must do. Derive, supply a store, and the whole suite
/// runs against it unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This suite is the contract, not a test of one.</strong> A signature can stay
/// identical while behaviour drifts, and every guarantee ADR-0006 and ADR-0015 rest on —
/// append-only refusal, fenced writes, atomic commit, a derived rather than remembered
/// resume position — is behaviour. Postgres, Redis, SQL Server and a custom store are
/// "the same journal" exactly to the extent that they pass this file.
/// </para>
/// <para>
/// <strong>Every member of <see cref="IFlowJournal"/> is pinned here.</strong> That is the
/// rule the surface was designed against: a method with no conformance test is a method
/// whose behaviour nobody has decided, and the honest response is to delete it rather than
/// to write it down twice.
/// </para>
/// <para>
/// <strong>The suite can fail, and it is proved to.</strong> Passing it means nothing unless
/// a store that gets a guarantee wrong is rejected by name, so
/// <c>TheSuiteRejectsAStoreThatIsWrongTests</c> runs deliberately broken stores through
/// these same methods and asserts each one is caught by the assertion whose name says why.
/// </para>
/// </remarks>
public abstract class JournalConformance
{
    /// <summary>A fresh, empty journal. Called once per test; no state may survive between them.</summary>
    protected abstract ValueTask<IFlowJournal> CreateJournalAsync();

    /// <summary>The ambient test cancellation token.</summary>
    protected static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------- the key

    /// <summary>
    /// The primary key includes the scope, so a loop can re-enter a range without
    /// overwriting its own history.
    /// </summary>
    /// <remarks>
    /// Commitment 1 of ADR-0015, and the reason the drawn schema in
    /// <c>docs/11-Distributed-Runtime.md §2</c> is wrong: <c>ForEach</c> shipped in P1, so a
    /// 500-element loop writes step 7 five hundred times and <c>(instance, step)</c> is not
    /// unique. <c>CompensationStack</c> made this exact change from <c>index</c> to
    /// <c>(index, scope)</c> for the same reason, and the journal is not allowed to be less
    /// precise than the compensation stack that has to undo it.
    /// </remarks>
    [Fact]
    public async Task TheStepKeyIncludesTheScope()
    {
        var journal = await CreateJournalAsync();
        var instance = await StartAsync(journal);

        var body = new StepKey(instance, StepScope.Root, 7, 1);
        var firstElement = new StepKey(instance, StepScope.Root.Element(0), 7, 1);
        var secondElement = new StepKey(instance, StepScope.Root.Element(1), 7, 1);
        var nested = new StepKey(instance, StepScope.Root.Element(1).Element(2), 7, 1);

        foreach (var key in new[] { body, firstElement, secondElement, nested })
        {
            var committed = await journal.CommitAsync(Step(key), Cancellation);

            ShouldSucceed(
                committed,
                $"step 7 in scope '{key.Scope.Text}' is a different row from step 7 in every " +
                "other scope. Without the scope in the key, the second element of a ForEach " +
                "collides with the first and the flow cannot be journaled at all.");
        }

        var frontier = await ReadFrontierAsync(journal, instance);

        frontier.Committed.Count.ShouldBe(
            4,
            "four scopes committed step 7, so there are four rows. A store that keyed on " +
            "(instance, step) has kept one of them.");
    }

    /// <summary>
    /// A key that has already been committed is refused, not overwritten.
    /// </summary>
    /// <remarks>
    /// Append-only is the property the whole design rests on — "the history is the truth" is
    /// only true if history cannot be edited. A store that silently upserts passes every
    /// other test in this file and destroys the audit trail and the replay source together.
    /// </remarks>
    [Fact]
    public async Task ARepeatedKeyIsRefusedRatherThanOverwritten()
    {
        var journal = await CreateJournalAsync();
        var instance = await StartAsync(journal);
        var key = StepKey.First(instance, 3);

        var first = await journal.CommitAsync(
            Step(key) with { CapabilityId = "inventory.reserve" }, Cancellation);

        ShouldSucceed(first, "the first commit of a key is accepted.");

        var second = await journal.CommitAsync(
            Step(key) with { CapabilityId = "payment.capture" }, Cancellation);

        ShouldFailWith(
            second,
            DurabilityErrors.DuplicateStepCode,
            ErrorCategory.Conflict,
            "an append-only table cannot overwrite a row, so it must reject the write.");

        var frontier = await ReadFrontierAsync(journal, instance);

        frontier.Committed.Count.ShouldBe(1, "the refused commit added no row.");
        frontier.Committed[0].CapabilityId.ShouldBe(
            "inventory.reserve",
            "the original row is untouched. A store that overwrote it has replaced history.");
    }

    /// <summary>A retry is a new attempt on a new row, not a replacement of the failed one.</summary>
    /// <remarks>
    /// The attempt is in the key so failure history survives. A store that kept only the last
    /// attempt would make "why did this instance take four minutes" unanswerable from the
    /// journal, which is one of the three things the journal exists for.
    /// </remarks>
    [Fact]
    public async Task ARetryIsANewAttemptRatherThanAReplacement()
    {
        var journal = await CreateJournalAsync();
        var instance = await StartAsync(journal);
        var first = StepKey.First(instance, 2);

        ShouldSucceed(
            await journal.CommitAsync(
                Step(first) with { Outcome = JournalOutcome.Failure }, Cancellation),
            "the failed attempt is recorded.");

        ShouldSucceed(
            await journal.CommitAsync(Step(first.NextAttempt()), Cancellation),
            "the retry is a different key, so it is a different row.");

        var frontier = await ReadFrontierAsync(journal, instance);

        frontier.Committed.Count.ShouldBe(2, "both attempts are in the history.");
        frontier.Find(first)!.Outcome.ShouldBe(
            JournalOutcome.Failure,
            "the failed attempt keeps its outcome after the retry succeeded.");
        frontier.Find(first.NextAttempt())!.Outcome.ShouldBe(JournalOutcome.Success);
    }

    // ---------------------------------------------------------------- fencing

    /// <summary>
    /// A write carrying a token below the instance's fence is refused, terminally.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the assertion ADR-0006 exists for. A node that pauses past its TTL — a GC
    /// pause, a partition, a stopped container — wakes up believing it still owns the
    /// instance. Lease expiry alone does not stop it; the token does, and it does so without
    /// reference to any clock.
    /// </para>
    /// <para>
    /// The category matters as much as the code. <see cref="ErrorCategory.Forbidden"/> is
    /// terminal, and a fenced-out writer must abort and discard its work: retrying with the
    /// same stale token can never succeed, so a retryable classification would turn a correct
    /// refusal into a loop.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AStaleFencingTokenIsRejected()
    {
        var journal = await CreateJournalAsync();
        var instance = await StartAsync(journal, new FencingToken(7));

        ShouldSucceed(
            await journal.FenceAsync(instance, new FencingToken(8), Cancellation),
            "node-2 acquired the lease and raised the fence to 8.");

        var zombie = await journal.CommitAsync(
            Step(StepKey.First(instance, 3)) with { Token = new FencingToken(7) },
            Cancellation);

        var error = ShouldFailWith(
            zombie,
            DurabilityErrors.FencedOutCode,
            ErrorCategory.Forbidden,
            "token 7 is below the fence of 8: node-1 lost the lease while it was paused.");

        error.Category.IsTerminal().ShouldBeTrue(
            "a fenced-out writer must abort, not retry. Retrying with the same token can " +
            "never succeed, and a retryable category invites a loop.");
    }

    /// <summary>
    /// Acquisition raises the fence before the new owner writes anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The sequence in <c>docs/11-Distributed-Runtime.md §3</c> has node-2 acquire token 8,
    /// read history, and only then commit — and node-1's write with token 7 is rejected
    /// during that window. A journal that learned tokens only from writes would still have
    /// 7 as its highest and would accept it, which is split brain with an extra step.
    /// </para>
    /// <para>
    /// This is why <see cref="IFlowJournal.FenceAsync"/> is on the interface at all. The
    /// lease store and the journal are separate plugins with no shared transaction, so the
    /// token has to be carried between them by the node that won it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheFenceRisesOnAcquisitionNotOnTheFirstWrite()
    {
        var journal = await CreateJournalAsync();
        var instance = await StartAsync(journal, new FencingToken(7));

        ShouldSucceed(
            await journal.FenceAsync(instance, new FencingToken(8), Cancellation),
            "node-2 announces ownership before reading history.");

        var frontier = await ReadFrontierAsync(journal, instance);

        frontier.Committed.ShouldBeEmpty("node-2 has not committed anything yet.");

        ShouldFailWith(
            await journal.CommitAsync(
                Step(StepKey.First(instance, 3)) with { Token = new FencingToken(7) },
                Cancellation),
            DurabilityErrors.FencedOutCode,
            ErrorCategory.Forbidden,
            "the fence is 8 from the moment of acquisition, not from node-2's first commit.");
    }

    /// <summary>The fence never goes down.</summary>
    /// <remarks>
    /// A monotonic token that a store will happily lower is not monotonic. The realistic way
    /// this breaks is a recovery scan that re-announces an old token it read from a stale
    /// row.
    /// </remarks>
    [Fact]
    public async Task TheFenceCannotBeLowered()
    {
        var journal = await CreateJournalAsync();
        var instance = await StartAsync(journal, new FencingToken(4));

        ShouldFailWith(
            await journal.FenceAsync(instance, new FencingToken(3), Cancellation),
            DurabilityErrors.FencedOutCode,
            ErrorCategory.Forbidden,
            "lowering the fence would re-admit every writer it had already excluded.");

        var instanceRecord = await ReadInstanceAsync(journal, instance);

        instanceRecord.Fence.ShouldBe(new FencingToken(4), "the fence is unchanged.");
    }

    /// <summary>One lease commits many steps, so the check is not-lower rather than higher.</summary>
    /// <remarks>
    /// The obvious over-correction. A store that required a strictly increasing token per
    /// write would reject the second step of every flow, and would look correct in a test
    /// that commits once.
    /// </remarks>
    [Fact]
    public async Task TheSameTokenMayCommitManySteps()
    {
        var journal = await CreateJournalAsync();
        var instance = await StartAsync(journal, new FencingToken(5));

        for (var stepId = 0; stepId < 3; stepId++)
        {
            ShouldSucceed(
                await journal.CommitAsync(
                    Step(StepKey.First(instance, stepId)) with { Token = new FencingToken(5) },
                    Cancellation),
                "a lease is held across many steps; the fence excludes older tokens, not equal ones.");
        }
    }

    // ---------------------------------------------------------------- atomicity

    /// <summary>The step row, the state bag and the outbox rows land together.</summary>
    /// <remarks>
    /// Writing the state and publishing the event as two operations is the dual-write
    /// problem, and it is the reason the outbox is in this transaction rather than beside it.
    /// </remarks>
    [Fact]
    public async Task AStepItsStateBagAndItsOutboxRowsCommitTogether()
    {
        var journal = await CreateJournalAsync();
        var instance = await StartAsync(journal);

        var commit = Step(StepKey.First(instance, 0)) with
        {
            StateBag = Payload(new ConformanceOrder("order-1", "tok", 2)),
            Outbox =
            [
                new OutboxWrite
                {
                    Type = "order.placed",
                    SchemaVersion = "1.0.0",
                    PartitionKey = "order-1",
                    Payload = JournalPayload.Of(new ConformanceEvent("order-1"), ConformanceJson.Default.ConformanceEvent),
                },
            ],
        };

        ShouldSucceed(await journal.CommitAsync(commit, Cancellation), "the commit is accepted.");

        var record = await ReadInstanceAsync(journal, instance);
        ShouldContainText(
            record.StateBagJson, "order-1", "the state bag was committed with the step.");

        var outbox = await ReadOutboxAsync(journal, instance);
        outbox.Count.ShouldBe(1, "the event was staged in the same transaction as the step.");
        outbox[0].Type.ShouldBe("order.placed");
        outbox[0].PartitionKey.ShouldBe("order-1", "per-key ordering needs the key to survive.");
        outbox[0].PublishedAt.ShouldBeNull("a staged event is pending until a publisher sends it.");
    }

    /// <summary>A refused commit writes nothing at all.</summary>
    /// <remarks>
    /// <para>
    /// The half of atomicity that is easy to lose. A store that validates the fence after
    /// inserting the outbox row publishes an event for a step that never happened — and a
    /// zombie node is exactly the situation in which that event is wrong.
    /// </para>
    /// <para>
    /// Asserted for a fenced-out commit and for a duplicate key, because they fail at
    /// different points in a plausible implementation.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ARefusedCommitWritesNothing()
    {
        var journal = await CreateJournalAsync();
        var instance = await StartAsync(journal, new FencingToken(2));
        var key = StepKey.First(instance, 0);

        ShouldSucceed(
            await journal.CommitAsync(Step(key) with { Token = new FencingToken(2) }, Cancellation),
            "the first commit is accepted.");

        var withEvent = new OutboxWrite
        {
            Type = "order.placed",
            SchemaVersion = "1.0.0",
            Payload = JournalPayload.Of(new ConformanceEvent("order-1"), ConformanceJson.Default.ConformanceEvent),
        };

        ShouldFailWith(
            await journal.CommitAsync(
                Step(StepKey.First(instance, 1)) with
                {
                    Token = new FencingToken(1),
                    Outbox = [withEvent],
                    StateBag = Payload(new ConformanceOrder("leaked", "tok", 1)),
                },
                Cancellation),
            DurabilityErrors.FencedOutCode,
            ErrorCategory.Forbidden,
            "token 1 is below the fence of 2.");

        ShouldFailWith(
            await journal.CommitAsync(
                Step(key) with
                {
                    Token = new FencingToken(2),
                    Outbox = [withEvent],
                    StateBag = Payload(new ConformanceOrder("leaked", "tok", 1)),
                },
                Cancellation),
            DurabilityErrors.DuplicateStepCode,
            ErrorCategory.Conflict,
            "the key is already committed.");

        var frontier = await ReadFrontierAsync(journal, instance);
        frontier.Committed.Count.ShouldBe(1, "neither refusal added a step row.");

        var outbox = await ReadOutboxAsync(journal, instance);
        outbox.ShouldBeEmpty(
            "a refused commit stages no event. An event published for a step that never " +
            "committed is a lie the consumer cannot detect.");

        var record = await ReadInstanceAsync(journal, instance);
        ShouldNotContainText(
            record.StateBagJson, "leaked", "a refused commit does not move the state bag.");
    }

    // ---------------------------------------------------------------- ordering

    /// <summary>History is read in commit order, which is not step order.</summary>
    /// <remarks>
    /// Once a flow forks, step index order and commit order are different sequences, and only
    /// one of them is what happened. Timestamps are not a substitute: two nodes have two
    /// clocks.
    /// </remarks>
    [Fact]
    public async Task HistoryIsReadInCommitOrder()
    {
        var journal = await CreateJournalAsync();
        var instance = await StartAsync(journal);

        foreach (var stepId in new[] { 5, 1, 9 })
        {
            ShouldSucceed(
                await journal.CommitAsync(Step(StepKey.First(instance, stepId)), Cancellation),
                "each branch commits when it finishes, not in index order.");
        }

        var frontier = await ReadFrontierAsync(journal, instance);

        frontier.Committed.Select(static step => step.Key.StepId).ShouldBe(
            [5, 1, 9],
            "the journal replays what happened, in the order it happened.");

        frontier.Committed.Select(static step => step.Sequence).ShouldBeInOrder(
            SortDirection.Ascending,
            "the sequence is what makes commit order recoverable by a reader that did not " +
            "observe it.");
    }

    // ---------------------------------------------------------------- replay

    /// <summary>What a step read from outside itself comes back exactly as it was recorded.</summary>
    /// <remarks>
    /// Commitment 4 of ADR-0015. A resumed step must see the same clock, the same ids and the
    /// same random stream it saw the first time, or "resume" means "run something else". The
    /// seed is the part that only recently became recordable: <c>new Random()</c> shows its
    /// seed to nobody, so the runtime now draws it and keeps it in
    /// <c>FlowExecutionContext.RandomSeed</c>, and this is the column it was drawn for.
    /// </remarks>
    [Fact]
    public async Task ACommittedStepReplaysItsRecordedNondeterminism()
    {
        var journal = await CreateJournalAsync();
        var instance = await StartAsync(journal);
        var key = StepKey.First(instance, 0);

        var captured = new NondeterminismCapture
        {
            UtcNow = new DateTimeOffset(2026, 7, 31, 9, 30, 0, TimeSpan.Zero),
            NewIds = [Guid.Parse("11111111-1111-1111-1111-111111111111")],
            RandomSeed = 1234567,
        };

        ShouldSucceed(
            await journal.CommitAsync(Step(key) with { Nondeterminism = captured }, Cancellation),
            "the capture is committed with the step.");

        var replayed = (await ReadFrontierAsync(journal, instance)).Find(key)!;

        replayed.Nondeterminism.UtcNow.ShouldBe(captured.UtcNow, "the clock is replayed, not re-read.");
        replayed.Nondeterminism.NewIds.ShouldBe(captured.NewIds, "ids are replayed in order.");
        replayed.Nondeterminism.RandomSeed.ShouldBe(
            captured.RandomSeed,
            "the seed is what makes the random stream reproducible at all.");
    }

    /// <summary>A step that drew no randomness records no seed.</summary>
    /// <remarks>
    /// "This run never asked for randomness" and "the seed happened to be zero" are different
    /// facts, and a store that flattened the first into the second would replay a run that
    /// drew no number as one that did. The runtime keeps the distinction as an
    /// <c>int?</c> for this reason; a store that stores zero for null throws it away.
    /// </remarks>
    [Fact]
    public async Task AStepThatDrewNoRandomnessRecordsNoSeed()
    {
        var journal = await CreateJournalAsync();
        var instance = await StartAsync(journal);
        var key = StepKey.First(instance, 0);

        ShouldSucceed(await journal.CommitAsync(Step(key), Cancellation), "the step is committed.");

        var replayed = (await ReadFrontierAsync(journal, instance)).Find(key)!;

        replayed.Nondeterminism.RandomSeed.ShouldBeNull(
            "null is not zero. A replay reading zero would build a generator this run never had.");
    }

    // ---------------------------------------------------------------- resume

    /// <summary>The resume position is a set of committed steps, not a number.</summary>
    /// <remarks>
    /// Commitment 2 of ADR-0015, and the reason <c>flow_instance.resume_from_step</c> stops
    /// being the resume position. A <c>Parallel</c> fork can be half done — branch A finished
    /// at step 4, branch B stopped at step 12 — and no single integer describes that.
    /// <c>Parallel</c> shipped in P1, so this is not a hypothetical shape.
    /// </remarks>
    [Fact]
    public async Task TheResumeFrontierIsASetNotACursor()
    {
        var journal = await CreateJournalAsync();
        var instance = await StartAsync(journal);

        // A fork: the branch through step 2 finished, the branch through step 5 did not.
        foreach (var stepId in new[] { 0, 1, 2, 5 })
        {
            ShouldSucceed(
                await journal.CommitAsync(Step(StepKey.First(instance, stepId)), Cancellation),
                "each branch commits its own steps.");
        }

        var frontier = await ReadFrontierAsync(journal, instance);

        frontier.IsCommitted(StepScope.Root, 2).ShouldBeTrue("step 2 committed.");
        frontier.IsCommitted(StepScope.Root, 5).ShouldBeTrue("step 5 committed.");
        frontier.IsCommitted(StepScope.Root, 3).ShouldBeFalse(
            "step 3 did not commit, and no scalar cursor can say that while step 5 has.");
        frontier.IsCommitted(StepScope.Root, 4).ShouldBeFalse("neither did step 4.");
    }

    /// <summary>The resume hint is an operator's convenience and never the answer.</summary>
    /// <remarks>
    /// The column survives because "roughly where is this stuck instance" is a real question
    /// asked of a table. It is not read by the engine, and this test is what makes that
    /// difference observable: the hint is set to a value that is wrong on purpose, and the
    /// frontier is still right. A store that answered the frontier from the hint would fail
    /// here and nowhere else.
    /// </remarks>
    [Fact]
    public async Task TheResumeHintIsNotTheResumeCursor()
    {
        var journal = await CreateJournalAsync();
        var instance = await StartAsync(journal);

        ShouldSucceed(
            await journal.CommitAsync(
                Step(StepKey.First(instance, 0)) with { ResumeHint = 99 },
                Cancellation),
            "a node wrote a hint that does not describe its own history.");

        ShouldSucceed(
            await journal.CommitAsync(Step(StepKey.First(instance, 1)), Cancellation),
            "the next step commits normally.");

        var frontier = await ReadFrontierAsync(journal, instance);

        frontier.Instance.ResumeHint.ShouldBe(99, "the hint is stored as written.");
        frontier.Committed.Count.ShouldBe(2, "the frontier is derived from rows, not from the hint.");
        frontier.IsCommitted(StepScope.Root, 99).ShouldBeFalse("nothing committed at step 99.");
    }

    // ---------------------------------------------------------------- sub-flows

    /// <summary>A sub-flow child is its own instance row, carrying its parent.</summary>
    /// <remarks>
    /// Commitment 3 of ADR-0015. WP-33 already refused to splice a child's plan into its
    /// parent's at compile time — it would make the parent's manifest claim the child's
    /// capabilities and discard the child's deadline and profile — and splicing in the
    /// journal would reintroduce the same untruth one layer down. It is also the only shape
    /// in which a detached child, which outlives the step that started it, has anywhere to
    /// live.
    /// </remarks>
    [Fact]
    public async Task AChildInstanceIsItsOwnRowCarryingItsParent()
    {
        var journal = await CreateJournalAsync();
        var parent = await StartAsync(journal);
        var child = Guid.NewGuid();
        var composedAt = StepScope.Root.Element(3);

        ShouldSucceed(
            await journal.StartAsync(
                Start(child) with
                {
                    FlowId = "payment.settle",
                    ParentInstanceId = parent,
                    ParentScope = composedAt,
                    ParentStepId = 6,
                },
                Cancellation),
            "the child opens its own instance row.");

        var record = await ReadInstanceAsync(journal, child);

        record.ParentInstanceId.ShouldBe(parent, "the child knows who composed it.");
        record.ParentScope.ShouldBe(composedAt, "including which iteration of the parent did.");
        record.ParentStepId.ShouldBe(6);
        record.FlowId.ShouldBe("payment.settle", "the child is its own flow, with its own identity.");
    }

    /// <summary>A child's steps stay in the child's history.</summary>
    /// <remarks>
    /// The parent recorded one step — the composition — and the child recorded its own. A
    /// store that spliced them would make the parent's history claim work the parent never
    /// did, and would make the child's deadline and profile unreadable.
    /// </remarks>
    [Fact]
    public async Task AChildsStepsAreNotSplicedIntoItsParent()
    {
        var journal = await CreateJournalAsync();
        var parent = await StartAsync(journal);
        var child = Guid.NewGuid();

        ShouldSucceed(
            await journal.StartAsync(
                Start(child) with { ParentInstanceId = parent, ParentStepId = 1 },
                Cancellation),
            "the child is started.");

        ShouldSucceed(
            await journal.CommitAsync(Step(StepKey.First(parent, 1)), Cancellation),
            "the parent records the step that composed the child.");

        ShouldSucceed(
            await journal.CommitAsync(Step(StepKey.First(child, 0)), Cancellation),
            "the child records its own first step.");

        var parentFrontier = await ReadFrontierAsync(journal, parent);
        var childFrontier = await ReadFrontierAsync(journal, child);

        parentFrontier.Committed.Count.ShouldBe(1, "one step, the composition.");
        parentFrontier.Committed[0].Key.InstanceId.ShouldBe(parent);
        childFrontier.Committed.Count.ShouldBe(1, "the child's step belongs to the child.");
        childFrontier.Committed[0].Key.InstanceId.ShouldBe(child);
    }

    /// <summary>A root instance has no parent, and the link is nullable on read.</summary>
    /// <remarks>
    /// A detached child can outlive its parent, so retention can archive a parent while a
    /// child is still running. An orphan must be legible rather than a foreign-key error,
    /// which starts with the column being genuinely optional.
    /// </remarks>
    [Fact]
    public async Task ARootInstanceHasNoParent()
    {
        var journal = await CreateJournalAsync();
        var instance = await StartAsync(journal);

        var record = await ReadInstanceAsync(journal, instance);

        record.ParentInstanceId.ShouldBeNull("a flow a trigger started has no parent.");
        record.ParentStepId.ShouldBeNull();
        record.ParentScope.ShouldBe(StepScope.Root);
    }

    // ---------------------------------------------------------------- payloads

    /// <summary>What was written is what comes back, member for member.</summary>
    /// <remarks>
    /// Commitment 5 of ADR-0015: payloads go through the generated JSON context, and a store
    /// persists what that produces. The journal is the replay source and the incident view,
    /// and both are worthless if a value is transformed on the way through.
    /// </remarks>
    [Fact]
    public async Task APayloadIsStoredAsTheGeneratedContextWroteIt()
    {
        var journal = await CreateJournalAsync();
        var instance = await StartAsync(journal);
        var key = StepKey.First(instance, 0);

        ShouldSucceed(
            await journal.CommitAsync(
                Step(key) with { Result = Payload(new ConformanceOrder("order-7", "tok", 3)) },
                Cancellation),
            "the result is committed.");

        var stored = (await ReadFrontierAsync(journal, instance)).Find(key)!.ResultJson;

        stored.ShouldNotBeNull("a store that drops the payload has no replay source.");
        ShouldContainText(stored, "order-7", "the value round-trips.");
        ShouldContainText(stored, "\"Quantity\":3", "so does every other member of the contract.");
    }

    /// <summary>
    /// A member the contract declared sensitive is not in the row that comes back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The journal is a new sink for <c>[Sensitive]</c> values and it arrives three phases
    /// before the fitness function that guards sinks. A <c>flow_step.result</c> written
    /// verbatim puts a marked member into a table retained for thirty to a hundred and eighty
    /// days, which is strictly worse than the log line the attribute was written for.
    /// </para>
    /// <para>
    /// <strong>This assertion is weak on purpose, and it is worth saying why.</strong>
    /// <see cref="JournalPayload"/> exposes no accessor for the value it carries, so a store
    /// has no route to the object graph and cannot serialise it a second way even if it
    /// wanted to — the guarantee is structural, and a structural guarantee is not something a
    /// test can usefully attack. What this test does catch is a store that drops the payload
    /// altogether, and what it does is state the requirement where a store author reads it.
    /// The redaction rule itself is attacked directly, and can fail, in
    /// <c>JournalPayloadTests</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ASensitiveMemberIsNotWrittenToTheJournal()
    {
        var journal = await CreateJournalAsync();
        var instance = await StartAsync(journal);
        var key = StepKey.First(instance, 0);

        var secret = "tok_live_4242424242424242";

        var payload = JournalPayload.Of(
            new ConformanceOrder("order-7", secret, 1),
            ConformanceJson.Default.ConformanceOrder,
            ["PaymentToken"]);

        ShouldSucceed(
            await journal.CommitAsync(
                Step(key) with { Result = payload, StateBag = payload },
                Cancellation),
            "the step is committed.");

        var frontier = await ReadFrontierAsync(journal, instance);
        var stored = frontier.Find(key)!.ResultJson;

        stored.ShouldNotBeNull("the payload is stored.");
        ShouldNotContainText(stored, secret, "the declared member never reaches the store.");
        ShouldContainText(stored, JournalPayload.Redacted, "and the reader can see it was withheld.");
        ShouldContainText(stored, "order-7", "everything else is still readable during an incident.");

        ShouldNotContainText(
            frontier.Instance.StateBagJson,
            secret,
            "the state bag is the same sink as the result and gets the same treatment.");
    }

    // ---------------------------------------------------------------- lifecycle

    /// <summary>An instance cannot be started twice.</summary>
    [Fact]
    public async Task AnInstanceCannotBeStartedTwice()
    {
        var journal = await CreateJournalAsync();
        var instance = await StartAsync(journal);

        ShouldFailWith(
            await journal.StartAsync(Start(instance), Cancellation),
            DurabilityErrors.InstanceExistsCode,
            ErrorCategory.Conflict,
            "starting an instance twice would replace a history rather than extend it.");
    }

    /// <summary>Reading an instance that is not there is a value, not an exception.</summary>
    /// <remarks>
    /// Retention deletes instances, so a caller asking about one that has aged out is
    /// ordinary. ADR-0007 puts expected outcomes in signatures; an exception here would put
    /// a routine condition on the exceptional path.
    /// </remarks>
    [Fact]
    public async Task ReadingAnUnknownInstanceIsAnErrorValue()
    {
        var journal = await CreateJournalAsync();

        ShouldFailWith(
            await journal.ReadInstanceAsync(Guid.NewGuid(), Cancellation),
            DurabilityErrors.InstanceNotFoundCode,
            ErrorCategory.NotFound,
            "an unknown instance is a not-found, not a throw.");

        ShouldFailWith(
            await journal.ReadResumeFrontierAsync(Guid.NewGuid(), Cancellation),
            DurabilityErrors.InstanceNotFoundCode,
            ErrorCategory.NotFound,
            "the same answer from the resume path, which is where recovery meets it.");
    }

    /// <summary>Completing records the terminal state and the final state bag.</summary>
    [Fact]
    public async Task CompletingRecordsTheTerminalStateAndTheFinalStateBag()
    {
        var journal = await CreateJournalAsync();
        var instance = await StartAsync(journal, new FencingToken(3));

        ShouldSucceed(
            await journal.CompleteAsync(instance,
                new FencingToken(3),
                FlowInstanceState.Completed,
                Payload(new ConformanceOrder("order-9", "tok", 1)), wake: null, Cancellation),
            "the owner completes the instance.");

        var record = await ReadInstanceAsync(journal, instance);

        record.State.ShouldBe(FlowInstanceState.Completed);
        ShouldContainText(record.StateBagJson, "order-9", "the final state bag is recorded.");
    }

    /// <summary>A finished instance takes no further steps.</summary>
    /// <remarks>
    /// The failure this prevents is a late-arriving branch, or a redelivered trigger,
    /// appending to a flow whose compensations have already run.
    /// </remarks>
    [Fact]
    public async Task AFinishedInstanceTakesNoFurtherSteps()
    {
        var journal = await CreateJournalAsync();
        var instance = await StartAsync(journal);

        ShouldSucceed(
            await journal.CompleteAsync(instance, new FencingToken(1), FlowInstanceState.Failed, JournalPayload.Empty, wake: null, Cancellation),
            "the instance failed and compensated.");

        ShouldFailWith(
            await journal.CommitAsync(Step(StepKey.First(instance, 4)), Cancellation),
            DurabilityErrors.InstanceTerminalCode,
            ErrorCategory.Conflict,
            "a terminal instance is finished. Appending to it would rewrite the outcome.");
    }

    /// <summary>Completing is fenced like every other write.</summary>
    /// <remarks>
    /// A zombie must not be able to declare an instance <c>Completed</c> that its successor
    /// is still executing. This is the write with the largest blast radius, so it is the one
    /// most worth checking.
    /// </remarks>
    [Fact]
    public async Task CompletingIsFencedLikeEveryOtherWrite()
    {
        var journal = await CreateJournalAsync();
        var instance = await StartAsync(journal, new FencingToken(2));

        ShouldSucceed(
            await journal.FenceAsync(instance, new FencingToken(3), Cancellation),
            "another node took over.");

        ShouldFailWith(
            await journal.CompleteAsync(instance, new FencingToken(2), FlowInstanceState.Completed, JournalPayload.Empty, wake: null, Cancellation),
            DurabilityErrors.FencedOutCode,
            ErrorCategory.Forbidden,
            "a zombie cannot complete an instance it no longer owns.");

        var record = await ReadInstanceAsync(journal, instance);

        record.State.ShouldNotBe(FlowInstanceState.Completed, "the refusal changed nothing.");
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Starts an instance and returns its id.</summary>
    protected static async Task<Guid> StartAsync(IFlowJournal journal, FencingToken? token = null)
    {
        ArgumentNullException.ThrowIfNull(journal);

        var instanceId = Guid.NewGuid();
        var start = Start(instanceId);

        var started = await journal.StartAsync(
            token is { } supplied ? start with { Token = supplied } : start,
            Cancellation);

        ShouldSucceed(started, "the harness must be able to start an instance.");

        return instanceId;
    }

    /// <summary>A minimal, valid instance start.</summary>
    protected static FlowInstanceStart Start(Guid instanceId) => new()
    {
        InstanceId = instanceId,
        FlowId = "order.place",
        FlowVersion = "1.2.0",
        Token = new FencingToken(1),
        TenantId = "tenant-a",
        CorrelationId = "corr-1",
    };

    /// <summary>A minimal, valid step commit for a key.</summary>
    protected static StepCommit Step(StepKey key) => new()
    {
        Key = key,
        Token = new FencingToken(1),
        CapabilityId = "inventory.reserve",
        CapabilityVersion = "2.1.0",
        Outcome = JournalOutcome.Success,
    };

    /// <summary>An unredacted payload, for the assertions that are not about redaction.</summary>
    protected static JournalPayload Payload(ConformanceOrder order) =>
        JournalPayload.Of(order, ConformanceJson.Default.ConformanceOrder);

    private static async Task<FlowInstanceRecord> ReadInstanceAsync(IFlowJournal journal, Guid instanceId)
    {
        var read = await journal.ReadInstanceAsync(instanceId, Cancellation);

        ShouldSucceed(read, "the instance was started, so it can be read.");

        return read.Value;
    }

    private static async Task<ResumeFrontier> ReadFrontierAsync(IFlowJournal journal, Guid instanceId)
    {
        var read = await journal.ReadResumeFrontierAsync(instanceId, Cancellation);

        ShouldSucceed(read, "the instance was started, so its frontier can be read.");

        return read.Value;
    }

    private static async Task<IReadOnlyList<OutboxRecord>> ReadOutboxAsync(IFlowJournal journal, Guid instanceId)
    {
        var read = await journal.ReadOutboxAsync(instanceId, Cancellation);

        ShouldSucceed(read, "the instance was started, so its outbox can be read.");

        return read.Value;
    }

    /// <summary>Asserts stored JSON carries a value, printing what was stored when it does not.</summary>
    protected static void ShouldContainText(string? actual, string expected, string because) =>
        (actual ?? string.Empty).Contains(expected, StringComparison.Ordinal).ShouldBeTrue(
            $"{because} The store wrote: {actual ?? "(null)"}");

    /// <summary>Asserts stored JSON does not carry a value.</summary>
    protected static void ShouldNotContainText(string? actual, string expected, string because) =>
        (actual ?? string.Empty).Contains(expected, StringComparison.Ordinal).ShouldBeFalse(
            $"{because} The store wrote: {actual ?? "(null)"}");

    /// <summary>Asserts a store call succeeded, printing the store's own refusal if not.</summary>
    protected static void ShouldSucceed<T>(Result<T> result, string because) =>
        result.IsSuccess.ShouldBeTrue(
            $"{because} The store refused instead: {(result.IsFailure ? result.Error.ToString() : "no error")}");

    /// <summary>Asserts a store call was refused with the documented code and category.</summary>
    protected static Error ShouldFailWith<T>(
        Result<T> result,
        string code,
        ErrorCategory category,
        string because)
    {
        result.IsFailure.ShouldBeTrue($"{because} The store accepted the call instead.");

        result.Error.Code.ShouldBe(
            code,
            $"{because} A caller branches on the code, so it is part of the contract and not " +
            "of any one store.");

        result.Error.Category.ShouldBe(
            category,
            $"{because} The category carries the retry decision, which is the half that " +
            "changes what a caller does next.");

        return result.Error;
    }
}
