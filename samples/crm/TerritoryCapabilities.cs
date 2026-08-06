using FlowX;

namespace Crm;

/// <summary>
/// Declares a territory: what falls into it, and whose patch it is.
/// </summary>
/// <remarks>
/// <strong>Rules rather than a list of accounts.</strong> A list is always one import behind — every
/// new account has to be assigned by somebody — and nobody can say which accounts are in no
/// territory at all, because an account missing from every list looks exactly like one nobody has
/// got to yet. Rules route the moment an account exists, and make the coverage question answerable.
/// </remarks>
[Capability("crm.territory.define", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.admin")]
public sealed class DefineCrmTerritory : ICapability<DefineTerritory, TerritoryDefined>
{
    private readonly TerritoryStore _territories;

    /// <summary>Creates the capability.</summary>
    /// <param name="territories">Writes the territory.</param>
    /// <exception cref="ArgumentNullException"><paramref name="territories"/> is null.</exception>
    public DefineCrmTerritory(TerritoryStore territories)
    {
        ArgumentNullException.ThrowIfNull(territories);

        _territories = territories;
    }

    /// <inheritdoc />
    public async ValueTask<Result<TerritoryDefined>> ExecuteAsync(
        DefineTerritory input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (!CustomValues.IsUsableName(input.Name))
        {
            return Result.Fail<TerritoryDefined>(CustomSchemaErrors.NameIsNotUsable(input.Name));
        }

        // A territory with no rules matches everything, so it becomes a catch-all at whatever
        // priority somebody happened to type, and everything behind it stops routing.
        if (input.Rules.Count == 0)
        {
            return Result.Fail<TerritoryDefined>(TerritoryErrors.TerritoryHasNoRules());
        }

        if (input.Rules.Count > TerritoryLimits.MaxRules
            || input.Owners.Count > TerritoryLimits.MaxOwners)
        {
            return Result.Fail<TerritoryDefined>(QueryErrors.TooManyCriteria(input.Rules.Count));
        }

        // Checked when the territory is saved, not when it routes. A rule naming an attribute the
        // subject does not have matches nothing for ever, and the symptom is a coverage gap that
        // reads as missing data rather than as a typo.
        foreach (var rule in input.Rules)
        {
            if (!RoutingAttributes.Of(rule.Subject)
                    .Contains(rule.Attribute, StringComparer.Ordinal))
            {
                return Result.Fail<TerritoryDefined>(
                    TerritoryErrors.AttributeIsNotOfSubject(rule.Subject, rule.Attribute));
            }
        }

        Guid? parent = null;

        if (input.Parent is { Length: > 0 } named)
        {
            if (await _territories.TerritoryIdAsync(ctx.TenantId, named, ct).ConfigureAwait(false)
                is not { } above)
            {
                return Result.Fail<TerritoryDefined>(TerritoryErrors.TerritoryNotFound(named));
            }

            parent = above;
        }

        var id = await _territories
            .SaveAsync(ctx.TenantId, ctx.NewId(), input, parent, ctx.UtcNow, ct)
            .ConfigureAwait(false);

        return id is { } saved
            ? Result.Ok(new TerritoryDefined(saved, input.Rules.Count))
            : Result.Fail<TerritoryDefined>(CustomSchemaErrors.NameIsTaken(input.Name));
    }
}

/// <summary>
/// Says which territory an account or a lead falls into, and whose patch that is.
/// </summary>
/// <remarks>
/// <strong>It reports how many territories it looked at.</strong> A routing nobody can explain is a
/// routing everybody overrides by hand, and the count is the cheapest thing that makes "why did
/// this go there" answerable without reading the rules back.
/// </remarks>
[Capability("crm.territory.route", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.read",
    Idempotent = true)]
public sealed class RouteToTerritory : ICapability<RouteSubject, RoutedTo>
{
    private readonly TerritoryStore _territories;

    /// <summary>Creates the capability.</summary>
    /// <param name="territories">Reads the rules and evaluates them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="territories"/> is null.</exception>
    public RouteToTerritory(TerritoryStore territories)
    {
        ArgumentNullException.ThrowIfNull(territories);

        _territories = territories;
    }

    /// <inheritdoc />
    public async ValueTask<Result<RoutedTo>> ExecuteAsync(
        RouteSubject input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var routed = await _territories
            .RouteAsync(ctx.TenantId, input.Subject, input.Id, ct)
            .ConfigureAwait(false);

        return routed is { } found
            ? Result.Ok(found)
            : Result.Fail<RoutedTo>(TerritoryErrors.SubjectNotFound(input.Id));
    }
}

/// <summary>
/// What is covered, and — the point of it — what is not.
/// </summary>
[Capability("crm.territory.coverage", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.read",
    Idempotent = true)]
public sealed class ReadTerritoryCoverage : ICapability<ReadCoverage, Coverage>
{
    private readonly TerritoryStore _territories;

    /// <summary>Creates the capability.</summary>
    /// <param name="territories">Walks the accounts against the rules.</param>
    /// <exception cref="ArgumentNullException"><paramref name="territories"/> is null.</exception>
    public ReadTerritoryCoverage(TerritoryStore territories)
    {
        ArgumentNullException.ThrowIfNull(territories);

        _territories = territories;
    }

    /// <inheritdoc />
    public async ValueTask<Result<Coverage>> ExecuteAsync(
        ReadCoverage input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        return Result.Ok(await _territories.CoverageAsync(ctx.TenantId, ct).ConfigureAwait(false));
    }
}

/// <summary>
/// Gives a person their number for a period.
/// </summary>
/// <remarks>
/// <strong>A quota is not a plan.</strong> A plan is committed upwards by the person who owns it; a
/// quota is assigned downwards by the person above them. They are different numbers on purpose,
/// and the difference between them is what a sales-operations review is about.
/// </remarks>
[Capability("crm.quota.set", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.admin")]
public sealed class SetCrmQuota : ICapability<SetQuota, QuotaSet>
{
    private readonly TerritoryStore _territories;
    private readonly PlanningStore _planning;

    /// <summary>Creates the capability.</summary>
    /// <param name="territories">Writes the quota.</param>
    /// <param name="planning">Reads the period.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public SetCrmQuota(TerritoryStore territories, PlanningStore planning)
    {
        ArgumentNullException.ThrowIfNull(territories);
        ArgumentNullException.ThrowIfNull(planning);

        _territories = territories;
        _planning = planning;
    }

    /// <inheritdoc />
    public async ValueTask<Result<QuotaSet>> ExecuteAsync(
        SetQuota input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (input.Target < 0)
        {
            return Result.Fail<QuotaSet>(PlanningErrors.TargetIsNegative());
        }

        // A ramp of nought is not a part-year seller; it is somebody with no number, and that is
        // said by not giving them a quota rather than by giving them one worth nothing.
        if (input.RampFactor is <= 0 or > 1)
        {
            return Result.Fail<QuotaSet>(TerritoryErrors.RampIsOutOfRange(input.RampFactor));
        }

        if (await _planning.PeriodAsync(ctx.TenantId, input.Period, ct).ConfigureAwait(false)
            is not { } period)
        {
            return Result.Fail<QuotaSet>(PlanningErrors.PeriodNotFound(input.Period));
        }

        var assigned = await _territories
            .SaveQuotaAsync(ctx.TenantId, ctx.NewId(), period.PeriodId, input, ctx.UtcNow, ct)
            .ConfigureAwait(false);

        return Result.Ok(new QuotaSet(input.UserId, assigned));
    }
}

/// <summary>
/// Assigned, committed and achieved, side by side.
/// </summary>
/// <remarks>
/// <strong>The middle column is the one a roll-up cannot show.</strong> A seller carrying five
/// hundred who has committed three hundred and eighty has a hole of a hundred and twenty, and every
/// commitment inside it is real — so no total of commitments anywhere reveals it. Reading the three
/// numbers together is the only place it appears.
/// </remarks>
[Capability("crm.quota.attainment", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.read",
    Idempotent = true)]
public sealed class ReadCrmQuotaAttainment : ICapability<ForPerformance, QuotaAttainmentReport>
{
    private readonly TerritoryStore _territories;
    private readonly PlanningStore _planning;
    private readonly ManagementStore _org;

    /// <summary>Creates the capability.</summary>
    /// <param name="territories">Reads the quotas and the actuals.</param>
    /// <param name="planning">Reads the period.</param>
    /// <param name="org">Says whose rows this caller sees.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public ReadCrmQuotaAttainment(
        TerritoryStore territories,
        PlanningStore planning,
        ManagementStore org)
    {
        ArgumentNullException.ThrowIfNull(territories);
        ArgumentNullException.ThrowIfNull(planning);
        ArgumentNullException.ThrowIfNull(org);

        _territories = territories;
        _planning = planning;
        _org = org;
    }

    /// <inheritdoc />
    public async ValueTask<Result<QuotaAttainmentReport>> ExecuteAsync(
        ForPerformance input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (await _planning.PeriodAsync(ctx.TenantId, input.Period, ct).ConfigureAwait(false)
            is not { } period)
        {
            return Result.Fail<QuotaAttainmentReport>(
                PlanningErrors.PeriodNotFound(input.Period));
        }

        if (await _org.ScopeAsync(ctx.TenantId, input.UserId, ct).ConfigureAwait(false)
            is not { } viewer)
        {
            return Result.Fail<QuotaAttainmentReport>(
                ManagementErrors.CallerIsNotInTheOrganisation());
        }

        var rows = await _territories
            .AttainmentAsync(ctx.TenantId, period, viewer.Scope, ct)
            .ConfigureAwait(false);

        return Result.Ok(new QuotaAttainmentReport(input.Period, rows));
    }
}
