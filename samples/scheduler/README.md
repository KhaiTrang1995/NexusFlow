# Sample — Scheduled and recurring work

**Claim proved:** cron work is an ordinary flow. Overlap policy, jitter, missed-fire
recovery, per-tenant fan-out and DST correctness are platform services, not
job-framework glue.

> [!IMPORTANT]
> **Two of this page's claims were wrong rather than merely unbuilt, and both are
> corrected below rather than implemented around.**
>
> **There is no leader election, and there will not be one.**
> [ADR-0031](../../docs/adr/ADR-0031-an-occurrence-names-the-instance-it-starts.md)
> refuses it: an election has to be *held*, a dead leader has to be *detected*, and
> hand-over has to *complete* — three mechanisms, each with a window in which a
> schedule fires twice or not at all, and a leader that has lost its lease without
> noticing fires anyway. What ships needs no coordination at all and is stronger.
> The [section below](#one-occurrence-one-run-no-leader) is the mechanism that
> replaced the sequence diagram this page used to draw.
>
> **The flow could not have compiled as printed.** It declared
> `Flow<ReconciliationRequest, ReconciliationReport>`, and `FLOWX1038` refuses a
> `[CronTrigger]` on a flow whose input is not `ScheduledFire`. A firing carries no
> body; nobody sends a reconciliation request, a night arrives.

## Running it

```bash
FLOWX_POSTGRES_CONNECTION="Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=postgres" \
FLOWX_SAMPLE_SCHEDULE_CRON="* * * * *" FLOWX_SAMPLE_SCHEDULE_SCAN=00:00:01 \
dotnet run --project samples/scheduler
```

**PostgreSQL is not optional here**, and that is the sample's point rather than a
dependency it happens to have. What makes one occurrence one run across a whole
fleet is `flow_instance`'s primary key; with no journal the sweep is disabled
outright rather than firing once per replica in silence. There is no HTTP endpoint
and no principal — nothing calls this application, a cron expression starts it, and
the capabilities run under `Authorization.Internal` with no request in the path.

`0 2 * * *` is the declared schedule and what the manifest publishes. `Program.cs`
registers a *second*, denser schedule from the environment for a demonstration
rather than overriding the first, so the declaration a reader sees stays the
declaration that runs.

## The flow

```csharp
[Flow("reconciliation.daily", Version = "1.0.0", Profile = ExecutionProfile.Durable)]
[CronTrigger("0 2 * * *",
    TimeZone   = "Europe/Berlin",       // DST-correct; never runs twice on the fall-back night
    Overlap    = OverlapPolicy.Skip,    // yesterday's run still going? skip today's
    MissedFire = MissedFirePolicy.RunOnce,
    Jitter     = "PT120S",              // spread load across tenants
    PerTenant  = true)]                 // fan out: one instance per active tenant
public sealed partial class DailyReconciliationFlow : Flow<ScheduledFire, ReconciliationReport>
{
    protected override void Define(IFlowBuilder<ScheduledFire, ReconciliationReport> flow) => flow
        .Step<LoadLedgerSnapshot>()
        .Step<LoadBankStatement>().WithPolicy(Policies.ExternalRead)
        .Parallel(p => p
            .Branch(exact => exact.Step<MatchByReference, Reconcilable>(ctx =>
                new Reconcilable(ctx.Get<LedgerSnapshot>(), ctx.Get<BankStatement>())))
            .Branch(fuzzy => fuzzy.Step<MatchByAmountAndDate, Reconcilable>(ctx =>
                new Reconcilable(ctx.Get<LedgerSnapshot>(), ctx.Get<BankStatement>()))),
         merge: MergeStrategy.AllSettled)
        .Step<ProduceReport, MatchSet>(ctx => new MatchSet(
            ctx.Get<LedgerSnapshot>(), ctx.Get<ReferenceMatches>(), ctx.Get<HeuristicMatches>()))
        .Emit(ctx => new ReconciliationCompleted(/* … */))
        .Return(ctx => ctx.Get<ReconciliationReport>());
}
```

The occurrence arrives as the flow's input and is journalled on
`flow_instance.input` like any other trigger's payload
([ADR-0033](../../docs/adr/ADR-0033-a-scheduled-flows-input-is-its-occurrence.md)),
because `FLOWX1007` and `FLOWX1011` forbid the flow reading a clock to work out
which occurrence it is. A run at 02:41 therefore reconciles the day that ended at
02:00, and so does a resumed one.

The mapping lambdas are what let one capability bind two earlier outputs; a
capability takes exactly one input contract, and each branch writes its own so that
two threads never share a slot of the state bag (`FLOWX1013`).

**The same capabilities are reachable on demand — but not from this flow.** A
schedule supplies its own payload and an HTTP endpoint binds a caller's, so one
flow cannot serve both and `FLOWX1038` says so. Running reconciliation manually
means a second flow over the same capabilities, which is the honest shape of
"transport-free business logic" and the limit
[09 §3](../../docs/09-Trigger-Model.md) records.

## One occurrence, one run, no leader

```mermaid
sequenceDiagram
    autonumber
    participant S1 as scheduler-1
    participant S2 as scheduler-2
    participant J as Journal + leases (PostgreSQL)

    Note over S1,S2: both compute 02:00 from the same expression, talking to nobody
    S1->>S1: id = uuidv8(sha256(flow ␀ version ␀ cron ␀ zone ␀ 02:00 ␀ tenant))
    S2->>S2: same inputs, same id
    S1->>J: acquire(id) → token 1, then StartAsync(id)
    S2->>J: acquire(id) → refused, held
    Note over S2: contended, not failed —<br/>the expected answer on n−1 nodes
    Note over S1: 💥 scheduler-1 dies mid-run
    S2->>J: recovery sweep takes the abandoned instance over at token 2
    Note over S2: the occurrence is not re-fired;<br/>the primary key already holds it
```

Nothing is elected and nothing is renewed at the *schedule* level. The lease is the
fast refusal while the winner runs and the primary key is the permanent one — which
is what a node restarted an hour later meets, and what a TTL cannot give.

## Policy semantics

| Option | Values | What it prevents |
|---|---|---|
| `Overlap` | `Skip` \| `Queue` \| `Concurrent` | a long run stacking on itself until the system dies |
| `MissedFire` | `Skip` \| `RunOnce` \| `RunAll` | a 2-hour outage producing 120 replayed minute-jobs at once |
| `TimeZone` | IANA id | the twice-yearly DST bug (double-run or skipped run) |
| `Jitter` | ISO-8601 duration | 500 tenants all hitting the same downstream at 02:00:00 |
| `PerTenant` | bool | writing your own tenant loop, and forgetting isolation inside it |

**`Overlap` is decided against the journal, never against a lease.** A lease answers
"is a node holding this *right now*", which is false for the whole window between a
node dying and a recovery sweep taking its instance over — so an overlap policy
built on leases would start a second run beside the resumed first, which is the
exact stacking it exists to prevent. `Skip` drops the occurrence and counts it;
`Queue` defers it and fires it when the run in front finishes; `Concurrent` does not
ask. A `Skip` schedule whose last firing is *suspended* stays overlapping until that
instance resolves, which is the honest reading of "the previous run is still going".

**`Jitter` is derived from the firing and never drawn at random**
([ADR-0059](../../docs/adr/ADR-0059-schedule-jitter-is-derived-from-the-firing.md)).
With no leader, *n* nodes race for one occurrence — so *n* independent random delays
fire at min(*n* draws), and the spread collapses towards zero exactly as the fleet
grows large enough to need it. The offset is a function of the instance id, so every
node computes the same instant and the firing moves as a unit. It changes *when a
firing is acted on* and nothing else: the occurrence, the derived id and the
`ScheduledFire` the flow binds are all un-jittered, because an id that moved when a
deployment widened its window would re-fire the whole catch-up horizon. A value that
is not a positive ISO-8601 duration is `FLOWX1045` at build time.

## Tests

`tests/Scheduler.Tests`. The fleet and overlap tests need a real PostgreSQL: they
are about what a *store* refuses, and an in-memory double would assert that against
code written for the test.

```csharp
[Fact]
public async Task FiveNodesFireOneOccurrenceOnce()
{
    await using var cluster = await SchedulerCluster.CreateAsync(Cancellation.Token);
    var nodes = cluster.Nodes(5);

    cluster.Clock.Advance(TimeSpan.FromHours(1));

    var reports = await Task.WhenAll(nodes.Select(n => n.RunOnceAsync(Cancellation.Token).AsTask()));

    (await cluster.InstanceCountAsync(Cancellation.Token)).ShouldBe(1);
    reports.Sum(static r => r.Contended).ShouldBe(4);
}

[Fact]
public async Task SkipDropsAnOccurrenceWhosePredecessorIsStillRunning()
{
    await using var bank = new GatedBank();          // holds the flow inside the bank read
    await using var cluster = await SchedulerCluster.CreateAsync(bank, Cancellation.Token);

    cluster.Register(SchedulerCluster.Minutely, OverlapPolicy.Skip);
    var fleet = cluster.Nodes(3);

    cluster.Clock.Advance(TimeSpan.FromMinutes(1));
    var overrunning = fleet[0].RunOnceAsync(Cancellation.Token).AsTask();
    await bank.EnteredAsync(Cancellation.Token);     // the run is genuinely still going

    cluster.Clock.Advance(TimeSpan.FromMinutes(1));

    (await fleet[1].RunOnceAsync(Cancellation.Token)).Skipped.ShouldBe(1);
}
```

**There is no `KillLeader` and no `SchedulerTestHost.WithVirtualTime()`**, and this
page used to print both. There is nothing to kill — a node holds no schedule-level
lease, so its death is not an event a schedule can observe — and what a replacement
pod *is*, is a fresh `FlowScheduleScan` over the same stores: `cluster.Replacement()`.
Time is a `FlowTestClock` the sweep reads through `IClock`, exactly as it does in
production. The overrun is a gate rather than a sleep, because a 25-hour run
simulated with a delay is a race on a loaded machine.

## Things to try

1. Set `Overlap = OverlapPolicy.Concurrent`, `FLOWX_SAMPLE_BANK_LATENCY=00:02:00`
   and `FLOWX_SAMPLE_SCHEDULE_CRON="* * * * *"` — watch instances stack, and see why
   `Skip` is the default.
   `ConcurrentLetsTheNextOccurrenceStartBesideTheRunningOne` is the same thing
   asserted.
2. Stop the scheduler for three hours and restart: compare `MissedFire` values
   `Skip`, `RunOnce` and `RunAll`. Beyond `FlowXOptions.ScheduleCatchUp` — one day
   by default — the firings outside the horizon are lost, with nothing to report
   them.
3. Set `FLOWX_SAMPLE_JITTER=PT30S` with several tenants and read
   `flow_instance.created_at`: the firings are spread across the window, and the
   same tenant lands in the same place every night.
4. Set the time zone to `America/New_York` and advance through a DST boundary — the
   02:00 job neither runs twice nor disappears.
   `TheAutumnFoldProducesOneFiringAndNotTwo` and
   `TheSpringGapProducesOneLateFiringAndNotNone` pin both directions.
