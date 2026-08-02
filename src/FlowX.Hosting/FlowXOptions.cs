using Microsoft.Extensions.Options;

namespace FlowX.Hosting;

/// <summary>Host-level configuration for the FlowX runtime.</summary>
/// <remarks>
/// Every setting here has a default that is safe, and none has a default that is
/// permissive. <see cref="ApplicationName"/> has no default at all, because a manifest
/// and a trace stream that cannot say which application produced them are worth
/// noticeably less than ones that can.
/// </remarks>
public sealed class FlowXOptions
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "FlowX";

    /// <summary>
    /// Identifies this application in the manifest, in traces and in audit records.
    /// Required.
    /// </summary>
    public string ApplicationName { get; set; } = string.Empty;

    /// <summary>How many flow contexts to retain between executions. See budget B2.</summary>
    public int MaxPooledContexts { get; set; } = 128;

    /// <summary>
    /// The deadline a flow gets when it declares none. Bounded on purpose: an
    /// unbounded flow is not a default anyone would choose deliberately.
    /// </summary>
    public TimeSpan DefaultDeadline { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long shutdown waits for in-flight flows before giving up.
    /// </summary>
    /// <remarks>
    /// Keep it below the orchestrator's termination grace period. A drain budget longer
    /// than the grace period is a drain that never completes — Kubernetes sends SIGKILL
    /// on its own schedule, not on this one.
    /// </remarks>
    public TimeSpan ShutdownDrainTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How this node identifies itself when it takes a lease.
    /// </summary>
    /// <remarks>
    /// It goes on the lease and into <c>lease.held</c>, so it is what an operator reads to
    /// answer "who has this instance". The machine name is a defensible default and a poor
    /// one under an orchestrator that recycles them; set it to the pod name where there is
    /// one.
    /// </remarks>
    public string NodeName { get; set; } = Environment.MachineName;

    /// <summary>How long a lease on a durable instance lasts without a renewal.</summary>
    /// <remarks>
    /// Shorter means a crashed node's instances are picked up sooner and every holder renews
    /// more often. It is a latency setting, not a safety one: what stops a paused node from
    /// corrupting an instance is the fencing token, which no value here weakens.
    /// </remarks>
    public TimeSpan LeaseTtl { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How often a held lease is renewed. A third of the TTL by default.</summary>
    /// <remarks>
    /// The margin absorbs two lost renewals — a GC pause, clock skew, a slow store — before
    /// the lease lapses. A node that has missed two in a row has something wrong with it that
    /// another node is better placed to work around.
    /// </remarks>
    public TimeSpan LeaseRenewalInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How often this node looks for instances a dead node left running.</summary>
    /// <remarks>
    /// Applied with jitter, and that is not decoration: identical nodes on an identical
    /// interval converge on the same instant, and a fleet that scans in lockstep is the
    /// thundering herd <c>docs/11-Distributed-Runtime.md</c> warns about wearing a timer.
    /// </remarks>
    public TimeSpan RecoveryScanInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How many candidates one scan asks the journal for.</summary>
    /// <remarks>
    /// A page, never the backlog. After an outage the number of abandoned instances is
    /// unbounded and the work a node can take is not, so a scan that fetched everything would
    /// turn one node's recovery into every node's memory pressure.
    /// </remarks>
    public int RecoveryScanBatchSize { get; set; } = 64;

    /// <summary>How many abandoned instances this node resumes at once.</summary>
    /// <remarks>
    /// The real limit on a stampede. Acquisition decides who wins each instance, but a design
    /// in which every node tries every instance is wrong even when it is safe — this bounds
    /// what one node attempts, and it does not ask for another page until the ones it took
    /// are finished.
    /// </remarks>
    public int MaxConcurrentRecoveries { get; set; } = 8;

    /// <summary>How often this node looks for parked instances whose wait has come due.</summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the resolution of every timer in every flow this node runs.</strong> A
    /// <c>.Delay(TimeSpan.FromSeconds(1))</c> under a ten-second sweep waits somewhere between
    /// one and eleven seconds — the wait is a lower bound, never an upper one, which is the
    /// same promise a scheduled trigger makes and the only one a sweep can keep.
    /// </para>
    /// <para>
    /// Applied with jitter for the reason <see cref="RecoveryScanInterval"/> is: identical
    /// nodes on an identical interval converge, and a fleet that sweeps in lockstep is a
    /// thundering herd wearing a timer.
    /// </para>
    /// </remarks>
    public TimeSpan TimerScanInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How many due instances one sweep asks the journal for.</summary>
    /// <remarks>
    /// A page, never the backlog — the reason <see cref="RecoveryScanBatchSize"/> is bounded,
    /// and one this sweep meets more often: a node that was down over a weekend comes back to
    /// every timer that fell due while it was gone, all due at once.
    /// </remarks>
    public int TimerScanBatchSize { get; set; } = 64;

    /// <summary>How often this node looks for schedule occurrences that have fallen due.</summary>
    /// <remarks>
    /// <para>
    /// <strong>The resolution of every <c>[CronTrigger]</c> this node fires, and the lower
    /// bound on how late a firing is.</strong> A schedule at <c>0 2 * * *</c> under a
    /// ten-second sweep fires between 02:00:00 and 02:00:10. Cron resolves to the minute, so
    /// anything below a minute buys nothing but store traffic; anything above one is latency an
    /// operator will eventually ask about.
    /// </para>
    /// <para>
    /// It is also the window <see cref="MissedFirePolicy.Skip"/> is measured against: an
    /// occurrence older than twice this is one a sweep missed, and <c>Skip</c> is the
    /// declaration that says not to run it late.
    /// </para>
    /// </remarks>
    public TimeSpan ScheduleScanInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How far back a sweep will look for an occurrence nothing fired.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the bound on a schedule's at-least-once, and it is the one number a
    /// deployment has to think about.</strong> A fleet that was down across an occurrence fires
    /// it late when it comes back; a fleet that was down for longer than this loses the
    /// occurrences that fell outside the window — silently, because there is nothing to report
    /// a firing that nothing was there to observe
    /// (<c>docs/adr/ADR-0032-a-missed-schedule-fires-late.md</c>).
    /// </para>
    /// <para>
    /// A day by default, which covers a rolling deploy, a node outage and a night. Raise it to
    /// cover a longer expected outage; the cost of raising it is bounded, because
    /// <see cref="MissedFirePolicy.RunOnce"/> — the default — fires the most recent missed
    /// occurrence and not every one of them.
    /// </para>
    /// <para>
    /// It is not unbounded, and could not usefully be: a sweep with no horizon on a fresh
    /// database has no occurrence to stop at, so a first deployment would fire every occurrence
    /// the expression has ever named.
    /// </para>
    /// </remarks>
    public TimeSpan ScheduleCatchUp { get; set; } = TimeSpan.FromDays(1);

    /// <summary>How many missed occurrences of one schedule a single sweep will fire.</summary>
    /// <remarks>
    /// Only reached under <see cref="MissedFirePolicy.RunAll"/>, which is the declaration that
    /// asks for every missed firing. A minute-by-minute schedule under a day's horizon has
    /// 1,440 of them, and a node that took them all at once would turn one outage into a second
    /// one. What is not taken this sweep is still inside the horizon on the next.
    /// </remarks>
    public int ScheduleFireBatchSize { get; set; } = 32;

    /// <summary>
    /// How often a node asks its brokers whether anything has arrived.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the worst-case latency from an event being published to its flow
    /// starting</strong>, and it is a poll rather than a long-lived push read on purpose —
    /// <see cref="FlowBusService"/> carries the argument. One second by default, which is a round
    /// trip per subscription per second on an idle node.
    /// </para>
    /// <para>
    /// It is not the interval a message is redelivered on. That is the broker's visibility
    /// timeout, and it is the broker's to configure.
    /// </para>
    /// </remarks>
    public TimeSpan BusScanInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How many messages one pass takes from each partition.</summary>
    /// <remarks>
    /// Entries within a partition are run one at a time and in order
    /// (<c>docs/adr/ADR-0037-the-consumer-offers-per-key-order.md</c>), so this is how long one
    /// node holds one partition's lease rather than how much it does at once. What is not taken
    /// this pass is still pending on the next.
    /// </remarks>
    public int BusReceiveBatchSize { get; set; } = 16;

    /// <summary>How many partitions one node serves concurrently, per subscription.</summary>
    /// <remarks>
    /// The concurrency partitioning exists to provide, and the whole of it: order is offered
    /// within a partition and nothing is offered across partitions, so this number is free to
    /// raise and cannot weaken a guarantee. It bounds flows in flight from one subscription per
    /// pass.
    /// </remarks>
    public int BusMaxConcurrentPartitions { get; set; } = 8;

    /// <summary>
    /// How many times a message may be delivered without its flow reaching a recorded outcome
    /// before it is dead-lettered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The bound on head-of-line blocking</strong>
    /// (<c>docs/adr/ADR-0038-a-poison-message-is-dead-lettered.md</c>). Without it, one message
    /// nobody can process stops its partition for ever — which is a bug rather than a durability
    /// strategy, as <c>KafkaTriggerAttribute.DeadLetter</c> has said since it was written.
    /// </para>
    /// <para>
    /// It counts <em>deliveries</em>, which is what a broker knows, and not attempts. A node that
    /// claimed a message and died before running anything has still spent one, so a flapping node
    /// can exhaust the budget without the flow having been tried — which is why the default is
    /// five rather than one or two.
    /// </para>
    /// </remarks>
    public int BusMaxDeliveries { get; set; } = 5;

    /// <summary>
    /// How often a node asks its change feed whether anything has been staged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="BusScanInterval"/>'s argument, one transport over. It is <em>half</em> the
    /// worst-case latency from a change committing to its flow starting: the other half is the
    /// feed's own visibility barrier, which for the PostgreSQL feed is the lifetime of the oldest
    /// transaction still open when the change landed
    /// (<c>docs/adr/ADR-0048-a-change-feed-advances-a-cursor.md</c>) and which no setting here
    /// shortens.
    /// </para>
    /// <para>
    /// It is not a redelivery interval. A change is re-offered only when the cursor was not
    /// committed past it, which is a crash rather than a timer.
    /// </para>
    /// </remarks>
    public TimeSpan ChangeScanInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How many changes one pass takes from each subscription.</summary>
    /// <remarks>
    /// Changes are run one at a time and in order, and the cursor is committed once at the end of
    /// the batch, so this is how much work one node does under one subscription lease and how
    /// much is re-read after a crash. What is not taken this pass is taken on the next.
    /// </remarks>
    public int ChangeReadBatchSize { get; set; } = 16;

    /// <summary>
    /// How far apart this deployment keeps its tenants' data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong><see cref="TenantIsolation.None"/> by default, and the default is the whole
    /// cost argument.</strong> A single-tenant deployment reaches no resolver, walks no claim,
    /// scopes no connection and issues no extra statement: the host reads this field once per
    /// invocation and branches away, which is the same bargain
    /// <c>ExecutionPlan.HasAuthorizedSteps</c> struck for the authorisation stage. Budget
    /// <strong>B2</strong> is untouched, and <c>EngineAllocationTests</c> is what says so
    /// rather than this paragraph.
    /// </para>
    /// <para>
    /// <strong>Setting it to <see cref="TenantIsolation.Row"/> makes a tenant mandatory.</strong>
    /// An invocation that names none is refused at admission with
    /// <c>tenant.required</c> rather than defaulted — a default tenant being the precise shape
    /// of a cross-tenant read (<c>docs/16 §9</c>) — and every journal connection is bound to
    /// the resolved tenant so the database refuses what the runtime somehow did not.
    /// </para>
    /// <para>
    /// <strong>It is a deployment setting, never a per-flow one.</strong> Tenancy does not
    /// appear in flow or capability logic at any level, which is what lets a tenant move from
    /// L1 to L3 without a code change — <c>docs/16 §2</c>'s main payoff.
    /// </para>
    /// </remarks>
    public TenantIsolation TenantIsolation { get; set; } = TenantIsolation.None;
}

/// <summary>
/// Validates <see cref="FlowXOptions"/> before the host starts.
/// </summary>
/// <remarks>
/// <para>
/// OWASP A05. The point is <em>when</em> this runs. Options validated lazily fail on
/// the first request after a deploy — a production incident, with traffic already
/// routed to the new pod. Validated at startup, the same mistake is a pod that never
/// becomes ready and a rollout that halts by itself.
/// </para>
/// <para>
/// Every problem is reported in one pass rather than the first one found. Fixing
/// configuration one error per deploy cycle is a loop nobody should be put through.
/// </para>
/// </remarks>
internal sealed class FlowXOptionsValidator : IValidateOptions<FlowXOptions>
{
    public ValidateOptionsResult Validate(string? name, FlowXOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ApplicationName))
        {
            failures.Add(
                $"{nameof(FlowXOptions.ApplicationName)} is required. It identifies this " +
                "application in the manifest, in traces and in audit records.");
        }

        if (options.MaxPooledContexts <= 0)
        {
            failures.Add(
                $"{nameof(FlowXOptions.MaxPooledContexts)} must be greater than zero; " +
                $"it is {options.MaxPooledContexts}. A pool of zero allocates a fresh " +
                "context per flow, which loses budget B2.");
        }

        if (options.DefaultDeadline <= TimeSpan.Zero)
        {
            failures.Add(
                $"{nameof(FlowXOptions.DefaultDeadline)} must be positive; it is " +
                $"{options.DefaultDeadline}. A zero deadline means every step starts " +
                "already expired, which presents as the service silently doing nothing.");
        }

        if (options.ShutdownDrainTimeout < TimeSpan.Zero)
        {
            failures.Add(
                $"{nameof(FlowXOptions.ShutdownDrainTimeout)} cannot be negative; it is " +
                $"{options.ShutdownDrainTimeout}.");
        }

        if (string.IsNullOrWhiteSpace(options.NodeName))
        {
            failures.Add(
                $"{nameof(FlowXOptions.NodeName)} is required. It is what a lease records as " +
                "its owner, and an unnamed owner makes 'who holds this instance' " +
                "unanswerable at exactly the moment it is asked.");
        }

        if (options.LeaseTtl <= TimeSpan.Zero)
        {
            failures.Add(
                $"{nameof(FlowXOptions.LeaseTtl)} must be positive; it is {options.LeaseTtl}. " +
                "A lease that has already expired when it is issued is not a lease.");
        }

        if (options.LeaseRenewalInterval <= TimeSpan.Zero)
        {
            failures.Add(
                $"{nameof(FlowXOptions.LeaseRenewalInterval)} must be positive; it is " +
                $"{options.LeaseRenewalInterval}.");
        }
        else if (options.LeaseRenewalInterval >= options.LeaseTtl)
        {
            failures.Add(
                $"{nameof(FlowXOptions.LeaseRenewalInterval)} ({options.LeaseRenewalInterval}) " +
                $"must be shorter than {nameof(FlowXOptions.LeaseTtl)} ({options.LeaseTtl}). " +
                "Renewing no sooner than the expiry means every renewal races the lapse it " +
                "exists to prevent; docs/11-Distributed-Runtime.md §3 asks for a third of it.");
        }

        if (options.RecoveryScanInterval <= TimeSpan.Zero)
        {
            failures.Add(
                $"{nameof(FlowXOptions.RecoveryScanInterval)} must be positive; it is " +
                $"{options.RecoveryScanInterval}. A zero interval is a scan loop with no " +
                "pause in it, which is a denial of service aimed at your own journal.");
        }

        if (options.RecoveryScanBatchSize <= 0)
        {
            failures.Add(
                $"{nameof(FlowXOptions.RecoveryScanBatchSize)} must be greater than zero; it " +
                $"is {options.RecoveryScanBatchSize}.");
        }

        if (options.TimerScanInterval <= TimeSpan.Zero)
        {
            failures.Add(
                $"{nameof(FlowXOptions.TimerScanInterval)} must be positive; it is " +
                $"{options.TimerScanInterval}. A zero interval is a sweep loop with no pause " +
                "in it, which is a denial of service aimed at your own journal.");
        }

        if (options.TimerScanBatchSize <= 0)
        {
            failures.Add(
                $"{nameof(FlowXOptions.TimerScanBatchSize)} must be greater than zero; it " +
                $"is {options.TimerScanBatchSize}. Zero is not 'timers disabled' — leave the " +
                "journal without an ITimerIndex for that.");
        }

        if (options.ScheduleScanInterval <= TimeSpan.Zero)
        {
            failures.Add(
                $"{nameof(FlowXOptions.ScheduleScanInterval)} must be positive; it is " +
                $"{options.ScheduleScanInterval}. A zero interval is a sweep loop with no pause " +
                "in it, which is a denial of service aimed at your own journal.");
        }

        if (options.ScheduleCatchUp < TimeSpan.Zero)
        {
            failures.Add(
                $"{nameof(FlowXOptions.ScheduleCatchUp)} cannot be negative; it is " +
                $"{options.ScheduleCatchUp}. Zero is a legitimate value and means 'never fire a " +
                "schedule late'; a negative one means nothing.");
        }

        if (options.ScheduleFireBatchSize <= 0)
        {
            failures.Add(
                $"{nameof(FlowXOptions.ScheduleFireBatchSize)} must be greater than zero; it is " +
                $"{options.ScheduleFireBatchSize}. Zero is not 'schedules disabled' — register " +
                "no schedule for that.");
        }

        if (options.BusScanInterval <= TimeSpan.Zero)
        {
            failures.Add(
                $"{nameof(FlowXOptions.BusScanInterval)} must be positive; it is " +
                $"{options.BusScanInterval}. A zero interval is a poll loop with no pause in it, " +
                "which is a denial of service aimed at your own broker.");
        }

        if (options.BusReceiveBatchSize <= 0)
        {
            failures.Add(
                $"{nameof(FlowXOptions.BusReceiveBatchSize)} must be greater than zero; it is " +
                $"{options.BusReceiveBatchSize}. Zero is not 'subscriptions disabled' — register " +
                "no subscription for that.");
        }

        if (options.BusMaxConcurrentPartitions <= 0)
        {
            failures.Add(
                $"{nameof(FlowXOptions.BusMaxConcurrentPartitions)} must be greater than zero; " +
                $"it is {options.BusMaxConcurrentPartitions}. Zero would serve no partition, " +
                "which is a subscription that reads nothing and reports nothing.");
        }

        if (options.BusMaxDeliveries <= 0)
        {
            failures.Add(
                $"{nameof(FlowXOptions.BusMaxDeliveries)} must be greater than zero; it is " +
                $"{options.BusMaxDeliveries}. Zero would dead-letter every message on its first " +
                "delivery, before anything had a chance to run it.");
        }

        if (options.ChangeScanInterval <= TimeSpan.Zero)
        {
            failures.Add(
                $"{nameof(FlowXOptions.ChangeScanInterval)} must be positive; it is " +
                $"{options.ChangeScanInterval}. A zero interval is a poll loop with no pause in " +
                "it, which is a denial of service aimed at your own journal.");
        }

        if (options.ChangeReadBatchSize <= 0)
        {
            failures.Add(
                $"{nameof(FlowXOptions.ChangeReadBatchSize)} must be greater than zero; it is " +
                $"{options.ChangeReadBatchSize}. Zero is not 'change subscriptions disabled' — " +
                "register no change subscription for that.");
        }

        if (options.MaxConcurrentRecoveries <= 0)
        {
            failures.Add(
                $"{nameof(FlowXOptions.MaxConcurrentRecoveries)} must be greater than zero; " +
                $"it is {options.MaxConcurrentRecoveries}. Zero is not 'recovery disabled' — " +
                "leave the journal without an IRecoveryIndex for that.");
        }

        // Refused at startup rather than downgraded at run time. A deployment that configured
        // Schema or Database and silently received Row would believe it had bought separation
        // it does not have — which is the "declared and inert" shape this work package exists
        // to remove, arriving through the very option meant to remove it. A pod that never
        // becomes ready is the cheaper failure, and it is this validator's whole purpose.
        if (options.TenantIsolation is not (TenantIsolation.None or TenantIsolation.Row))
        {
            failures.Add(TenantErrors.IsolationNotSupported(options.TenantIsolation).Message);
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
