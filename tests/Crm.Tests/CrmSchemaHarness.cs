using FlowX.Postgres;
using Npgsql;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// One deployment of the CRM schema, on a real PostgreSQL schema of its own.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No store here is a double, and that is the whole point of this project.</strong>
/// What package 3 delivers is thirteen tables, thirteen policies, one trigger, one partial
/// unique index and one view. Every one of those is the database's behaviour; every assertion
/// about them would pass vacuously against an in-memory anything.
/// </para>
/// <para>
/// <strong>A schema per harness, dropped on disposal.</strong> Two tests running side by side
/// must not be able to see each other's rows, or an isolation assertion would pass or fail
/// depending on what else the runner happened to be doing. It is also what makes the
/// <c>search_path</c> pinned onto <c>crm_activity_parent_must_exist</c> by migration
/// <c>0003</c> a claim with teeth: several schemas on one server, each with its own
/// <c>lead</c>.
/// </para>
/// <para>
/// <strong>Every statement a test issues goes through the same door the runtime uses.</strong>
/// <see cref="AsTenantAsync"/> assumes <c>flowx_tenant</c> and sets <c>flowx.tenant_id</c>
/// exactly as <c>CrmSchemaReader</c> does, so seeding a row is subject to the same
/// <c>WITH CHECK</c> that reading one is subject to. <see cref="AsOwnerAsync"/> exists for the
/// one thing a scoped connection cannot do — DDL — and the only test that needs it is the one
/// that switches a policy off to prove the others would notice.
/// </para>
/// </remarks>
internal sealed class CrmSchemaHarness : IAsyncDisposable
{
    /// <summary>One tenant. Named rather than "tenant-a" so a failure message reads.</summary>
    public const string Northwind = CrmTokens.NorthwindTenant;

    /// <summary>The other. Isolation is asserted in both directions, never once.</summary>
    public const string Contoso = CrmTokens.ContosoTenant;

    private readonly string _schema;

    private CrmSchemaHarness(NpgsqlDataSource dataSource, string schema)
    {
        DataSource = dataSource;
        _schema = schema;
    }

    /// <summary>The data source, with <c>search_path</c> already pointing at this schema.</summary>
    public NpgsqlDataSource DataSource { get; }

    /// <summary>
    /// The schema this harness owns, for a second data source that has to reach the same tables.
    /// </summary>
    /// <remarks>
    /// <see cref="CrmApplication"/> is the only caller: a host composed over
    /// <c>AddFlowXPostgres</c> builds its own pool and needs the same <c>search_path</c>, or the
    /// application would write its journal into <c>public</c> while the tests read tables here.
    /// </remarks>
    public string Schema => _schema;

    /// <summary>Creates a schema, migrates the CRM tables into it, and hands it back.</summary>
    /// <param name="cancellationToken">Cancels the setup.</param>
    /// <param name="throughVersion">
    /// The CRM migration to stop at. Defaults to everything. A test that stands the schema at
    /// <c>1</c> has the tables and no policies, which is the shape migration <c>0002</c> exists
    /// to change.
    /// </param>
    /// <returns>The prepared harness.</returns>
    /// <exception cref="InvalidOperationException">
    /// A database was promised by the environment and is not reachable.
    /// </exception>
    public static async ValueTask<CrmSchemaHarness> CreateAsync(
        CancellationToken cancellationToken,
        int? throughVersion = null)
    {
        if (!CrmDatabase.IsAvailable)
        {
            if (CrmDatabase.IsPromised)
            {
                throw CrmDatabase.Unreachable();
            }

            Assert.Skip(CrmDatabase.Reason);
        }

        var options = new PostgresJournalOptions
        {
            Schema = "flowx_crm_" + Guid.NewGuid().ToString("n"),
        };

        var dataSource = ServiceCollectionExtensions.BuildDataSource(
            CrmDatabase.ConnectionString!, options);

        var harness = new CrmSchemaHarness(dataSource, options.Schema);

        // The platform migrator is not run. Migration 0002 creates flowx_tenant itself and
        // grants it exactly the CRM tables, so nothing here needs flow_instance — and a test
        // project that migrated the journal to test a sample's schema would be asserting over
        // a wider surface than it changes.
        await harness.CreateSchemaAsync(cancellationToken).ConfigureAwait(false);

        await new CrmMigrator(dataSource)
            .MigrateAsync(throughVersion ?? CrmMigrator.TargetVersion, cancellationToken)
            .ConfigureAwait(false);

        return harness;
    }

    /// <summary>Runs a statement on a connection narrowed to one tenant.</summary>
    /// <param name="tenantId">The tenant to act as.</param>
    /// <param name="sql">The statement.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <param name="parameters">Bound values, by name.</param>
    /// <returns>Rows affected.</returns>
    public async ValueTask<int> AsTenantAsync(
        string? tenantId,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        var connection = await OpenAsTenantAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        using var command = Command(connection, sql, parameters);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one value on a connection narrowed to one tenant.</summary>
    /// <typeparam name="T">What the statement returns.</typeparam>
    /// <param name="tenantId">The tenant to act as.</param>
    /// <param name="sql">The statement.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <param name="parameters">Bound values, by name.</param>
    /// <returns>The scalar, or the type's default when the statement produced no row.</returns>
    public async ValueTask<T?> ScalarAsTenantAsync<T>(
        string? tenantId,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        var connection = await OpenAsTenantAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        using var command = Command(connection, sql, parameters);

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull ? default : (T)value;
    }

    /// <summary>Runs a statement as the role that created the schema, under no tenant scope.</summary>
    /// <param name="sql">The statement.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <remarks>
    /// DDL only. Every table here declares <c>FORCE ROW LEVEL SECURITY</c>, so this connection
    /// is under the policies too — being the owner buys the ability to <c>ALTER</c>, not the
    /// ability to read another tenant's rows, and that is exactly what <c>FORCE</c> is for.
    /// </remarks>
    public async ValueTask AsOwnerAsync(string sql, CancellationToken cancellationToken)
    {
        var connection = await DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        using var command = Command(connection, sql, []);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one value as the role that created the schema, under no tenant scope.</summary>
    /// <typeparam name="T">What the statement returns.</typeparam>
    /// <param name="sql">The statement.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <param name="parameters">Bound values, by name.</param>
    /// <returns>The scalar, or the type's default when the statement produced no row.</returns>
    /// <remarks>
    /// For the platform's tables rather than the CRM's. <c>outbox_event</c> carries no
    /// <c>tenant_id</c> and migration <c>0002</c> grants <c>flowx_tenant</c> the CRM tables and
    /// nothing else, so a scoped connection cannot read it — which is correct, and leaves this
    /// as the only way to assert that an emitted event was staged.
    /// </remarks>
    public async ValueTask<T?> ScalarAsOwnerAsync<T>(
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        var connection = await DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        using var command = Command(connection, sql, parameters);

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull ? default : (T)value;
    }

    /// <summary>The <c>PostgresException</c> a statement raised, or null when it did not.</summary>
    /// <param name="tenantId">The tenant to act as.</param>
    /// <param name="sql">The statement.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <param name="parameters">Bound values, by name.</param>
    /// <returns>The exception, for a test to assert an SQLSTATE on.</returns>
    public async ValueTask<PostgresException?> RefusalAsync(
        string? tenantId,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        try
        {
            await AsTenantAsync(tenantId, sql, cancellationToken, parameters).ConfigureAwait(false);

            return null;
        }
        catch (PostgresException refusal)
        {
            return refusal;
        }
    }

    // ------------------------------------------------------------------ seeding

    /// <summary>Inserts an account, and returns its id.</summary>
    /// <param name="tenantId">Whose account.</param>
    /// <param name="lifecycle">How far it has come. Decides whether the view sees it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async ValueTask<Guid> AccountAsync(
        string tenantId,
        Lifecycle lifecycle,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();

        await AsTenantAsync(
            tenantId,
            """
            INSERT INTO account (account_id, tenant_id, name, industry, lifecycle, region, owner_id)
            VALUES (@id, @tenant, @name, 'Software', @lifecycle, 'emea', @owner)
            """,
            cancellationToken,
            ("id", id),
            ("tenant", tenantId),
            ("name", "Account " + id.ToString("n")[..8]),
            ("lifecycle", lifecycle.ToString()),
            ("owner", Guid.NewGuid()))
            .ConfigureAwait(false);

        return id;
    }

    /// <summary>Inserts a contact at an account, and returns its id.</summary>
    /// <param name="tenantId">Whose contact.</param>
    /// <param name="accountId">Where they work.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async ValueTask<Guid> ContactAsync(
        string tenantId,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();

        await AsTenantAsync(
            tenantId,
            """
            INSERT INTO contact (contact_id, tenant_id, account_id, full_name, email, phone, is_primary)
            VALUES (@id, @tenant, @account, 'A Person', 'a.person@example.test', '+44 20 7946 0000', true)
            """,
            cancellationToken,
            ("id", id),
            ("tenant", tenantId),
            ("account", accountId))
            .ConfigureAwait(false);

        return id;
    }

    /// <summary>Inserts a lead, and returns its id.</summary>
    /// <param name="tenantId">Whose lead.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async ValueTask<Guid> LeadAsync(string tenantId, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();

        await AsTenantAsync(
            tenantId,
            """
            INSERT INTO lead (lead_id, tenant_id, company, contact_name, email, source, status, score, captured_at)
            VALUES (@id, @tenant, 'A Company', 'A Person', 'a.person@example.test', 'Web', 'New', 0, now())
            """,
            cancellationToken,
            ("id", id),
            ("tenant", tenantId))
            .ConfigureAwait(false);

        return id;
    }

    /// <summary>
    /// Publishes a process definition with one stage, and returns both ids.
    /// </summary>
    /// <param name="tenantId">Whose process.</param>
    /// <param name="version">Which version.</param>
    /// <param name="isActive">Whether new work starts on it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <param name="appliesTo">Which entity kind it governs.</param>
    public async ValueTask<(Guid Process, Guid Stage)> ProcessAsync(
        string tenantId,
        int version,
        bool isActive,
        CancellationToken cancellationToken,
        EntityKind appliesTo = EntityKind.Opportunity)
    {
        var process = Guid.NewGuid();
        var stage = Guid.NewGuid();

        await AsTenantAsync(
            tenantId,
            """
            INSERT INTO process_definition (process_id, tenant_id, applies_to, version, is_active, published_at)
            VALUES (@process, @tenant, @appliesTo, @version, @active, now())
            """,
            cancellationToken,
            ("process", process),
            ("tenant", tenantId),
            ("appliesTo", appliesTo.ToString()),
            ("version", version),
            ("active", isActive))
            .ConfigureAwait(false);

        await AsTenantAsync(
            tenantId,
            """
            INSERT INTO process_stage (stage_id, process_id, name, ordinal, is_terminal)
            VALUES (@stage, @process, 'Qualification', 0, false)
            """,
            cancellationToken,
            ("stage", stage),
            ("process", process))
            .ConfigureAwait(false);

        return (process, stage);
    }

    /// <summary>Inserts an opportunity, and returns its id.</summary>
    /// <param name="tenantId">Whose opportunity.</param>
    /// <param name="accountId">Whose business.</param>
    /// <param name="contactId">Who is being sold to.</param>
    /// <param name="stageId">The stage that pins its process version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async ValueTask<Guid> OpportunityAsync(
        string tenantId,
        Guid accountId,
        Guid contactId,
        Guid stageId,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();

        await AsTenantAsync(
            tenantId,
            """
            INSERT INTO opportunity (
                opportunity_id, tenant_id, account_id, primary_contact_id, name,
                amount, currency, stage_id, probability, expected_close, owner_id, stage_entered_at)
            VALUES (@id, @tenant, @account, @contact, 'A deal',
                50000.0000, 'EUR', @stage, 20, current_date + 30, @owner, now())
            """,
            cancellationToken,
            ("id", id),
            ("tenant", tenantId),
            ("account", accountId),
            ("contact", contactId),
            ("stage", stageId),
            ("owner", Guid.NewGuid()))
            .ConfigureAwait(false);

        return id;
    }

    /// <summary>Inserts a quote with one line, and returns both ids.</summary>
    /// <param name="tenantId">Whose quote.</param>
    /// <param name="opportunityId">What it prices.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async ValueTask<(Guid Quote, Guid Line)> QuoteAsync(
        string tenantId,
        Guid opportunityId,
        CancellationToken cancellationToken)
    {
        var quote = Guid.NewGuid();
        var line = Guid.NewGuid();

        await AsTenantAsync(
            tenantId,
            """
            INSERT INTO quote (quote_id, tenant_id, opportunity_id, status, subtotal, discount, total, currency, valid_until)
            VALUES (@quote, @tenant, @opportunity, 'Draft', 1000.0000, 0.0000, 1000.0000, 'EUR', now() + interval '30 days')
            """,
            cancellationToken,
            ("quote", quote),
            ("tenant", tenantId),
            ("opportunity", opportunityId))
            .ConfigureAwait(false);

        await AsTenantAsync(
            tenantId,
            """
            INSERT INTO quote_line (quote_line_id, quote_id, sku, quantity, unit_price, currency)
            VALUES (@line, @quote, 'SKU-1', 2, 500.0000, 'EUR')
            """,
            cancellationToken,
            ("line", line),
            ("quote", quote))
            .ConfigureAwait(false);

        return (quote, line);
    }

    /// <summary>Inserts an activity hanging off a parent, or reports why the trigger refused.</summary>
    /// <param name="tenantId">Whose activity.</param>
    /// <param name="kind">Which of the four kinds of parent.</param>
    /// <param name="parentId">The parent row.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The exception the trigger raised, or null when the insert stood.</returns>
    public ValueTask<PostgresException?> ActivityRefusalAsync(
        string tenantId,
        EntityKind kind,
        Guid parentId,
        CancellationToken cancellationToken) =>
        RefusalAsync(
            tenantId,
            """
            INSERT INTO activity (
                activity_id, tenant_id, kind, subject, relates_to_kind, relates_to_id,
                owner_id, due_at, status, escalation_count)
            VALUES (@id, @tenant, 'Task', 'Follow up', @relatesToKind, @relatesToId,
                @owner, now() + interval '1 day', 'Open', 0)
            """,
            cancellationToken,
            ("id", Guid.NewGuid()),
            ("tenant", tenantId),
            ("relatesToKind", kind.ToString()),
            ("relatesToId", parentId),
            ("owner", Guid.NewGuid()));

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            var connection = await DataSource.OpenConnectionAsync().ConfigureAwait(false);
            await using var closing = connection.ConfigureAwait(false);

            using var command = connection.CreateCommand();

            // The schema name is this harness's own, built from a Guid in CreateAsync.
            command.CommandText = $"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE";

            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            await DataSource.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask CreateSchemaAsync(CancellationToken cancellationToken)
    {
        var connection = await DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        using var command = connection.CreateCommand();

        command.CommandText = $"CREATE SCHEMA IF NOT EXISTS \"{_schema}\"";

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<NpgsqlConnection> OpenAsTenantAsync(
        string? tenantId,
        CancellationToken cancellationToken)
    {
        var connection = await DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await CrmTenantScope.ApplyAsync(connection, tenantId, cancellationToken)
                .ConfigureAwait(false);

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);

            throw;
        }
    }

    private static NpgsqlCommand Command(
        NpgsqlConnection connection,
        string sql,
        (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();

        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }
}
