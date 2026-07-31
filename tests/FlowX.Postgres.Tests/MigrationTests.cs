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
