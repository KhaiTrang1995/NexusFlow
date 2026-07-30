# 06 — Execution Engine

> **Status:** Accepted · **Audience:** runtime contributors, advanced users
> **Answers:** how does a flow actually run, and how is it still correct after a
> process dies?

---

## 1. The compiled execution plan

`FlowX.Compiler` turns a `Define(...)` body into static data. Nothing about the
shape of a flow is decided at run time.

```csharp
// GENERATED — obj/generated/FlowX/PlaceOrderFlow.Plan.g.cs
internal static class PlaceOrderFlowPlan
{
    public static readonly ExecutionPlan Instance = new(
        FlowId: "order.place",
        Version: new SemVer(1, 2, 0),
        Profile: ExecutionProfile.Durable,
        Steps: new StepPlan[]
        {
            new(Id: 0, Capability: CapabilitySlot.OrderValidate,
                Compensation: CapabilitySlot.None, Policies: PolicyChain.Empty),
            new(Id: 1, Capability: CapabilitySlot.InventoryReserve,
                Compensation: CapabilitySlot.InventoryRelease, Policies: PolicyChain.Retry3),
            new(Id: 2, Capability: CapabilitySlot.PaymentCapture,
                Compensation: CapabilitySlot.None,  Policies: PolicyChain.PaymentGateway),
            new(Id: 3, Kind: StepKind.Emit, EventType: EventSlot.OrderPlaced),
        },
        Deadline: TimeSpan.FromSeconds(30));
}
```

The Flow Engine's inner loop is therefore an index walk over a
`ReadOnlySpan<StepPlan>` — no dictionary lookups, no delegate allocation, no
virtual dispatch beyond the capability call itself.

```mermaid
flowchart LR
    A["Define() body"] --> B["Compiler: symbol graph"]
    B --> C["Validation:<br/>contracts, cycles, determinism"]
    C --> D["ExecutionPlan<br/><i>static readonly</i>"]
    C --> E["Dispatch switch<br/><i>zero reflection</i>"]
    C --> F["Trigger bindings"]
    C --> G["flowx.manifest.json"]
    D --> H["Flow Engine at run time"]
    E --> H
```

---

## 2. The engines and their single responsibilities

| Engine | Owns | Does **not** own |
|---|---|---|
| **Trigger** | envelope normalisation, admission, dedup, input binding | business logic, retries |
| **Flow** | plan walking, state machine, compensation, deadline | policy semantics, I/O |
| **Policy** | stage chain execution, retry/breaker/cache/authz | which policies apply (compile-time) |
| **Capability** | resolution + invocation, scope management | orchestration |
| **Event** | outbox write, publication, schema check | transport specifics (plugin does that) |
| **Scheduler** | timers, cron, delayed signals, leader election | flow semantics |
| **Stream** | offsets, windows, watermarks, backpressure | per-record business logic |
| **Observability** | spans, metrics, log scopes, replay feed | exporting (OTel SDK does that) |
| **Plugin Host** | plugin lifecycle, capability negotiation, health | plugin behaviour |

Each engine is a separate namespace with an interface, a single production
implementation, and its own test class. "One reason to change" is verified by
the `NoCyclicDependencies` and layering fitness functions.

---

## 3. The execution loop

```csharp
// Simplified from FlowX.Runtime/Flow/FlowEngine.cs — the shape is normative.
public async ValueTask<FlowResult> ExecuteAsync(
    ExecutionPlan plan, FlowContext ctx, CancellationToken ct)
{
    using var flowSpan = _obs.StartFlow(plan, ctx);       // no-op when unobserved
    var completed = new StepCursor(plan.Steps.Length);    // stack-allocated bitmap

    for (var i = ctx.ResumeFromStep; i < plan.Steps.Length; i++)
    {
        ref readonly var step = ref plan.Steps[i];
        ctx.Deadline.ThrowIfExceeded();

        var result = await _policy.RunAsync(in step, ctx, _capabilities, ct);

        if (result.IsFailure)
            return await CompensateAsync(plan, ctx, completed, result.Error, ct);

        completed.Mark(i);
        if (plan.Profile is ExecutionProfile.Durable)
            await _journal.CommitStepAsync(ctx.InstanceId, i, result, ctx.State, ct);
    }

    return FlowResult.Success(plan.Project(ctx));
}
```

Three properties this shape guarantees:

1. **Resumability** — `ctx.ResumeFromStep` is the only difference between a fresh
   run and a recovery. There is no separate recovery code path to rot.
2. **Deadline propagation** — checked at every step boundary and passed into
   every policy; a flow cannot outlive its budget by more than one step.
3. **Compensation symmetry** — `completed` is the exact set to unwind, in
   reverse order.

A conditional does not change that shape; it changes only how `i` moves.
`When`/`Otherwise` compiles into the **same flat array** as everything else: a
`Branch` step carrying the index to continue at when its predicate is false, and
a `Jump` step closing the `then` block so a taken branch skips the alternative.
The loop therefore becomes a `while` whose index advances either by one or to a
target — the engine holds no branch stack, never recurses, and allocates nothing
to take a branch. Targets are validated forward and in range when the graph is
built, which is the only reason the loop is guaranteed to terminate: the DSL
cannot express a loop, so a backward target is always a layout bug rather than
something an author asked for.

`Switch` is the same shape with more destinations: one `Switch` step carrying a
target per case plus a default target, and a `Jump` closing each case block. The
loop still advances by one or to a target, still holds no branch stack, and still
allocates nothing — taking a case is one bounds check, one read out of the node's
`CaseTargets`, and one assignment to `i`. Every case target is validated forward
and in range alongside the default, because a termination proof that covered only
the arm nobody takes would be the wrong way round.

Predicates are evaluated through `IStepDispatcher.Evaluate`, which is
**synchronous and returns `bool`**; a switch's selector runs through
`IStepDispatcher.Select`, which is synchronous and returns the matching case's
position, or `-1` for none. An awaitable predicate would put a state machine on
the hot path and would invite exactly the IO `FLOWX1011` forbids; a signature
that cannot express IO is cheaper to enforce than a diagnostic that reports it.
`Select` returns an `int` rather than the value it selected for a related reason:
returning the value would mean returning it as `object`, which boxes an `enum` on
every switch a flow takes.

---

## 4. Execution profiles — the central trade-off

```mermaid
flowchart TD
    Q1{"Must the flow survive<br/>a process crash?"}
    Q1 -- no --> E["Ephemeral<br/>~1 µs/step, 0 alloc<br/>state in memory"]
    Q1 -- yes --> Q2{"Continuous input<br/>from a stream?"}
    Q2 -- yes --> S["Streaming<br/>checkpointed offsets<br/>windows + backpressure"]
    Q2 -- no --> Q3{"Does it wait for<br/>external signals or timers?"}
    Q3 -- yes --> D2["Durable + Suspendable<br/>zero resources while waiting"]
    Q3 -- no --> D1["Durable<br/>~1 ms/step<br/>journaled per step"]

    style E fill:#2e7d32,color:#fff
    style D1 fill:#1168bd,color:#fff
    style D2 fill:#1168bd,color:#fff
    style S fill:#6a1b9a,color:#fff
```

| | `Ephemeral` | `Durable` | `Streaming` |
|---|---|---|---|
| State | pooled in memory | journal, per step | checkpoint, per batch |
| Crash | lost; caller retries | resumes on another node | resumes from checkpoint |
| Overhead/step | ~1 µs | ~1 ms (group-committed) | amortised per batch |
| Suspend/resume | no | yes (timers, signals) | n/a |
| Determinism required | no | **yes** | yes within a batch |
| Typical use | queries, CRUD, validation APIs | payments, sagas, onboarding | ingestion, enrichment, aggregation |

> **Default is `Ephemeral`.** Durability is a cost you opt into, per flow. This
> is the main architectural difference from Temporal-style engines, which make
> everything durable and charge everything for it. See
> [ADR-0003](adr/ADR-0003-execution-profiles.md).

---

## 5. The determinism boundary

Durable flows are replayed. Replay is only correct if flow logic is
deterministic. FlowX draws the boundary explicitly:

```mermaid
flowchart LR
    subgraph det["Deterministic zone — replayed"]
        F["Flow Define() body"]
        R["Routing / branching decisions"]
        P["Projection / Return expression"]
    end
    subgraph nondet["Non-deterministic zone — journaled, never replayed"]
        C["Capability bodies (I/O)"]
        T["ctx.Clock"]
        I["ctx.NewId()"]
        RND["ctx.Random"]
    end
    det -->|"invokes"| nondet
    nondet -->|"result recorded in journal"| det
```

| Rule | Diagnostic | Severity in `Durable` |
|---|---|---|
| No `DateTime.Now/UtcNow`, `DateTimeOffset.Now/UtcNow` in flows or capabilities | `FLOWX1007` | Error |
| No `Guid.NewGuid()`, `Random.Shared` | `FLOWX1008` | Error |
| No mutable static state reachable from a flow | `FLOWX1009` | Error |
| Flow branching, step input mappings and the `Return` projection may only read `ctx.State` and step results | `FLOWX1011` | Error |
| Anything in `ctx.State` must be serialisable by a generated STJ context | `FLOWX1006` | Error |

In `Ephemeral` flows these drop to Info — there is no replay, so there is no
determinism obligation. The analyzer reads the flow's declared profile, so the
same rule set is strict exactly where it matters.

**`FLOWX1011` is the exception, and ships as a Warning in `Ephemeral` rather than
Info.** It is the only rule in this table that is implemented, and `Ephemeral` is the
only profile the runtime executes today, so Info would have made it invisible in every
build anyone can currently run — which is the state it was already in. See
[FLOWX1011](diagnostics/FLOWX1011.md#why-the-severity-depends-on-the-profile). The
remaining rules keep the Info stance for whoever implements them, at which point this
paragraph is worth revisiting as a set rather than one row at a time.

Its row covers the whole deterministic zone drawn above, and not only the "routing /
branching decisions" box: the `Return` projection named in the diagram, the `Switch` and
`ForEach` selectors, the `Emit` and `EmitOnFailure` payload maps, and the
`Step<TCapability, TStepIn>` and `SubFlow` input mappings the replay contract below
requires to be byte-identical. The full list, and what the rule provably cannot see, is on
[its page](diagnostics/FLOWX1011.md#which-delegates-it-covers).

**Replay contract:** replaying a completed durable instance must produce
byte-identical step inputs and identical control flow. Verified by
`ReplayDeterminismTest`, which executes a corpus of flows, journals them,
replays, and diffs.

---

## 6. Suspension: waiting without holding resources

```csharp
protected override void Define(IFlowBuilder<OnboardCustomer, OnboardResult> flow) => flow
    .Step<CreateAccount>()
    .Step<SendVerificationEmail>()
    .AwaitSignal<EmailVerified>(timeout: TimeSpan.FromDays(3))
        .OnTimeout(f => f.Step<ExpireOnboarding>())
    .Step<ActivateAccount>()
    .Return(ctx => new OnboardResult(ctx.Get<AccountId>()));
```

```mermaid
sequenceDiagram
    autonumber
    participant FE as Flow Engine
    participant J as Journal
    participant SE as Scheduler Engine
    participant EXT as External caller

    FE->>J: state=Suspended, awaiting EmailVerified, deadline +3d
    FE->>SE: register timer(instance 42, +3d)
    FE->>FE: release lease, free context to pool
    Note over FE: zero threads, zero memory held anywhere
    EXT->>FE: POST /flows/42/signals/EmailVerified
    FE->>J: append signal, state=Pending
    FE->>FE: any node leases instance and resumes at next step
    alt no signal within 3 days
        SE->>FE: timer fires
        FE->>FE: run OnTimeout branch → ExpireOnboarding
    end
```

A suspended instance costs one row. A million suspended onboardings cost a
million rows and zero compute — this is what makes long-running business
processes affordable.

---

## 7. Compensation semantics

```mermaid
flowchart TD
    S1["step 1 ✅"] --> S2["step 2 ✅"] --> S3["step 3 ❌ fails"]
    S3 --> C2["compensate step 2<br/>(own retry policy)"]
    C2 --> C1["compensate step 1"]
    C1 --> END["state = Compensated"]
    C2 -- "compensation exhausted" --> DLQ["state = CompensationFailed<br/>dead-letter + alert + manual replay"]
```

Rules:

1. Compensation runs in **strict reverse order** of *successfully completed*
   steps only. A failed step is never compensated (it did not take effect — or if
   it did, that step is not idempotent and that is a capability bug).
2. Each compensation carries its **own** policy chain. Compensation retry is
   more aggressive than forward retry by default (5 attempts vs 3).
3. Compensation is **best-effort but loud**: exhaustion produces
   `flowx_flow_compensation_failed_total`, a dead-letter record, and a documented
   operator recovery path.
4. Compensation is itself journaled, so a crash during compensation resumes
   compensation — never re-runs forward steps.
5. `Ephemeral` flows may declare compensation, but the guarantee is weaker: a
   process crash during compensation loses it. The analyzer warns
   (`FLOWX1012`) when a compensable flow is `Ephemeral`.

---

## 8. Error propagation

```mermaid
flowchart TD
    A["Capability returns Result.Fail(Error)"] --> B{"ErrorCategory"}
    B -- "Validation / NotFound / Forbidden" --> C["Terminal — no retry"]
    B -- "Conflict / Unavailable / Internal" --> D["Retry policy applies"]
    D --> E{"Retries exhausted<br/>or deadline hit?"}
    E -- no --> A
    E -- yes --> C
    C --> F{"Prior compensable steps?"}
    F -- yes --> G["Compensate"]
    F -- no --> H["FlowResult.Failure"]
    G --> H
    H --> I["Trigger maps to transport:<br/>HTTP 4xx/5xx · gRPC status · DLQ"]

    X["Capability throws Exception"] --> Y["Runtime catches at the capability boundary"]
    Y --> Z["Error{Internal, code=capability.unhandled}<br/>+ exception logged with trace id<br/>+ flowx_capability_unhandled_total"]
    Z --> D
```

An unhandled exception is never silently converted into a business error. It is
recorded as a **defect signal** with its own metric, because a capability
throwing is a bug in that capability.

---

## 9. Concurrency and parallel steps

```csharp
flow.Parallel(p => p
        .Branch<CheckCredit>()
        .Branch<CheckFraud>()
        .Branch<CheckSanctions>(),
     merge: MergeStrategy.AllMustSucceed)
    .Step<ApproveApplication>();
```

| Strategy | Semantics | Failure behaviour |
|---|---|---|
| `AllMustSucceed` | wait for all branches | first failure cancels siblings via linked token |
| `AllSettled` | wait for all, collect results | flow continues; branch errors available in context |
| `FirstSuccess` | race | remaining branches cancelled |
| `Quorum(n)` | first *n* successes | remainder cancelled |

Parallel branches share the flow's deadline and its context, but write to
**disjoint** context slots — enforced at compile time (`FLOWX1013`) so parallel
steps cannot race on shared state. In `Durable` flows, each branch commits its
own journal entry; the merge point is a single checkpoint.

---

## 10. Backpressure (Streaming profile)

```mermaid
flowchart LR
    src["Source<br/>Kafka partition"] --> ch["Bounded channel<br/>capacity = N"]
    ch --> proc["Flow instances<br/>degree = D"]
    proc --> sink["Sink / commit offset"]
    ch -. "channel full" .-> pause["Pause partition consumption"]
    pause -. "below low-water mark" .-> src
```

The Stream Engine never grows an unbounded queue. When the channel is full it
**pauses consumption at the source** (Kafka `Pause`, AMQP prefetch, MQTT flow
control) rather than buffering in memory. This is asserted by
`BackpressureConformanceTest`: a deliberately slow capability must reduce source
throughput, not increase memory.

Offset commit is **after** flow completion, giving at-least-once semantics;
combined with capability idempotency this yields effectively-once processing.

---

## 11. Cancellation and deadlines

| Source | Effect |
|---|---|
| Client disconnect (HTTP) | linked token cancelled; ephemeral flow aborts, durable flow continues (the business operation was accepted) |
| Flow deadline exceeded | `TimedOut`; compensation runs if compensable |
| Shutdown (SIGTERM) | stop admission → finish in-flight up to grace period → release leases → exit |
| Operator cancel | `flowx cancel --instance 42` → `Compensating` |

Deadlines are **absolute**, set at trigger time, and never reset by a retry. A
retry that would exceed the deadline is not attempted — the policy engine
subtracts elapsed time before arming the next attempt. This prevents the classic
"3 retries × 30 s timeout inside a 10 s SLA" failure.

---

## 12. What the engine deliberately does not do

| Not done | Why | Alternative |
|---|---|---|
| Distributed transactions (2PC) | availability cost, operational fragility | sagas + compensation |
| Automatic capability parallelisation | implicit concurrency is unreviewable | explicit `Parallel` |
| Dynamic flow modification at run time | breaks P4 and replay determinism | deploy a new flow version |
| Retrying non-idempotent capabilities by default | data corruption risk | `[Capability(Idempotent = true)]` opt-in gates retry |
| Cross-flow shared mutable state | coupling, unanalysable | events, or an explicit store inside a capability |

---

**Next:** [07 — Capability Model](07-Capability-Model.md)
