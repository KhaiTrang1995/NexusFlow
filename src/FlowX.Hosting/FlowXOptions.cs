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

    /// <summary>
    /// Which background sweeps this host performs. Defaults to <see cref="HostSweeps.All"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The switch that makes the three-role topology in <c>docs/18-Cloud-Native.md §1</c>
    /// configurable rather than merely drawn. One image, three deployments:
    /// <c>HostSweeps.None</c> for an API host, <see cref="HostSweeps.Ingestion"/> for a
    /// worker, <see cref="HostSweeps.Durability"/> for a scheduler.
    /// </para>
    /// <para>
    /// This says what a host <em>should</em> do. Each scan still decides what it <em>can</em>
    /// do — a recovery sweep needs a journal whatever this says — and
    /// <see cref="HostSweeps"/> explains why the two are kept apart.
    /// </para>
    /// </remarks>
    public HostSweeps Sweeps { get; set; } = HostSweeps.All;

    /// <summary>
    /// How many items this node admits and has not yet finished. Unbounded by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The one thing that sheds.</strong> Every admission seam — <c>FlowBusScan</c>'s,
    /// <c>FlowStreamScan</c>'s, and both HTTP surfaces — takes a slot from
    /// <see cref="FlowAdmissionGate"/> around the run and refuses when there is none, in the
    /// shape the offering transport can act on: a broker keeps its backlog, a window keeps its
    /// checkpoint, a caller gets <c>429</c> with a <c>Retry-After</c>. Nothing is lost, because
    /// nothing shed was ever recorded as having run.
    /// </para>
    /// <para>
    /// <strong><see langword="null"/> rather than a sentinel, and unbounded rather than a
    /// number.</strong> A hosting option added to a release must not change what a running
    /// deployment does — <see cref="Sweeps"/>'s rule — and any default number here would be a
    /// guess at somebody else's hardware that silently caps their throughput on upgrade. Absent
    /// is the only honest default, and it costs nothing to read: the unbounded path allocates
    /// nothing and takes no interlocked operation (budget <strong>B6</strong>).
    /// </para>
    /// <para>
    /// <strong>It bounds admissions, not executions.</strong> A recovery resume and a delivered
    /// signal are work this node already accepted and journalled, so neither takes a slot —
    /// shedding them would strand an instance that has nowhere else to go. <c>FlowHost.InFlight</c>
    /// is the count that includes them, and it exists for the drain rather than for a ceiling.
    /// </para>
    /// </remarks>
    public int? MaxInFlightAdmissions { get; set; }

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

    /// <summary>How often a node runs a pass over its stream subscriptions.</summary>
    /// <remarks>
    /// A pass reads until the source is caught up or <see cref="StreamReadBudget"/> is spent, so
    /// on a busy stream this is the gap between passes rather than the latency of a record. It is
    /// not the latency of a <em>window</em>: a window closes when the watermark reaches its upper
    /// bound, and the watermark only moves when a record with a later event time arrives
    /// (<c>docs/adr/ADR-0056-the-watermark-is-observed-never-wall-clock.md</c>). No setting here
    /// closes a window that the data has not closed.
    /// </remarks>
    public TimeSpan StreamScanInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How many records may sit between the source and the windowing loop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the backpressure bound</strong>, and it is enforced by not reading rather
    /// than by blocking on a full buffer: <c>FlowStreamScan</c> computes the room left and issues
    /// no read when there is none, so a slow flow leaves the backlog in the source
    /// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/06-Execution-Engine.md">06 §10</a>).
    /// <c>StreamBackpressureTests</c> is what holds it to that, by counting the records the source
    /// was asked for while a deliberately slow consumer ran.
    /// </para>
    /// <para>
    /// Raising it buys throughput on a bursty source and costs exactly this many records of
    /// resident memory. It is not a batch size: a pass reads many times.
    /// </para>
    /// </remarks>
    public int StreamChannelCapacity { get; set; } = 256;

    /// <summary>
    /// How many records one subscription's open windows may hold at once.
    /// </summary>
    /// <remarks>
    /// <strong>The second half of the memory bound, and a refusal rather than a knob.</strong> A
    /// window is held until the watermark closes it, so a wide window over a fast stream is the
    /// one place this engine could grow without limit. Exceeding this stops the subscription with
    /// <c>stream.window_overflow</c>; it never evicts, because a window emitted without some of
    /// its records is an aggregate that is quietly wrong.
    /// </remarks>
    public int StreamMaxResidentRecords { get; set; } = 10_000;

    /// <summary>How many records one pass reads from one subscription before yielding.</summary>
    /// <remarks>
    /// Bounds how long one node holds a subscription lease, which is what lets another node take
    /// over a stream that is permanently behind. What is not read this pass is read on the next.
    /// </remarks>
    public int StreamReadBudget { get; set; } = 4096;

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

    /// <summary>
    /// The tenants a <c>[CronTrigger(PerTenant = true)]</c> schedule fans out over. Empty by
    /// default, which fires nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The one place a deployment has to name its tenants, and only because a cron
    /// occurrence knows nothing about who it is for.</strong> Every other tenanted path derives
    /// its tenant from the work in front of it — a claim, a schema, a broker field — and needs
    /// no list at all. This one is a fan-out, and a fan-out needs a set.
    /// </para>
    /// <para>
    /// <strong>Ignored where a store can answer better.</strong> At
    /// <see cref="TenantIsolation.Schema"/> the set is <c>tenant_schema</c>, so
    /// <c>AddFlowXPostgres</c> registers an <see cref="ITenantDirectory"/> over the registry and
    /// a tenant provisioned a moment ago gets its firing without a redeploy. This list is what
    /// answers at <see cref="TenantIsolation.Row"/>, where no such registry exists and a tenant
    /// that has never run a flow is indistinguishable from one that does not exist.
    /// </para>
    /// </remarks>
    public IList<string> Tenants { get; } = [];

    /// <summary>
    /// What each tenant is bounded to, so that one cannot starve another. Nothing, by default.
    /// </summary>
    /// <remarks>
    /// <strong>Read only where <see cref="TenantIsolation"/> is not
    /// <see cref="TenantIsolation.None"/>.</strong> Isolation and fairness are separate
    /// guarantees — the first stops a tenant reading another's rows and does nothing about a
    /// tenant consuming every slot — but there is nothing to be fair between on a deployment
    /// that resolves no tenant, so a single-tenant host never reaches this object at all.
    /// </remarks>
    public TenantFairness Fairness { get; } = new();

    /// <summary>
    /// Which region this deployment is, and which tenants may only be served by it. Nothing,
    /// by default.
    /// </summary>
    /// <remarks>
    /// <strong>A refusal at admission, and never a route.</strong> Setting
    /// <see cref="TenantResidency.Region"/> and pinning a tenant makes this deployment decline
    /// that tenant's work with <c>tenant.residency_refused</c> when the pin names somewhere
    /// else. It selects no store and forwards nothing — see <see cref="TenantResidency"/> for
    /// why that is the only thing a runtime can honestly offer here, and why it does not
    /// reopen ADR-0051.
    /// </remarks>
    public TenantResidency Residency { get; } = new();
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

        if (options.StreamScanInterval <= TimeSpan.Zero)
        {
            failures.Add(
                $"{nameof(FlowXOptions.StreamScanInterval)} must be positive; it is " +
                $"{options.StreamScanInterval}. A zero interval is a poll loop with no pause in it.");
        }

        if (options.StreamChannelCapacity <= 0)
        {
            failures.Add(
                $"{nameof(FlowXOptions.StreamChannelCapacity)} must be greater than zero; it is " +
                $"{options.StreamChannelCapacity}. Zero is not 'unbounded' and it is not " +
                "'disabled' — it is a channel nothing can be written to, and the whole of this " +
                "engine's memory bound is that this number exists.");
        }

        if (options.StreamMaxResidentRecords <= 0)
        {
            failures.Add(
                $"{nameof(FlowXOptions.StreamMaxResidentRecords)} must be greater than zero; it " +
                $"is {options.StreamMaxResidentRecords}. A window holds at least one record or " +
                "it is never opened.");
        }

        if (options.StreamReadBudget <= 0)
        {
            failures.Add(
                $"{nameof(FlowXOptions.StreamReadBudget)} must be greater than zero; it is " +
                $"{options.StreamReadBudget}. Zero is not 'stream subscriptions disabled' — " +
                "register no stream subscription for that.");
        }

        // Absent is unbounded and is the default; present and not positive is the one spelling
        // that cannot mean anything. Zero would admit nothing ever — a deployment that runs no
        // work at all, which nobody configures on purpose and which would present as the sweeps
        // silently doing nothing rather than as a misconfiguration.
        // A lifted comparison rather than a pattern and a conjunction, which is what keeps this
        // one branch like every other check here: null <= 0 is false, so "unset" falls through
        // to the unbounded default without a second test saying so.
        if (options.MaxInFlightAdmissions <= 0)
        {
            failures.Add(
                $"{nameof(FlowXOptions.MaxInFlightAdmissions)} must be greater than zero when it " +
                $"is set; it is {options.MaxInFlightAdmissions.GetValueOrDefault().ToString(System.Globalization.CultureInfo.InvariantCulture)}. " +
                "Leave it unset for no ceiling — that is the default, and zero is not a spelling " +
                "of it: zero sheds every item this node is ever offered.");
        }

        if (options.MaxConcurrentRecoveries <= 0)
        {
            failures.Add(
                $"{nameof(FlowXOptions.MaxConcurrentRecoveries)} must be greater than zero; " +
                $"it is {options.MaxConcurrentRecoveries}. Zero is not 'recovery disabled' — " +
                "leave the journal without an IRecoveryIndex for that.");
        }

        // Refused at startup rather than downgraded at run time. A deployment that configured a
        // level and silently received a weaker one would believe it had bought separation it
        // does not have — which is the "declared and inert" shape this work package exists to
        // remove, arriving through the very option meant to remove it. A pod that never becomes
        // ready is the cheaper failure, and it is this validator's whole purpose.
        //
        // Database stays here, and it is the one level whose absence is a decision rather than
        // an omission: it names docs/16 §2's L3/L4, a deployment per tenant, which the pod
        // serving it expresses as None against that tenant's own connection string. The
        // in-process reading of it is L2, and that is what Schema is. TenantErrors makes the
        // argument; this line is only where it is applied.
        //
        // What this validator CANNOT check is whether the store can serve the level — it sees
        // options and no services. FlowHost's constructor does that, against
        // FlowDurability.IsolationEnforced, and the two refusals are deliberately different
        // errors because they are repaired differently.
        if (options.TenantIsolation is TenantIsolation.Database)
        {
            failures.Add(TenantErrors.IsolationNotSupported(options.TenantIsolation).Message);
        }

        ValidateFairness(options, failures);
        ValidateResidency(options, failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>
    /// Refuses a fairness configuration that cannot do what it says at startup.
    /// </summary>
    /// <remarks>
    /// <strong>The first check is the one that matters.</strong> A deployment that declares a
    /// per-tenant bound on a host that resolves no tenant has configured a limit with no key to
    /// spend it under; nothing would be applied, nothing would say so, and the operator would
    /// believe the noisy neighbour was handled. That is the "declared and inert" shape the
    /// tenancy work exists to remove, so it fails the pod rather than the customer.
    /// </remarks>
    /// <summary>
    /// Refuses a residency configuration that could not refuse anything.
    /// </summary>
    /// <remarks>
    /// Both checks catch the same class of mistake as <see cref="ValidateFairness"/>'s first
    /// one: a control that is written down, reads as enforced, and cannot fire. A pin with no
    /// region to compare against would refuse every pinned tenant on every deployment, which
    /// looks like an outage rather than a misconfiguration; a pin on a host that resolves no
    /// tenant would refuse nobody, which looks like compliance.
    /// </remarks>
    private static void ValidateResidency(FlowXOptions options, List<string> failures)
    {
        if (!options.Residency.IsEnabled)
        {
            return;
        }

        if (options.TenantIsolation == TenantIsolation.None)
        {
            failures.Add(
                $"{nameof(FlowXOptions.Residency)} pins a tenant to a region and " +
                $"{nameof(FlowXOptions.TenantIsolation)} is None, so no tenant is ever resolved " +
                "and no pin could be applied. Declare an isolation level, or remove the pins — " +
                "a residency rule with nothing to key on is a compliance control that silently " +
                "permits everything.");
        }

        if (string.IsNullOrWhiteSpace(options.Residency.Region))
        {
            failures.Add(
                $"{nameof(FlowXOptions.Residency)} pins a tenant to a region and " +
                $"{nameof(TenantResidency.Region)} is not set, so this deployment does not know " +
                "where it is and every pinned tenant would be refused. Set the region this " +
                "deployment runs in.");
        }

        foreach (var (tenant, region) in options.Residency.Requirements)
        {
            if (string.IsNullOrWhiteSpace(region))
            {
                failures.Add(
                    $"{nameof(FlowXOptions.Residency)} pins tenant '{tenant}' to a blank " +
                    "region, which no deployment can ever match. Name a region, or leave the " +
                    "tenant unpinned.");
            }
        }
    }

    private static void ValidateFairness(FlowXOptions options, List<string> failures)
    {
        var fairness = options.Fairness;

        if (fairness.IsEnabled && options.TenantIsolation == TenantIsolation.None)
        {
            failures.Add(
                $"{nameof(FlowXOptions.Fairness)} declares a per-tenant bound and " +
                $"{nameof(FlowXOptions.TenantIsolation)} is None, so no tenant is ever resolved " +
                "and no bound could be applied. Declare an isolation level, or remove the " +
                "bounds — a limit with nothing to key on is a limit that silently does nothing.");
        }

        if (fairness.PermitsPerWindow < 0)
        {
            failures.Add(
                $"{nameof(TenantFairness.PermitsPerWindow)} cannot be negative; it is " +
                $"{fairness.PermitsPerWindow}. Zero is how a deployment declares no rate limit.");
        }

        if (fairness.PermitsPerWindow > 0 && fairness.Window <= TimeSpan.Zero)
        {
            failures.Add(
                $"{nameof(TenantFairness.Window)} must be positive; it is {fairness.Window}. " +
                "Permits granted over no time is an unbounded rate wearing a bound.");
        }

        if (fairness.QuotaPerWindow < 0)
        {
            failures.Add(
                $"{nameof(TenantFairness.QuotaPerWindow)} cannot be negative; it is " +
                $"{fairness.QuotaPerWindow}. Zero is how a deployment declares no quota.");
        }

        if (fairness.QuotaPerWindow > 0 && fairness.QuotaWindow <= TimeSpan.Zero)
        {
            failures.Add(
                $"{nameof(TenantFairness.QuotaWindow)} must be positive; it is " +
                $"{fairness.QuotaWindow}.");
        }

        if (fairness.MaxConcurrency < 0)
        {
            failures.Add(
                $"{nameof(TenantFairness.MaxConcurrency)} cannot be negative; it is " +
                $"{fairness.MaxConcurrency}. Zero is how a deployment declares no bulkhead.");
        }

        if (fairness.JournalWritesPerWindow < 0)
        {
            failures.Add(
                $"{nameof(TenantFairness.JournalWritesPerWindow)} cannot be negative; it is " +
                $"{fairness.JournalWritesPerWindow}. Zero is how a deployment declares no " +
                "journal write budget.");
        }

        if (fairness.JournalWritesPerWindow > 0 && fairness.JournalWriteWindow <= TimeSpan.Zero)
        {
            failures.Add(
                $"{nameof(TenantFairness.JournalWriteWindow)} must be positive; it is " +
                $"{fairness.JournalWriteWindow}. Rows granted over no time is an unbounded " +
                "write rate wearing a bound.");
        }

        if (fairness.JournalWritesPerWindow > 0 && fairness.JournalWriteBlock <= 0)
        {
            failures.Add(
                $"{nameof(TenantFairness.JournalWriteBlock)} must be positive; it is " +
                $"{fairness.JournalWriteBlock}. It is how many rows of credit one node draws " +
                "per round trip, so zero would be a budget nothing can ever spend.");
        }

        // Refused rather than rounded down. The shared bucket is denominated in blocks, so a
        // budget the block does not divide would silently enforce the next multiple below it —
        // a limit that does not mean what it says, which is the one failure this mechanism has
        // to be free of to be worth having.
        if (fairness.JournalWritesPerWindow > 0
            && fairness.JournalWriteBlock > 0
            && fairness.JournalWritesPerWindow % fairness.JournalWriteBlock != 0)
        {
            failures.Add(
                $"{nameof(TenantFairness.JournalWriteBlock)} ({fairness.JournalWriteBlock}) " +
                $"must divide {nameof(TenantFairness.JournalWritesPerWindow)} " +
                $"({fairness.JournalWritesPerWindow}). The shared budget is drawn a block at a " +
                $"time, so this deployment would enforce " +
                $"{fairness.JournalWriteBlocksPerWindow * fairness.JournalWriteBlock} rows per " +
                "window while declaring more.");
        }

        if (fairness.PerTenantScanShare < 0)
        {
            failures.Add(
                $"{nameof(TenantFairness.PerTenantScanShare)} cannot be negative; it is " +
                $"{fairness.PerTenantScanShare}. Zero is the page a sweep always asked for.");
        }

        foreach (var (tenant, weight) in fairness.Weights)
        {
            if (weight <= 0)
            {
                failures.Add(
                    $"{nameof(TenantFairness.Weights)}['{tenant}'] is {weight}. A weight of " +
                    "zero or less expresses 'never schedule this tenant', which is a suspension " +
                    "and not a weight — and it would be delivered as the starvation this " +
                    "setting exists to prevent.");
            }
        }
    }
}
