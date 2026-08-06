using FlowX;

namespace Crm;

/// <summary>
/// The plan tree of a period, so a gap can be traced to the level that owns it.
/// </summary>
[Capability("crm.planning.tree", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.read",
    Idempotent = true)]
public sealed class ReadCrmPlanTree : ICapability<ReadPlanTree, PlanTree>
{
    private readonly PlanningStore _planning;
    private readonly PerformanceStore _performance;

    /// <summary>Creates the capability.</summary>
    /// <param name="planning">Reads the period.</param>
    /// <param name="performance">Walks the tree.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public ReadCrmPlanTree(PlanningStore planning, PerformanceStore performance)
    {
        ArgumentNullException.ThrowIfNull(planning);
        ArgumentNullException.ThrowIfNull(performance);

        _planning = planning;
        _performance = performance;
    }

    /// <inheritdoc />
    public async ValueTask<Result<PlanTree>> ExecuteAsync(
        ReadPlanTree input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (await _planning.PeriodAsync(ctx.TenantId, input.Period, ct).ConfigureAwait(false)
            is not { } period)
        {
            return Result.Fail<PlanTree>(PlanningErrors.PeriodNotFound(input.Period));
        }

        var nodes = await _performance
            .TreeAsync(ctx.TenantId, period.PeriodId, input.Root, ct)
            .ConfigureAwait(false);

        return Result.Ok(new PlanTree(input.Period, nodes));
    }
}

/// <summary>
/// How the people in the caller's organisation are doing.
/// </summary>
/// <remarks>
/// <strong>Scoped the same way the roll-up is.</strong> A manager sees their line, a director the
/// organisation. Anything else would make "how is my team doing" answerable only by somebody who
/// could already see everybody.
/// </remarks>
[Capability("crm.performance.sales", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.read",
    Idempotent = true)]
public sealed class ReadCrmSalesPerformance : ICapability<ForPerformance, SalesPerformance>
{
    private readonly PlanningStore _planning;
    private readonly PerformanceStore _performance;
    private readonly ManagementStore _org;

    /// <summary>Creates the capability.</summary>
    /// <param name="planning">Reads the period.</param>
    /// <param name="performance">Reads the people.</param>
    /// <param name="org">Says whose rows this caller sees.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public ReadCrmSalesPerformance(
        PlanningStore planning,
        PerformanceStore performance,
        ManagementStore org)
    {
        ArgumentNullException.ThrowIfNull(planning);
        ArgumentNullException.ThrowIfNull(performance);
        ArgumentNullException.ThrowIfNull(org);

        _planning = planning;
        _performance = performance;
        _org = org;
    }

    /// <inheritdoc />
    public async ValueTask<Result<SalesPerformance>> ExecuteAsync(
        ForPerformance input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (await _planning.PeriodAsync(ctx.TenantId, input.Period, ct).ConfigureAwait(false)
            is not { } period)
        {
            return Result.Fail<SalesPerformance>(PlanningErrors.PeriodNotFound(input.Period));
        }

        if (await _org.ScopeAsync(ctx.TenantId, input.UserId, ct).ConfigureAwait(false)
            is not { } viewer)
        {
            return Result.Fail<SalesPerformance>(
                ManagementErrors.CallerIsNotInTheOrganisation());
        }

        var sellers = await _performance
            .SellersAsync(ctx.TenantId, period, viewer.Scope, ct)
            .ConfigureAwait(false);

        return Result.Ok(new SalesPerformance(input.Period, sellers));
    }
}

/// <summary>
/// How the deals themselves are doing.
/// </summary>
/// <remarks>
/// <strong>Not scoped by the reporting line, and that is a limit rather than an oversight.</strong>
/// An opportunity's owner is a <c>uuid</c> that predates the organisation table, so there is no
/// join from a deal to a person's subject. Saying so beats a scope that looked right and silently
/// counted everybody's deals as everybody's.
/// </remarks>
[Capability("crm.performance.deals", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.read",
    Idempotent = true)]
public sealed class ReadCrmDealPerformance : ICapability<ReadDealPerformance, DealPerformance>
{
    private readonly PlanningStore _planning;
    private readonly PerformanceStore _performance;

    /// <summary>Creates the capability.</summary>
    /// <param name="planning">Reads the period.</param>
    /// <param name="performance">Reads the deals.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public ReadCrmDealPerformance(PlanningStore planning, PerformanceStore performance)
    {
        ArgumentNullException.ThrowIfNull(planning);
        ArgumentNullException.ThrowIfNull(performance);

        _planning = planning;
        _performance = performance;
    }

    /// <inheritdoc />
    public async ValueTask<Result<DealPerformance>> ExecuteAsync(
        ReadDealPerformance input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (await _planning.PeriodAsync(ctx.TenantId, input.Period, ct).ConfigureAwait(false)
            is not { } period)
        {
            return Result.Fail<DealPerformance>(PlanningErrors.PeriodNotFound(input.Period));
        }

        var deals = await _performance
            .DealsAsync(ctx.TenantId, period, ctx.UtcNow, ct)
            .ConfigureAwait(false);

        return Result.Ok(deals);
    }
}

/// <summary>
/// Everything a board looks at, in one request, at the level the caller sits.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One request and not six.</strong> A board screen that fetched the roll-up, the tree,
/// the people, the deals and the scorecard separately would render in five stages — and could show
/// a roll-up taken at one instant beside a scorecard taken at another, which is how two numbers on
/// one page stop agreeing and nobody can say which is wrong.
/// </para>
/// <para>
/// <strong>Composed from the capabilities' stores, not from the capabilities.</strong> A capability
/// calling a capability is what <c>CapabilitiesDoNotCallCapabilities</c> refuses: two authorisation
/// stances on one call is one stance nobody can name. The stores are shared; the stance is this
/// one's alone.
/// </para>
/// </remarks>
[Capability("crm.board", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.read",
    Idempotent = true)]
public sealed class ReadExecutiveBoard : ICapability<ForPerformance, ExecutiveBoard>
{
    private readonly PlanningStore _planning;
    private readonly PerformanceStore _performance;
    private readonly ManagementStore _org;

    /// <summary>Creates the capability.</summary>
    /// <param name="planning">Reads the period and the roll-up.</param>
    /// <param name="performance">Reads the tree, the people and the deals.</param>
    /// <param name="org">Says what the caller sees, and reads the KPIs.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public ReadExecutiveBoard(
        PlanningStore planning,
        PerformanceStore performance,
        ManagementStore org)
    {
        ArgumentNullException.ThrowIfNull(planning);
        ArgumentNullException.ThrowIfNull(performance);
        ArgumentNullException.ThrowIfNull(org);

        _planning = planning;
        _performance = performance;
        _org = org;
    }

    /// <inheritdoc />
    public async ValueTask<Result<ExecutiveBoard>> ExecuteAsync(
        ForPerformance input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (await _planning.PeriodAsync(ctx.TenantId, input.Period, ct).ConfigureAwait(false)
            is not { } period)
        {
            return Result.Fail<ExecutiveBoard>(PlanningErrors.PeriodNotFound(input.Period));
        }

        if (await _org.ScopeAsync(ctx.TenantId, input.UserId, ct).ConfigureAwait(false)
            is not { } viewer)
        {
            return Result.Fail<ExecutiveBoard>(ManagementErrors.CallerIsNotInTheOrganisation());
        }

        var today = DateOnly.FromDateTime(ctx.UtcNow.UtcDateTime);

        var rollUp = await _planning
            .RollUpAsync(ctx.TenantId, period, today, viewer.Scope, ct)
            .ConfigureAwait(false);

        if (rollUp is not { } number)
        {
            return Result.Fail<ExecutiveBoard>(PlanningErrors.StrategyNotSet(input.Period));
        }

        var tree = await _performance
            .TreeAsync(ctx.TenantId, period.PeriodId, null, ct)
            .ConfigureAwait(false);

        var sellers = await _performance
            .SellersAsync(ctx.TenantId, period, viewer.Scope, ct)
            .ConfigureAwait(false);

        var deals = await _performance
            .DealsAsync(ctx.TenantId, period, ctx.UtcNow, ct)
            .ConfigureAwait(false);

        var kpis = await _org
            .KpisAsync(ctx.TenantId, period, today, null, ct)
            .ConfigureAwait(false);

        return Result.Ok(new ExecutiveBoard(
            input.Period,
            viewer,
            number,
            new PlanTree(input.Period, tree),
            new SalesPerformance(input.Period, sellers),
            deals,
            new Scorecard(input.Period, [.. kpis.Select(static row => row.Result)])));
    }
}
