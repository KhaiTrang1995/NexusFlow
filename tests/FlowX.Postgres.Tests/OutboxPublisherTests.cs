using FlowX.Conformance.InMemory;
using Shouldly;
using Xunit;

namespace FlowX.Postgres.Tests;

/// <summary>
/// WP-56: the half of <c>docs/11-Distributed-Runtime.md §5</c> that reads the outbox back.
/// </summary>
/// <remarks>
/// The staging half was already true — <c>PostgresFlowJournal.CommitAsync</c> writes the step
/// row, the instance update and the outbox rows in one transaction, and
/// <c>JournalConformance</c> has held it to that since WP-51. Everything below is about what
/// happens next: that a staged event reaches a publisher, in staging order per
/// <c>partition_key</c>; that a crash between the broker and the mark produces a duplicate
/// rather than a loss; and that two publishers over one table never hand the same event over
/// twice.
/// </remarks>
public sealed class OutboxPublisherTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>An emitted event reaches a publisher, and is then marked and not re-sent.</summary>
    /// <remarks>
    /// The first exit criterion, and the one <c>FLOWX1024</c> has been standing in for since
    /// the catalogue's first warning: an event staged by a step commit leaves the database.
    /// The second pass is half the assertion — a publisher that delivered and never marked
    /// would pass the first half and flood a consumer.
    /// </remarks>
    [Fact]
    public async Task AStagedEventReachesThePublisherExactlyOnceWhenNothingGoesWrong()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var broker = new RecordingEventPublisher();
        var publisher = schema.OutboxPublisher(broker);

        await StageAsync(schema, "order-7", ["order.placed"]);

        var first = await publisher.PublishPendingAsync(Cancellation);

        first.Claimed.ShouldBe(1, "the staged event is pending and claimable.");
        first.Published.ShouldBe(1, "and the broker took it.");
        first.Failure.ShouldBeNull();

        broker.DeliveredTypes.ShouldBe(["order.placed"]);

        (await schema.ScalarAsync(
            "SELECT count(*) FROM outbox_event WHERE published_at IS NULL", Cancellation))
            .ShouldBe(0L, "the row is marked in the same transaction that claimed it.");

        var second = await publisher.PublishPendingAsync(Cancellation);

        second.Claimed.ShouldBe(0, "a marked row is not pending.");
        broker.DeliveredTypes.Count.ShouldBe(1, "so it is not delivered a second time.");
    }

    /// <summary>
    /// A crash between publishing and marking republishes the event.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>At-least-once, and never zero.</strong> The publisher throws after the broker
    /// has the event and before <c>published_at</c> is written, which is the exact window
    /// <c>docs/11-Distributed-Runtime.md §4</c>'s flowchart asks about. The claim, the publish
    /// and the mark are one transaction, so the throw unwinds it and the row is pending again
    /// — and the next pass hands the same event over a second time.
    /// </para>
    /// <para>
    /// Two deliveries is the <em>correct</em> outcome, not a tolerated one. The alternative
    /// designs — mark first, or publish outside the transaction — turn this window into a
    /// lost event, and a lost event is the failure a consumer cannot detect.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ACrashBetweenPublishingAndMarkingDeliversTwiceRatherThanNever()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var broker = new RecordingEventPublisher
        {
            AfterPublish = (_, _) => throw new SimulatedCrashException(),
        };

        var publisher = schema.OutboxPublisher(broker);
        var staged = await StageAsync(schema, "order-7", ["order.placed"]);

        await Should.ThrowAsync<SimulatedCrashException>(
            async () => await publisher.PublishPendingAsync(Cancellation));

        broker.Delivered.Count.ShouldBe(1, "the broker had it before the process died.");

        (await schema.ScalarAsync(
            "SELECT count(*) FROM outbox_event WHERE published_at IS NULL", Cancellation))
            .ShouldBe(
                1L,
                "and the mark went with the transaction. A row that stayed marked here " +
                "would be an event nobody ever delivered and nobody could notice.");

        // The process comes back.
        broker.AfterPublish = null;

        var recovered = await publisher.PublishPendingAsync(Cancellation);

        recovered.Published.ShouldBe(1);

        broker.Delivered.Count.ShouldBe(
            2, "at-least-once: the same event twice, rather than once or never.");

        broker.Delivered[0].EventId.ShouldBe(
            broker.Delivered[1].EventId,
            "and it is the same event id both times, which is what a consumer deduplicates " +
            "on. A publisher that minted a new id per attempt would make idempotency " +
            "impossible to implement.");

        broker.Delivered[0].EventId.ShouldBe(staged[0]);
    }

    /// <summary>
    /// A broker that refuses the batch leaves every row pending.
    /// </summary>
    /// <remarks>
    /// The refusal path is separate from the crash path because it is the ordinary one: a
    /// broker is unreachable far more often than a process dies at a chosen instruction. The
    /// pass reports the failure rather than swallowing it, and marks nothing.
    /// </remarks>
    [Fact]
    public async Task ARefusedBatchIsMarkedNowhereAndReported()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var broker = new RecordingEventPublisher
        {
            Refusal = new Error("broker.unreachable", "No broker answered.", ErrorCategory.Unavailable),
        };

        var publisher = schema.OutboxPublisher(broker);

        await StageAsync(schema, "order-7", ["order.placed", "order.paid"]);

        var pass = await publisher.PublishPendingAsync(Cancellation);

        pass.Claimed.ShouldBe(2);
        pass.Published.ShouldBe(0);
        pass.Failure!.Code.ShouldBe("broker.unreachable", "the refusal reaches the caller.");

        (await schema.ScalarAsync(
            "SELECT count(*) FROM outbox_event WHERE published_at IS NULL", Cancellation))
            .ShouldBe(2L, "and nothing is marked.");
    }

    /// <summary>
    /// A publisher that accepts part of a batch has exactly that part marked.
    /// </summary>
    /// <remarks>
    /// <see cref="IEventPublisher"/>'s contract is a prefix: the first <c>n</c> arrived, in
    /// order, and nothing after them did. Marking more would lose the rest; marking less
    /// would redeliver what already arrived, which is permitted but wasteful. The event the
    /// publisher declined must still be the next one offered.
    /// </remarks>
    [Fact]
    public async Task OnlyThePrefixThePublisherAcknowledgedIsMarked()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var broker = new RecordingEventPublisher { AcceptCount = 2 };
        var publisher = schema.OutboxPublisher(broker);

        await StageAsync(schema, "order-7", ["order.placed", "order.paid", "order.shipped"]);

        var pass = await publisher.PublishPendingAsync(Cancellation);

        pass.Claimed.ShouldBe(3);
        pass.Published.ShouldBe(2);

        broker.DeliveredTypes.ShouldBe(["order.placed", "order.paid"]);

        (await schema.ScalarAsync(
            "SELECT count(*) FROM outbox_event WHERE published_at IS NULL", Cancellation))
            .ShouldBe(1L, "the third is still pending.");

        broker.AcceptCount = null;

        await publisher.PublishPendingAsync(Cancellation);

        broker.DeliveredTypes.ShouldBe(
            ["order.placed", "order.paid", "order.shipped"],
            "and the next pass resumes where the prefix stopped, in order.");
    }

    /// <summary>
    /// Events sharing a partition key arrive in the order they were staged.
    /// </summary>
    /// <remarks>
    /// Across two instances as well as within one, which is the case
    /// <c>outbox_event.sequence</c> cannot answer: that column is instance-local, allocated
    /// under <c>flow_instance</c>'s row lock, so instance A's 1 and instance B's 1 are
    /// unrelated numbers. <c>staged_seq</c> from migration <c>0004</c> is the order the rows
    /// were actually staged in, and this is the assertion that would fail without it.
    /// </remarks>
    [Fact]
    public async Task EventsSharingAPartitionKeyArriveInStagingOrder()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var broker = new RecordingEventPublisher();
        var publisher = schema.OutboxPublisher(broker);

        await StageAsync(schema, "customer-1", ["order.placed", "order.paid"]);
        await StageAsync(schema, "customer-1", ["order.shipped"]);
        await StageAsync(schema, "customer-1", ["order.delivered"]);

        await publisher.PublishPendingAsync(Cancellation);

        broker.DeliveredTypes.ShouldBe(
            ["order.placed", "order.paid", "order.shipped", "order.delivered"],
            "one key, one order, whatever instance staged each event.");
    }

    /// <summary>
    /// Two publishers over one table never hand the same event over twice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The exit criterion for <c>FOR UPDATE SKIP LOCKED</c>, arranged so that it can
    /// actually fail.</strong> Publisher A is blocked inside its broker call while it holds
    /// the row locks its claim took, and B runs a whole pass while A is stuck there. Without
    /// <c>SKIP LOCKED</c>, B blocks on A's locks and this test deadlocks until the gate opens
    /// — the assertion that B finished first is what distinguishes "skipped" from "waited".
    /// Without any locking at all, B claims the same rows and both publish them.
    /// </para>
    /// <para>
    /// The keys are distinct per event because the per-key guard is a different property,
    /// asserted separately: with one shared key B would correctly hold back everything behind
    /// A's claim and the test would prove ordering rather than exclusion.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TwoPublishersOverOneTableDoNotDoublePublish()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var claimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var slow = new RecordingEventPublisher
        {
            BeforePublish = async (_, _) =>
            {
                claimed.TrySetResult();
                await release.Task;
            },
        };

        var quick = new RecordingEventPublisher();

        // Two events, one claimable each, so A's claim provably leaves something for B.
        for (var i = 0; i < 6; i++)
        {
            await StageAsync(schema, $"key-{i}", [$"order.event-{i}"]);
        }

        var first = schema.OutboxPublisher(slow, batchSize: 3);
        var second = schema.OutboxPublisher(quick, batchSize: 3);

        var slowPass = Task.Run(async () => await first.PublishPendingAsync(Cancellation), Cancellation);

        await claimed.Task;

        // A is inside its broker call, holding three row locks. B must not wait for it.
        var quickPass = await second.PublishPendingAsync(Cancellation);

        quickPass.Claimed.ShouldBe(
            3,
            "SKIP LOCKED stepped over the three rows the first publisher holds and took the " +
            "other three. Blocking here instead would have hung until the gate opened.");

        release.TrySetResult();

        var slowResult = await slowPass;

        slowResult.Published.ShouldBe(3);
        quickPass.Published.ShouldBe(3);

        var everything = slow.Delivered.Concat(quick.Delivered).Select(static e => e.EventId).ToList();

        everything.Count.ShouldBe(6, "six staged events, six deliveries.");

        everything.Distinct().Count().ShouldBe(
            6,
            "and six distinct ids. Two publishers claiming the same row is the defect " +
            "SKIP LOCKED exists to prevent, and it would show up here as a repeat.");

        (await schema.ScalarAsync(
            "SELECT count(*) FROM outbox_event WHERE published_at IS NULL", Cancellation))
            .ShouldBe(0L, "and between them they drained the table.");
    }

    /// <summary>
    /// A second publisher does not overtake a key the first one is holding.
    /// </summary>
    /// <remarks>
    /// The ordering half of the same arrangement, and the reason the claim query carries a
    /// <c>NOT EXISTS</c> rather than just <c>SKIP LOCKED</c>. Publisher A holds the older of
    /// two events for one key; B skips that row and would, on the naive query, claim the
    /// newer one and reach the broker first — per-key ordering violated by the very mechanism
    /// that makes two publishers safe. Instead B claims nothing, and the pair is published in
    /// order by whoever gets there next.
    /// </remarks>
    [Fact]
    public async Task ASecondPublisherDoesNotOvertakeAKeyTheFirstIsHolding()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var claimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var slow = new RecordingEventPublisher
        {
            BeforePublish = async (_, _) =>
            {
                claimed.TrySetResult();
                await release.Task;
            },
        };

        var quick = new RecordingEventPublisher();

        await StageAsync(schema, "customer-1", ["order.placed"]);
        await StageAsync(schema, "customer-1", ["order.paid"]);

        var first = schema.OutboxPublisher(slow, batchSize: 1);
        var second = schema.OutboxPublisher(quick, batchSize: 1);

        var slowPass = Task.Run(async () => await first.PublishPendingAsync(Cancellation), Cancellation);

        await claimed.Task;

        var quickPass = await second.PublishPendingAsync(Cancellation);

        quickPass.Claimed.ShouldBe(
            0,
            "order.paid is pending and unlocked, and the second publisher still declines it: " +
            "its key has an older pending sibling this claim did not take. Publishing it " +
            "here would put order.paid on the wire before order.placed.");

        quick.Delivered.ShouldBeEmpty();

        release.TrySetResult();
        await slowPass;

        await second.PublishPendingAsync(Cancellation);

        slow.DeliveredTypes.ShouldBe(["order.placed"]);
        quick.DeliveredTypes.ShouldBe(["order.paid"], "and only once its predecessor had gone.");
    }

    /// <summary>An event with no partition key asks for no order and is not held behind one.</summary>
    /// <remarks>
    /// <c>OutboxWrite.PartitionKey</c> documents null as "unordered events". Applying the
    /// per-key guard to them would serialise the whole unkeyed stream behind its oldest
    /// member and invent a guarantee nobody declared.
    /// </remarks>
    [Fact]
    public async Task UnkeyedEventsAreNotHeldBehindEachOther()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var claimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var slow = new RecordingEventPublisher
        {
            BeforePublish = async (_, _) =>
            {
                claimed.TrySetResult();
                await release.Task;
            },
        };

        var quick = new RecordingEventPublisher();

        await StageAsync(schema, null, ["audit.first"]);
        await StageAsync(schema, null, ["audit.second"]);

        var first = schema.OutboxPublisher(slow, batchSize: 1);
        var second = schema.OutboxPublisher(quick, batchSize: 1);

        var slowPass = Task.Run(async () => await first.PublishPendingAsync(Cancellation), Cancellation);

        await claimed.Task;

        (await second.PublishPendingAsync(Cancellation)).Published.ShouldBe(
            1, "no key, no order to preserve, no reason to wait.");

        release.TrySetResult();
        await slowPass;

        quick.DeliveredTypes.ShouldBe(["audit.second"]);
        slow.DeliveredTypes.ShouldBe(["audit.first"]);
    }

    /// <summary>The polling loop drains what is there and stops when it is cancelled.</summary>
    /// <remarks>
    /// A shutdown that surfaces as an unhandled <c>OperationCanceledException</c> is a
    /// stack trace in every host's log at every deployment, which is how a loop teaches
    /// operators to ignore its logs.
    /// </remarks>
    [Fact]
    public async Task ThePollingLoopDrainsTheOutboxAndStopsCleanly()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var broker = new RecordingEventPublisher();
        var publisher = schema.OutboxPublisher(broker, pollInterval: TimeSpan.FromMilliseconds(10));

        await StageAsync(schema, "order-7", ["order.placed"]);

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);

        var loop = publisher.RunAsync(stopping.Token);

        // Waited for in the table rather than in the broker. Cancelling on the broker's hook
        // would land the cancellation inside the pass's own COMMIT, which is a legitimate
        // shutdown but redelivers — and a test that sometimes sees one delivery and sometimes
        // two is a test that has stopped asserting anything.
        await WaitForDrainAsync(schema);

        await stopping.CancelAsync();
        await loop;

        broker.DeliveredTypes.ShouldBe(["order.placed"], "the loop found it without being asked.");
    }

    /// <summary>Waits until the outbox has no pending rows left.</summary>
    private static async Task WaitForDrainAsync(PostgresTestSchema schema)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var pending = await schema.ScalarAsync(
                "SELECT count(*) FROM outbox_event WHERE published_at IS NULL", Cancellation);

            if (pending is 0L)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), Cancellation);
        }

        throw new InvalidOperationException(
            "The polling loop did not drain the outbox within 30 seconds.");
    }

    /// <summary>Stages events on a fresh instance, in one step commit.</summary>
    /// <remarks>
    /// Through <c>PostgresFlowJournal.CommitAsync</c> rather than through raw SQL, so
    /// everything asserted above is asserted about rows the adapter itself writes — including
    /// the <c>staged_seq</c> default, which a hand-written INSERT would have had to know
    /// about and therefore could not have tested.
    /// </remarks>
    private static async Task<IReadOnlyList<Guid>> StageAsync(
        PostgresTestSchema schema,
        string? partitionKey,
        string[] types)
    {
        var instance = Guid.NewGuid();

        await schema.Journal.StartAsync(
            new FlowInstanceStart
            {
                InstanceId = instance,
                FlowId = "order.place",
                FlowVersion = "1.0.0",
                Token = new FencingToken(1),
            },
            Cancellation);

        var committed = await schema.Journal.CommitAsync(
            new StepCommit
            {
                Key = StepKey.First(instance, 0),
                Token = new FencingToken(1),
                CapabilityId = "inventory.reserve",
                CapabilityVersion = "2.1.0",
                Outcome = JournalOutcome.Success,
                Outbox =
                [
                    .. types.Select(type => new OutboxWrite
                    {
                        Type = type,
                        SchemaVersion = "1.0.0",
                        PartitionKey = partitionKey,
                    }),
                ],
            },
            Cancellation);

        committed.IsSuccess.ShouldBeTrue(
            committed.IsFailure ? committed.Error.ToString() : string.Empty);

        var staged = await schema.Journal.ReadOutboxAsync(instance, Cancellation);

        return [.. staged.Value.Select(static e => e.EventId)];
    }
}
