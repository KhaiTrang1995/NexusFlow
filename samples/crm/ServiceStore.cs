using FlowX;
using Npgsql;
using NpgsqlTypes;

namespace Crm;

/// <summary>
/// The week the desk is open, the promises it makes, and the cases those promises are attached to.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No due date is computed in SQL.</strong> The week comes back as rows and
/// <c>BusinessCalendar</c> walks it — a pure function that can be tested at a hundred boundaries
/// without a database. A due date computed in a statement can be tested by opening a case and
/// waiting until Friday.
/// </para>
/// <para>
/// <strong>The promise is stamped once.</strong> An administrator lowering a response target on a
/// Tuesday must not retroactively breach every case raised on Monday, which is what recomputing
/// from the live policy would do.
/// </para>
/// </remarks>
public sealed class ServiceStore
{
    private const string ClearWeek = "DELETE FROM business_hours";

    private const string InsertDay = """
        INSERT INTO business_hours (tenant_id, day_of_week, opens_at, closes_at)
        VALUES (@tenant, @day, @opens, @closes)
        """;

    private const string ReadWeek = """
        SELECT day_of_week, opens_at, closes_at FROM business_hours ORDER BY day_of_week
        """;

    // The policy for a priority is single and live. Retiring the incumbent in the same call is
    // what keeps the partial unique index satisfiable without the caller having to know it exists.
    private const string RetirePolicy = """
        UPDATE sla_policy SET is_active = false WHERE priority = @priority AND is_active
        """;

    private const string InsertPolicy = """
        INSERT INTO sla_policy (
            policy_id, tenant_id, name, label, priority,
            first_response_minutes, resolution_minutes, business_hours_only, is_active)
        VALUES (@id, @tenant, @name, @label, @priority, @response, @resolution, @hours, true)
        ON CONFLICT (tenant_id, name) DO NOTHING
        RETURNING policy_id
        """;

    private const string PolicyForPriority = """
        SELECT policy_id, name, first_response_minutes, resolution_minutes, business_hours_only
        FROM sla_policy WHERE priority = @priority AND is_active
        """;

    private const string AccountExists = "SELECT 1 FROM account WHERE account_id = @id";

    // The next number for this tenant, read and written in one statement so the window between
    // them is the transaction's rather than the application's. The unique constraint settles what
    // is left, which is why the caller retries rather than locks.
    private const string InsertCase = """
        INSERT INTO support_case (
            case_id, tenant_id, case_number, account_id, contact_id, subject, description,
            status, priority, origin, owner_id, opened_by, opened_at,
            sla_policy_id, first_response_due_at, resolution_due_at)
        SELECT @id, @tenant, coalesce(max(case_number), 0) + 1, @account, @contact, @subject,
               @description, 'New', @priority, @origin, @owner, @owner, @now,
               @policy, @responseDue, @resolutionDue
        FROM support_case
        RETURNING case_number
        """;

    private const string ReadCase = """
        SELECT case_number, status, opened_at, first_responded_at, first_response_due_at,
               sla_policy_id, opened_by
        FROM support_case WHERE case_id = @id
        """;

    // The place in the thread is chosen inside the insert, not read first and passed in. Two
    // replies written at once would otherwise both read the same maximum, and the second would be
    // dropped by the primary key — losing something a person said to a customer, which is the one
    // thing a thread cannot lose. The caller retries on the collision instead.
    private const string InsertComment = """
        INSERT INTO case_comment (case_id, tenant_id, ordinal, author_id, body, is_public, created_at)
        SELECT @id, @tenant, coalesce(max(ordinal), 0) + 1, @author, @body, @public, @now
        FROM case_comment WHERE case_id = @id
        RETURNING ordinal
        """;

    // The response clock stops once. `first_responded_at IS NULL` in the predicate rather than in
    // the application is what makes two replies landing together stop it at one instant instead of
    // at whichever the pool happened to run last.
    private const string StopResponseClock = """
        UPDATE support_case
        SET first_responded_at = @now
        WHERE case_id = @id AND first_responded_at IS NULL
        """;

    private const string MoveCase = """
        UPDATE support_case
        SET status = @status,
            closed_at = CASE WHEN @status = 'Closed' THEN @now ELSE NULL END
        WHERE case_id = @id
        """;

    // Everything live, the tightest promise first. A case with no promise sorts last rather than
    // first, because nulls sorting first would put the unpromised queue above the late one.
    private const string LiveQueue = """
        SELECT case_id, case_number, subject, status, priority, owner_id, opened_at,
               first_response_due_at, resolution_due_at, first_responded_at
        FROM support_case
        WHERE status <> 'Closed'
          AND (NOT @mine OR owner_id = @owner)
          AND (@priority IS NULL OR priority = @priority)
        ORDER BY resolution_due_at ASC NULLS LAST, case_number
        LIMIT @limit
        """;

    private readonly NpgsqlDataSource _source;

    /// <summary>Builds the store over the application's data source.</summary>
    /// <param name="source">The pool <c>AddFlowXPostgres</c> built.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public ServiceStore(NpgsqlDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
    }

    /// <summary>Replaces the week the desk is open.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="week">The open days.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many days were written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="week"/> is null.</exception>
    public async ValueTask<int> SaveWeekAsync(
        string? tenantId,
        IReadOnlyList<OpeningHours> week,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(week);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        // Replaced whole rather than merged. A week half from the old declaration and half from
        // the new is a week nobody wrote down, and the desk would not be able to say which.
        var clear = connection.CreateCommand();
        await using var closingClear = clear.ConfigureAwait(false);

        clear.CommandText = ClearWeek;
        await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        foreach (var day in week)
        {
            var write = connection.CreateCommand();
            await using var closingWrite = write.ConfigureAwait(false);

            write.CommandText = InsertDay;

            Add(write, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
            Add(write, "day", NpgsqlDbType.Smallint, (short)day.Day);
            Add(write, "opens", NpgsqlDbType.Time, day.Opens.ToTimeSpan());
            Add(write, "closes", NpgsqlDbType.Time, day.Closes.ToTimeSpan());

            await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return week.Count;
    }

    /// <summary>The week the desk is open.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The open days, or empty when the desk has declared none.</returns>
    public async ValueTask<IReadOnlyList<OpeningHours>> WeekAsync(
        string? tenantId,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        return await WeekAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Declares a promise, retiring whatever governed the priority before it.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="id">What the policy is to be called.</param>
    /// <param name="request">What was asked for.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The id and whether it took over, or null when the name is already in use.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public async ValueTask<SlaPolicyDefined?> SavePolicyAsync(
        string? tenantId,
        Guid id,
        DefineSlaPolicy request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var retire = connection.CreateCommand();
        await using var closingRetire = retire.ConfigureAwait(false);

        retire.CommandText = RetirePolicy;
        Add(retire, "priority", NpgsqlDbType.Text, request.Priority.ToString());

        var replaced = await retire.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;

        var write = connection.CreateCommand();
        await using var closingWrite = write.ConfigureAwait(false);

        write.CommandText = InsertPolicy;

        Add(write, "id", NpgsqlDbType.Uuid, id);
        Add(write, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
        Add(write, "name", NpgsqlDbType.Text, request.Name);
        Add(write, "label", NpgsqlDbType.Text, request.Label);
        Add(write, "priority", NpgsqlDbType.Text, request.Priority.ToString());
        Add(write, "response", NpgsqlDbType.Integer, request.FirstResponseMinutes);
        Add(write, "resolution", NpgsqlDbType.Integer, request.ResolutionMinutes);
        Add(write, "hours", NpgsqlDbType.Boolean, request.BusinessHoursOnly);

        return await write.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is Guid saved
            ? new SlaPolicyDefined(saved, replaced)
            : null;
    }

    /// <summary>Raises a case, with the promise that governs it stamped on.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="id">What the case is to be called.</param>
    /// <param name="request">What was asked for.</param>
    /// <param name="owner">Whose it is.</param>
    /// <param name="now">When it arrived.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The case, or a refusal.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public async ValueTask<Result<CaseOpened>> OpenCaseAsync(
        string? tenantId,
        Guid id,
        OpenCase request,
        string owner,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        if (await ScalarAsync(connection, AccountExists, request.AccountId, cancellationToken)
                .ConfigureAwait(false) is null)
        {
            return Result.Fail<CaseOpened>(ServiceErrors.AccountNotFound(request.AccountId));
        }

        var policy = await PolicyAsync(connection, request.Priority, cancellationToken)
            .ConfigureAwait(false);

        DateTimeOffset? responseDue = null;
        DateTimeOffset? resolutionDue = null;

        if (policy is not null && !policy.BusinessHoursOnly)
        {
            // A promise measured around the clock is plain addition, not a walk over a week that
            // never shuts. Written separately because a week of twenty-four-hour windows loses a
            // second a day at the midnight boundary, and a promise nobody can reproduce by hand is
            // the kind of small wrongness a desk argues about for a month.
            responseDue = now.AddMinutes(policy.FirstResponseMinutes);
            resolutionDue = now.AddMinutes(policy.ResolutionMinutes);
        }
        else if (policy is not null)
        {
            var week = await WeekAsync(connection, cancellationToken).ConfigureAwait(false);

            if (week.Count == 0)
            {
                return Result.Fail<CaseOpened>(ServiceErrors.NoBusinessHours());
            }

            responseDue = BusinessCalendar.Due(now, policy.FirstResponseMinutes, week);
            resolutionDue = BusinessCalendar.Due(now, policy.ResolutionMinutes, week);

            if (responseDue is null || resolutionDue is null)
            {
                return Result.Fail<CaseOpened>(ServiceErrors.NoBusinessHours());
            }
        }

        for (var attempt = 0; attempt < ServiceLimits.NumberAttempts; attempt++)
        {
            var write = connection.CreateCommand();
            await using var closingWrite = write.ConfigureAwait(false);

            write.CommandText = InsertCase;

            Add(write, "id", NpgsqlDbType.Uuid, id);
            Add(write, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
            Add(write, "account", NpgsqlDbType.Uuid, request.AccountId);
            Add(write, "contact", NpgsqlDbType.Uuid, (object?)request.ContactId ?? DBNull.Value);
            Add(write, "subject", NpgsqlDbType.Text, request.Subject);
            Add(write, "description", NpgsqlDbType.Text, request.Description);
            Add(write, "priority", NpgsqlDbType.Text, request.Priority.ToString());
            Add(write, "origin", NpgsqlDbType.Text, request.Origin.ToString());
            Add(write, "owner", NpgsqlDbType.Text, owner);
            Add(write, "now", NpgsqlDbType.TimestampTz, now);
            Add(write, "policy", NpgsqlDbType.Uuid, (object?)policy?.PolicyId ?? DBNull.Value);
            Add(write, "responseDue", NpgsqlDbType.TimestampTz, (object?)responseDue ?? DBNull.Value);
            Add(write, "resolutionDue", NpgsqlDbType.TimestampTz, (object?)resolutionDue ?? DBNull.Value);

            try
            {
                var number = (long)(await write.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false))!;

                return Result.Ok(new CaseOpened(
                    id, number, policy?.Name, responseDue, resolutionDue));
            }
            catch (PostgresException failure)
                when (failure.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // Two cases raised in the same instant read the same maximum. The constraint
                // settles it and the loser asks again, which is cheaper than a lock every case
                // would have to take for a collision most desks never see.
            }
        }

        return Result.Fail<CaseOpened>(ServiceErrors.NumberContended());
    }

    /// <summary>Reads a case's clock.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="id">Which case.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The case, or null.</returns>
    public async ValueTask<StoredCase?> CaseAsync(
        string? tenantId,
        Guid id,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = ReadCase;
        Add(command, "id", NpgsqlDbType.Uuid, id);

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new StoredCase(
            id,
            reader.GetInt64(0),
            reader.GetString(1),
            await reader.GetFieldValueAsync<DateTimeOffset>(2, cancellationToken).ConfigureAwait(false),
            await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false)
                ? null
                : await reader.GetFieldValueAsync<DateTimeOffset>(3, cancellationToken).ConfigureAwait(false),
            await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false)
                ? null
                : await reader.GetFieldValueAsync<DateTimeOffset>(4, cancellationToken).ConfigureAwait(false),
            !await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false),
            reader.GetString(6));
    }

    /// <summary>Writes a comment, stops the response clock if this is the first reply.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="id">Which case.</param>
    /// <param name="author">Who is speaking.</param>
    /// <param name="request">What they said.</param>
    /// <param name="stopsTheClock">Whether this is a reply the response promise is measured by.</param>
    /// <param name="now">When.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Where the comment sits, and whether it stopped the clock.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public async ValueTask<(int Ordinal, bool Stopped)> CommentAsync(
        string? tenantId,
        Guid id,
        string author,
        CommentOnCase request,
        bool stopsTheClock,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var ordinal = 0;

        for (var attempt = 0; attempt < ServiceLimits.NumberAttempts; attempt++)
        {
            var write = connection.CreateCommand();
            await using var closingWrite = write.ConfigureAwait(false);

            write.CommandText = InsertComment;

            Add(write, "id", NpgsqlDbType.Uuid, id);
            Add(write, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
            Add(write, "author", NpgsqlDbType.Text, author);
            Add(write, "body", NpgsqlDbType.Text, request.Body);
            Add(write, "public", NpgsqlDbType.Boolean, request.IsPublic);
            Add(write, "now", NpgsqlDbType.TimestampTz, now);

            try
            {
                ordinal = (int)(await write.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false))!;

                break;
            }
            catch (PostgresException failure)
                when (failure.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // Somebody else took the place. Ask for the next one rather than drop what was
                // written.
            }
        }

        var stopped = false;

        if (stopsTheClock)
        {
            var stop = connection.CreateCommand();
            await using var closingStop = stop.ConfigureAwait(false);

            stop.CommandText = StopResponseClock;

            Add(stop, "id", NpgsqlDbType.Uuid, id);
            Add(stop, "now", NpgsqlDbType.TimestampTz, now);

            // The row count, not a prior read. Two replies landing together stop the clock at one
            // instant and only one of them is told it did.
            stopped = await stop.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }

        if (request.Status is { } status)
        {
            var move = connection.CreateCommand();
            await using var closingMove = move.ConfigureAwait(false);

            move.CommandText = MoveCase;

            Add(move, "id", NpgsqlDbType.Uuid, id);
            Add(move, "status", NpgsqlDbType.Text, status.ToString());
            Add(move, "now", NpgsqlDbType.TimestampTz, now);

            await move.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return (ordinal, stopped);
    }

    /// <summary>The live queue.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="request">What to show.</param>
    /// <param name="owner">Who is asking.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The cases, the tightest promise first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public async ValueTask<IReadOnlyList<StoredQueuedCase>> QueueAsync(
        string? tenantId,
        ReadCaseWorklist request,
        string owner,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = LiveQueue;

        Add(command, "mine", NpgsqlDbType.Boolean, request.MineOnly);
        Add(command, "owner", NpgsqlDbType.Text, owner);
        Add(
            command,
            "priority",
            NpgsqlDbType.Text,
            (object?)request.Priority?.ToString() ?? DBNull.Value);
        Add(command, "limit", NpgsqlDbType.Integer, ServiceLimits.MaxQueue);

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        var cases = new List<StoredQueuedCase>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cases.Add(new StoredQueuedCase(
                reader.GetGuid(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                await reader.GetFieldValueAsync<DateTimeOffset>(6, cancellationToken).ConfigureAwait(false),
                await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false)
                    ? null
                    : await reader.GetFieldValueAsync<DateTimeOffset>(7, cancellationToken).ConfigureAwait(false),
                await reader.IsDBNullAsync(8, cancellationToken).ConfigureAwait(false)
                    ? null
                    : await reader.GetFieldValueAsync<DateTimeOffset>(8, cancellationToken).ConfigureAwait(false),
                await reader.IsDBNullAsync(9, cancellationToken).ConfigureAwait(false)
                    ? null
                    : await reader.GetFieldValueAsync<DateTimeOffset>(9, cancellationToken).ConfigureAwait(false)));
        }

        return cases;
    }

    private static async ValueTask<IReadOnlyList<OpeningHours>> WeekAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var closing = command.ConfigureAwait(false);

        command.CommandText = ReadWeek;

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        var week = new List<OpeningHours>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            week.Add(new OpeningHours(
                (DayOfWeek)reader.GetInt16(0),
                TimeOnly.FromTimeSpan(
                    await reader.GetFieldValueAsync<TimeSpan>(1, cancellationToken).ConfigureAwait(false)),
                TimeOnly.FromTimeSpan(
                    await reader.GetFieldValueAsync<TimeSpan>(2, cancellationToken).ConfigureAwait(false))));
        }

        return week;
    }

    private static async ValueTask<StoredPolicy?> PolicyAsync(
        NpgsqlConnection connection,
        CasePriority priority,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var closing = command.ConfigureAwait(false);

        command.CommandText = PolicyForPriority;
        Add(command, "priority", NpgsqlDbType.Text, priority.ToString());

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new StoredPolicy(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetBoolean(4))
            : null;
    }

    private static async ValueTask<object?> ScalarAsync(
        NpgsqlConnection connection,
        string sql,
        Guid id,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var closing = command.ConfigureAwait(false);

        command.CommandText = sql;
        Add(command, "id", NpgsqlDbType.Uuid, id);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Add(NpgsqlCommand command, string name, NpgsqlDbType type, object value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });

    private ValueTask<NpgsqlConnection> OpenAsync(string? tenantId, CancellationToken cancellationToken) =>
        CrmTenantScope.OpenAsync(_source, tenantId, cancellationToken);
}

/// <summary>A promise, as stored.</summary>
/// <param name="PolicyId">Its id.</param>
/// <param name="Name">Its identifier.</param>
/// <param name="FirstResponseMinutes">How long until somebody must have replied.</param>
/// <param name="ResolutionMinutes">How long until it must be finished.</param>
/// <param name="BusinessHoursOnly">Whether the clock stops when the desk shuts.</param>
public sealed record StoredPolicy(
    Guid PolicyId,
    string Name,
    int FirstResponseMinutes,
    int ResolutionMinutes,
    bool BusinessHoursOnly);

/// <summary>A case's clock, as stored.</summary>
/// <param name="CaseId">Its id.</param>
/// <param name="Number">What to say on the telephone.</param>
/// <param name="Status">Where it stands.</param>
/// <param name="OpenedAt">When it arrived.</param>
/// <param name="FirstRespondedAt">When somebody first replied, or null.</param>
/// <param name="FirstResponseDueAt">When they were meant to, or null.</param>
/// <param name="HasPolicy">Whether a promise governs it.</param>
/// <param name="OpenedBy">Who raised it.</param>
public sealed record StoredCase(
    Guid CaseId,
    long Number,
    string Status,
    DateTimeOffset OpenedAt,
    DateTimeOffset? FirstRespondedAt,
    DateTimeOffset? FirstResponseDueAt,
    bool HasPolicy,
    string OpenedBy);

/// <summary>One case in the queue, as stored.</summary>
/// <param name="CaseId">Its id.</param>
/// <param name="Number">What to say on the telephone.</param>
/// <param name="Subject">What it is about.</param>
/// <param name="Status">Where it stands.</param>
/// <param name="Priority">How urgent.</param>
/// <param name="OwnerId">Whose it is.</param>
/// <param name="OpenedAt">When it arrived.</param>
/// <param name="FirstResponseDueAt">When somebody must have replied, or null.</param>
/// <param name="ResolutionDueAt">When it must be finished, or null.</param>
/// <param name="FirstRespondedAt">When somebody first replied, or null.</param>
public sealed record StoredQueuedCase(
    Guid CaseId,
    long Number,
    string Subject,
    string Status,
    string Priority,
    string OwnerId,
    DateTimeOffset OpenedAt,
    DateTimeOffset? FirstResponseDueAt,
    DateTimeOffset? ResolutionDueAt,
    DateTimeOffset? FirstRespondedAt);
