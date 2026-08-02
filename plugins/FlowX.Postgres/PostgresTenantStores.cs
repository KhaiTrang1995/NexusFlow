using System.Collections.Concurrent;
using Npgsql;


namespace FlowX.Postgres;

/// <summary>
/// One bounded connection pool per tenant, each already pointed at that tenant's schema.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is what <see cref="TenantIsolation.Schema"/> is, and the pooling answer is the
/// whole of it.</strong> <see cref="TenantIsolation.Row"/> changes what a policy filters;
/// schema isolation changes where a connection points, and "where a connection points" is
/// session state on a physical socket that outlives the borrower. Setting <c>search_path</c>
/// per borrow on a shared pool would put a tenant's schema on a connection the next borrower
/// inherits — the same class of defect <c>TenantScope</c> had to guard against, with none of
/// <c>TenantScope</c>'s defence, because a stale <c>search_path</c> produces rows rather than
/// refusals.
/// </para>
/// <para>
/// So it is not set per borrow. The schema is written into each tenant's <em>connection
/// string</em>, which Npgsql sends in the startup packet: it is a property of the physical
/// connection from the moment it is opened, <c>DISCARD ALL</c> restores it rather than clearing
/// it, and there is no statement anywhere that could change it. A tenant's pool holds only that
/// tenant's connections, so the question "could a pooled connection carry a tenant's schema to
/// the next borrower" does not have a safe answer — it has no borrower to carry it to.
/// </para>
/// <para>
/// <strong>The row policy is applied on top, not instead.</strong> A connection from this map
/// still assumes <c>flowx_tenant</c> and still sets <c>flowx.tenant_id</c>, so migration
/// <c>0008</c>'s policies are live inside the tenant's schema too. The two walls fail in
/// opposite directions and that is the point: a wrong <c>search_path</c> lands on rows whose
/// <c>tenant_id</c> is somebody else's and the policy returns nothing, while a wrong
/// <c>flowx.tenant_id</c> lands in a schema that holds no such rows. Neither mistake reads
/// another tenant's data; both read none.
/// </para>
/// <para>
/// <strong>Provisioning is once per tenant per process, and the database arbitrates the
/// race.</strong> <see cref="PostgresMigrator"/> already takes an advisory lock keyed on the
/// schema name and applies its migrations in one transaction, so two nodes meeting the same new
/// tenant at the same instant produce one migrated schema and one no-op — the same mechanism
/// that makes a rolling update safe, reused rather than reinvented.
/// </para>
/// </remarks>
public sealed class PostgresTenantStores : IAsyncDisposable, IDisposable
{
    /// <summary>The registry table, in the control schema.</summary>
    /// <remarks>
    /// <para>
    /// <strong>It exists for the sweeps and for nothing else.</strong> A recovery scan and a
    /// timer sweep are node-wide by construction — they look for work nobody is holding — so at
    /// this level they have to ask every tenant, and "every tenant" is a set that has to be
    /// written down somewhere. Nothing on the execution path reads it: a tenant's schema is
    /// derived from its id (<see cref="TenantSchemaName"/>), so a registry that is stale can
    /// cost a sweep a tenant and can never misroute a read.
    /// </para>
    /// <para>
    /// Created from code rather than by a migration, for the reason <c>schema_migration</c> is:
    /// it belongs to the control schema alone, and a migration would also create it — empty and
    /// misleading — inside every tenant schema the migrator touches.
    /// </para>
    /// </remarks>
    private const string RegistryDdl =
        """
        CREATE TABLE IF NOT EXISTS tenant_schema (
            tenant_id   text        NOT NULL PRIMARY KEY,
            schema_name text        NOT NULL UNIQUE,
            created_at  timestamptz NOT NULL DEFAULT now()
        )
        """;

    private readonly NpgsqlDataSource _control;
    private readonly string _connectionString;
    private readonly PostgresJournalOptions _options;

    private readonly ConcurrentDictionary<string, NpgsqlDataSource> _stores =
        new(StringComparer.Ordinal);

    /// <summary>Serialises the provisioning path, so one new tenant is provisioned once.</summary>
    /// <remarks>
    /// One gate for every tenant rather than one each. Provisioning happens once per tenant per
    /// process and the steady state never reaches it — the dictionary answers first, with no
    /// lock — so what a shared gate costs is that two <em>different</em> new tenants arriving in
    /// the same instant are migrated one after the other. What a gate per tenant would cost is a
    /// dictionary of gates that has to be pruned or leaks one entry per tenant for ever.
    /// </remarks>
    private readonly SemaphoreSlim _provisioning = new(1, 1);

    /// <summary>Creates the map.</summary>
    /// <param name="control">
    /// The data source over the control schema, which holds the migration ledger, the leases
    /// and the registry. Not owned: it is the same singleton the lease store uses.
    /// </param>
    /// <param name="connectionString">
    /// How to reach the server. Every tenant's data source is this string with its
    /// <c>Search Path</c> and pool bound replaced.
    /// </param>
    /// <param name="options">Where the tables live and how many pools they may have.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The connection string is empty.</exception>
    public PostgresTenantStores(
        NpgsqlDataSource control,
        string connectionString,
        PostgresJournalOptions options)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(options);

        _control = control;
        _connectionString = connectionString;
        _options = options;
    }

    /// <summary>The schema one tenant's rows live in.</summary>
    /// <param name="tenantId">The tenant, exactly as it was resolved.</param>
    /// <returns>The derived schema name.</returns>
    /// <exception cref="ArgumentException"><paramref name="tenantId"/> is null or blank.</exception>
    public string SchemaFor(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        return TenantSchemaName.For(_options.TenantSchemas.Prefix, tenantId);
    }

    /// <summary>
    /// The data source for one tenant, provisioning its schema the first time it is asked for.
    /// </summary>
    /// <param name="tenantId">The tenant, exactly as it was resolved.</param>
    /// <param name="cancellationToken">Cancels the provisioning.</param>
    /// <returns>A data source whose connections resolve to that tenant's schema.</returns>
    /// <exception cref="ArgumentException"><paramref name="tenantId"/> is null or blank.</exception>
    /// <exception cref="InvalidOperationException">
    /// The schema is absent and this deployment does not provision at run time.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <strong>The steady state takes no lock at all</strong> — a tenant that has been seen
    /// before is one dictionary read on the path of every journal call, which is what makes
    /// schema isolation cost the same per call as row isolation once a process is warm.
    /// </para>
    /// <para>
    /// <strong>A failed provisioning is not remembered.</strong> Nothing is added to the map
    /// until the schema is migrated and registered, so a tenant whose first request met a
    /// database that was down is retried on the next one rather than permanently refused by a
    /// memoised failure — and the half-built data source is disposed rather than leaked.
    /// </para>
    /// </remarks>
    public async ValueTask<NpgsqlDataSource> ForAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        if (_stores.TryGetValue(tenantId, out var known))
        {
            return known;
        }

        await _provisioning.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Asked again under the gate: a caller that queued behind the tenant it was waiting
            // for must not migrate it a second time.
            if (_stores.TryGetValue(tenantId, out known))
            {
                return known;
            }

            var store = await ProvisionAsync(tenantId, cancellationToken).ConfigureAwait(false);

            _stores[tenantId] = store;

            return store;
        }
        finally
        {
            _provisioning.Release();
        }
    }

    /// <summary>
    /// Every tenant this store has a schema for, as the registry records them.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The tenant ids, ordered so a sweep visits them in a stable order.</returns>
    /// <remarks>
    /// Read on every sweep rather than cached, because the set is the point: a tenant
    /// provisioned by another node five seconds ago has instances this node is supposed to be
    /// able to recover, and a cached list is a list that does not contain it. The read is one
    /// sequential scan of a table with one row per tenant, on an interval measured in seconds.
    /// </remarks>
    public async ValueTask<IReadOnlyList<string>> KnownTenantsAsync(
        CancellationToken cancellationToken)
    {
        await EnsureRegistryAsync(cancellationToken).ConfigureAwait(false);

        var connection = await _control.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        using var command = connection.CreateCommand();

        command.CommandText = "SELECT tenant_id FROM tenant_schema ORDER BY tenant_id";

        var tenants = new List<string>();

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tenants.Add(reader.GetString(0));
        }

        return tenants;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The control data source is not disposed: it is the singleton the lease store, the
    /// retention sweeper and the migrator all hold, and closing it from here would close
    /// connections they are still using. Only the pools this map created are its to close.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        foreach (var store in _stores.Values)
        {
            await store.DisposeAsync().ConfigureAwait(false);
        }

        _stores.Clear();
        _provisioning.Dispose();
    }

    /// <inheritdoc />
    /// <remarks>
    /// <strong>Both, because a container decides which one it calls and this type does not
    /// get to insist.</strong> <c>ServiceProvider.Dispose</c> throws outright on a singleton
    /// that is only <see cref="IAsyncDisposable"/> — so a host disposed synchronously, which is
    /// what a plain <c>using var host = builder.Build()</c> does, would fail at shutdown on a
    /// deployment that had run correctly for a month. <see cref="NpgsqlDataSource"/> offers both
    /// for the same reason, which is why there is nothing to give up by matching it.
    /// </remarks>
    public void Dispose()
    {
        foreach (var store in _stores.Values)
        {
            store.Dispose();
        }

        _stores.Clear();
        _provisioning.Dispose();
    }

    /// <summary>Builds one tenant's data source and makes sure its schema is ready.</summary>
    private async ValueTask<NpgsqlDataSource> ProvisionAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        var schema = SchemaFor(tenantId);

        var settings = new NpgsqlConnectionStringBuilder(_connectionString)
        {
            // In the startup packet, not in a statement. This is the property that makes the
            // pool safe rather than merely careful — see the remarks on this class.
            SearchPath = schema,

            // docs/16 §5: "Connection pools are per-tenant and bounded, so a tenant with 10 000
            // idle connections is impossible."
            MaxPoolSize = _options.TenantSchemas.MaxPoolSizePerTenant,
        };

        var store = new NpgsqlDataSourceBuilder(settings.ConnectionString).Build();

        try
        {
            if (_options.TenantSchemas.ProvisionOnFirstUse)
            {
                await new PostgresMigrator(store, _options with { Schema = schema, CreateSchemaIfMissing = true })
                    .MigrateAsync(cancellationToken)
                    .ConfigureAwait(false);

                await RegisterAsync(tenantId, schema, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await RequireSchemaAsync(tenantId, schema, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            await store.DisposeAsync().ConfigureAwait(false);

            throw;
        }

        return store;
    }

    /// <summary>
    /// Records the tenant in the registry, after its schema is migrated and not before.
    /// </summary>
    /// <remarks>
    /// The order is the fail-closed one. Registering first would advertise a schema to every
    /// node's sweep before the tables in it existed, and a sweep that queried it would fail
    /// rather than find nothing. Registering after means a crash between the two leaves a
    /// migrated schema no sweep visits — which loses recovery for one tenant until the next
    /// process reaches this line again, rather than breaking the sweep for all of them.
    /// </remarks>
    private async ValueTask RegisterAsync(
        string tenantId,
        string schema,
        CancellationToken cancellationToken)
    {
        await EnsureRegistryAsync(cancellationToken).ConfigureAwait(false);

        var connection = await _control.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        using var command = connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO tenant_schema (tenant_id, schema_name)
            VALUES (@tenant, @schema)
            ON CONFLICT (tenant_id) DO NOTHING
            """;

        command.Parameters.Add(Db.Text("tenant", tenantId));
        command.Parameters.Add(Db.Text("schema", schema));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses a tenant whose schema nobody provisioned, by name.
    /// </summary>
    /// <remarks>
    /// Only reachable when the deployment turned <see cref="TenantSchemaOptions.ProvisionOnFirstUse"/>
    /// off, and it exists so that the refusal names the tenant and the schema rather than
    /// arriving as <c>relation "flow_instance" does not exist</c> from the first statement that
    /// happens to run.
    /// </remarks>
    private async ValueTask RequireSchemaAsync(
        string tenantId,
        string schema,
        CancellationToken cancellationToken)
    {
        var connection = await _control.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        using var command = connection.CreateCommand();

        command.CommandText = "SELECT to_regnamespace(@schema) IS NOT NULL";
        command.Parameters.Add(Db.Text("schema", schema));

        var present = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        if (present is true)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Tenant '{tenantId}' has no schema. It would live in '{schema}', which does not " +
            $"exist, and {nameof(TenantSchemaOptions)}.{nameof(TenantSchemaOptions.ProvisionOnFirstUse)} " +
            "is off — so this deployment provisions tenants out of band and this one was not " +
            "provisioned. Run the migrator against that schema, or turn provisioning on.");
    }

    /// <summary>Creates the registry table if it is not there yet.</summary>
    /// <remarks>
    /// Issued on the paths that read or write it rather than once at construction, for
    /// <c>PostgresMigrator.EnsureLedgerAsync</c>'s reason: a store is built by a container long
    /// before anything has decided the database is reachable, and a constructor that opened a
    /// connection would make wiring the adapter fail where using it should.
    /// </remarks>
    private async ValueTask EnsureRegistryAsync(CancellationToken cancellationToken)
    {
        var connection = await _control.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        using var command = connection.CreateCommand();

        command.CommandText = RegistryDdl;

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
