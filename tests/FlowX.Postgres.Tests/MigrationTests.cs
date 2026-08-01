using FlowX.Conformance.InMemory;
using Shouldly;
using Xunit;

namespace FlowX.Postgres.Tests;

/// <summary>
/// The migration half of WP-53: expand/contract proved, not asserted.
/// </summary>
/// <remarks>
/// <c>docs/11-Distributed-Runtime.md §7.4</c> requires journal schema changes to be
/// additive within a release — "add nullable, backfill, switch reads, drop later — never a
/// breaking migration in one release" — because a rolling update runs both releases at
/// once. Two of the tests below read the script text and need no database; the rest stand a
/// schema at the previous release and check that the previous release still works.
/// </remarks>
public sealed class MigrationTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>Every migration is embedded, numbered from one and never renumbered.</summary>
    [Fact]
    public void TheMigrationListIsWellFormed()
    {
        PostgresMigrator.Migrations.ShouldNotBeEmpty("a schema with no migrations creates nothing.");

        PostgresMigrator.Migrations
            .Select(static migration => migration.Version)
            .ShouldBe(
                Enumerable.Range(1, PostgresMigrator.Migrations.Count),
                "versions are sequential from one. A gap or a repeat makes the ledger's " +
                "'have I applied this' question unanswerable.");

        foreach (var migration in PostgresMigrator.Migrations)
        {
            PostgresMigrator.ReadScript(migration).ShouldNotBeNullOrWhiteSpace(
                $"{migration} has no script. A migration listed without its .sql is a " +
                "package that cannot create its own schema.");
        }
    }

    /// <summary>
    /// No migration after the first destroys anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The executable form of the expand/contract rule, and the reason it is a text scan
    /// rather than a behavioural test: the property is "this release contains no
    /// destructive statement", and the only way to check that for a statement nobody wrote
    /// yet is to check the statements that are there.
    /// </para>
    /// <para>
    /// A contract migration is not forbidden forever — dropping a column is the fourth step
    /// of the rule. It is forbidden in the release that adds its replacement, which is what
    /// this catches: a single migration that adds the new column and drops the old one
    /// breaks every pod still running the previous release.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoMigrationAfterTheFirstIsDestructive()
    {
        string[] destructive =
        [
            "DROP TABLE", "DROP COLUMN", "DROP CONSTRAINT", "RENAME TO", "RENAME COLUMN",
            "SET NOT NULL", "TRUNCATE", "DROP INDEX",
        ];

        foreach (var migration in PostgresMigrator.Migrations.Skip(1))
        {
            var script = PostgresMigrator.ReadScript(migration).ToUpperInvariant();

            foreach (var statement in destructive)
            {
                script.ShouldNotContain(
                    statement,
                    Case.Sensitive,
                    $"{migration} contains '{statement}', which a pod running the previous " +
                    "release cannot survive. Expand/contract: add nullable, backfill, " +
                    "switch reads, and drop in a later release " +
                    "(docs/11-Distributed-Runtime.md §7.4).");
            }
        }
    }

    /// <summary>Migrating twice applies nothing the second time.</summary>
    /// <remarks>
    /// Every pod of a rolling update runs the migrator, so "already applied" is the ordinary
    /// case rather than the exception.
    /// </remarks>
    [Fact]
    public async Task MigratingIsIdempotent()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var applied = await schema.Migrator.AppliedVersionsAsync(Cancellation);

        applied.ShouldBe(
            PostgresMigrator.Migrations.Select(static migration => migration.Version).ToList(),
            "the fixture migrated to the latest version.");

        (await schema.Migrator.MigrateAsync(Cancellation)).ShouldBe(
            PostgresMigrator.TargetVersion,
            "a second migration is a no-op, not a duplicate-key error.");

        (await schema.Migrator.AppliedVersionsAsync(Cancellation)).Count.ShouldBe(
            applied.Count,
            "and it recorded nothing new.");
    }

    /// <summary>
    /// The expand migration leaves the previous release working.
    /// </summary>
    /// <remarks>
    /// The rollout this models: the schema moves to version 2 while pods running version 1
    /// are still serving. Those pods write rows that know nothing of
    /// <c>state_bag_sequence</c>, and they have to keep succeeding — which they do only
    /// because the column is nullable and defaults to nothing.
    /// </remarks>
    [Fact]
    public async Task TheExpandMigrationDoesNotBreakTheReleaseBeforeIt()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation, throughVersion: 1);

        var instance = Guid.NewGuid();

        // The previous release's writer: raw SQL that has never heard of the new column.
        await schema.ExecuteAsync(
            $$"""
              INSERT INTO flow_instance (instance_id, flow_id, flow_version, state, fence, state_bag)
              VALUES ('{{instance}}', 'order.place', '1.2.0', 'Running', 1, '{"a":1}');
              INSERT INTO flow_step (instance_id, scope, step_id, attempt, sequence,
                                     capability_id, capability_version, outcome)
              VALUES ('{{instance}}', '', 0, 1, 1, 'inventory.reserve', '2.1.0', 'Success');
              """,
            Cancellation);

        await schema.Migrator.MigrateAsync(2, Cancellation);

        (await schema.ScalarAsync(
            $"SELECT state_bag::text FROM flow_instance WHERE instance_id = '{instance}'",
            Cancellation))
            .ShouldBe("{\"a\":1}", "the expand migration rewrote no existing value.");

        (await schema.ScalarAsync(
            $"SELECT state_bag_sequence FROM flow_instance WHERE instance_id = '{instance}'",
            Cancellation))
            .ShouldBe(1L, "the backfill derived the snapshot's position from committed rows.");

        // And the previous release keeps writing, still not mentioning the column.
        var second = Guid.NewGuid();

        await schema.ExecuteAsync(
            $"""
             INSERT INTO flow_instance (instance_id, flow_id, flow_version, state, fence)
             VALUES ('{second}', 'order.place', '1.2.0', 'Running', 1)
             """,
            Cancellation);

        (await schema.ScalarAsync(
            $"SELECT state_bag_sequence FROM flow_instance WHERE instance_id = '{second}'",
            Cancellation))
            .ShouldBeNull(
                "a writer that does not know about the column leaves it null, which is the " +
                "whole reason the column has no default and no NOT NULL.");
    }

    /// <summary>
    /// A previous release's writer keeps staging outbox rows after <c>0004</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same rollout as the test above, one migration later, and the shape of the risk is
    /// different. <c>0002</c> added a nullable column: an old writer omits it and gets null,
    /// which is why that test asserts a null. <c>0004</c> adds <c>staged_seq</c> as
    /// <strong>NOT NULL</strong>, because the publisher orders by it and a null would be a
    /// row it cannot place. An old writer that omits a NOT NULL column is refused unless the
    /// column has a default — so the default is the entire compatibility argument, and this
    /// is where it is checked rather than asserted in a comment.
    /// </para>
    /// <para>
    /// The writer below is the previous release: raw SQL naming exactly the columns
    /// <c>JournalSql.InsertOutbox</c> named at version 3.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheOutboxExpandMigrationDoesNotBreakTheReleaseBeforeIt()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation, throughVersion: 3);

        var instance = Guid.NewGuid();

        await schema.ExecuteAsync(
            $$"""
              INSERT INTO flow_instance (instance_id, flow_id, flow_version, state, fence)
              VALUES ('{{instance}}', 'order.place', '1.2.0', 'Running', 1);
              INSERT INTO outbox_event (event_id, instance_id, sequence, ordinal, type,
                                        schema_version, partition_key, payload)
              VALUES (gen_random_uuid(), '{{instance}}', 1, 0, 'order.placed',
                      '1.0.0', 'order-7', '{"a":1}');
              """,
            Cancellation);

        await schema.Migrator.MigrateAsync(4, Cancellation);

        (await schema.ScalarAsync(
            $"SELECT count(*) FROM outbox_event WHERE instance_id = '{instance}' AND staged_seq IS NULL",
            Cancellation))
            .ShouldBe(
                0L,
                "the rewrite gave the row a sequence value, so an event staged before this " +
                "release still has a position the publisher can order it by.");

        // And the previous release keeps writing, still not mentioning the column.
        await schema.ExecuteAsync(
            $$"""
              INSERT INTO outbox_event (event_id, instance_id, sequence, ordinal, type,
                                        schema_version, partition_key, payload)
              VALUES (gen_random_uuid(), '{{instance}}', 2, 0, 'order.paid',
                      '1.0.0', 'order-7', '{"b":2}')
              """,
            Cancellation);

        var published = await schema.OutboxPublisher(new RecordingEventPublisher())
            .PublishPendingAsync(Cancellation);

        published.Published.ShouldBe(
            2,
            "a writer that has never heard of staged_seq takes the default, so its rows are " +
            "ordered and publishable rather than refused by a NOT NULL it cannot satisfy.");
    }

    /// <summary>The current adapter works against the schema the migrator produces.</summary>
    /// <remarks>
    /// The other direction of the same rollout, and the one that catches a migration that
    /// forgot a column the code writes.
    /// </remarks>
    [Fact]
    public async Task TheAdapterWritesAgainstTheMigratedSchema()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var instance = Guid.NewGuid();

        var started = await schema.Journal.StartAsync(
            new FlowInstanceStart
            {
                InstanceId = instance,
                FlowId = "order.place",
                FlowVersion = "1.2.0",
                Token = new FencingToken(1),
            },
            Cancellation);

        started.IsSuccess.ShouldBeTrue(
            $"the migrated schema accepts an instance. {(started.IsFailure ? started.Error.ToString() : string.Empty)}");
    }
}
