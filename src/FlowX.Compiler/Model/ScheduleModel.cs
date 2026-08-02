namespace FlowX.Compiler.Model;

/// <summary>
/// One flow's <c>[CronTrigger]</c>, reduced to everything the schedule registration needs and
/// nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <strong><see cref="Cron"/> and <see cref="TimeZone"/> are copied off the
/// <see cref="TriggerModel"/> the manifest publishes, not read a second time.</strong> That is
/// the arrangement <see cref="HttpEndpointModel"/> has with a route, and it matters more here:
/// the expression and the zone are two of the five values every node derives the instance id
/// from
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0031-an-occurrence-names-the-instance-it-starts.md">ADR-0026</a>),
/// so a registration carrying a different string from the manifest's would not merely mislead a
/// reader — it would split one schedule into two that never see each other's firings.
/// </para>
/// <para>
/// <strong><see cref="MissedFire"/> is the one field read from the attribute directly</strong>,
/// because the manifest deliberately does not publish it
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0034-the-manifest-publishes-a-schedules-address.md">ADR-0029</a>):
/// it decides what this deployment does about work that is late, which is not a promise to
/// anyone outside the application. It is not an address, so reading it separately cannot
/// desynchronise one.
/// </para>
/// <para>
/// <see cref="PerTenant"/> is read from the attribute for the same reason and is not published
/// either: it decides how many instances one occurrence produces on this deployment, which is a
/// fan-out over a tenant set nobody outside the application can see.
/// </para>
/// <para>
/// <c>Overlap</c> and <c>Jitter</c> are declared on the same attribute and are not here, because
/// nothing in this release reads them — <c>docs/09-Trigger-Model.md §8</c> is where that is
/// recorded.
/// </para>
/// </remarks>
public sealed class ScheduleModel
{
    /// <summary>Creates a model of one flow's schedule.</summary>
    /// <param name="flowId">The flow's business id, e.g. <c>ledger.reconcile</c>.</param>
    /// <param name="flowTypeName">The flow's fully qualified type name.</param>
    /// <param name="methodName">The C# name of the generated extension method.</param>
    /// <param name="cron">The expression, exactly as the manifest states it.</param>
    /// <param name="timeZone">The IANA zone, exactly as the manifest states it.</param>
    /// <param name="missedFire">The declared <c>MissedFirePolicy</c> member's name.</param>
    /// <param name="perTenant">Whether one occurrence fires once per tenant.</param>
    public ScheduleModel(
        string flowId,
        string flowTypeName,
        string methodName,
        string cron,
        string timeZone,
        string missedFire,
        bool perTenant = false)
    {
        FlowId = flowId;
        FlowTypeName = flowTypeName;
        MethodName = methodName;
        Cron = cron;
        TimeZone = timeZone;
        MissedFire = missedFire;
        PerTenant = perTenant;
    }

    /// <summary>The flow's business id.</summary>
    public string FlowId { get; }

    /// <summary>The flow's fully qualified type name.</summary>
    public string FlowTypeName { get; }

    /// <summary>The C# name of the generated extension method, e.g. <c>AddReconcileLedgerFlowSchedule</c>.</summary>
    public string MethodName { get; }

    /// <summary>The five-field expression, exactly as the manifest states it.</summary>
    public string Cron { get; }

    /// <summary>The IANA zone, exactly as the manifest states it.</summary>
    public string TimeZone { get; }

    /// <summary>The declared <c>MissedFirePolicy</c> member, by name.</summary>
    public string MissedFire { get; }

    /// <summary>Whether one occurrence of this schedule fires once per tenant.</summary>
    public bool PerTenant { get; }
}
