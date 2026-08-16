using System.Globalization;
using FlowX.Conformance;
using FlowX.Conformance.InMemory;
using Npgsql;
using Shouldly;
using Xunit;

namespace FlowX.Postgres.Tests;

/// <summary>
/// One empty schema, its own data source, and the three adapters over it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A schema per test, not a truncate per test.</strong> The conformance suite
/// requires "a fresh, empty journal … no state may survive between them", and a schema is
/// the cheapest thing in PostgreSQL that genuinely satisfies it — no leftover sequence
/// values, no leftover lease tokens, and no ordering dependency between tests that would
/// let one of them pass because of what another left behind.
/// </para>
/// <para>
/// It also means the migrator runs once per test rather than once per run, which is a
/// useful accident: by the end of a conformance pass the schema has been created from
/// nothing several dozen times.
/// </para>
/// </remarks>
internal sealed class PostgresTestSchema : IAsyncDisposable
{
    private PostgresTestSchema(
        NpgsqlDataSource dataSource,
        PostgresJournalOptions options,
        PostgresTenantStores? stores)
    {
        DataSource = dataSource;
        Options = options;
        TenantStores = stores;
        Journal = stores is null
            ? new PostgresFlowJournal(dataSource)
            : new PostgresFlowJournal(dataSource, stores);
        Leases = new PostgresLeaseStore(dataSource);
        RecoveryIndex = new PostgresRecoveryIndex(dataSource);
        Retention = new PostgresRetention(dataSource);
        ChangeFeed = new PostgresChangeFeed(dataSource);
        Migrator = new PostgresMigrator(dataSource, options);
        RecoveryScan = stores is null
            ? RecoveryIndex
            : new PostgresTenantRecoveryIndex(stores);
        TimerSweep = stores is null
            ? new PostgresTimerIndex(dataSource)
            : new PostgresTenantTimerIndex(stores);
    }

    /// <summary>The per-tenant pools, or null when every tenant shares this schema.</summary>
    public PostgresTenantStores? TenantStores { get; }

    /// <summary>
    /// The recovery scan as a host would resolve it: fanned out across tenant schemas where the
    /// deployment has them, and the single-schema query where it does not.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="RecoveryIndex"/> rather than replacing it, because the two are
    /// asserted about different things. That one is the query — its plan, its ordering, its
    /// partial index — and it is the same query at both levels. This one is the service, and at
    /// schema level it is a different type.
    /// </remarks>
    public IRecoveryIndex RecoveryScan { get; }

    /// <summary>The timer sweep as a host would resolve it, for <see cref="RecoveryScan"/>'s reason.</summary>
    public ITimerIndex TimerSweep { get; }

    /// <summary>The data source, with <c>search_path</c> already pointing at the schema.</summary>
    public NpgsqlDataSource DataSource { get; }

    /// <summary>Where the tables live.</summary>
    public PostgresJournalOptions Options { get; }

    /// <summary>The journal under test.</summary>
    public PostgresFlowJournal Journal { get; }

    /// <summary>The lease store under test.</summary>
    public PostgresLeaseStore Leases { get; }

    /// <summary>The recovery scan's query, under test.</summary>
    public PostgresRecoveryIndex RecoveryIndex { get; }

    /// <summary>The retention sweeper under test, with the default consumer set.</summary>
    public PostgresRetention Retention { get; }

    /// <summary>The change feed under test, which is also what writes <c>change_cursor</c>.</summary>
    public PostgresChangeFeed ChangeFeed { get; }

    /// <summary>
    /// A retention sweeper over this schema that knows what consumes its outbox.
    /// </summary>
    /// <param name="consumers">The publisher and the change subscriptions, if any.</param>
    /// <returns>The sweeper.</returns>
    /// <remarks>
    /// Constructed per call for <see cref="OutboxPublisher"/>'s reason: the consumer set is what
    /// the sweep's answer depends on, so a test that varies it needs more than one of these.
    /// </remarks>
    public PostgresRetention RetentionFor(RetentionConsumers consumers) =>
        new(DataSource, consumers);

    /// <summary>The migrator for this schema.</summary>
    public PostgresMigrator Migrator { get; }

    /// <summary>
    /// An outbox publisher over this schema, draining to a given broker.
    /// </summary>
    /// <param name="broker">Where published events go.</param>
    /// <param name="batchSize">How many events one pass claims. Defaults to §5's 500.</param>
    /// <param name="pollInterval">How long <c>RunAsync</c> waits after an empty pass.</param>
    /// <returns>The publisher.</returns>
    /// <remarks>
    /// Constructed per call rather than held as a property, because more than one of these
    /// against one schema is exactly the arrangement the <c>SKIP LOCKED</c> tests exist to
    /// make: two publishers, two brokers, one table. A single shared instance would have made
    /// that untestable and the sharing invisible.
    /// </remarks>
    public PostgresOutboxPublisher OutboxPublisher(
        IEventPublisher broker,
        int? batchSize = null,
        TimeSpan? pollInterval = null)
    {
        var options = new PostgresOutboxOptions();

        if (batchSize is { } size)
        {
            options = options with { BatchSize = size };
        }

        if (pollInterval is { } interval)
        {
            options = options with { PollInterval = interval };
        }

        return new PostgresOutboxPublisher(DataSource, broker, options);
    }

    /// <summary>
    /// Creates a schema and brings it to a version, or refuses to pretend it did.
    /// </summary>
    /// <param name="cancellationToken">Cancels the setup.</param>
    /// <param name="throughVersion">The schema version to migrate to. Defaults to the latest.</param>
    /// <returns>The prepared schema.</returns>
    /// <exception cref="InvalidOperationException">
    /// A database was promised by the environment and is not reachable.
    /// </exception>
    public static ValueTask<PostgresTestSchema> CreateAsync(
        CancellationToken cancellationToken,
        int? throughVersion = null) =>
        CreateAsync(tenantSchemas: false, throughVersion, cancellationToken);

    /// <summary>
    /// Creates a control schema whose tenants each get a schema of their own.
    /// </summary>
    /// <param name="cancellationToken">Cancels the setup.</param>
    /// <returns>The prepared store, with per-tenant pools.</returns>
    /// <remarks>
    /// The prefix is unique to this fixture, so two tests running side by side cannot provision
    /// the same tenant into the same schema — which would make an isolation assertion pass or
    /// fail depending on what else the runner happened to be doing.
    /// </remarks>
    public static ValueTask<PostgresTestSchema> CreateWithTenantSchemasAsync(
        CancellationToken cancellationToken) =>
        CreateAsync(tenantSchemas: true, throughVersion: null, cancellationToken);

    /// <summary>
    /// Creates a schema under a name the caller chose, dropping whatever was there before.
    /// </summary>
    /// <param name="name">The schema name, which must be the same on every run.</param>
    /// <param name="cancellationToken">Cancels the setup.</param>
    /// <returns>The prepared schema.</returns>
    /// <remarks>
    /// <para>
    /// <strong>The one caller is <see cref="PooledTenantIsolationTests"/>, and the constant
    /// name is the whole reason it exists.</strong> Every other test takes a fresh random
    /// schema, which is the right default and is unusable behind a transaction pooler: the
    /// schema there is resolved server-side, by a role default or the pooler's own database
    /// line, and neither can be told a name that changes every run.
    /// </para>
    /// <para>
    /// The previous run's schema is dropped rather than migrated into, because the conformance
    /// contract requires a journal with nothing in it and a re-run over a surviving schema is
    /// a migration no-op over yesterday's rows.
    /// </para>
    /// </remarks>
    public static ValueTask<PostgresTestSchema> CreateNamedAsync(
        string name,
        CancellationToken cancellationToken) =>
        CreateAsync(tenantSchemas: false, throughVersion: null, cancellationToken, name);

    private static async ValueTask<PostgresTestSchema> CreateAsync(
        bool tenantSchemas,
        int? throughVersion,
        CancellationToken cancellationToken,
        string? name = null)
    {
        if (!PostgresTestDatabase.IsAvailable)
        {
            if (PostgresTestDatabase.IsPromised)
            {
                throw PostgresTestDatabase.Unreachable();
            }

            Assert.Skip(PostgresTestDatabase.Reason);
        }

        var identity = Guid.NewGuid().ToString("n");

        var options = new PostgresJournalOptions
        {
            Schema = name ?? "flowx_t_" + identity,
            TenantSchemas = new TenantSchemaOptions
            {
                IsEnabled = tenantSchemas,

                // 22 characters, which is the whole prefix budget: a fixture-unique prefix is
                // also what makes DisposeAsync able to find every schema it created.
                Prefix = "t" + identity[..20] + "_",
            },
        };

        var dataSource = ServiceCollectionExtensions.BuildDataSource(
            PostgresTestDatabase.ConnectionString!, options);

        var stores = tenantSchemas
            ? new PostgresTenantStores(dataSource, PostgresTestDatabase.ConnectionString!, options)
            : null;

        var schema = new PostgresTestSchema(dataSource, options, stores);

        if (name is not null)
        {
            await DropAsync(dataSource, name, cancellationToken).ConfigureAwait(false);
        }

        await schema.Migrator
            .MigrateAsync(throughVersion ?? PostgresMigrator.TargetVersion, cancellationToken)
            .ConfigureAwait(false);

        return schema;
    }

    /// <summary>
    /// Opens an instance in this schema, moves it to a state, and makes its row as cold as a
    /// dead node would have left it.
    /// </summary>
    /// <param name="state">The state to leave the instance in.</param>
    /// <param name="idleFor">
    /// How long ago the row was last written. <see cref="TimeSpan.Zero"/> leaves it as freshly
    /// written, which is how a healthy instance is arranged.
    /// </param>
    /// <param name="tenantId">The partition key, or null for an untenanted instance.</param>
    /// <param name="cancellationToken">Cancels the arrangement.</param>
    /// <returns>The instance that was opened.</returns>
    /// <remarks>
    /// <para>
    /// <strong>Every row is written by the adapter.</strong> The state is reached through the
    /// journal's own calls — a commit for the states a running flow passes through, a
    /// completion for the states it ends in — so no assertion built on this can pass against a
    /// row shape <c>PostgresFlowJournal</c> does not produce.
    /// </para>
    /// <para>
    /// The one thing raw SQL does is move <c>updated_at</c> into the past, because staleness is
    /// the premise of the whole recovery query and the alternative is a test that sleeps for a
    /// lease TTL. <c>RecoveryStore</c> names that as the expected exception, and the reference
    /// store takes it too.
    /// </para>
    /// <para>
    /// Here rather than in either caller, because both <c>RecoveryIndexTests</c> and the
    /// conformance derivation need it and two copies of a fixture are two fixtures that can
    /// drift apart while agreeing about nothing.
    /// </para>
    /// </remarks>
    public async ValueTask<Guid> AbandonAsync(
        FlowInstanceState state,
        TimeSpan idleFor,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        var instance = Guid.NewGuid();

        var lease = await Leases.AcquireAsync(
            instance, "dead-node", TimeSpan.FromMilliseconds(1), cancellationToken);

        lease.IsSuccess.ShouldBeTrue(
            "a node takes the lease before it opens the instance row. The TTL is a millisecond " +
            "because the node this stands for is not coming back.");

        // Written through the scoped journal only where the tenant's rows live somewhere else.
        // At row level the arrangement deliberately uses the unscoped journal — the row is what
        // the test is arranging, and scoping it would be asserting the write path twice — but at
        // schema level the unscoped journal writes into the control schema, which is not where
        // this instance is supposed to end up.
        var journal = Writer(tenantId);

        var started = await journal.StartAsync(
            new FlowInstanceStart
            {
                InstanceId = instance,
                FlowId = RecoveryStore.FlowId,
                FlowVersion = RecoveryStore.FlowVersion,
                TenantId = tenantId,
                Token = lease.Value.Token,
            },
            cancellationToken);

        started.IsSuccess.ShouldBeTrue(started.IsFailure ? started.Error.ToString() : string.Empty);

        await MoveAsync(journal, instance, lease.Value.Token, state, cancellationToken);

        if (idleFor > TimeSpan.Zero)
        {
            await ExecuteAsync(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"""
                     UPDATE flow_instance
                        SET updated_at = now() - interval '{(long)idleFor.TotalSeconds} seconds'
                      WHERE instance_id = '{instance}'
                     """),
                tenantId,
                cancellationToken);
        }

        return instance;
    }

    /// <summary>Which journal an arrangement for this tenant has to be written through.</summary>
    public IFlowJournal Writer(string? tenantId) =>
        TenantStores is not null && tenantId is { Length: > 0 }
            ? Journal.ForTenant(tenantId)
            : Journal;

    /// <summary>The data source a tenant's rows are actually in.</summary>
    /// <param name="tenantId">The tenant, or null for the control schema.</param>
    /// <param name="cancellationToken">Cancels the lookup, and any provisioning it triggers.</param>
    /// <returns>The tenant's data source, or the control one.</returns>
    public async ValueTask<NpgsqlDataSource> SourceFor(
        string? tenantId,
        CancellationToken cancellationToken) =>
        TenantStores is not null && tenantId is { Length: > 0 }
            ? await TenantStores.ForAsync(tenantId, cancellationToken)
            : DataSource;

    /// <summary>Moves a freshly opened instance to the state under test.</summary>
    private static async ValueTask MoveAsync(
        IFlowJournal journal,
        Guid instance,
        FencingToken token,
        FlowInstanceState state,
        CancellationToken cancellationToken)
    {
        if (state is FlowInstanceState.Pending)
        {
            return;
        }

        if (state is FlowInstanceState.Completed
            or FlowInstanceState.Failed
            or FlowInstanceState.TimedOut
            or FlowInstanceState.CompensationFailed)
        {
            var completed = await journal.CompleteAsync(
                instance, token, state, JournalPayload.Empty, wake: null, cancellationToken);

            completed.IsSuccess.ShouldBeTrue(
                completed.IsFailure ? completed.Error.ToString() : string.Empty);

            return;
        }

        var committed = await journal.CommitAsync(
            new StepCommit
            {
                Key = StepKey.First(instance, 0),
                Token = token,
                CapabilityId = "order.validate",
                CapabilityVersion = "1.0.0",
                Outcome = JournalOutcome.Success,
                State = state,
            },
            cancellationToken);

        committed.IsSuccess.ShouldBeTrue(
            committed.IsFailure ? committed.Error.ToString() : string.Empty);
    }

    /// <summary>
    /// Opens an instance an outbox row may be staged against, and nothing else.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The instance id.</returns>
    /// <remarks>
    /// <c>outbox_event.instance_id</c> carries a foreign key, so a staged row needs a real
    /// instance behind it. The lease is taken and the row opened through the adapter, for
    /// <see cref="AbandonAsync"/>'s reason: a fixture that inserted the row itself could pass
    /// against a shape the journal does not produce.
    /// </remarks>
    public ValueTask<Guid> StageableInstanceAsync(CancellationToken cancellationToken) =>
        StageableInstanceAsync(tenantId: null, cancellationToken);

    /// <summary>
    /// Opens an instance belonging to one tenant, through that tenant's own journal.
    /// </summary>
    /// <param name="tenantId">Whose instance, or null for an untenanted one.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The instance id.</returns>
    /// <remarks>
    /// Written through <see cref="Writer"/> rather than through the control journal, because at
    /// schema isolation the row has to land in that tenant's schema for the change feed to find
    /// it there — and at row isolation the connection has to be bound for the policy's
    /// <c>WITH CHECK</c> to accept the tenant it names.
    /// </remarks>
    public async ValueTask<Guid> StageableInstanceAsync(
        string? tenantId, CancellationToken cancellationToken)
    {
        var instance = Guid.NewGuid();

        var lease = await Leases.AcquireAsync(
            instance, "test-node", TimeSpan.FromMinutes(5), cancellationToken);

        lease.IsSuccess.ShouldBeTrue(lease.IsFailure ? lease.Error.ToString() : string.Empty);

        var started = await Writer(tenantId).StartAsync(
            new FlowInstanceStart
            {
                InstanceId = instance,
                FlowId = RecoveryStore.FlowId,
                FlowVersion = RecoveryStore.FlowVersion,
                TenantId = tenantId,
                Token = lease.Value.Token,
            },
            cancellationToken);

        started.IsSuccess.ShouldBeTrue(started.IsFailure ? started.Error.ToString() : string.Empty);

        return instance;
    }

    /// <summary>
    /// Stages one outbox row in its own committed transaction.
    /// </summary>
    /// <param name="instance">The instance the event belongs to.</param>
    /// <param name="type">The event type, which is a change subscription's address.</param>
    /// <param name="payload">The body, as JSON.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The event's identity.</returns>
    public ValueTask<Guid> StageAsync(
        Guid instance, string type, string payload, CancellationToken cancellationToken) =>
        StageAsync(instance, type, payload, tenantId: null, cancellationToken);

    /// <summary>Stages one outbox row in the schema the instance's tenant owns.</summary>
    /// <param name="instance">The instance the event belongs to.</param>
    /// <param name="type">The event type, which is a change subscription's address.</param>
    /// <param name="payload">The body, as JSON.</param>
    /// <param name="tenantId">Whose schema, or null for the control one.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The event's identity.</returns>
    public async ValueTask<Guid> StageAsync(
        Guid instance,
        string type,
        string payload,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        await using var staging = await BeginAsync(tenantId, cancellationToken);

        var eventId = await staging.StageAsync(instance, type, payload, cancellationToken);

        await staging.CommitAsync(cancellationToken);

        return eventId;
    }

    /// <summary>
    /// Opens a transaction against this schema that the caller commits when it chooses.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The open transaction.</returns>
    /// <remarks>
    /// The arrangement the snapshot barrier exists for, and the only one in which the gap it
    /// closes is reachable: one transaction staging and holding while another stages and
    /// commits behind it.
    /// </remarks>
    public ValueTask<StagingTransaction> BeginAsync(CancellationToken cancellationToken) =>
        BeginAsync(tenantId: null, cancellationToken);

    /// <summary>Opens a staging transaction against one tenant's schema.</summary>
    /// <param name="tenantId">Whose schema, or null for the control one.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The open transaction.</returns>
    public async ValueTask<StagingTransaction> BeginAsync(
        string? tenantId, CancellationToken cancellationToken)
    {
        var source = await SourceFor(tenantId, cancellationToken);
        var connection = await source.OpenConnectionAsync(cancellationToken);
        var transaction = await connection.BeginTransactionAsync(cancellationToken);

        return new StagingTransaction(connection, transaction);
    }

    /// <summary>
    /// The payloads of one type's staged events that are below the visibility barrier, oldest
    /// first.
    /// </summary>
    /// <param name="type">The event type.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The payloads, in the order a feed would offer them.</returns>
    /// <remarks>
    /// The barrier and the ordering are both the feed's, written out here rather than called
    /// through <c>PostgresChangeFeed</c> so that the property is asserted about the database
    /// rather than about the adapter that relies on it.
    /// </remarks>
    public async ValueTask<IReadOnlyList<string>> BelowBarrierAsync(
        string type, CancellationToken cancellationToken)
    {
        await using var connection = await DataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT payload::text
              FROM outbox_event
             WHERE type = @type
               AND staged_xid < pg_snapshot_xmin(pg_current_snapshot())
             ORDER BY staged_xid, staged_seq
            """;

        command.Parameters.AddWithValue("type", type);

        var payloads = new List<string>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            payloads.Add(reader.GetString(0));
        }

        return payloads;
    }

    /// <summary>Runs a statement against this schema.</summary>
    /// <param name="sql">The statement.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many rows it affected.</returns>
    public Task<int> ExecuteAsync(string sql, CancellationToken cancellationToken) =>
        ExecuteAsync(sql, tenantId: null, cancellationToken);

    /// <summary>Runs a statement against the schema one tenant's rows are in.</summary>
    /// <param name="sql">The statement.</param>
    /// <param name="tenantId">Whose schema, or null for the control schema.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many rows it affected.</returns>
    /// <remarks>
    /// The statement runs as the migrating role rather than as <c>flowx_tenant</c>, which is
    /// what lets an arrangement backdate a row that the policy would otherwise hide from it.
    /// Nothing asserted is arranged this way — only <c>updated_at</c>, which is the premise of
    /// the recovery query and whose alternative is a test that sleeps for a lease TTL.
    /// </remarks>
    public async Task<int> ExecuteAsync(
        string sql,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        var source = await SourceFor(tenantId, cancellationToken);

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = sql;

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Reads a single value from this schema.</summary>
    /// <param name="sql">The query.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The first column of the first row, or null.</returns>
    public async Task<object?> ScalarAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = await DataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = sql;

        var value = await command.ExecuteScalarAsync(cancellationToken);

        return value is DBNull ? null : value;
    }

    /// <summary>Drops the schema and everything in it.</summary>
    public async ValueTask DisposeAsync()
    {
        if (TenantStores is not null)
        {
            await TenantStores.DisposeAsync();

            // Every schema this fixture's prefix owns, dropped before the control schema that
            // records them. A test that provisions three tenants creates three schemas, and a
            // suite that left them behind would grow the database by one schema per tenant per
            // run until an unrelated catalogue query started timing out.
            await using var connection = await DataSource.OpenConnectionAsync();
            await using var drop = connection.CreateCommand();

            drop.CommandText =
                """
                SELECT set_config('flowx.drop_prefix', @prefix, false);
                DO $$
                DECLARE victim text;
                BEGIN
                    FOR victim IN
                        SELECT nspname FROM pg_namespace
                         WHERE nspname LIKE current_setting('flowx.drop_prefix') || '%'
                    LOOP
                        EXECUTE format('DROP SCHEMA IF EXISTS %I CASCADE', victim);
                    END LOOP;
                END
                $$;
                """;

            drop.Parameters.AddWithValue("prefix", Options.TenantSchemas.Prefix);

            await drop.ExecuteNonQueryAsync();
        }

        await DropAsync(DataSource, Options.Schema, CancellationToken.None);

        await DataSource.DisposeAsync();
    }

    /// <summary>Drops a schema and everything in it, whether or not it is there.</summary>
    /// <param name="dataSource">The direct connection to issue it on.</param>
    /// <param name="name">The schema to drop.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <remarks>
    /// The same parameterised-identifier route the migrator uses to create it: the name
    /// reaches SQL as a value and is quoted by <c>format('%I')</c>, so a schema name is not a
    /// place a statement can be assembled from.
    /// </remarks>
    private static async ValueTask DropAsync(
        NpgsqlDataSource dataSource,
        string name,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT set_config('flowx.drop_schema', @schema, false);
            DO $$
            BEGIN
                EXECUTE format('DROP SCHEMA IF EXISTS %I CASCADE', current_setting('flowx.drop_schema'));
            END
            $$;
            """;

        command.Parameters.AddWithValue("schema", name);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>
/// A transaction the test holds open, so that a row can be staged and not yet committed.
/// </summary>
/// <remarks>
/// Staged with raw SQL rather than through <c>PostgresFlowJournal</c>, because
/// <c>CommitStepAsync</c> opens and commits its own transaction — which is exactly the thing
/// this type exists to keep the test in control of. The columns written are the ones the
/// journal writes; <c>staged_seq</c> and <c>staged_xid</c> come from their defaults, which is
/// how a writer that has never heard of them behaves and is the property the expand-only
/// migration claims.
/// </remarks>
internal sealed class StagingTransaction : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;

    internal StagingTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    /// <summary>Stages one event inside this transaction.</summary>
    /// <param name="instance">The instance the event belongs to.</param>
    /// <param name="type">The event type.</param>
    /// <param name="payload">The body, as JSON.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The event's identity.</returns>
    public async ValueTask<Guid> StageAsync(
        Guid instance, string type, string payload, CancellationToken cancellationToken)
    {
        var eventId = Guid.NewGuid();

        await using var command = _connection.CreateCommand();

        command.Transaction = _transaction;
        command.CommandText =
            """
            INSERT INTO outbox_event
                (event_id, instance_id, sequence, ordinal, type, schema_version, partition_key, payload)
            VALUES (@event, @instance, 1, 0, @type, '1.0.0', @key, @payload::json)
            """;

        command.Parameters.AddWithValue("event", eventId);
        command.Parameters.AddWithValue("instance", instance);
        command.Parameters.AddWithValue("type", type);
        command.Parameters.AddWithValue("key", instance.ToString());
        command.Parameters.AddWithValue("payload", payload);

        await command.ExecuteNonQueryAsync(cancellationToken);

        return eventId;
    }

    /// <summary>Commits, making everything staged here visible.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task CommitAsync(CancellationToken cancellationToken) =>
        _transaction.CommitAsync(cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
