using Npgsql;
using NpgsqlTypes;

namespace Crm;

/// <summary>
/// Reads the active process for one entity kind, whole.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Three statements on one connection.</strong> A stage list read a moment before its
/// transitions is a graph that can be missing an edge it had — and the screen this feeds is the
/// one an administrator opens to check what they just published.
/// </para>
/// <para>
/// <strong>The occupancy count is a left join, not a second question.</strong> Counting
/// opportunities per stage in a separate request means the counts are from a different instant
/// than the stages, and the stage that gained a deal between the two appears empty.
/// </para>
/// </remarks>
public sealed class ProcessViewStore
{
    private const string Definition = """
        SELECT process_id, version
        FROM process_definition
        WHERE applies_to = @kind AND is_active
        LIMIT 1
        """;

    private const string Stages = """
        SELECT s.stage_id, s.name, s.ordinal, s.is_terminal, count(o.opportunity_id)
        FROM process_stage s
        LEFT JOIN opportunity o ON o.stage_id = s.stage_id
        WHERE s.process_id = @process
        GROUP BY s.stage_id, s.name, s.ordinal, s.is_terminal
        ORDER BY s.ordinal
        """;

    // Guards and actions arrive with their transition rather than after it: three statements and
    // not three-times-N, which is what a per-transition read would be.
    private const string Transitions = """
        SELECT f.name, t.name, x.trigger, x.ordinal,
               coalesce(g.fields, '{}'::text[]),
               coalesce(g.operators, '{}'::text[]),
               coalesce(g.values, '{}'::text[]),
               coalesce(a.kinds, '{}'::text[])
        FROM process_transition x
        JOIN process_stage f ON f.stage_id = x.from_stage_id
        JOIN process_stage t ON t.stage_id = x.to_stage_id
        LEFT JOIN LATERAL (
            SELECT array_agg(field ORDER BY guard_id) AS fields,
                   array_agg(operator ORDER BY guard_id) AS operators,
                   array_agg(value ORDER BY guard_id) AS values
            FROM transition_guard WHERE transition_id = x.transition_id
        ) AS g ON true
        LEFT JOIN LATERAL (
            SELECT array_agg(kind ORDER BY ordinal) AS kinds
            FROM transition_action WHERE transition_id = x.transition_id
        ) AS a ON true
        WHERE f.process_id = @process
        ORDER BY f.ordinal, x.ordinal
        """;

    private readonly NpgsqlDataSource _source;

    /// <summary>Builds the store over the application's data source.</summary>
    /// <param name="source">The pool <c>AddFlowXPostgres</c> built.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public ProcessViewStore(NpgsqlDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
    }

    /// <summary>Reads the active process, or reports that none is published.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="kind">Which entity kind.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The process, or null when none is active.</returns>
    public async ValueTask<ProcessView?> ReadAsync(
        string? tenantId,
        EntityKind kind,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var active = await ReadDefinitionAsync(connection, kind, cancellationToken).ConfigureAwait(false);

        if (active is not { } definition)
        {
            return null;
        }

        return new ProcessView(
            kind.ToString(),
            definition.Version,
            await ReadStagesAsync(connection, definition.Id, cancellationToken).ConfigureAwait(false),
            await ReadTransitionsAsync(connection, definition.Id, cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask<(Guid Id, int Version)?> ReadDefinitionAsync(
        NpgsqlConnection connection,
        EntityKind kind,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var closing = command.ConfigureAwait(false);

        command.CommandText = Definition;
        command.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Text) { Value = kind.ToString() });

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetGuid(0), reader.GetInt32(1))
            : null;
    }

    private static async ValueTask<IReadOnlyList<ProcessStageView>> ReadStagesAsync(
        NpgsqlConnection connection,
        Guid process,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var closing = command.ConfigureAwait(false);

        command.CommandText = Stages;
        command.Parameters.Add(new NpgsqlParameter("process", NpgsqlDbType.Uuid) { Value = process });

        var stages = new List<ProcessStageView>();

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            stages.Add(new ProcessStageView(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetBoolean(3),
                (int)reader.GetInt64(4)));
        }

        return stages;
    }

    private static async ValueTask<IReadOnlyList<ProcessTransitionView>> ReadTransitionsAsync(
        NpgsqlConnection connection,
        Guid process,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var closing = command.ConfigureAwait(false);

        command.CommandText = Transitions;
        command.Parameters.Add(new NpgsqlParameter("process", NpgsqlDbType.Uuid) { Value = process });

        var transitions = new List<ProcessTransitionView>();

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var fields = await reader.GetFieldValueAsync<string[]>(4, cancellationToken)
                .ConfigureAwait(false);
            var operators = await reader.GetFieldValueAsync<string[]>(5, cancellationToken)
                .ConfigureAwait(false);
            var values = await reader.GetFieldValueAsync<string[]>(6, cancellationToken)
                .ConfigureAwait(false);

            var guards = new List<ProcessGuardView>(fields.Length);

            for (var index = 0; index < fields.Length; index++)
            {
                guards.Add(new ProcessGuardView(fields[index]!, operators[index]!, values[index]!));
            }

            transitions.Add(new ProcessTransitionView(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                guards,
                await reader.GetFieldValueAsync<string[]>(7, cancellationToken)
                    .ConfigureAwait(false)));
        }

        return transitions;
    }

    private ValueTask<NpgsqlConnection> OpenAsync(string? tenantId, CancellationToken cancellationToken) =>
        _source.OpenAsync(tenantId, cancellationToken);
}
