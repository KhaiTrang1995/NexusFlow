using Npgsql;
using NpgsqlTypes;

namespace Crm;

/// <summary>
/// Tasks, their escalations and the pipeline sweep.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Both sweeps are one statement each, and that is not an optimisation.</strong> Reading
/// every overdue row into the process and updating them one at a time would give a window in
/// which a second sweep sees the same rows unescalated. The <c>UPDATE … WHERE</c> is what makes
/// "once per window" a property of the database rather than of how fast the sweep runs.
/// </para>
/// <para>
/// <strong>The interval arithmetic mirrors <see cref="SlaRules"/> exactly.</strong>
/// <c>due_at + window * escalation_count &lt;= now</c> is
/// <see cref="SlaRules.NeedsEscalation"/> written in SQL, and <c>TaskSweepTests</c> is what
/// keeps the two saying the same thing.
/// </para>
/// </remarks>
public sealed class WorkStore
{
    private const string InsertActivity = """
        INSERT INTO activity (
            activity_id, tenant_id, kind, subject, relates_to_kind, relates_to_id,
            owner_id, due_at, status, escalation_count)
        VALUES (@id, @tenant, @kind, @subject, @relatesKind, @relatesId, @owner, @due, 'Open', 0)
        ON CONFLICT (activity_id) DO NOTHING
        """;

    private const string EscalateOverdue = """
        UPDATE activity
        SET escalation_count = escalation_count + 1,
            status = 'Escalated'
        WHERE kind = 'Task'
          AND status IN ('Open', 'Escalated')
          AND due_at IS NOT NULL
          AND escalation_count < @ceiling
          AND due_at + (@window * escalation_count) <= @now
        """;

    private const string CountStale = """
        SELECT count(*)
        FROM opportunity
        WHERE outcome IS NULL
          AND stage_entered_at + @after <= @now
        """;

    private readonly NpgsqlDataSource _source;

    /// <summary>Builds the store over the application's data source.</summary>
    /// <param name="source">The pool <c>AddFlowXPostgres</c> built.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public WorkStore(NpgsqlDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
    }

    /// <summary>Writes a task.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="id">The activity's id.</param>
    /// <param name="task">What was asked for.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ArgumentNullException"><paramref name="task"/> is null.</exception>
    public async ValueTask CreateAsync(
        string? tenantId,
        Guid id,
        CreateTask task,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = InsertActivity;
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = id });
        command.Parameters.Add(new NpgsqlParameter("tenant", NpgsqlDbType.Text) { Value = tenantId ?? string.Empty });
        command.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Text) { Value = task.Kind.ToString() });
        command.Parameters.Add(new NpgsqlParameter("subject", NpgsqlDbType.Text) { Value = task.Subject });
        command.Parameters.Add(new NpgsqlParameter("relatesKind", NpgsqlDbType.Text)
        {
            Value = task.RelatesTo.Kind.ToString(),
        });
        command.Parameters.Add(new NpgsqlParameter("relatesId", NpgsqlDbType.Uuid) { Value = task.RelatesTo.Id });
        command.Parameters.Add(new NpgsqlParameter("owner", NpgsqlDbType.Uuid) { Value = task.Owner });
        command.Parameters.Add(new NpgsqlParameter("due", NpgsqlDbType.TimestampTz)
        {
            Value = (object?)task.DueAt ?? DBNull.Value,
        });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Escalates every task whose next escalation is due.</summary>
    /// <param name="tenantId">The tenant this occurrence fired for.</param>
    /// <param name="now">The occurrence's instant, not the wall clock.</param>
    /// <param name="window">How long between escalations.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many rows moved.</returns>
    /// <remarks>
    /// <strong>The clock is the occurrence's and that is what makes a replay reproducible.</strong>
    /// A schedule that fires late must escalate what was due when it should have fired, not what
    /// is due by the time it got there.
    /// </remarks>
    public async ValueTask<int> EscalateAsync(
        string? tenantId,
        DateTimeOffset now,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = EscalateOverdue;
        command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });
        command.Parameters.Add(new NpgsqlParameter("window", NpgsqlDbType.Interval) { Value = window });
        command.Parameters.Add(new NpgsqlParameter("ceiling", NpgsqlDbType.Integer)
        {
            Value = SlaPolicy.MaxEscalations,
        });

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Counts the open opportunities that have gone quiet.</summary>
    /// <param name="tenantId">The tenant this occurrence fired for.</param>
    /// <param name="now">The occurrence's instant.</param>
    /// <param name="after">How long is too long.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many are stale.</returns>
    public async ValueTask<int> CountStaleAsync(
        string? tenantId,
        DateTimeOffset now,
        TimeSpan after,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = CountStale;
        command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });
        command.Parameters.Add(new NpgsqlParameter("after", NpgsqlDbType.Interval) { Value = after });

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long count
            ? (int)count
            : 0;
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
