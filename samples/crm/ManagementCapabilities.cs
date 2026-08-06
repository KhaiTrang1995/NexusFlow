using FlowX;

namespace Crm;

/// <summary>
/// Places a person in the organisation, which is what decides whose plans they see.
/// </summary>
/// <remarks>
/// <strong>The loop is refused at the write.</strong> A cycle in a reporting line makes every
/// roll-up below it either wrong or non-terminating, and a settings screen is exactly where the
/// two-person loop gets created by accident — A reports to B on Monday, B is moved under A on
/// Thursday by somebody else.
/// </remarks>
[Capability("crm.org.member", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.admin")]
public sealed class PlaceOrgMember : ICapability<SetOrgMember, OrgMemberSet>
{
    private readonly ManagementStore _org;

    /// <summary>Creates the capability.</summary>
    /// <param name="org">Writes the line.</param>
    /// <exception cref="ArgumentNullException"><paramref name="org"/> is null.</exception>
    public PlaceOrgMember(ManagementStore org)
    {
        ArgumentNullException.ThrowIfNull(org);

        _org = org;
    }

    /// <inheritdoc />
    public async ValueTask<Result<OrgMemberSet>> ExecuteAsync(
        SetOrgMember input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (input.UserId is not { Length: > 0 } || input.DisplayName is not { Length: > 0 })
        {
            return Result.Fail<OrgMemberSet>(ManagementErrors.MemberNotFound(input.UserId ?? ""));
        }

        if (input.ReportsTo is { Length: > 0 } manager
            && await _org.ScopeAsync(ctx.TenantId, manager, ct).ConfigureAwait(false) is null)
        {
            return Result.Fail<OrgMemberSet>(ManagementErrors.MemberNotFound(manager));
        }

        var reports = await _org.PlaceAsync(ctx.TenantId, input, ct).ConfigureAwait(false);

        return reports is { } below
            ? Result.Ok(new OrgMemberSet(input.UserId, below))
            : Result.Fail<OrgMemberSet>(
                ManagementErrors.ReportingLineWouldLoop(input.UserId));
    }
}

/// <summary>
/// Writes an objective of an account plan.
/// </summary>
/// <remarks>
/// <strong>Only on an account plan.</strong> A target against a deal is the deal's amount and a
/// target against a channel is its lead count; an objective belongs where the question "what are
/// we trying to achieve with these people this year" is asked, and that is an account.
/// </remarks>
[Capability("crm.planning.objective", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.write")]
public sealed class SetPlanObjective : ICapability<SetObjective, ObjectiveSet>
{
    private readonly ManagementStore _org;

    /// <summary>Creates the capability.</summary>
    /// <param name="org">Writes the objective.</param>
    /// <exception cref="ArgumentNullException"><paramref name="org"/> is null.</exception>
    public SetPlanObjective(ManagementStore org)
    {
        ArgumentNullException.ThrowIfNull(org);

        _org = org;
    }

    /// <inheritdoc />
    public async ValueTask<Result<ObjectiveSet>> ExecuteAsync(
        SetObjective input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (input.Target < 0)
        {
            return Result.Fail<ObjectiveSet>(PlanningErrors.TargetIsNegative());
        }

        if (await _org.PlanAsync(ctx.TenantId, input.Plan, ct).ConfigureAwait(false)
            is not { } plan)
        {
            return Result.Fail<ObjectiveSet>(PlanningErrors.PlanNotFound(input.Plan));
        }

        if (plan.Kind != nameof(PlanKind.Account))
        {
            return Result.Fail<ObjectiveSet>(
                ManagementErrors.NotOfThisPlanKind(plan.Kind, "objectives"));
        }

        var outstanding = await _org
            .SaveObjectiveAsync(ctx.TenantId, plan.Id, input, ct)
            .ConfigureAwait(false);

        return Result.Ok(new ObjectiveSet(input.Ordinal, outstanding));
    }
}

/// <summary>
/// Puts a person on a plan's relationship map.
/// </summary>
/// <remarks>
/// <strong>The number that comes back is how many are not on side.</strong> In a large B2B sale
/// the question that loses deals is not "what do they need" but "who has not agreed yet", and a
/// map that only counted names would answer the wrong one.
/// </remarks>
[Capability("crm.planning.stakeholder", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.write")]
public sealed class SetPlanStakeholder : ICapability<SetStakeholder, StakeholderSet>
{
    private readonly ManagementStore _org;

    /// <summary>Creates the capability.</summary>
    /// <param name="org">Writes the map.</param>
    /// <exception cref="ArgumentNullException"><paramref name="org"/> is null.</exception>
    public SetPlanStakeholder(ManagementStore org)
    {
        ArgumentNullException.ThrowIfNull(org);

        _org = org;
    }

    /// <inheritdoc />
    public async ValueTask<Result<StakeholderSet>> ExecuteAsync(
        SetStakeholder input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (input.Influence is < 1 or > 5)
        {
            return Result.Fail<StakeholderSet>(
                ManagementErrors.InfluenceIsOutOfRange(input.Influence));
        }

        if (await _org.PlanAsync(ctx.TenantId, input.Plan, ct).ConfigureAwait(false)
            is not { } plan)
        {
            return Result.Fail<StakeholderSet>(PlanningErrors.PlanNotFound(input.Plan));
        }

        // A demand plan has no people to map — a channel is not somebody who can be sceptical.
        if (plan.Kind == nameof(PlanKind.MarketingLead))
        {
            return Result.Fail<StakeholderSet>(
                ManagementErrors.NotOfThisPlanKind(plan.Kind, "stakeholders"));
        }

        var (mapped, opposed) = await _org
            .SaveStakeholderAsync(ctx.TenantId, plan.Id, input, ct)
            .ConfigureAwait(false);

        return Result.Ok(new StakeholderSet(mapped, opposed));
    }
}

/// <summary>
/// Raises a risk against a plan, or closes one.
/// </summary>
/// <remarks>
/// A closed risk stays on the register. It is the evidence that raising them works, and a register
/// that deleted them would make every quarter look like the first one.
/// </remarks>
[Capability("crm.planning.risk", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.write")]
public sealed class SetPlanRisk : ICapability<SetRisk, RiskSet>
{
    private readonly ManagementStore _org;

    /// <summary>Creates the capability.</summary>
    /// <param name="org">Writes the risk.</param>
    /// <exception cref="ArgumentNullException"><paramref name="org"/> is null.</exception>
    public SetPlanRisk(ManagementStore org)
    {
        ArgumentNullException.ThrowIfNull(org);

        _org = org;
    }

    /// <inheritdoc />
    public async ValueTask<Result<RiskSet>> ExecuteAsync(
        SetRisk input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (await _org.PlanAsync(ctx.TenantId, input.Plan, ct).ConfigureAwait(false)
            is not { } plan)
        {
            return Result.Fail<RiskSet>(PlanningErrors.PlanNotFound(input.Plan));
        }

        var open = await _org.SaveRiskAsync(ctx.TenantId, plan.Id, input, ct).ConfigureAwait(false);

        return Result.Ok(new RiskSet(input.Ordinal, open));
    }
}

/// <summary>
/// Declares a number the leadership team reviews.
/// </summary>
/// <remarks>
/// <strong>A definition, not a number.</strong> A stored figure is one that was true once; a KPI
/// here is a source this build knows how to compute, a target and a direction, so the actual is
/// read live and the target is the only thing a person maintains.
/// </remarks>
[Capability("crm.kpi.define", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.admin")]
public sealed class DefineCrmKpi : ICapability<DefineKpi, KpiDefined>
{
    private readonly ManagementStore _org;

    /// <summary>Creates the capability.</summary>
    /// <param name="org">Writes the KPI.</param>
    /// <exception cref="ArgumentNullException"><paramref name="org"/> is null.</exception>
    public DefineCrmKpi(ManagementStore org)
    {
        ArgumentNullException.ThrowIfNull(org);

        _org = org;
    }

    /// <inheritdoc />
    public async ValueTask<Result<KpiDefined>> ExecuteAsync(
        DefineKpi input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (!CustomValues.IsUsableName(input.Name))
        {
            return Result.Fail<KpiDefined>(CustomSchemaErrors.NameIsNotUsable(input.Name));
        }

        if (input.Target < 0)
        {
            return Result.Fail<KpiDefined>(PlanningErrors.TargetIsNegative());
        }

        var id = await _org
            .SaveKpiAsync(ctx.TenantId, ctx.NewId(), input, ctx.UtcNow, ct)
            .ConfigureAwait(false);

        return Result.Ok(new KpiDefined(id));
    }
}

/// <summary>
/// Runs every KPI for a period: the target, the live actual, and whether it is on track.
/// </summary>
/// <remarks>
/// <strong>Off-track first.</strong> A scorecard is walked by what is wrong with it, and a review
/// that opens on the numbers that are fine spends its first ten minutes on them.
/// </remarks>
[Capability("crm.kpi.scorecard", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.read",
    Idempotent = true)]
public sealed class ReadCrmScorecard : ICapability<ReadScorecard, Scorecard>
{
    private readonly ManagementStore _org;
    private readonly PlanningStore _planning;

    /// <summary>Creates the capability.</summary>
    /// <param name="org">Reads the KPIs and computes them.</param>
    /// <param name="planning">Reads the period they are computed over.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public ReadCrmScorecard(ManagementStore org, PlanningStore planning)
    {
        ArgumentNullException.ThrowIfNull(org);
        ArgumentNullException.ThrowIfNull(planning);

        _org = org;
        _planning = planning;
    }

    /// <inheritdoc />
    public async ValueTask<Result<Scorecard>> ExecuteAsync(
        ReadScorecard input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (await _planning.PeriodAsync(ctx.TenantId, input.Period, ct).ConfigureAwait(false)
            is not { } period)
        {
            return Result.Fail<Scorecard>(PlanningErrors.PeriodNotFound(input.Period));
        }

        var kpis = await _org
            .KpisAsync(
                ctx.TenantId, period, DateOnly.FromDateTime(ctx.UtcNow.UtcDateTime), null, ct)
            .ConfigureAwait(false);

        return Result.Ok(new Scorecard(input.Period, [.. kpis.Select(static row => row.Result)]));
    }
}

/// <summary>
/// Records what was said about a number, and what the number was when it was said.
/// </summary>
/// <remarks>
/// <strong>This one stores the actual, and a plan must not.</strong> The difference is what the
/// figure claims: a plan's cached actual claims to be current and is wrong between refreshes; a
/// review's claims only to be what the number was when the commentary beside it was written, which
/// is what a minute of a meeting is for. A minute that silently updated itself would not be one.
/// </remarks>
[Capability("crm.kpi.review", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.admin")]
public sealed class RecordKpiReview : ICapability<ForReview, KpiReviewed>
{
    private readonly ManagementStore _org;
    private readonly PlanningStore _planning;

    /// <summary>Creates the capability.</summary>
    /// <param name="org">Reads the KPI and writes the review.</param>
    /// <param name="planning">Reads the period.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public RecordKpiReview(ManagementStore org, PlanningStore planning)
    {
        ArgumentNullException.ThrowIfNull(org);
        ArgumentNullException.ThrowIfNull(planning);

        _org = org;
        _planning = planning;
    }

    /// <inheritdoc />
    public async ValueTask<Result<KpiReviewed>> ExecuteAsync(
        ForReview input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var request = input.Request;

        if (await _planning.PeriodAsync(ctx.TenantId, request.Period, ct).ConfigureAwait(false)
            is not { } period)
        {
            return Result.Fail<KpiReviewed>(PlanningErrors.PeriodNotFound(request.Period));
        }

        var kpis = await _org
            .KpisAsync(
                ctx.TenantId, period, DateOnly.FromDateTime(ctx.UtcNow.UtcDateTime),
                request.Kpi, ct)
            .ConfigureAwait(false);

        if (kpis.Count == 0)
        {
            return Result.Fail<KpiReviewed>(ManagementErrors.KpiNotFound(request.Kpi));
        }

        var (id, result) = kpis[0];

        await _org
            .ReviewAsync(
                ctx.TenantId, id, period.PeriodId, result.Actual, request.Commentary,
                input.UserId, ctx.UtcNow, ct)
            .ConfigureAwait(false);

        return Result.Ok(new KpiReviewed(result.Name, result.Actual, result.Status));
    }
}
