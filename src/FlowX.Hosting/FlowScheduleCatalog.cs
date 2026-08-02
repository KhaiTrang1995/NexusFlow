using FlowX.Runtime;

namespace FlowX.Hosting;

/// <summary>
/// One declared schedule, as the node that has to fire it holds it.
/// </summary>
/// <param name="FlowId">The flow's business identity, from <c>[Flow]</c>.</param>
/// <param name="FlowVersion">The exact version this node would run it at.</param>
/// <param name="Cron">The parsed expression and the zone it is read in.</param>
/// <param name="MissedFire">
/// What to do about occurrences that fell due while nothing was there to take them
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0032-a-missed-schedule-fires-late.md">ADR-0027</a>).
/// </param>
/// <remarks>
/// <para>
/// <strong>Four values, and three of them are in the instance id.</strong> The flow id, the
/// version, the expression and the zone are exactly what
/// <see cref="ScheduleOccurrence.InstanceIdFor"/> derives from, which is why they are carried
/// here as a unit rather than reassembled at each firing: a schedule that lost its version
/// between registration and derivation would fold a canary onto the version it was replacing.
/// </para>
/// <param name="PerTenant">
/// Whether one occurrence is one firing per tenant, from <c>CronTriggerAttribute.PerTenant</c>.
/// </param>
/// <para>
/// <strong>What is deliberately absent.</strong> <c>Overlap</c> and <c>Jitter</c> are declared
/// on <c>CronTriggerAttribute</c> and are not here, because nothing reads them: this release
/// binds <c>Schedule</c> and does not bind those two (<c>docs/09-Trigger-Model.md §8</c>).
/// Carrying them would put a value on this record that no code branches on, which is the shape
/// of debt the deleted <c>FLOWX1032</c> existed to report. <c>PerTenant</c> was in that list
/// until <see cref="FlowScheduleScan"/> learned to fan out, and is now the term that decides
/// whether an occurrence produces one instance or one per tenant.
/// </para>
/// </remarks>
public sealed record FlowSchedule(
    string FlowId,
    string FlowVersion,
    CronSchedule Cron,
    MissedFirePolicy MissedFire,
    bool PerTenant = false)
{
    /// <summary>Reads a declared schedule, throwing on an expression or zone it cannot read.</summary>
    /// <param name="flowId">The flow's business identity.</param>
    /// <param name="flowVersion">The version this node carries.</param>
    /// <param name="cron">A five-field cron expression.</param>
    /// <param name="timeZone">An IANA time zone id.</param>
    /// <param name="missedFire">Behaviour after downtime.</param>
    /// <param name="perTenant">Whether one occurrence fires once per tenant.</param>
    /// <returns>The schedule.</returns>
    /// <exception cref="ArgumentException">
    /// The expression or the zone could not be read. Thrown rather than returned because this
    /// runs at composition time from generated code that read a compile-time constant: a
    /// deployment whose cron expression is unreadable should be a pod that never becomes ready,
    /// which is the same stance <c>FlowXOptionsValidator</c> takes and for the same reason
    /// (OWASP A05). A job that silently never runs is the alternative.
    /// </exception>
    public static FlowSchedule Create(
        string flowId,
        string flowVersion,
        string cron,
        string timeZone,
        MissedFirePolicy missedFire,
        bool perTenant = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowVersion);

        var parsed = CronSchedule.Parse(cron, timeZone);

        if (parsed.IsFailure)
        {
            throw new ArgumentException(
                $"Flow '{flowId}' declares a schedule this host cannot read. {parsed.Error.Message}",
                nameof(cron));
        }

        return new FlowSchedule(flowId, flowVersion, parsed.Value, missedFire, perTenant);
    }

    /// <summary>The id the instance for one firing of this schedule is started under.</summary>
    /// <param name="occurrence">The instant the expression named.</param>
    /// <param name="tenantId">Whose firing, or null for a schedule that fires once.</param>
    public Guid InstanceIdFor(DateTimeOffset occurrence, string? tenantId = null) =>
        ScheduleOccurrence.InstanceIdFor(
            FlowId, FlowVersion, Cron.Expression, Cron.TimeZoneId, occurrence, tenantId);

    /// <summary>The input the flow started by one firing binds.</summary>
    /// <param name="occurrence">The instant the expression named.</param>
    public ScheduledFire FireFor(DateTimeOffset occurrence) =>
        new(occurrence, Cron.Expression, Cron.TimeZoneId);
}

/// <summary>One schedule this node can fire, and the flow it fires.</summary>
/// <param name="Schedule">When it fires, and under which id.</param>
/// <param name="Flow">The compiled plan and its dispatcher.</param>
public sealed record ScheduleRegistration(FlowSchedule Schedule, FlowRegistration Flow);

/// <summary>
/// Which schedules this node fires, and the plans behind them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The mirror of <see cref="FlowCatalog"/>, and separate from it because it answers
/// the opposite question.</strong> <see cref="FlowCatalog"/> maps an identity a journal row
/// already carries back to a plan — a sweep finds an instance and has to work out what it is.
/// This one holds the declarations that have <em>no</em> instance yet, and is walked in the
/// other direction: for each schedule, has anything fallen due. Folding them together would
/// mean one dictionary keyed for one question and enumerated for the other.
/// </para>
/// <para>
/// Registered rather than discovered, for <see cref="FlowCatalog"/>'s reason: reflecting over
/// loaded assemblies to find <c>[CronTrigger]</c> would be a trim-time dependency on types
/// nothing statically references, which constraint C2 forbids. The generated
/// <c>AddFlowXSchedules</c> is a call per schedule and is legible in a stack trace.
/// </para>
/// </remarks>
public sealed class FlowScheduleCatalog
{
    private readonly Lock _gate = new();
    private readonly Dictionary<ScheduleKey, ScheduleRegistration> _schedules = [];

    /// <summary>How many schedules this node fires.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _schedules.Count;
            }
        }
    }

    /// <summary>Every registered schedule, ordered so a sweep is deterministic.</summary>
    public IReadOnlyList<ScheduleRegistration> Registrations
    {
        get
        {
            lock (_gate)
            {
                return [.. _schedules
                    .OrderBy(static entry => entry.Key.FlowId, StringComparer.Ordinal)
                    .ThenBy(static entry => entry.Key.FlowVersion, StringComparer.Ordinal)
                    .ThenBy(static entry => entry.Key.Cron, StringComparer.Ordinal)
                    .Select(static entry => entry.Value)];
            }
        }
    }

    /// <summary>Makes a schedule fireable on this node.</summary>
    /// <param name="schedule">When it fires, and under which id.</param>
    /// <param name="plan">The compiled flow.</param>
    /// <param name="dispatcher">Invokes the capability behind each step index.</param>
    /// <returns>The same catalogue, so registrations chain.</returns>
    /// <exception cref="ArgumentException">
    /// The plan is not the flow the schedule names, or it does not declare
    /// <see cref="ExecutionProfile.Durable"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <strong>An ephemeral flow is refused, and this is the load-bearing check in the
    /// type.</strong> Nothing journals an ephemeral instance, so the derived id is inert:
    /// <c>FlowHost</c> takes no lease, writes no row, and every node in the fleet runs the
    /// firing. That is the "fires once per node" failure with no symptom at all — no error, no
    /// duplicate-key refusal, nothing in the journal to count. Refusing here turns it into a
    /// pod that never becomes ready. <c>FLOWX1038</c> is the earlier half of the same rule and
    /// catches it at compile time; this one catches a hand-written registration and a plan that
    /// changed profile after the code that registers it was generated.
    /// </para>
    /// <para>
    /// Last registration wins for a given <c>(id, version, expression)</c>, for
    /// <see cref="FlowCatalog.Add"/>'s reason: a duplicate is a composition root registering
    /// twice, and throwing would make an idempotent one a startup crash. Two <em>different</em>
    /// expressions on one flow are two schedules and both are kept — a flow may declare
    /// <c>[CronTrigger]</c> more than once.
    /// </para>
    /// </remarks>
    public FlowScheduleCatalog Add(FlowSchedule schedule, ExecutionPlan plan, IStepDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(dispatcher);

        if (!string.Equals(plan.Flow.Id, schedule.FlowId, StringComparison.Ordinal) ||
            !string.Equals(plan.Flow.Version, schedule.FlowVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The schedule names '{schedule.FlowId}@{schedule.FlowVersion}' and the plan is " +
                $"'{plan.Flow.Id}@{plan.Flow.Version}'. The identity is what the instance id is " +
                "derived from, so a mismatch would fire one flow under another's occurrences.",
                nameof(plan));
        }

        if (plan.Flow.Profile != ExecutionProfile.Durable)
        {
            throw new ArgumentException(
                $"Flow '{schedule.FlowId}' declares a schedule and the profile " +
                $"'{plan.Flow.Profile}'. A scheduled flow must declare Durable: nothing " +
                "journals an ephemeral instance, so there is no primary key to refuse a second " +
                "node's firing and every node in the fleet would run every occurrence — with " +
                "no error, no duplicate row and nothing anywhere to count.",
                nameof(plan));
        }

        lock (_gate)
        {
            _schedules[new ScheduleKey(schedule.FlowId, schedule.FlowVersion, schedule.Cron.Expression)] =
                new ScheduleRegistration(schedule, new FlowRegistration(plan, dispatcher));
        }

        return this;
    }

    /// <summary>Ordinal by construction: an id, a version and an expression are identifiers.</summary>
    private readonly record struct ScheduleKey(string FlowId, string FlowVersion, string Cron);
}
