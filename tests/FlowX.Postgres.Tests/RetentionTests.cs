using FlowX.Conformance.InMemory;
using Shouldly;
using Xunit;

namespace FlowX.Postgres.Tests;

/// <summary>
/// The retention half of ADR-0015: the windows in
/// <c>docs/11-Distributed-Runtime.md §2</c> as something that deletes rows.
/// </summary>
public sealed class RetentionTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>The document's numbers are the numbers in the table.</summary>
    /// <remarks>
    /// A retention policy that lives only in prose is a policy nobody applies. Seeding it
    /// makes it readable by an operator and by the sweeper; asserting the seed makes the
    /// document and the schema one statement rather than two.
    /// </remarks>
    [Fact]
    public async Task TheDocumentedWindowsAreSeeded()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        (await schema.ScalarAsync(
            "SELECT retain_for FROM retention_policy WHERE flow_id = '*' AND state_class = 'Completed'",
            Cancellation))
            .ShouldBe(TimeSpan.FromDays(30), "docs/11 §2: a completed instance is kept 30 days.");

        (await schema.ScalarAsync(
            "SELECT retain_for FROM retention_policy WHERE flow_id = '*' AND state_class = 'Failed'",
            Cancellation))
            .ShouldBe(TimeSpan.FromDays(180), "and a failed one for 180.");

        (await schema.ScalarAsync(
            "SELECT retain_for FROM retention_policy WHERE flow_id = '*' AND state_class = 'Suspended'",
            Cancellation))
            .ShouldBeNull(
                "a suspended instance is kept until it completes or its deadline passes, " +
                "which is a question about the instance rather than about the clock.");
    }

    /// <summary>An instance past its window goes, and takes its steps with it.</summary>
    [Fact]
    public async Task APurgeRemovesInstancesPastTheirWindow()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var instance = await CompletedInstanceAsync(schema, 40);

        (await schema.ScalarAsync("SELECT count(*) FROM flow_step", Cancellation))
            .ShouldBe(1L, "the instance recorded a step.");

        var sweep = await schema.Retention.PurgeAsync(Cancellation);

        sweep.CompletedInstances.ShouldBe(1, "40 days is past the 30-day window.");

        (await schema.Journal.ReadInstanceAsync(instance, Cancellation)).IsFailure.ShouldBeTrue(
            "the instance is gone.");

        (await schema.ScalarAsync("SELECT count(*) FROM flow_step", Cancellation))
            .ShouldBe(0L, "and its steps went with it, by cascade rather than by a second sweep.");
    }

    /// <summary>An instance inside its window stays.</summary>
    /// <remarks>
    /// The direction that matters more. A sweeper that deleted too much would pass the test
    /// above and destroy the audit trail the journal exists to be.
    /// </remarks>
    [Fact]
    public async Task APurgeLeavesInstancesInsideTheirWindow()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var recent = await CompletedInstanceAsync(schema, 10);
        var failed = await CompletedInstanceAsync(schema, 40, FlowInstanceState.Failed);

        var sweep = await schema.Retention.PurgeAsync(Cancellation);

        sweep.CompletedInstances.ShouldBe(0, "10 days is inside the 30-day window.");
        sweep.FailedInstances.ShouldBe(0, "and 40 days is well inside the 180-day one.");

        (await schema.Journal.ReadInstanceAsync(recent, Cancellation)).IsSuccess.ShouldBeTrue();
        (await schema.Journal.ReadInstanceAsync(failed, Cancellation)).IsSuccess.ShouldBeTrue(
            "a failed instance is kept six times as long, because it is the one an " +
            "investigation comes back to.");
    }

    /// <summary>A per-flow window overrides the default.</summary>
    [Fact]
    public async Task APerFlowWindowOverridesTheDefault()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var instance = await CompletedInstanceAsync(schema, 40);

        await schema.Retention.SetPolicyAsync(
            "order.place", "Completed", TimeSpan.FromDays(365), Cancellation);

        (await schema.Retention.PurgeAsync(Cancellation)).CompletedInstances.ShouldBe(
            0, "the flow's own window is a year, so 40 days is nowhere near it.");

        (await schema.Journal.ReadInstanceAsync(instance, Cancellation)).IsSuccess.ShouldBeTrue();
    }

    /// <summary>
    /// Archiving a parent leaves its still-running child readable.
    /// </summary>
    /// <remarks>
    /// ADR-0015 states this as a consequence it accepts: a <c>Detached</c> sub-flow can
    /// outlive its parent, so "completed instance + steps: 30 days" can archive a parent
    /// while a child is still running, and "the parent link must be nullable on read, and an
    /// orphan must be legible rather than a foreign-key error". A foreign key on
    /// <c>parent_instance_id</c> would have turned the purge itself into the error. This is
    /// the test that would have found it.
    /// </remarks>
    [Fact]
    public async Task PurgingAParentLeavesItsRunningChildLegible()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var parent = await CompletedInstanceAsync(schema, 40);
        var child = Guid.NewGuid();

        await schema.Journal.StartAsync(
            new FlowInstanceStart
            {
                InstanceId = child,
                FlowId = "payment.settle",
                FlowVersion = "1.0.0",
                Token = new FencingToken(1),
                ParentInstanceId = parent,
                ParentStepId = 3,
            },
            Cancellation);

        (await schema.Retention.PurgeAsync(Cancellation)).CompletedInstances.ShouldBe(
            1, "the parent's window has passed, and a live child does not block it.");

        var orphan = await schema.Journal.ReadInstanceAsync(child, Cancellation);

        orphan.IsSuccess.ShouldBeTrue(
            "the detached child outlived its parent and is still readable. " +
            $"{(orphan.IsFailure ? orphan.Error.ToString() : string.Empty)}");

        orphan.Value.ParentInstanceId.ShouldBe(
            parent,
            "and it still reports the parent it had. The link is a record of what composed " +
            "it, not a guarantee that the row is still there.");
    }

    /// <summary>
    /// An instance past its window is kept while it still holds an unpublished event.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The obligation WP-56 inherited.</strong> ADR-0016 recorded, under Retention,
    /// that a purge cascades to the instance's outbox rows "including any that were never
    /// published. Today nothing publishes them, so nothing is lost; when WP-56 lands a
    /// publisher, the purge needs a guard against removing a pending event." The publisher has
    /// landed, so the premise is gone: a thirty-day-old completed instance can be sitting on
    /// an event a consumer is still owed, and the cascade would take it with no row anywhere
    /// recording that it happened.
    /// </para>
    /// <para>
    /// The window is not the question. The instance below is ten days past a thirty-day one,
    /// and there is no age at which discarding an unsent event becomes correct.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task APurgeKeepsAnInstanceThatStillHoldsAnUnpublishedEvent()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var instance = await CompletedInstanceAsync(schema, 40, emits: "order.placed");

        var sweep = await schema.Retention.PurgeAsync(Cancellation);

        sweep.CompletedInstances.ShouldBe(
            0,
            "the window has passed and the instance stays, because deleting it would take " +
            "an event no consumer has seen with it.");

        sweep.HeldForPendingEvents.ShouldBe(
            1,
            "and the sweep says so. A guard that silently retains rows is a leak with good " +
            "manners — this is the number an operator watches to notice that nothing is " +
            "publishing.");

        (await schema.Journal.ReadInstanceAsync(instance, Cancellation)).IsSuccess.ShouldBeTrue();

        (await schema.ScalarAsync("SELECT count(*) FROM outbox_event", Cancellation))
            .ShouldBe(1L, "and the event is still there to be published.");
    }

    /// <summary>Once the event is published, the instance goes on the next sweep.</summary>
    /// <remarks>
    /// The other direction, and the one that keeps the guard from being a retention stop. A
    /// guard that held instances forever would trade a silent data loss for a disk silently
    /// filling up, which is a worse bargain than it looks: the first is eventually noticed by
    /// a consumer, and the second by nobody until the database stops accepting writes.
    /// </remarks>
    [Fact]
    public async Task APublishedEventStopsHoldingItsInstanceBack()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var instance = await CompletedInstanceAsync(schema, 40, emits: "order.placed");

        (await schema.Retention.PurgeAsync(Cancellation)).CompletedInstances.ShouldBe(0);

        var published = await schema.OutboxPublisher(new RecordingEventPublisher())
            .PublishPendingAsync(Cancellation);

        published.Published.ShouldBe(1, "the publisher drained it.");

        var sweep = await schema.Retention.PurgeAsync(Cancellation);

        sweep.CompletedInstances.ShouldBe(1, "and now the window is the only question again.");
        sweep.HeldForPendingEvents.ShouldBe(0, "nothing is being held back.");

        (await schema.Journal.ReadInstanceAsync(instance, Cancellation)).IsFailure.ShouldBeTrue(
            "the instance is gone.");

        (await schema.ScalarAsync("SELECT count(*) FROM outbox_event", Cancellation))
            .ShouldBe(
                0L,
                "and its outbox row went with it by cascade, which is what the seven-day " +
                "OutboxPublished window would otherwise have done more slowly.");
    }

    /// <summary>A failed instance is held by a pending event for the same reason.</summary>
    /// <remarks>
    /// The guard is one rule spliced into both purges rather than a clause on the completed
    /// one. A flow that failed can have emitted before it did — a saga publishes as it unwinds
    /// — and a hundred and eighty days later that event is no less undelivered than it was on
    /// the first day.
    /// </remarks>
    [Fact]
    public async Task TheGuardCoversTheFailedWindowToo()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        await CompletedInstanceAsync(
            schema, 200, FlowInstanceState.Failed, emits: "order.cancelled");

        var sweep = await schema.Retention.PurgeAsync(Cancellation);

        sweep.FailedInstances.ShouldBe(0, "200 days is past the 180-day window, and it stays.");
        sweep.HeldForPendingEvents.ShouldBe(1);
    }

    /// <summary>Records an instance that finished a given number of days ago.</summary>
    /// <param name="schema">The schema to record it in.</param>
    /// <param name="daysAgo">How long ago it last changed.</param>
    /// <param name="state">The terminal state it reached.</param>
    /// <param name="emits">An event staged by its one step, or null for an instance that emits nothing.</param>
    /// <returns>The instance.</returns>
    private static async Task<Guid> CompletedInstanceAsync(
        PostgresTestSchema schema,
        int daysAgo,
        FlowInstanceState state = FlowInstanceState.Completed,
        string? emits = null)
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

        await schema.Journal.CommitAsync(
            new StepCommit
            {
                Key = StepKey.First(instance, 0),
                Token = new FencingToken(1),
                CapabilityId = "inventory.reserve",
                CapabilityVersion = "2.1.0",
                Outcome = JournalOutcome.Success,
                Outbox = emits is null
                    ? []
                    : [new OutboxWrite { Type = emits, SchemaVersion = "1.0.0", PartitionKey = "order-7" }],
            },
            Cancellation);

        await schema.Journal.CompleteAsync(
            instance, new FencingToken(1), state, JournalPayload.Empty, Cancellation);

        // Age the row. The sweeper reads updated_at, which the adapter sets to now() on
        // every write, so backdating it is the only way to test a 30-day window in a test.
        await schema.ExecuteAsync(
            $"UPDATE flow_instance SET updated_at = now() - interval '{daysAgo} days' " +
            $"WHERE instance_id = '{instance}'",
            Cancellation);

        return instance;
    }
}
