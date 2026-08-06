using Npgsql;
using NpgsqlTypes;

namespace Crm;

/// <summary>
/// The bulk job queue: what was submitted, how far it got, and what it refused.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The claim is a short transaction, not one held across the work.</strong> The connector
/// sweep holds its claim for the length of its batch because sending is the work and it must not
/// happen twice. A job's work is writing records whose ids are derived, so writing them twice is
/// a no-op — which buys the ability to claim in milliseconds and do the work outside any lock. A
/// sweeper that dies mid-chunk leaves the job <c>Pending</c> and another finishes it.
/// </para>
/// <para>
/// <strong>Progress is written with the chunk that made it.</strong> <c>processed</c> is durable,
/// so resuming means continuing rather than starting again — which is the difference between a job
/// that must succeed in one attempt and one that only has to succeed eventually.
/// </para>
/// </remarks>
public sealed class BulkJobStore
{
    private const string InsertJob = """
        INSERT INTO bulk_job (
            job_id, tenant_id, kind, object_id, status, total, scopes, request, created_at)
        VALUES (@id, @tenant, @kind, @object, 'Pending', @total, @scopes, @request::jsonb, @now)
        """;

    // FOR UPDATE SKIP LOCKED inside the subquery, so two sweepers take different jobs rather than
    // both taking the oldest. The attempt is counted at the claim and not at the failure: a
    // sweeper that dies leaves no record of having tried, and a job nothing can process would
    // otherwise be claimed forever.
    private const string ClaimJob = """
        UPDATE bulk_job SET attempts = attempts + 1
        WHERE job_id = (
            SELECT job_id FROM bulk_job
            WHERE status = 'Pending' AND attempts < @maxAttempts
            ORDER BY created_at
            LIMIT 1
            FOR UPDATE SKIP LOCKED)
        RETURNING job_id, kind, object_id, total, processed, failed, scopes, request::text
        """;

    private const string AbandonExhausted = """
        UPDATE bulk_job SET status = 'Failed', finished_at = @now
        WHERE status = 'Pending' AND attempts >= @maxAttempts
        """;

    // `attempts = 0` because the counter is about a chunk that cannot be made to work, not about
    // how long the job has been running. A job of a hundred chunks would otherwise be abandoned
    // for having been unlucky five times across an hour of successful work.
    private const string AdvanceJob = """
        UPDATE bulk_job
        SET processed   = @processed,
            failed      = @failed,
            attempts    = 0,
            result      = coalesce(@result::jsonb, result),
            status      = CASE WHEN @processed >= total THEN 'Succeeded' ELSE 'Pending' END,
            finished_at = CASE WHEN @processed >= total THEN @now END
        WHERE job_id = @id
        """;

    // DO NOTHING, because a chunk re-run after a crash reports the same refusals again and a row
    // that failed twice did not fail twice.
    private const string InsertError = """
        INSERT INTO bulk_job_error (job_id, tenant_id, ordinal, message)
        VALUES (@id, @tenant, @ordinal, @message)
        ON CONFLICT (job_id, ordinal) DO NOTHING
        """;

    private const string ReadJobById = """
        SELECT kind, status, total, processed, failed, result::text
        FROM bulk_job WHERE job_id = @id
        """;

    private const string ReadJobErrors = """
        SELECT ordinal, message FROM bulk_job_error WHERE job_id = @id ORDER BY ordinal
        """;

    private readonly NpgsqlDataSource _source;

    /// <summary>Builds the store over the application's data source.</summary>
    /// <param name="source">The pool <c>AddFlowXPostgres</c> built.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public BulkJobStore(NpgsqlDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
    }

    /// <summary>Records a submitted job.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="jobId">What the job is to be called.</param>
    /// <param name="kind"><c>Import</c> or <c>Export</c>.</param>
    /// <param name="target">Which object it is about.</param>
    /// <param name="total">How much work it is.</param>
    /// <param name="scopes">What the submitter held.</param>
    /// <param name="request">The rows, or the criteria.</param>
    /// <param name="now">When it was submitted.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async ValueTask SubmitAsync(
        string? tenantId,
        Guid jobId,
        string kind,
        Guid target,
        int total,
        IReadOnlyList<string> scopes,
        string request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = InsertJob;

        Add(command, "id", NpgsqlDbType.Uuid, jobId);
        Add(command, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
        Add(command, "kind", NpgsqlDbType.Text, kind);
        Add(command, "object", NpgsqlDbType.Uuid, target);
        Add(command, "total", NpgsqlDbType.Integer, total);
        command.Parameters.Add(new NpgsqlParameter<string[]>("scopes", [.. scopes]));
        Add(command, "request", NpgsqlDbType.Text, request);
        Add(command, "now", NpgsqlDbType.TimestampTz, now);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gives up on jobs that have failed too many times.</summary>
    /// <param name="tenantId">The tenant being swept.</param>
    /// <param name="maxAttempts">How many failures is too many.</param>
    /// <param name="now">When the sweep ran.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many jobs were given up on.</returns>
    public async ValueTask<int> AbandonAsync(
        string? tenantId,
        int maxAttempts,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = AbandonExhausted;

        Add(command, "maxAttempts", NpgsqlDbType.Integer, maxAttempts);
        Add(command, "now", NpgsqlDbType.TimestampTz, now);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Takes the oldest job nothing else is working on.</summary>
    /// <param name="tenantId">The tenant being swept.</param>
    /// <param name="maxAttempts">Jobs at or past this are left for <see cref="AbandonAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The job, or null when there is nothing to do.</returns>
    public async ValueTask<ClaimedJob?> ClaimAsync(
        string? tenantId,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = ClaimJob;

        Add(command, "maxAttempts", NpgsqlDbType.Integer, maxAttempts);

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var scopes = await reader
            .GetFieldValueAsync<string[]>(6, cancellationToken)
            .ConfigureAwait(false);

        return new ClaimedJob(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetGuid(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            scopes,
            reader.GetString(7));
    }

    /// <summary>Records the progress a chunk made.</summary>
    /// <param name="tenantId">The tenant being swept.</param>
    /// <param name="jobId">Which job.</param>
    /// <param name="processed">How much is now dealt with.</param>
    /// <param name="failed">How many rows have been refused in total.</param>
    /// <param name="result">An export's document, or null to leave what is there.</param>
    /// <param name="now">When the chunk finished.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async ValueTask AdvanceAsync(
        string? tenantId,
        Guid jobId,
        int processed,
        int failed,
        string? result,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = AdvanceJob;

        Add(command, "id", NpgsqlDbType.Uuid, jobId);
        Add(command, "processed", NpgsqlDbType.Integer, processed);
        Add(command, "failed", NpgsqlDbType.Integer, failed);
        Add(command, "now", NpgsqlDbType.TimestampTz, now);

        command.Parameters.Add(new NpgsqlParameter("result", NpgsqlDbType.Text)
        {
            Value = (object?)result ?? DBNull.Value,
        });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records which rows a chunk refused.</summary>
    /// <param name="tenantId">The tenant being swept.</param>
    /// <param name="jobId">Which job.</param>
    /// <param name="errors">The refusals, by ordinal.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ArgumentNullException"><paramref name="errors"/> is null.</exception>
    public async ValueTask RecordErrorsAsync(
        string? tenantId,
        Guid jobId,
        IReadOnlyList<JobRowError> errors,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(errors);

        if (errors.Count == 0)
        {
            return;
        }

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        foreach (var error in errors)
        {
            var command = connection.CreateCommand();
            await using var closingCommand = command.ConfigureAwait(false);

            command.CommandText = InsertError;

            Add(command, "id", NpgsqlDbType.Uuid, jobId);
            Add(command, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
            Add(command, "ordinal", NpgsqlDbType.Integer, error.Ordinal);
            Add(command, "message", NpgsqlDbType.Text, error.Message);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reads a job and what it refused.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="jobId">Which job.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The job, or null when this tenant has no such job.</returns>
    public async ValueTask<StoredJob?> ReadAsync(
        string? tenantId,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        StoredJob? job;

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = ReadJobById;
        Add(command, "id", NpgsqlDbType.Uuid, jobId);

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            job = new StoredJob(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false)
                    ? null
                    : reader.GetString(5),
                []);
        }

        var errorsCommand = connection.CreateCommand();
        await using var closingErrors = errorsCommand.ConfigureAwait(false);

        errorsCommand.CommandText = ReadJobErrors;
        Add(errorsCommand, "id", NpgsqlDbType.Uuid, jobId);

        var errorReader = await errorsCommand
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var closingErrorReader = errorReader.ConfigureAwait(false);

        var errors = new List<JobRowError>();

        while (await errorReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            errors.Add(new JobRowError(errorReader.GetInt32(0), errorReader.GetString(1)));
        }

        return job with { Errors = errors };
    }

    private static void Add(NpgsqlCommand command, string name, NpgsqlDbType type, object value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });

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
}

/// <summary>A job the sweep has taken.</summary>
/// <param name="JobId">Which job.</param>
/// <param name="Kind"><c>Import</c> or <c>Export</c>.</param>
/// <param name="Target">Which object it is about.</param>
/// <param name="Total">How much work it is.</param>
/// <param name="Processed">How much was already dealt with, by an earlier pass.</param>
/// <param name="Failed">How many rows have been refused so far.</param>
/// <param name="Scopes">What the submitter held, and what the sweep decides on.</param>
/// <param name="Request">The rows, or the criteria.</param>
public sealed record ClaimedJob(
    Guid JobId,
    string Kind,
    Guid Target,
    int Total,
    int Processed,
    int Failed,
    IReadOnlyList<string> Scopes,
    string Request);

/// <summary>A job as stored, for a caller asking after it.</summary>
/// <param name="Kind"><c>Import</c> or <c>Export</c>.</param>
/// <param name="Status">Where it has got to.</param>
/// <param name="Total">How much work it is.</param>
/// <param name="Processed">How much is dealt with.</param>
/// <param name="Failed">How many rows were refused.</param>
/// <param name="Result">An export's document, or null.</param>
/// <param name="Errors">Which rows were refused.</param>
public sealed record StoredJob(
    string Kind,
    string Status,
    int Total,
    int Processed,
    int Failed,
    string? Result,
    IReadOnlyList<JobRowError> Errors);
