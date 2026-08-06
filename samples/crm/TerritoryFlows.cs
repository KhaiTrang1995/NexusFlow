using FlowX;

namespace Crm;

/// <summary>Declares a territory, its rules and its owners.</summary>
[Flow("crm.territory.define", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/territories", Idempotent = true)]
public sealed partial class DefineTerritoryFlow : Flow<DefineTerritory, TerritoryDefined>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<DefineTerritory, TerritoryDefined> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<DefineCrmTerritory>()
            .Return(ctx => ctx.Get<TerritoryDefined>());
    }
}

/// <summary>Says which territory an account or a lead falls into.</summary>
[Flow("crm.territory.route", Version = "1.0.0", Profile = ExecutionProfile.Ephemeral, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/territories/routes")]
public sealed partial class RouteFlow : Flow<RouteSubject, RoutedTo>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<RouteSubject, RoutedTo> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<RouteToTerritory>()
            .Return(ctx => ctx.Get<RoutedTo>());
    }
}

/// <summary>What is covered, and what is not.</summary>
[Flow("crm.territory.coverage", Version = "1.0.0", Profile = ExecutionProfile.Ephemeral, Owner = "crm-platform")]
[FlowDeadline("PT30S")]
[HttpTrigger("POST", "/api/v1/crm/territories/coverage")]
public sealed partial class CoverageFlow : Flow<ReadCoverage, Coverage>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<ReadCoverage, Coverage> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<ReadTerritoryCoverage>()
            .Return(ctx => ctx.Get<Coverage>());
    }
}

/// <summary>Gives a person their number for a period.</summary>
[Flow("crm.quota.set", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/quotas", Idempotent = true)]
public sealed partial class SetQuotaFlow : Flow<SetQuota, QuotaSet>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<SetQuota, QuotaSet> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<SetCrmQuota>()
            .Return(ctx => ctx.Get<QuotaSet>());
    }
}

/// <summary>Assigned, committed and achieved, side by side.</summary>
[Flow("crm.quota.attainment", Version = "1.0.0", Profile = ExecutionProfile.Ephemeral, Owner = "crm-platform")]
[FlowDeadline("PT30S")]
[HttpTrigger("POST", "/api/v1/crm/quotas/attainment")]
public sealed partial class QuotaAttainmentFlow : Flow<ReadQuotaAttainment, QuotaAttainmentReport>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<ReadQuotaAttainment, QuotaAttainmentReport> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<ReadCrmQuotaAttainment, ForPerformance>(
                ctx => new ForPerformance(ctx.Input.Period, Caller.Subject(ctx.Principal)))
            .Return(ctx => ctx.Get<QuotaAttainmentReport>());
    }
}
