using FlowX;

namespace Crm;

/// <summary>
/// Writes a task somebody owes.
/// </summary>
/// <remarks>
/// <strong>Its id is the engine's, not a derivation, and the reason is that two identical tasks
/// are a legitimate thing to want.</strong> "Call them back" against the same account twice in a
/// week is two tasks. What stops a retried POST from writing two is the trigger's idempotency
/// key, which is the caller's statement that this is the same request — a stronger claim than
/// "the fields match".
/// </remarks>
[Capability("crm.task.create", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.write",
    Idempotent = true,
    SideEffects = ["crm.activity.written"])]
public sealed class CreateTaskForSubject : ICapability<CreateTask, TaskCreated>
{
    private readonly WorkStore _store;

    /// <summary>Creates the capability.</summary>
    /// <param name="store">Writes the activity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public CreateTaskForSubject(WorkStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <inheritdoc />
    public async ValueTask<Result<TaskCreated>> ExecuteAsync(
        CreateTask input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var id = ctx.NewId();

        try
        {
            await _store.CreateAsync(ctx.TenantId, id, input, ct).ConfigureAwait(false);
        }
        catch (Npgsql.PostgresException failure) when (failure.SqlState == "23503" || failure.SqlState == "P0001")
        {
            // Migration 0003's trigger raises P0001 when relates_to_id names nothing, and the
            // foreign keys raise 23503. Both mean the same thing to a caller, and neither is
            // worth showing them as a database error.
            return Result.Fail<TaskCreated>(WorkErrors.TaskHasNoSubject(input.RelatesTo));
        }

        return Result.Ok(new TaskCreated(id, input.DueAt));
    }
}

/// <summary>
/// Escalates every task whose next escalation has fallen due.
/// </summary>
/// <remarks>
/// <strong>The occurrence's instant is the clock, and the sweep is one statement.</strong> Both
/// are what make the schedule reproducible: a fire that is replayed escalates exactly what it
/// escalated the first time, and a fire that overlaps another cannot escalate the same row
/// twice.
/// </remarks>
[Capability("crm.task.escalate", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = false,
    SideEffects = ["crm.activity.written"])]
public sealed class EscalateOverdueTasks : ICapability<ScheduledFire, TasksEscalated>
{
    private readonly WorkStore _store;

    /// <summary>Creates the capability.</summary>
    /// <param name="store">Runs the sweep.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public EscalateOverdueTasks(WorkStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <inheritdoc />
    public async ValueTask<Result<TasksEscalated>> ExecuteAsync(
        ScheduledFire input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var escalated = await _store
            .EscalateAsync(ctx.TenantId, input.OccurrenceAt, SlaPolicy.EscalationWindow, ct)
            .ConfigureAwait(false);

        return Result.Ok(new TasksEscalated(escalated, input.OccurrenceAt));
    }
}

/// <summary>
/// Counts the open opportunities nobody has moved for a fortnight.
/// </summary>
/// <remarks>
/// <strong>It counts rather than writes, and that is a decision.</strong> What to do about a
/// stale opportunity is a business rule, and §7 is where a deployment says so — a transition
/// with a <c>stale</c> trigger and whatever actions it wants. A sweep that hard-coded "create a
/// task" would be the sample deciding something the configured process exists to decide.
/// </remarks>
[Capability("crm.opportunity.sweep_stale", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true)]
public sealed class SweepStaleOpportunities : ICapability<ScheduledFire, StaleOpportunitiesSwept>
{
    private readonly WorkStore _store;

    /// <summary>Creates the capability.</summary>
    /// <param name="store">Runs the count.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public SweepStaleOpportunities(WorkStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <inheritdoc />
    public async ValueTask<Result<StaleOpportunitiesSwept>> ExecuteAsync(
        ScheduledFire input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var stale = await _store
            .CountStaleAsync(ctx.TenantId, input.OccurrenceAt, SlaPolicy.StaleAfter, ct)
            .ConfigureAwait(false);

        return Result.Ok(new StaleOpportunitiesSwept(stale, input.OccurrenceAt));
    }
}

// ------------------------------------------------------------------------------------- flows

/// <summary>Writes a task.</summary>
[Flow("crm.task.create", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-sales")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/tasks", Idempotent = true)]
public sealed partial class CreateTaskFlow : Flow<CreateTask, TaskCreated>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<CreateTask, TaskCreated> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<CreateTaskForSubject>()
            .Return(ctx => ctx.Get<TaskCreated>());
    }
}

/// <summary>
/// Escalates overdue tasks, hourly, once across the fleet.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Once across the fleet is the scheduler's, not this flow's.</strong> Every replica
/// carries this schedule and one of them holds the lease for an occurrence; that is what
/// <c>samples/scheduler</c> demonstrates and what the platform's own suite asserts. Nothing here
/// coordinates, and a sample that tried to would be reimplementing it worse.
/// </para>
/// <para>
/// <strong><c>PerTenant</c>, because the rows are a tenant's.</strong> One occurrence per tenant
/// gives the capability a <c>TenantId</c> to narrow the connection with — without it the sweep
/// would need a connection that could see everybody's tasks, which is the one thing §5 spends
/// its row-level security preventing.
/// </para>
/// <para>
/// <strong>Hourly rather than daily, with the window a day.</strong> The schedule decides how
/// promptly an escalation happens; <see cref="SlaPolicy.EscalationWindow"/> decides how often
/// the same task is escalated. Separating them means a deployment can sweep more often without
/// nagging more often.
/// </para>
/// </remarks>
[Flow("crm.task.escalation", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT60S")]
[CronTrigger("0 * * * *", PerTenant = true)]
public sealed partial class EscalateOverdueTasksFlow : Flow<ScheduledFire, TasksEscalated>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<ScheduledFire, TasksEscalated> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<EscalateOverdueTasks>()
            .Return(ctx => ctx.Get<TasksEscalated>());
    }
}

/// <summary>Counts opportunities nobody has moved, nightly.</summary>
/// <remarks>
/// <strong>Nightly, because nobody acts on it before morning.</strong> An hourly count of what
/// has not moved in a fortnight would be the same number twenty-four times.
/// </remarks>
[Flow("crm.opportunity.stale_sweep", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT60S")]
[CronTrigger("0 6 * * *", PerTenant = true)]
public sealed partial class SweepStaleOpportunitiesFlow : Flow<ScheduledFire, StaleOpportunitiesSwept>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<ScheduledFire, StaleOpportunitiesSwept> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<SweepStaleOpportunities>()
            .Return(ctx => ctx.Get<StaleOpportunitiesSwept>());
    }
}
