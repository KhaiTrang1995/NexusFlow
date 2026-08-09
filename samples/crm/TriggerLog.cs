using Npgsql;
using NpgsqlTypes;

namespace Crm;

// -------------------------------------------------------------------------------- what is asked

/// <summary>Asks what the configured process made of one application of a trigger.</summary>
/// <param name="ApplicationId">
/// The handle <see cref="OpportunityAdvanced"/> handed back. <strong>The act, not the
/// opportunity.</strong> Two people pressing advance a second apart are two applications with two
/// answers, and a read keyed on the opportunity would give the second person the first person's.
/// </param>
public sealed record ReadTriggerOutcome(Guid ApplicationId);

// ------------------------------------------------------------------------------ what comes back

/// <summary>
/// The three things that can have become of an applied trigger.
/// </summary>
/// <remarks>
/// <strong>Two of these used to be one.</strong> A caller could see that the deal had not moved
/// and had no way to tell whether the feed had not reached the change yet or whether the engine
/// had run and decided against it. Neither is an error — declining is what a process is for — so
/// there was nothing to catch, and the client guessed.
/// </remarks>
public enum TriggerOutcome
{
    /// <summary>
    /// The engine has not decided yet. Ask again: the change feed is what runs it, and the answer
    /// arrives when it does.
    /// </summary>
    Pending,

    /// <summary>A transition matched, every guard on it held, and the opportunity moved.</summary>
    Moved,

    /// <summary>
    /// The engine ran and took nothing. No transition carries this trigger out of the stage the
    /// record was in, or one does and a guard on it did not hold.
    /// </summary>
    Declined,
}

/// <summary>What the process did with one application of a trigger.</summary>
/// <param name="ApplicationId">Which application is being reported on.</param>
/// <param name="OpportunityId">Whose.</param>
/// <param name="Trigger">What was applied, in the administrator's vocabulary.</param>
/// <param name="Outcome">Which of the three things happened.</param>
/// <param name="Stage">
/// Where this application left the deal: the stage it entered when
/// <see cref="TriggerOutcome.Moved"/>, and the stage it was applied from otherwise. Named rather
/// than identified because it is a sentence a screen prints.
/// </param>
/// <param name="ActionsRun">How many configured actions ran. Zero unless it moved.</param>
/// <param name="AppliedAt">When somebody applied it.</param>
/// <param name="DecidedAt">When the engine answered, or null while it has not.</param>
public sealed record TriggerOutcomeView(
    Guid ApplicationId,
    Guid OpportunityId,
    string Trigger,
    TriggerOutcome Outcome,
    string Stage,
    int ActionsRun,
    DateTimeOffset AppliedAt,
    DateTimeOffset? DecidedAt);

// ------------------------------------------------------------------------------------ the rows

/// <summary>
/// The register of applied triggers: who asked for what, and what the engine did with it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Written from both sides of the seam, and that is the whole point.</strong> The advance
/// inserts the row in the same act that stages <c>opportunity.stage.changed</c>; the engine
/// updates it when the change feed brings that event round. Nothing here decides anything or
/// changes when the transition runs — it records an answer that was already being computed and
/// thrown away.
/// </para>
/// <para>
/// <strong>Every statement is a <c>const</c>,</strong> for <c>SqlFitnessTests</c>'s reason, and
/// the tenant reaches the connection through <see cref="CrmTenantScope"/>.
/// </para>
/// </remarks>
public sealed class TriggerLogStore
{
    /// <remarks>
    /// <strong>An <c>INSERT … SELECT</c> and not two statements.</strong> The stage recorded has
    /// to be the one the trigger was applied from, and a capability that read the stage and then
    /// wrote it could record a stage somebody else's transition had already moved the deal out
    /// of. The row's tenant comes from the opportunity's own row for the same reason.
    ///
    /// <para>
    /// <c>ON CONFLICT DO NOTHING</c> because the advance is idempotent: a replay journals the
    /// same application id, and a second insert under it is the same act, not a second one.
    /// </para>
    /// </remarks>
    private const string Apply = """
        INSERT INTO opportunity_trigger (
            application_id, tenant_id, opportunity_id, trigger, from_stage_id, applied_at)
        SELECT @application, o.tenant_id, o.opportunity_id, @trigger, o.stage_id, @now
        FROM opportunity o
        WHERE o.opportunity_id = @opportunity
        ON CONFLICT (application_id) DO NOTHING
        """;

    /// <remarks>
    /// One statement for both answers, because they are one answer: a transition or the absence
    /// of one. Two statements would be two places for the <c>decided_at</c> stamp to be forgotten,
    /// and a decision with no timestamp is indistinguishable from one that never came.
    /// </remarks>
    private const string Decide = """
        UPDATE opportunity_trigger
        SET outcome       = CASE WHEN @transition IS NULL THEN 'Declined' ELSE 'Moved' END,
            transition_id = @transition,
            to_stage_id   = @stage,
            actions_run   = @actions,
            decided_at    = @now
        WHERE application_id = @application
        """;

    private const string SelectOutcome = """
        SELECT t.application_id, t.opportunity_id, t.trigger, t.outcome,
               coalesce(entered.name, applied.name), t.actions_run, t.applied_at, t.decided_at
        FROM opportunity_trigger t
        JOIN process_stage applied ON applied.stage_id = t.from_stage_id
        LEFT JOIN process_stage entered ON entered.stage_id = t.to_stage_id
        WHERE t.application_id = @application
        """;

    private readonly NpgsqlDataSource _source;

    /// <summary>Builds the store over the application's data source.</summary>
    /// <param name="source">The pool <c>AddFlowXPostgres</c> built.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public TriggerLogStore(NpgsqlDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
    }

    /// <summary>Records that somebody applied a trigger, and where the deal was when they did.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="applicationId">The handle the caller is given.</param>
    /// <param name="opportunityId">What the trigger was applied to.</param>
    /// <param name="trigger">What was applied.</param>
    /// <param name="now">The flow's clock.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async ValueTask ApplyAsync(
        string? tenantId,
        Guid applicationId,
        Guid opportunityId,
        string trigger,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = Apply;
        command.Parameters.Add(new NpgsqlParameter("application", NpgsqlDbType.Uuid) { Value = applicationId });
        command.Parameters.Add(new NpgsqlParameter("opportunity", NpgsqlDbType.Uuid) { Value = opportunityId });
        command.Parameters.Add(new NpgsqlParameter("trigger", NpgsqlDbType.Text) { Value = trigger });
        command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records what the engine did with an application.</summary>
    /// <param name="tenantId">The tenant the change attested.</param>
    /// <param name="applicationId">Which application. Nothing is written when it names no row.</param>
    /// <param name="transitionId">The transition taken, or null when the engine declined.</param>
    /// <param name="stageId">The stage entered, or null when the engine declined.</param>
    /// <param name="actionsRun">How many configured actions ran.</param>
    /// <param name="now">The engine's clock.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <remarks>
    /// <strong>An application the feed offers twice is decided twice, to the same values.</strong>
    /// That is why this is an <c>UPDATE</c> of a known row rather than an append: a log of
    /// attempts would grow a second entry on every redelivery, and a reader would have to know
    /// which of them was the answer.
    /// </remarks>
    public async ValueTask DecideAsync(
        string? tenantId,
        Guid applicationId,
        Guid? transitionId,
        Guid? stageId,
        int actionsRun,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = Decide;
        command.Parameters.Add(new NpgsqlParameter("application", NpgsqlDbType.Uuid) { Value = applicationId });
        command.Parameters.Add(new NpgsqlParameter("transition", NpgsqlDbType.Uuid)
        {
            Value = (object?)transitionId ?? DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("stage", NpgsqlDbType.Uuid)
        {
            Value = (object?)stageId ?? DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("actions", NpgsqlDbType.Integer) { Value = actionsRun });
        command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads what became of one application.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="applicationId">Which application.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The outcome, or null when this tenant applied no such trigger.</returns>
    public async ValueTask<TriggerOutcomeView?> ReadAsync(
        string? tenantId,
        Guid applicationId,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = SelectOutcome;
        command.Parameters.Add(new NpgsqlParameter("application", NpgsqlDbType.Uuid) { Value = applicationId });

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new TriggerOutcomeView(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            Enum.Parse<TriggerOutcome>(reader.GetString(3)),
            reader.GetString(4),
            reader.GetInt32(5),
            await reader.GetFieldValueAsync<DateTimeOffset>(6, cancellationToken).ConfigureAwait(false),
            await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false)
                ? null
                : await reader.GetFieldValueAsync<DateTimeOffset>(7, cancellationToken).ConfigureAwait(false));
    }

    private ValueTask<NpgsqlConnection> OpenAsync(string? tenantId, CancellationToken cancellationToken) =>
        CrmTenantScope.OpenAsync(_source, tenantId, cancellationToken);
}
