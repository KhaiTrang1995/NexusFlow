using Npgsql;
using NpgsqlTypes;

namespace Crm;

/// <summary>
/// Writes a seed's rows, and writes none of them twice.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every statement is <c>ON CONFLICT DO NOTHING</c> on the primary key, and that is the
/// whole idempotency design.</strong> The id came from the alias, so the second run's insert
/// collides with the first run's row and reports nothing written. No ledger, no checksum, no
/// "has this file been applied" table — those all answer the question at the granularity of the
/// file, which is the wrong granularity: a run that fails on item nine has applied eight, and a
/// file-level ledger either forgets them or refuses to continue.
/// </para>
/// <para>
/// <strong>Rows are written under the tenant's own connection scope.</strong> Same as every
/// other store here: <c>flowx_tenant</c> cannot bypass row-level security, so a mistake in the
/// document's tenant is refused by the database and not merely by the code that read the file.
/// The check in <see cref="SeedErrors.TenantNotServed"/> is the first of those two, not the only
/// one.
/// </para>
/// </remarks>
public sealed class SeedStore
{
    private const string InsertAccount = """
        INSERT INTO account (account_id, tenant_id, name, industry, lifecycle, region, owner_id)
        VALUES (@id, @tenant, @name, @industry, @lifecycle, @region, @owner)
        ON CONFLICT (account_id) DO NOTHING
        """;

    private const string InsertContact = """
        INSERT INTO contact (contact_id, tenant_id, account_id, full_name, email, phone, is_primary)
        VALUES (@id, @tenant, @account, @name, @email, @phone, @primary)
        ON CONFLICT (contact_id) DO NOTHING
        """;

    private const string InsertOpportunity = """
        INSERT INTO opportunity (
            opportunity_id, tenant_id, account_id, primary_contact_id, name, amount, currency,
            stage_id, probability, expected_close, owner_id, stage_entered_at)
        VALUES (@id, @tenant, @account, @contact, @name, @amount, @currency, @stage, @probability,
            @close, @owner, @now)
        ON CONFLICT (opportunity_id) DO NOTHING
        """;

    private const string InsertLead = """
        INSERT INTO lead (
            lead_id, tenant_id, company, contact_name, email, source, status, score, owner_id,
            captured_at)
        VALUES (@id, @tenant, @company, @name, @email, @source, @status, @score, @owner, @now)
        ON CONFLICT (lead_id) DO NOTHING
        """;

    private const string ReadActiveProcess = """
        SELECT process_id, version
        FROM process_definition
        WHERE applies_to = @kind AND is_active
        LIMIT 1
        """;

    private const string InsertProcess = """
        INSERT INTO process_definition (
            process_id, tenant_id, applies_to, version, is_active, published_at)
        VALUES (@id, @tenant, @kind, @version, true, @now)
        ON CONFLICT (process_id) DO NOTHING
        """;

    private const string InsertStage = """
        INSERT INTO process_stage (stage_id, process_id, name, ordinal, is_terminal)
        VALUES (@id, @process, @name, @ordinal, @terminal)
        ON CONFLICT (stage_id) DO NOTHING
        """;

    private const string InsertTransition = """
        INSERT INTO process_transition (transition_id, from_stage_id, to_stage_id, trigger, ordinal)
        VALUES (@id, @from, @to, @trigger, @ordinal)
        ON CONFLICT (transition_id) DO NOTHING
        """;

    private readonly NpgsqlDataSource _source;

    /// <summary>Builds the store over the application's data source.</summary>
    /// <param name="source">The pool <c>AddFlowXPostgres</c> built.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public SeedStore(NpgsqlDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
    }

    /// <summary>Writes an account.</summary>
    /// <param name="tenant">Whose.</param>
    /// <param name="id">The id derived from its alias.</param>
    /// <param name="account">What to write.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>Whether a row was inserted rather than already there.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="account"/> is null.</exception>
    public async ValueTask<bool> WriteAccountAsync(
        string tenant,
        Guid id,
        SeedAccount account,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(account);

        var connection = await OpenAsync(tenant, ct).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = InsertAccount;
        Add(command, "id", NpgsqlDbType.Uuid, id);
        Add(command, "tenant", NpgsqlDbType.Text, tenant);
        Add(command, "name", NpgsqlDbType.Text, account.Name);
        Add(command, "industry", NpgsqlDbType.Text, account.Industry);
        Add(command, "lifecycle", NpgsqlDbType.Text, account.Lifecycle.ToString());
        Add(command, "region", NpgsqlDbType.Text, account.Region);
        Add(command, "owner", NpgsqlDbType.Uuid, account.Owner);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <summary>Writes a contact.</summary>
    /// <param name="tenant">Whose.</param>
    /// <param name="id">The id derived from its alias.</param>
    /// <param name="accountId">The account it belongs to.</param>
    /// <param name="contact">What to write.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>Whether a row was inserted rather than already there.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="contact"/> is null.</exception>
    public async ValueTask<bool> WriteContactAsync(
        string tenant,
        Guid id,
        Guid accountId,
        SeedContact contact,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(contact);

        var connection = await OpenAsync(tenant, ct).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = InsertContact;
        Add(command, "id", NpgsqlDbType.Uuid, id);
        Add(command, "tenant", NpgsqlDbType.Text, tenant);
        Add(command, "account", NpgsqlDbType.Uuid, accountId);
        Add(command, "name", NpgsqlDbType.Text, contact.FullName);
        Add(command, "email", NpgsqlDbType.Text, (object?)contact.Email ?? DBNull.Value);
        Add(command, "phone", NpgsqlDbType.Text, (object?)contact.Phone ?? DBNull.Value);
        Add(command, "primary", NpgsqlDbType.Boolean, contact.Primary);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <summary>Writes an opportunity.</summary>
    /// <param name="tenant">Whose.</param>
    /// <param name="id">The id derived from its alias.</param>
    /// <param name="accountId">The account.</param>
    /// <param name="contactId">The primary contact.</param>
    /// <param name="stageId">The stage it sits in.</param>
    /// <param name="opportunity">What to write.</param>
    /// <param name="now">The instant it entered that stage.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>Whether a row was inserted rather than already there.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="opportunity"/> is null.</exception>
    public async ValueTask<bool> WriteOpportunityAsync(
        string tenant,
        Guid id,
        Guid accountId,
        Guid contactId,
        Guid stageId,
        SeedOpportunity opportunity,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(opportunity);

        var connection = await OpenAsync(tenant, ct).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = InsertOpportunity;
        Add(command, "id", NpgsqlDbType.Uuid, id);
        Add(command, "tenant", NpgsqlDbType.Text, tenant);
        Add(command, "account", NpgsqlDbType.Uuid, accountId);
        Add(command, "contact", NpgsqlDbType.Uuid, contactId);
        Add(command, "name", NpgsqlDbType.Text, opportunity.Name);
        Add(command, "amount", NpgsqlDbType.Numeric, opportunity.Amount);
        Add(command, "currency", NpgsqlDbType.Text, opportunity.Currency);
        Add(command, "stage", NpgsqlDbType.Uuid, stageId);
        Add(command, "probability", NpgsqlDbType.Integer, opportunity.Probability);
        Add(command, "close", NpgsqlDbType.Date, opportunity.ExpectedClose);
        Add(command, "owner", NpgsqlDbType.Uuid, opportunity.Owner);
        Add(command, "now", NpgsqlDbType.TimestampTz, now);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <summary>Writes a lead.</summary>
    /// <param name="tenant">Whose.</param>
    /// <param name="id">The id derived from its alias.</param>
    /// <param name="lead">What to write.</param>
    /// <param name="now">When it was captured.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>Whether a row was inserted rather than already there.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="lead"/> is null.</exception>
    public async ValueTask<bool> WriteLeadAsync(
        string tenant,
        Guid id,
        SeedLead lead,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lead);

        var connection = await OpenAsync(tenant, ct).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = InsertLead;
        Add(command, "id", NpgsqlDbType.Uuid, id);
        Add(command, "tenant", NpgsqlDbType.Text, tenant);
        Add(command, "company", NpgsqlDbType.Text, lead.Company);
        Add(command, "name", NpgsqlDbType.Text, lead.ContactName);
        Add(command, "email", NpgsqlDbType.Text, (object?)lead.Email ?? DBNull.Value);
        Add(command, "source", NpgsqlDbType.Text, lead.Source.ToString());
        Add(command, "status", NpgsqlDbType.Text, lead.Status.ToString());
        Add(command, "score", NpgsqlDbType.Integer, lead.Score);
        Add(command, "owner", NpgsqlDbType.Uuid, (object?)lead.Owner ?? DBNull.Value);
        Add(command, "now", NpgsqlDbType.TimestampTz, now);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <summary>Which definition drives an entity kind for this tenant, if any is active.</summary>
    /// <param name="tenant">Whose.</param>
    /// <param name="kind">Which kind.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>The active definition's id and version, or null when there is none.</returns>
    public async ValueTask<(Guid Id, int Version)?> ActiveProcessAsync(
        string tenant,
        EntityKind kind,
        CancellationToken ct)
    {
        var connection = await OpenAsync(tenant, ct).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = ReadActiveProcess;
        Add(command, "kind", NpgsqlDbType.Text, kind.ToString());

        var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? (reader.GetGuid(0), reader.GetInt32(1))
            : null;
    }

    /// <summary>
    /// Publishes a definition, its stages and its transitions, or none of them.
    /// </summary>
    /// <param name="tenant">Whose.</param>
    /// <param name="process">What to publish.</param>
    /// <param name="now">The instant it was published.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>Whether it was written rather than already there.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="process"/> is null.</exception>
    /// <remarks>
    /// <strong>One transaction, unlike everything else here.</strong> A definition is not a row,
    /// it is a graph: a definition with half its stages is a pipeline an opportunity can enter
    /// and not leave, and <c>process_transition</c>'s foreign keys would let exactly that be
    /// committed one statement at a time.
    /// </remarks>
    public async ValueTask<bool> WriteProcessAsync(
        string tenant,
        SeedProcess process,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(process);

        var connection = await OpenAsync(tenant, ct).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var closingTransaction = transaction.ConfigureAwait(false);

        var processId = SeedIds.For(tenant, "process", process.Alias);
        var written = await ExecuteAsync(
            connection,
            InsertProcess,
            command =>
            {
                Add(command, "id", NpgsqlDbType.Uuid, processId);
                Add(command, "tenant", NpgsqlDbType.Text, tenant);
                Add(command, "kind", NpgsqlDbType.Text, process.AppliesTo.ToString());
                Add(command, "version", NpgsqlDbType.Integer, process.Version);
                Add(command, "now", NpgsqlDbType.TimestampTz, now);
            },
            ct).ConfigureAwait(false);

        for (var ordinal = 0; ordinal < process.Stages.Count; ordinal++)
        {
            var stage = process.Stages[ordinal]!;

            await ExecuteAsync(
                connection,
                InsertStage,
                command =>
                {
                    Add(command, "id", NpgsqlDbType.Uuid, StageId(tenant, process.Alias, stage.Name));
                    Add(command, "process", NpgsqlDbType.Uuid, processId);
                    Add(command, "name", NpgsqlDbType.Text, stage.Name);
                    Add(command, "ordinal", NpgsqlDbType.Integer, ordinal);
                    Add(command, "terminal", NpgsqlDbType.Boolean, stage.Terminal);
                },
                ct).ConfigureAwait(false);
        }

        for (var ordinal = 0; ordinal < process.Transitions.Count; ordinal++)
        {
            var transition = process.Transitions[ordinal]!;

            await ExecuteAsync(
                connection,
                InsertTransition,
                command =>
                {
                    Add(
                        command,
                        "id",
                        NpgsqlDbType.Uuid,
                        SeedIds.For(
                            tenant,
                            "transition",
                            $"{process.Alias}:{transition.From}>{transition.To}:{transition.Trigger}"));
                    Add(command, "from", NpgsqlDbType.Uuid, StageId(tenant, process.Alias, transition.From));
                    Add(command, "to", NpgsqlDbType.Uuid, StageId(tenant, process.Alias, transition.To));
                    Add(command, "trigger", NpgsqlDbType.Text, transition.Trigger);
                    Add(command, "ordinal", NpgsqlDbType.Integer, ordinal);
                },
                ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return written;
    }

    /// <summary>The id a stage of a seeded process always has.</summary>
    /// <param name="tenant">Whose.</param>
    /// <param name="processAlias">The process's alias in the file.</param>
    /// <param name="stage">The stage's name.</param>
    /// <returns>The identifier.</returns>
    public static Guid StageId(string tenant, string processAlias, string stage) =>
        SeedIds.For(tenant, "stage", processAlias + ":" + stage);

    private static async ValueTask<bool> ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        Action<NpgsqlCommand> bind,
        CancellationToken ct)
    {
        var command = connection.CreateCommand();
        await using var closing = command.ConfigureAwait(false);

        command.CommandText = sql;
        bind(command);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    private static void Add(NpgsqlCommand command, string name, NpgsqlDbType type, object value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });

    private async ValueTask<NpgsqlConnection> OpenAsync(string tenant, CancellationToken ct)
    {
        var connection = await _source.OpenConnectionAsync(ct).ConfigureAwait(false);

        try
        {
            await CrmTenantScope.ApplyAsync(connection, tenant, ct).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return connection;
    }
}
