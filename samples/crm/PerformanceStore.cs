using Npgsql;
using NpgsqlTypes;

namespace Crm;

/// <summary>
/// The plan tree, how the people are doing, and how the deals are doing.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A node's committed total is its children's, not its descendants'.</strong> A grandchild
/// is already inside its own parent's total; adding both would count it twice, and the resulting
/// board would show a group covering its number twice over.
/// </para>
/// <para>
/// <strong>Every percentage is null rather than zero when its denominator is empty.</strong> A
/// seller with no number is not a seller at nought per cent, and a win rate over no decisions is
/// not nought either — it is not a number. Rendering both as zero is what puts the wrong person
/// at the bottom of a table.
/// </para>
/// </remarks>
public sealed class PerformanceStore
{
    // Parents before children, and the depth so a client can indent without walking the list
    // twice. The cap is what stops a cycle created outside this API from hanging a board.
    private const string Tree = """
        WITH RECURSIVE tree AS (
            SELECT p.plan_id, p.parent_plan_id, p.name, p.label, p.kind, p.owner_id,
                   p.target_amount, 1 AS depth, p.name::text AS path
            FROM plan p
            WHERE p.period_id = @period
              AND (CASE WHEN @rooted THEN p.name = @root ELSE p.parent_plan_id IS NULL END)
            UNION ALL
            SELECT c.plan_id, c.parent_plan_id, c.name, c.label, c.kind, c.owner_id,
                   c.target_amount, tree.depth + 1, tree.path || '/' || c.name
            FROM plan c
            JOIN tree ON c.parent_plan_id = tree.plan_id
            WHERE tree.depth < @maxDepth
        )
        SELECT t.name, t.label, t.kind, t.depth, t.owner_id,
               coalesce(t.target_amount, 0),
               (SELECT name FROM plan WHERE plan_id = t.parent_plan_id),
               coalesce((SELECT sum(coalesce(k.target_amount, 0)) FROM plan k
                         WHERE k.parent_plan_id = t.plan_id), 0),
               (SELECT count(*) FROM plan k WHERE k.parent_plan_id = t.plan_id)
        FROM tree t
        ORDER BY t.path
        """;

    // One row per person in scope. The three money columns are read live from the plans and the
    // opportunities; nothing here is stored against a person.
    private const string Sellers = """
        SELECT m.user_id, m.display_name, m.role,
               coalesce((SELECT sum(p.target_amount) FROM plan p
                         WHERE p.period_id = @period AND p.owner_id = m.user_id
                           AND p.target_amount IS NOT NULL), 0),
               coalesce((SELECT sum(o.amount) FROM opportunity o
                         WHERE o.outcome IS NULL
                           AND o.account_id IN (SELECT p.account_id FROM plan p
                                                WHERE p.period_id = @period
                                                  AND p.owner_id = m.user_id
                                                  AND p.account_id IS NOT NULL)), 0),
               coalesce((SELECT sum(o.amount) FROM opportunity o
                         WHERE o.outcome = 'Won'
                           AND o.stage_entered_at >= @from AND o.stage_entered_at < @to
                           AND o.account_id IN (SELECT p.account_id FROM plan p
                                                WHERE p.period_id = @period
                                                  AND p.owner_id = m.user_id
                                                  AND p.account_id IS NOT NULL)), 0)
        FROM org_member m
        WHERE (@unrestricted OR m.user_id = ANY(@scope))
        ORDER BY m.user_id
        LIMIT @limit
        """;

    // One statement for the whole deal picture. Six aggregates over one scan rather than six
    // statements, because a board that read them separately could show six different instants.
    private const string Deals = """
        SELECT count(*) FILTER (WHERE outcome IS NULL),
               coalesce(sum(amount) FILTER (WHERE outcome IS NULL), 0),
               count(*) FILTER (WHERE outcome = 'Won'
                   AND stage_entered_at >= @from AND stage_entered_at < @to),
               coalesce(sum(amount) FILTER (WHERE outcome = 'Won'
                   AND stage_entered_at >= @from AND stage_entered_at < @to), 0),
               count(*) FILTER (WHERE outcome = 'Lost'
                   AND stage_entered_at >= @from AND stage_entered_at < @to),
               coalesce(sum(amount) FILTER (WHERE outcome = 'Lost'
                   AND stage_entered_at >= @from AND stage_entered_at < @to), 0),
               count(*) FILTER (WHERE outcome IS NULL AND stage_entered_at < @stalledBefore)
        FROM opportunity
        """;

    private readonly NpgsqlDataSource _source;

    /// <summary>Builds the store over the application's data source.</summary>
    /// <param name="source">The pool <c>AddFlowXPostgres</c> built.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public PerformanceStore(NpgsqlDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
    }

    /// <summary>The plan tree of a period.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="period">Which period.</param>
    /// <param name="root">One plan to start from, or null for the top-level ones.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The nodes, parents before their children.</returns>
    public async ValueTask<IReadOnlyList<PlanNode>> TreeAsync(
        string? tenantId,
        Guid period,
        string? root,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = Tree;

        Add(command, "period", NpgsqlDbType.Uuid, period);
        Add(command, "rooted", NpgsqlDbType.Boolean, root is { Length: > 0 });
        Add(command, "root", NpgsqlDbType.Text, (object?)root ?? string.Empty);
        Add(command, "maxDepth", NpgsqlDbType.Integer, PlanningLimits.MaxDepth);

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        var nodes = new List<PlanNode>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var target = reader.GetDecimal(5);
            var committed = reader.GetDecimal(7);

            nodes.Add(new PlanNode(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false)
                    ? null
                    : reader.GetString(6),
                reader.GetString(4),
                target,
                committed,
                target - committed,
                (int)reader.GetInt64(8)));
        }

        return nodes;
    }

    /// <summary>How the people in a scope are doing.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="period">Which period.</param>
    /// <param name="scope">Whose rows to read. Empty means no restriction.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The people, weakest attainment first.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public async ValueTask<IReadOnlyList<SellerPerformance>> SellersAsync(
        string? tenantId,
        StoredPeriod period,
        IReadOnlyList<string> scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(scope);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = Sellers;

        Add(command, "period", NpgsqlDbType.Uuid, period.PeriodId);
        AddWindow(command, period);
        Add(command, "limit", NpgsqlDbType.Integer, PerformanceLimits.MaxSellers);

        command.Parameters.Add(
            new NpgsqlParameter("unrestricted", NpgsqlDbType.Boolean) { Value = scope.Count == 0 });

        command.Parameters.Add(new NpgsqlParameter<string[]>("scope", [.. scope]));

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        var sellers = new List<SellerPerformance>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var committed = reader.GetDecimal(3);
            var won = reader.GetDecimal(5);

            sellers.Add(new SellerPerformance(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                committed,
                reader.GetDecimal(4),
                won,

                // Null and not zero. A person who committed nothing is not a person at nought per
                // cent, and sorting them as one puts the wrong name at the bottom of the table.
                committed == 0 ? null : Math.Round(won / committed * 100m, 1)));
        }

        return
        [
            .. sellers
                .OrderBy(static seller => seller.Attainment ?? decimal.MaxValue)
                .ThenBy(static seller => seller.UserId, StringComparer.Ordinal),
        ];
    }

    /// <summary>How the deals are doing.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="period">Which period.</param>
    /// <param name="today">What counts as now, for staleness.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The picture.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="period"/> is null.</exception>
    public async ValueTask<DealPerformance> DealsAsync(
        string? tenantId,
        StoredPeriod period,
        DateTimeOffset today,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(period);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = Deals;

        AddWindow(command, period);
        Add(command, "stalledBefore", NpgsqlDbType.TimestampTz,
            today.AddDays(-PerformanceLimits.StalledAfterDays));

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        var won = (int)reader.GetInt64(2);
        var lost = (int)reader.GetInt64(4);
        var decided = won + lost;
        var wonValue = reader.GetDecimal(3);

        return new DealPerformance(
            period.Name,
            (int)reader.GetInt64(0),
            reader.GetDecimal(1),
            won,
            wonValue,
            lost,
            reader.GetDecimal(5),

            // Null over no decisions. A win rate of nought per cent is a claim that deals were
            // lost, and a quarter in which nothing closed did not lose them.
            decided == 0 ? null : Math.Round(won / (decimal)decided * 100m, 1),
            won == 0 ? null : Math.Round(wonValue / won, 2),
            (int)reader.GetInt64(6));
    }

    private static void AddWindow(NpgsqlCommand command, StoredPeriod period)
    {
        Add(command, "from", NpgsqlDbType.TimestampTz,
            new DateTimeOffset(period.StartsOn.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));

        Add(command, "to", NpgsqlDbType.TimestampTz,
            new DateTimeOffset(
                period.EndsOn.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
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
