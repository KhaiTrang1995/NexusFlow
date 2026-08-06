using Npgsql;
using NpgsqlTypes;

namespace Crm;

/// <summary>
/// The approval processes, the requests against them, and the register of what was said.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No criterion is evaluated in SQL.</strong> The criteria come back as rows and
/// <c>ProcessRules.Holds</c> decides — the same evaluator transition guards, validation rules,
/// roll-up filters, list-view criteria and territory rules use. Six features, one vocabulary, and
/// nothing assembled into a statement.
/// </para>
/// <para>
/// <strong>The decision register is append-only by grant.</strong> A record of what somebody said
/// that somebody else can edit is not a record, and the primary key on (request, step) is what
/// makes a step undecidable twice — a race between two clicks resolves to one row and one refusal
/// rather than to whichever arrived last.
/// </para>
/// </remarks>
public sealed class ApprovalStore
{
    private const string InsertProcess = """
        INSERT INTO approval_process (
            process_id, tenant_id, name, label, subject, priority, is_active, created_at)
        VALUES (@id, @tenant, @name, @label, @subject, @priority, true, @now)
        ON CONFLICT (tenant_id, name) DO NOTHING
        RETURNING process_id
        """;

    private const string InsertCriterion = """
        INSERT INTO approval_criterion (process_id, tenant_id, ordinal, attribute, operator, value)
        VALUES (@process, @tenant, @ordinal, @attribute, @operator, @value)
        """;

    private const string InsertStep = """
        INSERT INTO approval_step (
            process_id, tenant_id, ordinal, label, approver_kind, approver)
        VALUES (@process, @tenant, @ordinal, @label, @kind, @approver)
        """;

    // Every active process for a subject with its criteria, in the order they are considered.
    private const string ProcessesForSubject = """
        SELECT p.process_id, p.name, p.label, c.attribute, c.operator, c.value
        FROM approval_process p
        LEFT JOIN approval_criterion c ON c.process_id = p.process_id
        WHERE p.subject = @subject AND p.is_active
        ORDER BY p.priority, p.name, c.ordinal
        """;

    private const string StepsOfProcess = """
        SELECT ordinal, label, approver_kind, approver
        FROM approval_step WHERE process_id = @process ORDER BY ordinal
        """;

    private const string InsertRequest = """
        INSERT INTO approval_request (
            request_id, tenant_id, process_id, subject, subject_id,
            submitted_by, submitted_at, status, current_step)
        VALUES (@id, @tenant, @process, @subject, @subjectId, @by, @now, 'Pending', 0)
        """;

    private const string ReadRequest = """
        SELECT r.process_id, r.subject, r.subject_id, r.submitted_by, r.status, r.current_step,
               p.name
        FROM approval_request r
        JOIN approval_process p ON p.process_id = r.process_id
        WHERE r.request_id = @id
        """;

    private const string InsertDecision = """
        INSERT INTO approval_decision (
            request_id, tenant_id, ordinal, decided_by, decision, note, decided_at)
        VALUES (@id, @tenant, @ordinal, @by, @decision, @note, @now)
        ON CONFLICT (request_id, ordinal) DO NOTHING
        """;

    private const string AdvanceRequest = """
        UPDATE approval_request
        SET current_step = @step,
            status = @status,
            decided_at = CASE WHEN @status = 'Pending' THEN NULL ELSE @now END
        WHERE request_id = @id
        """;

    // Everything still waiting, with the step that is being waited on. The approver is resolved in
    // the application, because SubmittersManager needs the reporting line's recursion and a
    // statement that inlined it would be a second copy of a walk that already exists once.
    private const string PendingRequests = """
        SELECT r.request_id, p.name, r.subject, r.subject_id, r.submitted_by, r.submitted_at,
               r.current_step, s.label, s.approver_kind, s.approver
        FROM approval_request r
        JOIN approval_process p ON p.process_id = r.process_id
        JOIN approval_step s ON s.process_id = r.process_id AND s.ordinal = r.current_step
        WHERE r.status = 'Pending'
        ORDER BY r.submitted_at, r.request_id
        """;

    private const string QuoteFacts = """
        SELECT discount::text, subtotal::text, total::text, status
        FROM quote WHERE quote_id = @id
        """;

    private const string OpportunityFacts = """
        SELECT amount::text, probability::text, currency
        FROM opportunity WHERE opportunity_id = @id
        """;

    private const string PlanFacts = """
        SELECT target_amount::text, kind FROM plan WHERE plan_id = @id
        """;

    private readonly NpgsqlDataSource _source;

    /// <summary>Builds the store over the application's data source.</summary>
    /// <param name="source">The pool <c>AddFlowXPostgres</c> built.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public ApprovalStore(NpgsqlDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
    }

    /// <summary>Declares a process, its criteria and its steps.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="id">What the process is to be called.</param>
    /// <param name="request">What was asked for.</param>
    /// <param name="now">When.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The id, or null when the name is already in use.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public async ValueTask<Guid?> SaveProcessAsync(
        string? tenantId,
        Guid id,
        DefineApprovalProcess request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = InsertProcess;

        Add(command, "id", NpgsqlDbType.Uuid, id);
        Add(command, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
        Add(command, "name", NpgsqlDbType.Text, request.Name);
        Add(command, "label", NpgsqlDbType.Text, request.Label);
        Add(command, "subject", NpgsqlDbType.Text, request.Subject.ToString());
        Add(command, "priority", NpgsqlDbType.Integer, request.Priority);
        Add(command, "now", NpgsqlDbType.TimestampTz, now);

        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            is not Guid saved)
        {
            return null;
        }

        for (var ordinal = 0; ordinal < request.Criteria.Count; ordinal++)
        {
            var criterion = request.Criteria[ordinal];

            var write = connection.CreateCommand();
            await using var closingWrite = write.ConfigureAwait(false);

            write.CommandText = InsertCriterion;

            Add(write, "process", NpgsqlDbType.Uuid, saved);
            Add(write, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
            Add(write, "ordinal", NpgsqlDbType.Integer, ordinal);
            Add(write, "attribute", NpgsqlDbType.Text, criterion.Attribute);
            Add(write, "operator", NpgsqlDbType.Text, criterion.Operator.ToString());
            Add(write, "value", NpgsqlDbType.Text, criterion.Value);

            await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        for (var ordinal = 0; ordinal < request.Steps.Count; ordinal++)
        {
            var step = request.Steps[ordinal];

            var write = connection.CreateCommand();
            await using var closingWrite = write.ConfigureAwait(false);

            write.CommandText = InsertStep;

            Add(write, "process", NpgsqlDbType.Uuid, saved);
            Add(write, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
            Add(write, "ordinal", NpgsqlDbType.Integer, ordinal);
            Add(write, "label", NpgsqlDbType.Text, step.Label);
            Add(write, "kind", NpgsqlDbType.Text, step.Kind.ToString());
            Add(write, "approver", NpgsqlDbType.Text, (object?)step.Approver ?? DBNull.Value);

            await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return saved;
    }

    /// <summary>The first active process whose criteria all hold for a subject.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="subject">What sort of thing.</param>
    /// <param name="id">Which one.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// The process and its steps, or null when nothing applies. Null is the common answer: most
    /// quotes are under every threshold anybody set.
    /// </returns>
    public async ValueTask<MatchedProcess?> GoverningAsync(
        string? tenantId,
        ApprovalSubject subject,
        Guid id,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var facts = await FactsAsync(connection, subject, id, cancellationToken)
            .ConfigureAwait(false);

        if (facts is null)
        {
            return null;
        }

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = ProcessesForSubject;
        Add(command, "subject", NpgsqlDbType.Text, subject.ToString());

        var ordered = new List<(Guid Id, string Name, string Label)>();
        var criteria = new Dictionary<Guid, List<ApprovalCriterion>>();

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var process = reader.GetGuid(0);

                if (!criteria.TryGetValue(process, out var carried))
                {
                    carried = [];
                    criteria[process] = carried;
                    ordered.Add((process, reader.GetString(1), reader.GetString(2)));
                }

                if (!await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false))
                {
                    carried.Add(new ApprovalCriterion(
                        reader.GetString(3),
                        Enum.Parse<GuardOperator>(reader.GetString(4)),
                        reader.GetString(5)));
                }
            }
        }

        foreach (var (process, name, label) in ordered)
        {
            // Every criterion, and a process with none applies to everything of its subject —
            // which is a legitimate thing to configure and says so by having no criteria.
            if (!criteria[process].All(criterion => ProcessRules.Holds(
                    criterion.Operator,
                    criterion.Value,
                    facts.TryGetValue(criterion.Attribute, out var value) ? value : null)))
            {
                continue;
            }

            var steps = await StepsAsync(connection, process, cancellationToken)
                .ConfigureAwait(false);

            return new MatchedProcess(process, name, label, steps);
        }

        return null;
    }

    /// <summary>Records a request.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="id">What the request is to be called.</param>
    /// <param name="process">Which process governs it.</param>
    /// <param name="subject">What sort of thing.</param>
    /// <param name="subjectId">Which one.</param>
    /// <param name="by">Who asked.</param>
    /// <param name="now">When.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Whether it was written, or false when something is already waiting on the thing.</returns>
    public async ValueTask<bool> SubmitAsync(
        string? tenantId,
        Guid id,
        Guid process,
        ApprovalSubject subject,
        Guid subjectId,
        string by,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = InsertRequest;

        Add(command, "id", NpgsqlDbType.Uuid, id);
        Add(command, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
        Add(command, "process", NpgsqlDbType.Uuid, process);
        Add(command, "subject", NpgsqlDbType.Text, subject.ToString());
        Add(command, "subjectId", NpgsqlDbType.Uuid, subjectId);
        Add(command, "by", NpgsqlDbType.Text, by);
        Add(command, "now", NpgsqlDbType.TimestampTz, now);

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (PostgresException failure) when (failure.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // The partial unique index on the live rows. Caught rather than pre-checked, because a
            // read-then-write leaves a window in which two submissions both find nothing.
            return false;
        }
    }

    /// <summary>Reads a request and the process behind it.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="id">Which request.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The request, or null.</returns>
    public async ValueTask<StoredRequest?> RequestAsync(
        string? tenantId,
        Guid id,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = ReadRequest;
        Add(command, "id", NpgsqlDbType.Uuid, id);

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        Guid process;
        StoredRequest? found;

        await using (reader.ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            process = reader.GetGuid(0);

            found = new StoredRequest(
                id,
                process,
                reader.GetString(6),
                Enum.Parse<ApprovalSubject>(reader.GetString(1)),
                reader.GetGuid(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                []);
        }

        return found with
        {
            Steps = await StepsAsync(connection, process, cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>Writes a decision and moves the request on.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="id">Which request.</param>
    /// <param name="ordinal">Which step.</param>
    /// <param name="by">Who decided.</param>
    /// <param name="decision">What they said.</param>
    /// <param name="note">Why.</param>
    /// <param name="step">Which step is next.</param>
    /// <param name="status">Where the request now stands.</param>
    /// <param name="now">When.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Whether the decision was written, or false when the step was already decided.</returns>
    public async ValueTask<bool> DecideAsync(
        string? tenantId,
        Guid id,
        int ordinal,
        string by,
        ApprovalDecision decision,
        string note,
        int step,
        string status,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var write = connection.CreateCommand();
        await using var closingWrite = write.ConfigureAwait(false);

        write.CommandText = InsertDecision;

        Add(write, "id", NpgsqlDbType.Uuid, id);
        Add(write, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
        Add(write, "ordinal", NpgsqlDbType.Integer, ordinal);
        Add(write, "by", NpgsqlDbType.Text, by);
        Add(write, "decision", NpgsqlDbType.Text, decision.ToString());
        Add(write, "note", NpgsqlDbType.Text, note);
        Add(write, "now", NpgsqlDbType.TimestampTz, now);

        // DO NOTHING and a row count, so two clicks on one step resolve to one decision and one
        // refusal rather than to whichever arrived last.
        if (await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
        {
            return false;
        }

        var advance = connection.CreateCommand();
        await using var closingAdvance = advance.ConfigureAwait(false);

        advance.CommandText = AdvanceRequest;

        Add(advance, "id", NpgsqlDbType.Uuid, id);
        Add(advance, "step", NpgsqlDbType.Integer, step);
        Add(advance, "status", NpgsqlDbType.Text, status);
        Add(advance, "now", NpgsqlDbType.TimestampTz, now);

        await advance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>Everything still waiting, with the step each is waiting on.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The pending requests, oldest first.</returns>
    public async ValueTask<IReadOnlyList<PendingRequest>> PendingAsync(
        string? tenantId,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = PendingRequests;

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        var pending = new List<PendingRequest>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            pending.Add(new PendingRequest(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetGuid(3),
                reader.GetString(4),
                await reader.GetFieldValueAsync<DateTimeOffset>(5, cancellationToken)
                    .ConfigureAwait(false),
                reader.GetInt32(6),
                reader.GetString(7),
                Enum.Parse<ApproverKind>(reader.GetString(8)),
                await reader.IsDBNullAsync(9, cancellationToken).ConfigureAwait(false)
                    ? null
                    : reader.GetString(9)));
        }

        return pending;
    }

    private static async ValueTask<IReadOnlyList<StoredStep>> StepsAsync(
        NpgsqlConnection connection,
        Guid process,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var closing = command.ConfigureAwait(false);

        command.CommandText = StepsOfProcess;
        Add(command, "process", NpgsqlDbType.Uuid, process);

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        var steps = new List<StoredStep>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            steps.Add(new StoredStep(
                reader.GetInt32(0),
                reader.GetString(1),
                Enum.Parse<ApproverKind>(reader.GetString(2)),
                await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false)
                    ? null
                    : reader.GetString(3)));
        }

        return steps;
    }

    private static async ValueTask<IReadOnlyDictionary<string, string?>?> FactsAsync(
        NpgsqlConnection connection,
        ApprovalSubject subject,
        Guid id,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var closing = command.ConfigureAwait(false);

        // Three constants chosen by the subject. A table name cannot be a parameter, and building
        // one from a caller's value is the shape SqlFitnessTests exists to refuse.
        command.CommandText = subject switch
        {
            ApprovalSubject.Quote => QuoteFacts,
            ApprovalSubject.Opportunity => OpportunityFacts,
            _ => PlanFacts,
        };

        Add(command, "id", NpgsqlDbType.Uuid, id);

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var names = ApprovalAttributes.Of(subject);
        var facts = new Dictionary<string, string?>(StringComparer.Ordinal);

        for (var column = 0; column < names.Count; column++)
        {
            facts[names[column]] =
                await reader.IsDBNullAsync(column, cancellationToken).ConfigureAwait(false)
                    ? null
                    : reader.GetString(column);
        }

        return facts;
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

/// <summary>One step of a process, as stored.</summary>
/// <param name="Ordinal">Where it sits.</param>
/// <param name="Label">What to show.</param>
/// <param name="Kind">How the approver is found.</param>
/// <param name="Approver">A subject, a role, or null.</param>
public sealed record StoredStep(int Ordinal, string Label, ApproverKind Kind, string? Approver);

/// <summary>The process that governs a submission, with what it will ask for.</summary>
/// <param name="ProcessId">Its id.</param>
/// <param name="Name">Its identifier.</param>
/// <param name="Label">What to show.</param>
/// <param name="Steps">Who has to say yes.</param>
public sealed record MatchedProcess(
    Guid ProcessId,
    string Name,
    string Label,
    IReadOnlyList<StoredStep> Steps);

/// <summary>A request as stored.</summary>
/// <param name="RequestId">Its id.</param>
/// <param name="ProcessId">Which process governs it.</param>
/// <param name="Process">That process's name.</param>
/// <param name="Subject">What sort of thing.</param>
/// <param name="SubjectId">Which one.</param>
/// <param name="SubmittedBy">Who asked.</param>
/// <param name="Status">Where it stands.</param>
/// <param name="CurrentStep">Which step is being waited on.</param>
/// <param name="Steps">The process's steps.</param>
public sealed record StoredRequest(
    Guid RequestId,
    Guid ProcessId,
    string Process,
    ApprovalSubject Subject,
    Guid SubjectId,
    string SubmittedBy,
    string Status,
    int CurrentStep,
    IReadOnlyList<StoredStep> Steps);

/// <summary>A request waiting on somebody, with the step's rule for finding them.</summary>
/// <param name="RequestId">Which request.</param>
/// <param name="Process">Which process.</param>
/// <param name="Subject">What sort of thing.</param>
/// <param name="SubjectId">Which one.</param>
/// <param name="SubmittedBy">Who asked.</param>
/// <param name="SubmittedAt">When.</param>
/// <param name="Step">Which step.</param>
/// <param name="StepLabel">What it is called.</param>
/// <param name="Kind">How its approver is found.</param>
/// <param name="Approver">A subject, a role, or null.</param>
public sealed record PendingRequest(
    Guid RequestId,
    string Process,
    string Subject,
    Guid SubjectId,
    string SubmittedBy,
    DateTimeOffset SubmittedAt,
    int Step,
    string StepLabel,
    ApproverKind Kind,
    string? Approver);
