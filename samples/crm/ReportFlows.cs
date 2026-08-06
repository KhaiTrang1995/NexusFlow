using FlowX;

namespace Crm;

/// <summary>Saves a report an administrator built.</summary>
[Flow("crm.report.define", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/reports", Idempotent = true)]
public sealed partial class DefineReportFlow : Flow<DefineReport, ReportDefined>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<DefineReport, ReportDefined> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<DefineCrmReport>()
            .Return(ctx => ctx.Get<ReportDefined>());
    }
}

/// <summary>Runs a saved report.</summary>
/// <remarks><c>Ephemeral</c>: it writes nothing, and a dashboard polls it.</remarks>
[Flow("crm.report.run", Version = "1.0.0", Profile = ExecutionProfile.Ephemeral, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/reports/runs")]
public sealed partial class RunReportFlow : Flow<RunReport, ReportResult>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<RunReport, ReportResult> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<RunCrmReport>()
            .Return(ctx => ctx.Get<ReportResult>());
    }
}

/// <summary>Saves a dashboard: an ordered set of saved reports.</summary>
[Flow("crm.dashboard.define", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/dashboards", Idempotent = true)]
public sealed partial class DefineDashboardFlow : Flow<DefineDashboard, DashboardDefined>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<DefineDashboard, DashboardDefined> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<DefineCrmDashboard>()
            .Return(ctx => ctx.Get<DashboardDefined>());
    }
}

/// <summary>Runs every report on a dashboard, in one request.</summary>
[Flow("crm.dashboard.run", Version = "1.0.0", Profile = ExecutionProfile.Ephemeral, Owner = "crm-platform")]
[FlowDeadline("PT30S")]
[HttpTrigger("POST", "/api/v1/crm/dashboards/runs")]
public sealed partial class RunDashboardFlow : Flow<RunDashboard, DashboardResult>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<RunDashboard, DashboardResult> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<RunCrmDashboard>()
            .Return(ctx => ctx.Get<DashboardResult>());
    }
}
