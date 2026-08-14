using FlowX.Conformance;
using Npgsql;
using Shouldly;
using Xunit;

namespace FlowX.Postgres.Tests;

/// <summary>
/// Row-level tenant isolation through a **transaction-pooling proxy**, which is where it
/// stopped holding once and would stop holding again unnoticed.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this file exists.</strong> On 2026-08-14 the tenant binding was
/// <c>set_config(…, false)</c> — session-scoped — issued once when a connection was opened.
/// That is correct in front of Npgsql's pool, where a client connection <em>is</em> a server
/// session, and wrong in front of PgBouncer in transaction mode, where one server connection
/// is shared between clients. Reproduced at <c>default_pool_size = 1</c>: A bound
/// <c>tenant-A</c>, B bound <c>tenant-B</c>, and A's next statement read <c>tenant-B</c>.
/// A cross-tenant read.
/// </para>
/// <para>
/// <strong>Why the ordinary suite could not have caught it.</strong> Every other test here
/// reaches PostgreSQL directly, and the defect does not exist on a direct connection — the
/// same three steps return the empty setting, which is correct. The bug lived entirely in
/// the gap between "a connection" and "a server session", so only a run through a proxy that
/// separates the two can see it.
/// </para>
/// <para>
/// <strong>Why it is not the whole suite pointed at the proxy.</strong> That was tried and it
/// is not a test, it is a different harness: <see cref="PostgresTestSchema"/> issues DDL and
/// the runner prepares statements, and neither survives transaction pooling. So the schema is
/// built on the direct connection, exactly as every other test builds it, and only the
/// isolation questions are asked through the pool.
/// </para>
/// <para>
/// <strong>Opt-in, and loud about it.</strong> Gated on its own variable rather than on
/// <c>FLOWX_POSTGRES_CONNECTION</c>, because a pooled endpoint is a second piece of
/// infrastructure and a run without one has not tested this. Unset skips with a reason; set
/// set but unable to reach this run's schema skips too, naming which of the two known
/// PgBouncer incompatibilities it hit — see <c>UnreachableSchemaAsync</c>.
/// </para>
/// </remarks>
public sealed class PooledTenantIsolationTests
{
    /// <summary>The variable naming a transaction-pooling endpoint onto the same database.</summary>
    private const string PooledVariable = "FLOWX_POSTGRES_POOLED_CONNECTION";

    private const string TenantA = "acme";
    private const string TenantB = "globex";

    /// <summary>
    /// How many times the interleaving is repeated.
    /// </summary>
    /// <remarks>
    /// One round would prove the property only if the proxy happened to hand both tenants the
    /// same server connection, which at a pool size above one it may not. Twenty rounds of
    /// alternating tenants make sharing near-certain at any realistic pool size, and the
    /// defect this guards against fails on the first round that shares.
    /// </remarks>
    private const int Rounds = 20;

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static string? PooledConnectionString =>
        Environment.GetEnvironmentVariable(PooledVariable) is { Length: > 0 } value ? value : null;

    /// <summary>
    /// A tenant-scoped journal reaching the database through a transaction-pooling proxy still
    /// cannot read another tenant's instance, and can still read its own.
    /// </summary>
    /// <remarks>
    /// Both halves are asserted every round. The first is the defect; the second is what stops
    /// a "fix" that binds nothing at all from passing, which is the shape a careless repair of
    /// the first would take.
    /// </remarks>
    [Fact]
    public async Task TenantIsolationHoldsThroughATransactionPoolingProxy()
    {
        if (PooledConnectionString is not { } pooledConnectionString)
        {
            Assert.Skip(
                $"{PooledVariable} is not set, so tenant isolation has not been tested through a " +
                "transaction-pooling proxy — the one arrangement in which it has actually been " +
                "seen to fail. Point it at a PgBouncer in `pool_mode = transaction` in front of " +
                $"the same database {PostgresTestDatabase.ConnectionVariable} names, for example " +
                "'Host=localhost;Port=6432;Username=postgres;Database=postgres'. Npgsql needs " +
                "auto-prepare left off, which is its default.");

            return;
        }

        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var ofA = await schema.AbandonAsync(
            FlowInstanceState.Running, TimeSpan.Zero, TenantA, Cancellation);

        var ofB = await schema.AbandonAsync(
            FlowInstanceState.Running, TimeSpan.Zero, TenantB, Cancellation);

        // The same schema the direct connection just migrated, reached the long way round.
        await using var pooled = ServiceCollectionExtensions.BuildDataSource(
            pooledConnectionString, schema.Options);

        if (await UnreachableSchemaAsync(pooled, schema.Options.Schema) is { } why)
        {
            Assert.Skip(why);

            return;
        }

        var journal = new PostgresFlowJournal(pooled);
        var journalOfA = journal.ForTenant(TenantA);
        var journalOfB = journal.ForTenant(TenantB);

        for (var round = 0; round < Rounds; round++)
        {
            // B goes first so that, if a binding outlives its transaction, the value sitting on
            // the shared server connection when A asks is B's. That ordering is the whole
            // reproduction, and it is why the loop alternates rather than batching each tenant.
            (await journalOfB.ReadInstanceAsync(ofB, Cancellation)).IsSuccess.ShouldBeTrue(
                $"round {round}: tenant B could not read its own instance through the pool.");

            var aReadsB = await journalOfA.ReadInstanceAsync(ofB, Cancellation);

            aReadsB.IsFailure.ShouldBeTrue(
                $"round {round}: tenant A read tenant B's instance through a transaction-pooling " +
                "proxy. The tenant binding has stopped being local to the transaction that does " +
                "the work, so it is outliving its client and being applied to somebody else's " +
                "statement — the cross-tenant read blocker B-5 records.");

            aReadsB.Error.Code.ShouldBe(DurabilityErrors.InstanceNotFoundCode);

            (await journalOfA.ReadInstanceAsync(ofA, Cancellation)).IsSuccess.ShouldBeTrue(
                $"round {round}: tenant A could not read its own instance. An isolation that " +
                "refuses everybody is not an isolation, it is an outage — and it is what a " +
                "repair that simply stopped binding would look like.");
        }
    }

    /// <summary>
    /// Says why the pooled endpoint cannot reach this run's schema, or null when it can.
    /// </summary>
    /// <param name="pooled">The data source built on the pooled connection string.</param>
    /// <param name="schemaName">The schema this run migrated on the direct connection.</param>
    /// <returns>The reason to skip, or null to proceed.</returns>
    /// <remarks>
    /// <para>
    /// <strong>This probe exists because of a second incompatibility, and it is worth reading
    /// before configuring anything.</strong> The adapter selects its schema with Npgsql's
    /// <c>SearchPath</c>, which travels as a PostgreSQL <em>startup parameter</em>. PgBouncer
    /// rejects the connection outright — <c>08P01: unsupported startup parameter:
    /// search_path</c> — and the documented remedy, <c>ignore_startup_parameters =
    /// search_path</c>, makes it accept the connection and then <em>discard the schema</em>,
    /// after which every statement runs against the wrong one and answers
    /// <c>42P01: relation "flow_instance" does not exist</c>.
    /// </para>
    /// <para>
    /// Both were reproduced on 2026-08-14. Neither is safe, so a pooled endpoint has to resolve
    /// the schema by some other route — the schema on PgBouncer's own database line, or a role
    /// default — and a run whose endpoint does not is a run that has tested nothing. It skips
    /// and says which of the two it hit rather than failing, because the endpoint being
    /// unusable is a fact about the deployment and not a defect this test found.
    /// </para>
    /// </remarks>
    private static async Task<string?> UnreachableSchemaAsync(
        NpgsqlDataSource pooled,
        string schemaName)
    {
        try
        {
            await using var probe = await pooled.OpenConnectionAsync(Cancellation);
            await using var command = probe.CreateCommand();

            command.CommandText = "SELECT to_regclass('flow_instance') IS NOT NULL";

            var reachable = await command.ExecuteScalarAsync(Cancellation) as bool?;

            return reachable is true
                ? null
                : $"{PooledVariable} answers, but `flow_instance` is not on its search path, so "
                  + $"this run's schema '{schemaName}' is not what its statements reach. PgBouncer "
                  + "discards the `search_path` startup parameter the adapter selects the schema "
                  + "with — see this method's remarks — so the endpoint must carry the schema "
                  + "itself, on its database line or as a role default. Nothing about tenant "
                  + "isolation has been tested by this run.";
        }
        catch (NpgsqlException failure)
        {
            return $"{PooledVariable} is set and the endpoint refused the connection: "
                + $"{failure.Message}. PgBouncer rejects the `search_path` startup parameter the "
                + "adapter uses to select its schema, which is the first of the two "
                + "incompatibilities in this method's remarks. Nothing about tenant isolation has "
                + "been tested by this run.";
        }
    }
}
