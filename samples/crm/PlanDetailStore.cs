using Npgsql;
using NpgsqlTypes;

namespace Crm;

/// <summary>
/// Reads one plan and everything hung off it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Six statements on one connection, not six round trips from the client.</strong> A
/// screen that fetched the plan, then its objectives, then its steps would draw a plan whose
/// steps were read a moment after its risks — and the moment matters, because a step going
/// overdue between two of those requests shows as a plan that disagrees with itself.
/// </para>
/// <para>
/// <strong>Overdue is decided here.</strong> <c>due_on &lt; current_date</c> is the database's
/// clock, one clock, the same one the escalation sweep uses. A client comparing dates against its
/// own would report a step overdue in Sydney and not in Lisbon on the same afternoon.
/// </para>
/// </remarks>
public sealed class PlanDetailStore
{
    private const string Header = """
        SELECT p.plan_id, p.name, p.label, p.kind, d.name, p.owner_id, p.target_amount, p.currency
        FROM plan p
        JOIN plan_period d ON d.period_id = p.period_id
        WHERE p.name = @name
        """;

    private const string Objectives = """
        SELECT ordinal, description, measure, target, status
        FROM plan_objective WHERE plan_id = @plan ORDER BY ordinal
        """;

    // `owner_id` is here because the write is an upsert of the whole row: a client marking a step
    // done has to send the owner back, and one that could not read it would send whoever pressed
    // the tick.
    private const string Steps = """
        SELECT ordinal, description, owner_id, due_on, completed_at IS NOT NULL,
               completed_at IS NULL AND due_on < current_date
        FROM plan_step WHERE plan_id = @plan ORDER BY ordinal
        """;

    private const string Risks = """
        SELECT ordinal, description, severity, mitigation, is_open
        FROM plan_risk WHERE plan_id = @plan ORDER BY ordinal
        """;

    private const string Qualification = """
        SELECT element, is_answered, note
        FROM plan_qualification WHERE plan_id = @plan ORDER BY element
        """;

    // The name is joined from `contact` rather than stored beside the stakeholder: a copy would
    // be a second spelling of somebody's name, and the two disagree the first time one changes.
    private const string Stakeholders = """
        SELECT s.contact_id, c.full_name, s.role, s.sentiment, s.influence
        FROM account_stakeholder s
        JOIN contact c ON c.contact_id = s.contact_id
        WHERE s.plan_id = @plan
        ORDER BY s.influence DESC, c.full_name
        """;

    private readonly NpgsqlDataSource _source;

    /// <summary>Builds the store over the application's data source.</summary>
    /// <param name="source">The pool <c>AddFlowXPostgres</c> built.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public PlanDetailStore(NpgsqlDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
    }

    /// <summary>Reads one plan, or reports that this tenant has none by that name.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="name">Which plan.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The plan, or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public async ValueTask<PlanDetail?> ReadAsync(
        string? tenantId,
        string name,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var header = await ReadHeaderAsync(connection, name, cancellationToken).ConfigureAwait(false);

        if (header is not { } plan)
        {
            return null;
        }

        return new PlanDetail(
            plan.Name,
            plan.Label,
            plan.Kind,
            plan.Period,
            plan.Owner,
            plan.Target,
            plan.Currency,
            await ReadAllAsync(
                connection, Objectives, plan.Id,
                reader => new PlanObjectiveRow(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetDecimal(3),
                    reader.GetString(4)),
                cancellationToken).ConfigureAwait(false),
            await ReadAllAsync(
                connection, Steps, plan.Id,
                reader => new PlanStepRow(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    DateOnly.FromDateTime(reader.GetDateTime(3)),
                    reader.GetBoolean(4),
                    reader.GetBoolean(5)),
                cancellationToken).ConfigureAwait(false),
            await ReadAllAsync(
                connection, Risks, plan.Id,
                reader => new PlanRiskRow(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetBoolean(4)),
                cancellationToken).ConfigureAwait(false),
            await ReadAllAsync(
                connection, Qualification, plan.Id,
                reader => new PlanQualificationRow(
                    reader.GetString(0),
                    reader.GetBoolean(1),
                    reader.GetString(2)),
                cancellationToken).ConfigureAwait(false),
            await ReadAllAsync(
                connection, Stakeholders, plan.Id,
                reader => new PlanStakeholderRow(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt32(4)),
                cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask<PlanHeader?> ReadHeaderAsync(
        NpgsqlConnection connection,
        string name,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var closing = command.ConfigureAwait(false);

        command.CommandText = Header;
        command.Parameters.Add(new NpgsqlParameter("name", NpgsqlDbType.Text) { Value = name });

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new PlanHeader(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            // `owner_id` is text on `plan` and not a uuid: a plan's owner is a user identifier,
            // which is whatever the directory calls a person, and this sample's are strings.
            reader.GetString(5),
            await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false)
                ? null
                : reader.GetDecimal(6),
            await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false)
                ? null
                : reader.GetString(7));
    }

    private static async ValueTask<IReadOnlyList<T>> ReadAllAsync<T>(
        NpgsqlConnection connection,
        string sql,
        Guid plan,
        Func<NpgsqlDataReader, T> read,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var closing = command.ConfigureAwait(false);

        command.CommandText = sql;
        command.Parameters.Add(new NpgsqlParameter("plan", NpgsqlDbType.Uuid) { Value = plan });

        var rows = new List<T>();

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(read(reader));
        }

        return rows;
    }

    private async ValueTask<NpgsqlConnection> OpenAsync(
        string? tenantId,
        CancellationToken cancellationToken)
    {
        var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await CrmTenantScope.ApplyAsync(connection, tenantId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return connection;
    }

    private readonly record struct PlanHeader(
        Guid Id,
        string Name,
        string Label,
        string Kind,
        string Period,
        string Owner,
        decimal? Target,
        string? Currency);
}
