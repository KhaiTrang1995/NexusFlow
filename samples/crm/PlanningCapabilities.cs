using FlowX;

namespace Crm;

/// <summary>
/// Declares a period that plans are made for.
/// </summary>
/// <remarks>
/// <strong>A child period is checked to sit inside its parent.</strong> A quarter that sticks out
/// of its year makes a roll-up that counts something twice or loses it, and neither shows up as an
/// error — only as a total nobody can reconcile, three weeks later, in a board pack.
/// </remarks>
[Capability("crm.planning.period", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.admin")]
public sealed class DefinePlanPeriod : ICapability<DefinePeriod, PeriodDefined>
{
    private readonly PlanningStore _planning;

    /// <summary>Creates the capability.</summary>
    /// <param name="planning">Writes the period.</param>
    /// <exception cref="ArgumentNullException"><paramref name="planning"/> is null.</exception>
    public DefinePlanPeriod(PlanningStore planning)
    {
        ArgumentNullException.ThrowIfNull(planning);

        _planning = planning;
    }

    /// <inheritdoc />
    public async ValueTask<Result<PeriodDefined>> ExecuteAsync(
        DefinePeriod input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (!CustomValues.IsUsableName(input.Name))
        {
            return Result.Fail<PeriodDefined>(CustomSchemaErrors.NameIsNotUsable(input.Name));
        }

        if (input.EndsOn < input.StartsOn)
        {
            return Result.Fail<PeriodDefined>(PlanningErrors.PeriodEndsBeforeItStarts());
        }

        Guid? parent = null;

        if (input.Parent is { Length: > 0 } named)
        {
            if (await _planning.PeriodAsync(ctx.TenantId, named, ct).ConfigureAwait(false)
                is not { } outer)
            {
                return Result.Fail<PeriodDefined>(PlanningErrors.PeriodNotFound(named));
            }

            if (input.StartsOn < outer.StartsOn || input.EndsOn > outer.EndsOn)
            {
                return Result.Fail<PeriodDefined>(
                    PlanningErrors.PeriodIsNotInsideItsParent(input.Name, named));
            }

            parent = outer.PeriodId;
        }

        var id = await _planning
            .SavePeriodAsync(ctx.TenantId, ctx.NewId(), input, parent, ctx.UtcNow, ct)
            .ConfigureAwait(false);

        return id is { } saved
            ? Result.Ok(new PeriodDefined(saved))
            : Result.Fail<PeriodDefined>(CustomSchemaErrors.NameIsTaken(input.Name));
    }
}

/// <summary>
/// Sets the number and the words a period is planned against.
/// </summary>
/// <remarks>
/// <strong>One per period, and setting it again replaces it.</strong> A table of every number a
/// leadership team has ever said would need a rule about which one is current, and that rule is
/// the one somebody gets wrong in the week before a board meeting.
/// </remarks>
[Capability("crm.planning.strategy", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.admin")]
public sealed class SetSalesStrategy : ICapability<SetStrategy, StrategySet>
{
    private readonly PlanningStore _planning;

    /// <summary>Creates the capability.</summary>
    /// <param name="planning">Writes the strategy.</param>
    /// <exception cref="ArgumentNullException"><paramref name="planning"/> is null.</exception>
    public SetSalesStrategy(PlanningStore planning)
    {
        ArgumentNullException.ThrowIfNull(planning);

        _planning = planning;
    }

    /// <inheritdoc />
    public async ValueTask<Result<StrategySet>> ExecuteAsync(
        SetStrategy input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (input.Target < 0)
        {
            return Result.Fail<StrategySet>(PlanningErrors.TargetIsNegative());
        }

        if (await _planning.PeriodAsync(ctx.TenantId, input.Period, ct).ConfigureAwait(false)
            is not { } period)
        {
            return Result.Fail<StrategySet>(PlanningErrors.PeriodNotFound(input.Period));
        }

        var id = await _planning
            .SaveStrategyAsync(ctx.TenantId, ctx.NewId(), period.PeriodId, input, ctx.UtcNow, ct)
            .ConfigureAwait(false);

        return Result.Ok(new StrategySet(id));
    }
}

/// <summary>
/// Commits one plan against a period.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What each kind must carry, and must not, is checked here as well as by the table.</strong>
/// A marketing plan with a money target and no lead target is a row that would roll up into the
/// revenue number twice — once as a commitment and once as the pipeline it was meant to create.
/// </para>
/// <para>
/// <strong>A channel is checked against the five a lead can arrive from.</strong> A plan for a
/// sixth is a plan nothing can ever be counted against, so it reports zero for ever and reads as a
/// marketing failure rather than as a typo.
/// </para>
/// </remarks>
[Capability("crm.planning.plan", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.write")]
public sealed class DefineCrmPlan : ICapability<DefinePlan, PlanDefined>
{
    private readonly PlanningStore _planning;

    /// <summary>Creates the capability.</summary>
    /// <param name="planning">Writes the plan.</param>
    /// <exception cref="ArgumentNullException"><paramref name="planning"/> is null.</exception>
    public DefineCrmPlan(PlanningStore planning)
    {
        ArgumentNullException.ThrowIfNull(planning);

        _planning = planning;
    }

    /// <inheritdoc />
    public async ValueTask<Result<PlanDefined>> ExecuteAsync(
        DefinePlan input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (!CustomValues.IsUsableName(input.Name))
        {
            return Result.Fail<PlanDefined>(CustomSchemaErrors.NameIsNotUsable(input.Name));
        }

        if (Shape(input) is { } wrong)
        {
            return Result.Fail<PlanDefined>(wrong);
        }

        if (await _planning.PeriodAsync(ctx.TenantId, input.Period, ct).ConfigureAwait(false)
            is not { } period)
        {
            return Result.Fail<PlanDefined>(PlanningErrors.PeriodNotFound(input.Period));
        }

        var id = await _planning
            .SavePlanAsync(ctx.TenantId, ctx.NewId(), period.PeriodId, input, ctx.UtcNow, ct)
            .ConfigureAwait(false);

        return id is { } saved
            ? Result.Ok(new PlanDefined(saved))
            : Result.Fail<PlanDefined>(CustomSchemaErrors.NameIsTaken(input.Name));
    }

    private static Error? Shape(DefinePlan input)
    {
        var money = input.Kind is PlanKind.Account or PlanKind.Opportunity;

        if ((input.Account is not null) != (input.Kind == PlanKind.Account))
        {
            return PlanningErrors.KindAndFieldsDisagree(input.Kind, nameof(DefinePlan.Account));
        }

        if ((input.Opportunity is not null) != (input.Kind == PlanKind.Opportunity))
        {
            return PlanningErrors.KindAndFieldsDisagree(input.Kind, nameof(DefinePlan.Opportunity));
        }

        if ((input.Channel is not null) != (input.Kind == PlanKind.MarketingLead))
        {
            return PlanningErrors.KindAndFieldsDisagree(input.Kind, nameof(DefinePlan.Channel));
        }

        if ((input.TargetLeads is not null) != (input.Kind == PlanKind.MarketingLead))
        {
            return PlanningErrors.KindAndFieldsDisagree(input.Kind, nameof(DefinePlan.TargetLeads));
        }

        if ((input.TargetAmount is not null) != money)
        {
            return PlanningErrors.KindAndFieldsDisagree(input.Kind, nameof(DefinePlan.TargetAmount));
        }

        if ((input.Currency is not null) != money)
        {
            return PlanningErrors.KindAndFieldsDisagree(input.Kind, nameof(DefinePlan.Currency));
        }

        if (input.Channel is { } channel
            && !PlanningLimits.Channels.Contains(channel, StringComparer.Ordinal))
        {
            return PlanningErrors.ChannelIsNotALeadSource(channel);
        }

        return input.TargetAmount < 0 || input.TargetLeads < 0
            ? PlanningErrors.TargetIsNegative()
            : null;
    }
}

/// <summary>
/// Records whether one thing about a deal is actually known.
/// </summary>
[Capability("crm.planning.qualification", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.write")]
public sealed class RecordQualification : ICapability<AnswerQualification, QualificationRecorded>
{
    private readonly PlanningStore _planning;

    /// <summary>Creates the capability.</summary>
    /// <param name="planning">Writes the answer.</param>
    /// <exception cref="ArgumentNullException"><paramref name="planning"/> is null.</exception>
    public RecordQualification(PlanningStore planning)
    {
        ArgumentNullException.ThrowIfNull(planning);

        _planning = planning;
    }

    /// <inheritdoc />
    public async ValueTask<Result<QualificationRecorded>> ExecuteAsync(
        AnswerQualification input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var answered = await _planning
            .AnswerAsync(ctx.TenantId, input.Plan, input, ct)
            .ConfigureAwait(false);

        return answered is { } count
            ? Result.Ok(new QualificationRecorded(count, PlanningLimits.Elements))
            : Result.Fail<QualificationRecorded>(PlanningErrors.PlanNotFound(input.Plan));
    }
}

/// <summary>
/// Writes a step of the mutual action plan, or marks one done.
/// </summary>
[Capability("crm.planning.step", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.write")]
public sealed class SetCrmPlanStep : ICapability<SetPlanStep, PlanStepSet>
{
    private readonly PlanningStore _planning;

    /// <summary>Creates the capability.</summary>
    /// <param name="planning">Writes the step.</param>
    /// <exception cref="ArgumentNullException"><paramref name="planning"/> is null.</exception>
    public SetCrmPlanStep(PlanningStore planning)
    {
        ArgumentNullException.ThrowIfNull(planning);

        _planning = planning;
    }

    /// <inheritdoc />
    public async ValueTask<Result<PlanStepSet>> ExecuteAsync(
        SetPlanStep input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var outstanding = await _planning
            .SaveStepAsync(ctx.TenantId, input, ctx.UtcNow, ct)
            .ConfigureAwait(false);

        return outstanding is { } count
            ? Result.Ok(new PlanStepSet(input.Ordinal, count))
            : Result.Fail<PlanStepSet>(PlanningErrors.PlanNotFound(input.Plan));
    }
}

/// <summary>
/// Says how a period is looking: the number, what was committed, and the difference.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The gap is the point.</strong> Target minus committed, positive when the plans do not
/// add up to the ambition. Nothing here closes it: there is no reconciling adjustment in the
/// schema and no scaling of children to make a parent add up, because a planning tool that
/// balanced itself would be one that told a board the number was covered when it was not.
/// </para>
/// <para>
/// <strong>Refused when no strategy has been set</strong>, rather than answered with a target of
/// zero — which would show every commitment covering nothing and a gap of minus everything, and
/// read as good news.
/// </para>
/// </remarks>
[Capability("crm.planning.rollup", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.read",
    Idempotent = true)]
public sealed class ReadPeriodRollUp : ICapability<ReadRollUp, PeriodRollUp>
{
    private readonly PlanningStore _planning;

    /// <summary>Creates the capability.</summary>
    /// <param name="planning">Reads the plans and the live actuals.</param>
    /// <exception cref="ArgumentNullException"><paramref name="planning"/> is null.</exception>
    public ReadPeriodRollUp(PlanningStore planning)
    {
        ArgumentNullException.ThrowIfNull(planning);

        _planning = planning;
    }

    /// <inheritdoc />
    public async ValueTask<Result<PeriodRollUp>> ExecuteAsync(
        ReadRollUp input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (await _planning.PeriodAsync(ctx.TenantId, input.Period, ct).ConfigureAwait(false)
            is not { } period)
        {
            return Result.Fail<PeriodRollUp>(PlanningErrors.PeriodNotFound(input.Period));
        }

        var rollUp = await _planning
            .RollUpAsync(ctx.TenantId, period, DateOnly.FromDateTime(ctx.UtcNow.UtcDateTime), ct)
            .ConfigureAwait(false);

        return rollUp is { } found
            ? Result.Ok(found)
            : Result.Fail<PeriodRollUp>(PlanningErrors.StrategyNotSet(input.Period));
    }
}
