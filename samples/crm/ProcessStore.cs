using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace Crm;

/// <summary>
/// The configured process, read from the tables an administrator writes.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Reads the definition, never the decision.</strong> Which transition to take and
/// whether its guards hold are <see cref="ProcessRules"/>'s business, and they are pure. This
/// class turns rows into the records those functions take, and afterwards writes what the
/// actions did. Keeping the two apart is what makes the decision testable without a server and
/// reproducible on a replay.
/// </para>
/// <para>
/// <strong>Every statement is a <c>const</c>.</strong> <c>SqlFitnessTests</c> refuses SQL text
/// built at run time, and the tenant reaches the connection through
/// <see cref="CrmTenantScope"/> rather than reaching a statement as text.
/// </para>
/// </remarks>
public sealed class ProcessStore
{
    private const string SelectStageProcess = """
        SELECT d.process_id, d.applies_to, d.version, d.is_active
        FROM process_stage s
        JOIN process_definition d ON d.process_id = s.process_id
        WHERE s.stage_id = @stage
        """;

    private const string SelectStages = """
        SELECT stage_id, process_id, name, ordinal, is_terminal
        FROM process_stage
        WHERE process_id = @process
        ORDER BY ordinal
        """;

    private const string SelectTransitions = """
        SELECT t.transition_id, t.from_stage_id, t.to_stage_id, t.trigger, t.ordinal
        FROM process_transition t
        JOIN process_stage s ON s.stage_id = t.from_stage_id
        WHERE s.process_id = @process
        ORDER BY t.ordinal
        """;

    private const string SelectGuards = """
        SELECT g.guard_id, g.transition_id, g.field, g.operator, g.value
        FROM transition_guard g
        JOIN process_transition t ON t.transition_id = g.transition_id
        JOIN process_stage s ON s.stage_id = t.from_stage_id
        WHERE s.process_id = @process
        """;

    private const string SelectActions = """
        SELECT a.action_id, a.transition_id, a.kind, a.parameters, a.ordinal
        FROM transition_action a
        JOIN process_transition t ON t.transition_id = a.transition_id
        JOIN process_stage s ON s.stage_id = t.from_stage_id
        WHERE s.process_id = @process
        ORDER BY a.ordinal
        """;

    private const string SelectFacts = """
        SELECT o.amount, o.currency, o.probability, a.region, a.industry, o.owner_id
        FROM opportunity o
        JOIN account a ON a.account_id = o.account_id
        WHERE o.opportunity_id = @opportunity
        """;

    private const string SelectStage = "SELECT stage_id FROM opportunity WHERE opportunity_id = @opportunity";

    private const string MoveStage = """
        UPDATE opportunity SET stage_id = @stage, stage_entered_at = @now
        WHERE opportunity_id = @opportunity
        """;

    private const string SetProbability = """
        UPDATE opportunity SET probability = @probability WHERE opportunity_id = @opportunity
        """;

    private const string InsertActivity = """
        INSERT INTO activity (
            activity_id, tenant_id, kind, subject, relates_to_kind, relates_to_id,
            owner_id, due_at, status, escalation_count)
        VALUES (@id, @tenant, @kind, @subject, 'Opportunity', @relates, @owner, @due, 'Open', 0)
        """;

    private readonly NpgsqlDataSource _source;

    /// <summary>Builds the store over the application's data source.</summary>
    /// <param name="source">The pool <c>AddFlowXPostgres</c> built.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public ProcessStore(NpgsqlDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
    }

    /// <summary>
    /// The definition an opportunity's current stage belongs to, and everything in it.
    /// </summary>
    /// <param name="tenantId">The tenant the change attested.</param>
    /// <param name="stageId">The stage the entity is in.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The stages and candidates, or null when the stage is unknown.</returns>
    /// <remarks>
    /// <strong>Reached through the entity's stage, which is what pins the version.</strong> §6
    /// asks that an opportunity already in flight stays on the definition it started on. There
    /// is no version column on <c>opportunity</c> to say so: a stage belongs to exactly one
    /// definition, so following <c>stage_id</c> lands on that version and cannot land on the
    /// one an administrator published this morning.
    /// </remarks>
    public async ValueTask<ProcessSnapshot?> ReadForStageAsync(
        string? tenantId,
        Guid stageId,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var process = await ReadProcessAsync(connection, stageId, cancellationToken).ConfigureAwait(false);

        if (process is null)
        {
            return null;
        }

        var stages = await ReadStagesAsync(connection, process.Id, cancellationToken).ConfigureAwait(false);
        var transitions = await ReadTransitionsAsync(connection, process.Id, cancellationToken).ConfigureAwait(false);
        var guards = await ReadGuardsAsync(connection, process.Id, cancellationToken).ConfigureAwait(false);
        var actions = await ReadActionsAsync(connection, process.Id, cancellationToken).ConfigureAwait(false);

        var candidates = transitions
            .Select(transition => new TransitionCandidate(
                transition,
                guards.Where(g => g.Transition == transition.Id).ToList(),
                actions.Where(a => a.Transition == transition.Id).OrderBy(a => a.Ordinal).ToList()))
            .ToList();

        return new ProcessSnapshot(process, stages, candidates);
    }

    /// <summary>The whitelisted fields of one opportunity.</summary>
    /// <param name="tenantId">The tenant the change attested.</param>
    /// <param name="opportunityId">The opportunity.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Its facts, or null when the caller's tenant cannot see it.</returns>
    public async ValueTask<ProcessFacts?> ReadFactsAsync(
        string? tenantId,
        Guid opportunityId,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = SelectFacts;
        command.Parameters.Add(new NpgsqlParameter("opportunity", NpgsqlDbType.Uuid) { Value = opportunityId });

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ProcessFacts(
            reader.GetDecimal(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetGuid(5));
    }

    /// <summary>The stage an opportunity is in.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="opportunityId">The opportunity.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Its stage, or null when the tenant cannot see it.</returns>
    public async ValueTask<Guid?> ReadStageAsync(
        string? tenantId,
        Guid opportunityId,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = SelectStage;
        command.Parameters.Add(new NpgsqlParameter("opportunity", NpgsqlDbType.Uuid) { Value = opportunityId });

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is Guid stage
            ? stage
            : null;
    }

    /// <summary>Moves an opportunity to a stage.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="opportunityId">The opportunity.</param>
    /// <param name="stageId">Where it goes.</param>
    /// <param name="now">The engine's clock.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async ValueTask MoveAsync(
        string? tenantId,
        Guid opportunityId,
        Guid stageId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = MoveStage;
        command.Parameters.Add(new NpgsqlParameter("opportunity", NpgsqlDbType.Uuid) { Value = opportunityId });
        command.Parameters.Add(new NpgsqlParameter("stage", NpgsqlDbType.Uuid) { Value = stageId });
        command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes an activity a configured action asked for.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="id">The activity's id, derived by the caller.</param>
    /// <param name="opportunityId">What it hangs off.</param>
    /// <param name="subject">What it says.</param>
    /// <param name="owner">Who owes it.</param>
    /// <param name="dueAt">When, or null.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async ValueTask CreateTaskAsync(
        string? tenantId,
        Guid id,
        Guid opportunityId,
        string subject,
        Guid owner,
        DateTimeOffset? dueAt,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = InsertActivity;
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = id });
        command.Parameters.Add(new NpgsqlParameter("tenant", NpgsqlDbType.Text) { Value = tenantId ?? string.Empty });
        command.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Text) { Value = "Task" });
        command.Parameters.Add(new NpgsqlParameter("subject", NpgsqlDbType.Text) { Value = subject });
        command.Parameters.Add(new NpgsqlParameter("relates", NpgsqlDbType.Uuid) { Value = opportunityId });
        command.Parameters.Add(new NpgsqlParameter("owner", NpgsqlDbType.Uuid) { Value = owner });
        command.Parameters.Add(new NpgsqlParameter("due", NpgsqlDbType.TimestampTz)
        {
            Value = (object?)dueAt ?? DBNull.Value,
        });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes a whitelisted field a <c>SetField</c> action names.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="opportunityId">The opportunity.</param>
    /// <param name="probability">The new probability.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <remarks>
    /// <strong>Probability and nothing else, deliberately.</strong> A <c>SetField</c> that could
    /// write any column would be the configuration deciding what a deployment's data means, and
    /// the guard whitelist would then be the only thing left that is checked. Widening this is a
    /// code change, which is §7.3's line.
    /// </remarks>
    public async ValueTask SetProbabilityAsync(
        string? tenantId,
        Guid opportunityId,
        int probability,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = SetProbability;
        command.Parameters.Add(new NpgsqlParameter("opportunity", NpgsqlDbType.Uuid) { Value = opportunityId });
        command.Parameters.Add(new NpgsqlParameter("probability", NpgsqlDbType.Integer) { Value = probability });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ProcessDefinition?> ReadProcessAsync(
        NpgsqlConnection connection,
        Guid stageId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = SelectStageProcess;
        command.Parameters.Add(new NpgsqlParameter("stage", NpgsqlDbType.Uuid) { Value = stageId });

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ProcessDefinition(
            reader.GetGuid(0),
            Enum.Parse<EntityKind>(reader.GetString(1)),
            reader.GetInt32(2),
            reader.GetBoolean(3),
            DateTimeOffset.MinValue);
    }

    private static async ValueTask<List<ProcessStage>> ReadStagesAsync(
        NpgsqlConnection connection,
        Guid process,
        CancellationToken cancellationToken)
    {
        var rows = new List<ProcessStage>();

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = SelectStages;
        command.Parameters.Add(new NpgsqlParameter("process", NpgsqlDbType.Uuid) { Value = process });

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new ProcessStage(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
                reader.GetInt32(3), reader.GetBoolean(4)));
        }

        return rows;
    }

    private static async ValueTask<List<ProcessTransition>> ReadTransitionsAsync(
        NpgsqlConnection connection,
        Guid process,
        CancellationToken cancellationToken)
    {
        var rows = new List<ProcessTransition>();

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = SelectTransitions;
        command.Parameters.Add(new NpgsqlParameter("process", NpgsqlDbType.Uuid) { Value = process });

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new ProcessTransition(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2),
                reader.GetString(3), reader.GetInt32(4)));
        }

        return rows;
    }

    private static async ValueTask<List<TransitionGuard>> ReadGuardsAsync(
        NpgsqlConnection connection,
        Guid process,
        CancellationToken cancellationToken)
    {
        var rows = new List<TransitionGuard>();

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = SelectGuards;
        command.Parameters.Add(new NpgsqlParameter("process", NpgsqlDbType.Uuid) { Value = process });

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new TransitionGuard(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
                Enum.Parse<GuardOperator>(reader.GetString(3)), reader.GetString(4)));
        }

        return rows;
    }

    private static async ValueTask<List<TransitionAction>> ReadActionsAsync(
        NpgsqlConnection connection,
        Guid process,
        CancellationToken cancellationToken)
    {
        var rows = new List<TransitionAction>();

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = SelectActions;
        command.Parameters.Add(new NpgsqlParameter("process", NpgsqlDbType.Uuid) { Value = process });

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new TransitionAction(
                reader.GetGuid(0), reader.GetGuid(1),
                Enum.Parse<ActionKind>(reader.GetString(2)),
                reader.GetString(3), reader.GetInt32(4)));
        }

        return rows;
    }

    private async ValueTask<NpgsqlConnection> OpenAsync(string? tenantId, CancellationToken cancellationToken)
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
}

/// <summary>A definition, read once, with everything the decision needs.</summary>
/// <param name="Definition">Which process, and which version.</param>
/// <param name="Stages">Its stages.</param>
/// <param name="Candidates">Its transitions, with guards and actions.</param>
public sealed record ProcessSnapshot(
    ProcessDefinition Definition,
    IReadOnlyList<ProcessStage> Stages,
    IReadOnlyList<TransitionCandidate> Candidates);

/// <summary>The parameters a configured action carries, read out of its JSON.</summary>
/// <remarks>
/// <strong>Read leniently and applied strictly.</strong> A parameter that is missing or
/// unreadable falls back to a stated default rather than failing the transition: an
/// administrator's typo in one action's parameters should not stop the other actions on that
/// transition, and every fallback here is visible in the row the action writes.
/// </remarks>
public static class ActionParameters
{
    /// <summary>Reads a text parameter.</summary>
    /// <param name="json">The action's <c>parameters</c> column.</param>
    /// <param name="name">Which parameter.</param>
    /// <param name="fallback">What to use when it is absent.</param>
    /// <returns>The value, or <paramref name="fallback"/>.</returns>
    public static string Text(string json, string name, string fallback)
    {
        using var document = Parse(json);

        return document is not null &&
               document.RootElement.TryGetProperty(name, out var value) &&
               value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;
    }

    /// <summary>Reads a numeric parameter.</summary>
    /// <param name="json">The action's <c>parameters</c> column.</param>
    /// <param name="name">Which parameter.</param>
    /// <param name="fallback">What to use when it is absent or unreadable.</param>
    /// <returns>The value, or <paramref name="fallback"/>.</returns>
    public static int Number(string json, string name, int fallback)
    {
        using var document = Parse(json);

        if (document is null || !document.RootElement.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            System.Text.Json.JsonValueKind.String when int.TryParse(
                value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => fallback,
        };
    }

    private static System.Text.Json.JsonDocument? Parse(string json)
    {
        try
        {
            return System.Text.Json.JsonDocument.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
