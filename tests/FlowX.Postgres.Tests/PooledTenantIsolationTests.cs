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
/// but unable to reach the schema skips too, saying what to do about it — see
/// <see cref="UnreachableSchemaAsync"/>. CI supplies the endpoint, so neither skip is the
/// state a pull request is merged in.
/// </para>
/// <para>
/// <strong>Why the schema has a fixed name here and nowhere else.</strong> Every other test
/// takes a fresh random schema and puts it on the connection as Npgsql's <c>SearchPath</c>,
/// which travels as a PostgreSQL startup parameter — the one thing a transaction pooler
/// cannot carry. Behind the pool the schema has to be resolved server-side instead, by a
/// role default or the pooler's own database line, and neither can be told a name that
/// changes every run. So the name is a constant, the role default is applied on the direct
/// connection before the pooled data source is built, and the pooled data source is built
/// with <c>SetSearchPathOnConnection = false</c> — which is the deployment shape
/// <see cref="PostgresJournalOptions.SetSearchPathOnConnection"/> exists for, exercised here
/// rather than only described.
/// </para>
/// </remarks>
public sealed class PooledTenantIsolationTests
{
    /// <summary>The variable naming a transaction-pooling endpoint onto the same database.</summary>
    private const string PooledVariable = "FLOWX_POSTGRES_POOLED_CONNECTION";

    /// <summary>
    /// The schema this test migrates, resolved server-side by the pooled endpoint.
    /// </summary>
    /// <remarks>
    /// Constant on purpose, for the reason on this class. It is also what makes a pooler's
    /// cached server connection harmless: the name never changes, so a session that resolved
    /// it yesterday resolves the same schema today.
    /// </remarks>
    private const string PooledSchema = "flowx_pooled";

    private const string TenantA = "acme";
    private const string TenantB = "globex";

    /// <summary>
    /// How many times each tenant repeats its questions, against the other running at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The count is doing real work here, and an earlier draft of this test proves
    /// it.</strong> That draft asked the two tenants' questions one after another and passed
    /// against the defective binding as readily as against the fixed one — because a binding
    /// that outlives its transaction can only be read by somebody else if somebody else runs
    /// in between, and sequential code never lets them. It tested nothing and said it had
    /// tested something, which is worse than the skip it replaced.
    /// </para>
    /// <para>
    /// So the two arms run concurrently and each repeats: the window between one client's bind
    /// and its own next statement is short, the other client has to land inside it, and rounds
    /// are how a short window becomes a near-certain one. The assertion does not depend on the
    /// interleaving — no tenant may ever read the other's row, whatever order the proxy
    /// chooses — so more rounds only make the test more likely to catch a regression, never
    /// more likely to invent one.
    /// </para>
    /// </remarks>
    private const int Rounds = 50;

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static string? PooledConnectionString =>
        Environment.GetEnvironmentVariable(PooledVariable) is { Length: > 0 } value ? value : null;

    /// <summary>
    /// A tenant-scoped journal reaching the database through a transaction-pooling proxy still
    /// cannot read another tenant's instance, and can still read its own.
    /// </summary>
    /// <remarks>
    /// This method arranges; <see cref="AskAsync"/> asserts. Both tenants ask from their own
    /// arm rather than one interrogating the other, because the property is symmetric and a
    /// binding that leaks leaks in whichever direction the proxy happens to schedule.
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
                "auto-prepare left off, which is its default. Nothing else has to be " +
                $"configured: this test puts schema '{PooledSchema}' on the endpoint's role " +
                "itself, because a role default is the one route to a schema that a shared " +
                "server session carries.");

            return;
        }

        await using var schema = await PostgresTestSchema.CreateNamedAsync(
            PooledSchema, Cancellation);

        var ofA = await schema.AbandonAsync(
            FlowInstanceState.Running, TimeSpan.Zero, TenantA, Cancellation);

        var ofB = await schema.AbandonAsync(
            FlowInstanceState.Running, TimeSpan.Zero, TenantB, Cancellation);

        await ResolveSchemaServerSideAsync(schema, pooledConnectionString);

        // The same schema the direct connection just migrated, reached the long way round --
        // and with the startup parameter turned off, because that is the half of this
        // arrangement a pooler rejects outright.
        await using var pooled = ServiceCollectionExtensions.BuildDataSource(
            pooledConnectionString,
            schema.Options with { SetSearchPathOnConnection = false });

        if (await UnreachableSchemaAsync(pooled, schema.Options.Schema) is { } why)
        {
            Assert.Skip(why);

            return;
        }

        var journal = new PostgresFlowJournal(pooled);

        // Both arms are started before either is awaited, which is the entire arrangement: two
        // clients sharing one pooled server session, each able to land between the other's bind
        // and the statement that bind was for.
        var arms = Task.WhenAll(
            Task.Run(
                () => AskAsync(journal.ForTenant(TenantA), TenantA, ofA, ofB, TenantB),
                Cancellation),
            Task.Run(
                () => AskAsync(journal.ForTenant(TenantB), TenantB, ofB, ofA, TenantA),
                Cancellation));

        await arms;
    }

    /// <summary>
    /// Asks one tenant's two questions, repeatedly, while the other tenant does the same.
    /// </summary>
    /// <param name="journal">The journal scoped to this tenant.</param>
    /// <param name="tenant">Which tenant this arm is, for the failure message.</param>
    /// <param name="own">An instance belonging to this tenant.</param>
    /// <param name="foreign">An instance belonging to the other one.</param>
    /// <param name="otherTenant">Who the foreign instance belongs to.</param>
    /// <remarks>
    /// Both questions are asked every round. The first is the defect — a binding that outlived
    /// its transaction is another client's binding by the time it is read. The second is what
    /// stops a "fix" that binds nothing at all from passing, which is the shape a careless
    /// repair of the first would take: an isolation that refuses everybody is an outage.
    /// </remarks>
    private static async Task AskAsync(
        IFlowJournal journal,
        string tenant,
        Guid own,
        Guid foreign,
        string otherTenant)
    {
        for (var round = 0; round < Rounds; round++)
        {
            var reachesForeign = await journal.ReadInstanceAsync(foreign, Cancellation);

            reachesForeign.IsFailure.ShouldBeTrue(
                $"round {round}: tenant {tenant} read tenant {otherTenant}'s instance through a " +
                "transaction-pooling proxy. The tenant binding has stopped being local to the " +
                "transaction that does the work, so it is outliving its client and being applied " +
                "to somebody else's statement — the cross-tenant read blocker B-5 records.");

            reachesForeign.Error.Code.ShouldBe(DurabilityErrors.InstanceNotFoundCode);

            (await journal.ReadInstanceAsync(own, Cancellation)).IsSuccess.ShouldBeTrue(
                $"round {round}: tenant {tenant} could not read its own instance. An isolation " +
                "that refuses everybody is not an isolation, it is an outage — and it is what a " +
                "repair that simply stopped binding would look like.");
        }
    }

    /// <summary>
    /// Makes the pooled endpoint resolve this run's schema, the only way a pooler can.
    /// </summary>
    /// <param name="schema">The schema already migrated on the direct connection.</param>
    /// <param name="pooledConnectionString">The endpoint whose role and database to configure.</param>
    /// <remarks>
    /// <para>
    /// A role default is applied when the <em>server</em> session starts, so it survives a
    /// proxy that shares one server session between clients — which is the property no
    /// client-sent setting has. <c>PostgresJournalOptions.SetSearchPathOnConnection</c> names
    /// this as the known-good route; this is the test that makes the claim answerable.
    /// </para>
    /// <para>
    /// <strong>It is deliberately not reset afterwards.</strong> The default is the pooled
    /// endpoint's configuration rather than this test's state: it names a constant schema, it
    /// is idempotent, and it is what a deployment behind a pooler has to have set anyway.
    /// Resetting it would leave the next run depending on the pooler dropping its cached
    /// server connections between the two, which nothing here can make it do.
    /// </para>
    /// </remarks>
    private static async Task ResolveSchemaServerSideAsync(
        PostgresTestSchema schema,
        string pooledConnectionString)
    {
        var endpoint = new NpgsqlConnectionStringBuilder(pooledConnectionString);

        await using var connection = await schema.DataSource.OpenConnectionAsync(Cancellation);
        await using var command = connection.CreateCommand();

        // Identifiers, so they cannot be parameters -- passed as values and quoted by format,
        // which is the route PostgresTestSchema.DropAsync takes for the same reason.
        command.CommandText =
            """
            SELECT set_config('flowx.pooled_role', @role, false),
                   set_config('flowx.pooled_database', @database, false),
                   set_config('flowx.pooled_schema', @schema, false);
            DO $$
            BEGIN
                EXECUTE format(
                    'ALTER ROLE %I IN DATABASE %I SET search_path = %I',
                    current_setting('flowx.pooled_role'),
                    current_setting('flowx.pooled_database'),
                    current_setting('flowx.pooled_schema'));
            END
            $$;
            """;

        command.Parameters.AddWithValue("role", endpoint.Username!);
        command.Parameters.AddWithValue("database", endpoint.Database!);
        command.Parameters.AddWithValue("schema", schema.Options.Schema);

        await command.ExecuteNonQueryAsync(Cancellation);
    }

    /// <summary>
    /// Says why the pooled endpoint cannot reach this run's schema, or null when it can.
    /// </summary>
    /// <param name="pooled">The data source built on the pooled connection string.</param>
    /// <param name="schemaName">The schema this run migrated on the direct connection.</param>
    /// <returns>The reason to skip, or null to proceed.</returns>
    /// <remarks>
    /// <para>
    /// <strong>What this probe is left guarding, now that the two incompatibilities are
    /// handled.</strong> Selecting the schema with Npgsql's <c>SearchPath</c> sends a
    /// PostgreSQL <em>startup parameter</em>; PgBouncer rejects the connection outright —
    /// <c>08P01: unsupported startup parameter: search_path</c> — and its documented remedy,
    /// <c>ignore_startup_parameters = search_path</c>, makes it accept the connection and then
    /// <em>discard the schema</em>, after which every statement answers <c>42P01: relation
    /// "flow_instance" does not exist</c>. Both were reproduced on 2026-08-14. This test sends
    /// no startup parameter and sets a role default instead, so neither should now happen.
    /// </para>
    /// <para>
    /// One thing can still leave the endpoint pointing elsewhere and it is not a defect in the
    /// adapter: a pooler holding a <em>cached</em> server session that was opened before the
    /// role default existed. That session keeps the search path it started with. PgBouncer's
    /// <c>RECONNECT</c> clears it, and it cannot recur afterwards, because the schema name is
    /// a constant. A run that hits it has tested nothing, so it skips saying exactly that
    /// rather than failing — the endpoint's state is a fact about the deployment, not a defect
    /// this test found — and a green suite never counts it as a pass.
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
                  + $"schema '{schemaName}' is not what its statements reach. The role default "
                  + "this test just set is applied when a server session starts, so the endpoint "
                  + "is serving a session it opened earlier — issue `RECONNECT` on PgBouncer's "
                  + "admin console, or restart it, and run again. Nothing about tenant isolation "
                  + "has been tested by this run.";
        }
        catch (NpgsqlException failure)
        {
            return $"{PooledVariable} is set and the endpoint refused the connection: "
                + $"{failure.Message}. Nothing about tenant isolation has been tested by this run.";
        }
    }
}
