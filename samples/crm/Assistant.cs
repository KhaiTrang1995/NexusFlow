using FlowX;
using Npgsql;
using NpgsqlTypes;

namespace Crm;

/// <summary>Asks for a plain-language summary of an account.</summary>
/// <param name="AccountId">Whose account.</param>
public sealed record SummariseAccount(Guid AccountId);

/// <summary>What is going on with an account, in numbers a person or a model can read.</summary>
/// <param name="AccountId">The account.</param>
/// <param name="Name">Its name.</param>
/// <param name="Lifecycle">Where it is in its life.</param>
/// <param name="OpenOpportunities">How many deals are still open.</param>
/// <param name="OpenValue">What they are worth together.</param>
/// <param name="Currency">What that is in, or the empty string when there are none.</param>
/// <param name="OpenTasks">How many tasks somebody still owes.</param>
/// <param name="LastActivityAt">When anything last happened, or null.</param>
public sealed record AccountSummary(
    Guid AccountId,
    string Name,
    Lifecycle Lifecycle,
    int OpenOpportunities,
    decimal OpenValue,
    string Currency,
    int OpenTasks,
    DateTimeOffset? LastActivityAt);

/// <summary>Refusals the assistant can produce.</summary>
public static class AssistantErrors
{
    /// <summary>That account is not in this tenant, or is gone.</summary>
    /// <param name="accountId">What was named.</param>
    /// <remarks>
    /// <strong>The same refusal an agent gets and a person gets.</strong> It says nothing about
    /// whether the account exists somewhere else, because a not-found that could be told apart
    /// from a forbidden is a way to enumerate another tenant's accounts one guess at a time.
    /// </remarks>
    public static Error AccountNotFound(Guid accountId) =>
        new Error(
            "crm.account_not_found",
            "That account is not in this tenant.",
            ErrorCategory.NotFound)
            .With("accountId", accountId);
}

/// <summary>Reads what an account has open.</summary>
public sealed class AssistantStore
{
    private const string SelectSummary = """
        SELECT a.name,
               a.lifecycle,
               coalesce(o.open_count, 0),
               coalesce(o.open_value, 0),
               coalesce(o.currency, ''),
               coalesce(t.open_tasks, 0),
               t.last_activity_at
        FROM account a
        LEFT JOIN (
            SELECT account_id,
                   count(*)      AS open_count,
                   sum(amount)   AS open_value,
                   min(currency) AS currency
            FROM opportunity
            WHERE outcome IS NULL
            GROUP BY account_id
        ) o ON o.account_id = a.account_id
        LEFT JOIN (
            SELECT relates_to_id,
                   count(*) FILTER (WHERE status IN ('Open', 'Escalated')) AS open_tasks,
                   max(due_at)                                            AS last_activity_at
            FROM activity
            WHERE relates_to_kind = 'Account'
            GROUP BY relates_to_id
        ) t ON t.relates_to_id = a.account_id
        WHERE a.account_id = @account
        """;

    private readonly NpgsqlDataSource _source;

    /// <summary>Builds the store over the application's data source.</summary>
    /// <param name="source">The pool <c>AddFlowXPostgres</c> built.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public AssistantStore(NpgsqlDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
    }

    /// <summary>Summarises one account.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="accountId">The account.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The summary, or null when this tenant cannot see the account.</returns>
    /// <remarks>
    /// <strong>One statement, on a connection narrowed to the caller's tenant.</strong> The two
    /// sub-selects see only what the policies of migration <c>0002</c> let them see, so an
    /// account this tenant does not have is not a filtered row — it is a row that is not there.
    /// </remarks>
    public async ValueTask<AccountSummary?> SummariseAsync(
        string? tenantId,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        await CrmTenantScope.ApplyAsync(connection, tenantId, cancellationToken).ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = SelectSummary;
        command.Parameters.Add(new NpgsqlParameter("account", NpgsqlDbType.Uuid) { Value = accountId });

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var lastActivity = await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false)
            ? (DateTimeOffset?)null
            : await reader.GetFieldValueAsync<DateTimeOffset>(6, cancellationToken).ConfigureAwait(false);

        return new AccountSummary(
            accountId,
            reader.GetString(0),
            Enum.Parse<Lifecycle>(reader.GetString(1)),
            (int)reader.GetInt64(2),
            reader.GetDecimal(3),
            reader.GetString(4),
            (int)reader.GetInt64(5),
            lastActivity);
    }
}

/// <summary>
/// Summarises an account for whoever asked — a person over HTTP or a model over MCP.
/// </summary>
/// <remarks>
/// <strong>It holds one stance and both transports meet it.</strong> <c>crm.read</c> is declared
/// here and checked in the step loop against the caller's claims, whichever transport carried
/// them. Nothing in this class knows which one did, and there is no second check on the agent
/// path to fall out of step with this one — which is docs/15 §7's claim written as a capability
/// rather than as a sentence.
/// </remarks>
[Capability("crm.account.summarise", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.read",
    Idempotent = true)]
public sealed class SummariseAccountForCaller : ICapability<SummariseAccount, AccountSummary>
{
    private readonly AssistantStore _store;

    /// <summary>Creates the capability.</summary>
    /// <param name="store">Reads the account.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public SummariseAccountForCaller(AssistantStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <inheritdoc />
    public async ValueTask<Result<AccountSummary>> ExecuteAsync(
        SummariseAccount input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        return await _store.SummariseAsync(ctx.TenantId, input.AccountId, ct).ConfigureAwait(false)
            is { } summary
            ? Result.Ok(summary)
            : Result.Fail<AccountSummary>(AssistantErrors.AccountNotFound(input.AccountId));
    }
}

/// <summary>
/// The one flow this sample publishes to an agent.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Reads only, so <c>Confirmation = Never</c> is a fact rather than a preference.</strong>
/// The capability declares no side effects, so there is nothing a human would be confirming.
/// The flows that do write — a quote, an order, a discount approval — carry no
/// <c>[AgentTrigger]</c> at all, which is a stronger statement than a confirmation prompt: what
/// is not published cannot be called.
/// </para>
/// <para>
/// <strong><c>Ephemeral</c>, because it has nothing to unwind.</strong> One step, one read, no
/// emit. A journaled instance per question a model asks would be a row per question and no
/// property gained.
/// </para>
/// <para>
/// <strong>Both triggers, deliberately.</strong> The point of the package is that the two
/// surfaces are one surface: the same plan, the same capability, the same stance, and the same
/// refusal for the same caller.
/// </para>
/// </remarks>
[Flow("crm.account.summary", Version = "1.0.0", Profile = ExecutionProfile.Ephemeral, Owner = "crm-platform")]
[FlowDeadline("PT10S")]
[HttpTrigger("POST", "/api/v1/crm/account-summaries")]
[AgentTrigger(
    Description =
        "Summarise a CRM account: its lifecycle, how many opportunities are open and what they " +
        "are worth, how many tasks are outstanding, and when anything last happened. Reads only.",
    Confirmation = ConfirmationMode.Never)]
public sealed partial class SummariseAccountFlow : Flow<SummariseAccount, AccountSummary>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<SummariseAccount, AccountSummary> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<SummariseAccountForCaller>()
            .Return(ctx => ctx.Get<AccountSummary>());
    }
}
