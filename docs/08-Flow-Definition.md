# 08 — Flow Definition

> **Status:** Accepted · **Audience:** application engineers
> **Answers:** what is the DSL, what control flow exists, and what does it compile into?

---

## 1. The shape of a flow

```csharp
[Flow("order.place", Version = "1.2.0", Profile = ExecutionProfile.Durable)]
[HttpTrigger("POST", "/api/v1/orders", Idempotent = true)]
[KafkaTrigger("orders.requested", Group = "order-placement")]
public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
{
    protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) => flow
        .Step<ValidateOrder>()
        .Step<ReserveInventory>().CompensateWith<ReleaseInventory>()
        .Step<CapturePayment>().WithPolicy(Policies.PaymentGateway)
        .Emit<OrderPlaced>()
        .Return(ctx => new OrderPlacedResult(ctx.Get<OrderId>(), ctx.Get<Capture>().Reference));
}
```

Three things are true of every flow:

1. **`Define` is a declaration, not a script.** It runs at most once (and often
   zero times — the compiler resolves it statically). It must be pure.
2. **The flow never names its transport.** `[HttpTrigger]` is metadata read by
   the compiler; the flow body cannot see it.
3. **The flow contains no business rules.** Rules live in capabilities.
   The flow expresses *order, condition, and recovery* — nothing else.

---

## 2. Data flow between steps

Steps do not pass values positionally. Each step's output is placed into the
typed context state bag, and the next step's input is bound from it.

```mermaid
flowchart LR
    IN["PlaceOrder<br/>(flow input)"] --> S1["order.validate"]
    S1 -->|"ValidatedOrder"| CTX[("FlowContext.State<br/>typed slots")]
    CTX --> S2["inventory.reserve"]
    S2 -->|"Reservation"| CTX
    CTX --> S3["payment.capture"]
    S3 -->|"Capture"| CTX
    CTX --> RET["Return projection<br/>→ OrderPlacedResult"]
```

Binding is resolved **at compile time**. If `CapturePayment` needs a
`CaptureRequest` and nothing in the context can produce one, the build fails
with a diagnostic that names the missing type and lists what *is* available:

```
error FLOWX1020: Step 3 'payment.capture' requires 'CaptureRequest' but the flow
                 context can only supply: PlaceOrder, ValidatedOrder, Reservation.
                 Add a mapping: .Step<CapturePayment, CaptureRequest>(ctx => ...)
                 See https://flowx.dev/diag/FLOWX1020
```

Explicit mapping when the shapes differ:

```csharp
.Step<CapturePayment, CaptureRequest>(ctx => new CaptureRequest(
    ctx.Get<ValidatedOrder>().Id,
    ctx.Get<ValidatedOrder>().Total,
    ctx.Input.PaymentMethod))
```

Both type arguments are written explicitly: C# cannot infer the mapping's result
type from a lambda body, and FlowX will not trade that away for a prettier call
site — an `object`-typed mapping would move a whole class of binding errors from
build time to run time, which is the opposite of what this platform is for.

**Where the mapped value lives: at the step, and nowhere else.** The mapping is
compiled into a `static readonly Func<FlowContext<TIn>, TStepIn>` field — built once
at type initialisation, like every other delegate on this builder — and the generated
dispatcher calls it and passes the result straight to the capability:

```csharp
var result = await _capturePayment
    .ExecuteAsync(StepInputs.Step2(Typed(ctx)), ctx, ct)
    .ConfigureAwait(false);
```

It is **not** written back into the state bag. The bag is keyed on `typeof(T)` and a
mapping exists precisely because nothing earlier put a `CaptureRequest` there, so
storing one would invent a producer [FLOWX1020](diagnostics/FLOWX1020.md) cannot see —
and two mapped steps of the same contract in one flow would overwrite each other's
input. As a local, each mapped step has its own delegate and its own value, and neither
is visible to anything else. The step's *output* still goes into the bag, exactly as an
unmapped step's does, which is what later steps and the `.Return(...)` projection bind
to.

Two consequences worth stating:

- **A compensation on a mapped step re-runs the mapping.** There is no bag entry to read,
  and the mapping is pure by construction ([FLOWX1011](diagnostics/FLOWX1011.md)), so it
  reproduces the input the step ran with. This is the same guarantee an unmapped
  compensation has, which reads the bag at unwind time rather than at the step.
- **`TStepIn` must be the capability's declared input**, or something implicitly
  convertible to it. C# infers it from the lambda and constrains it to nothing, so
  [FLOWX1029](diagnostics/FLOWX1029.md) checks it — otherwise a mismatch would arrive as
  a `CS1503` inside generated source.

The mapping costs nothing to reach: a cached static delegate invoked through
`FlowContext<TIn>`, which is a `readonly struct` over one reference. Budget **B2**
is unaffected — measured at 0 B in `EngineAllocationTests`. What the mapping's *body*
allocates is the author's own.

---

## 3. Control flow

### 3.1 Conditional

```csharp
flow.Step<AssessRisk>()
    .When(ctx => ctx.Get<RiskScore>().Value > 80, high => high
        .Step<RequestManualReview>()
        .Step<RecordReviewDecision>())
    .Otherwise(low => low
        .Step<AutoApprove>())
    .Step<NotifyApplicant>();
```

> The high-risk arm used to read `.AwaitSignal<ReviewDecision>(TimeSpan.FromHours(24))`,
> which is the honest shape for a review a person performs — and for two phases it did not
> compile, because [`FLOWX1031`](diagnostics/FLOWX1031.md) was an error on `AwaitSignal`.
> **It compiles and waits since WP-63.** The example keeps the two-flow shape because it is
> also a legitimate design and because this section is about conditionals rather than about
> waiting; a flow that wants the wait writes it, and [§3.5](#35-waiting) is the account. The
> one thing to know before moving it inside a conditional is that a suspension there suspends
> the **whole instance**, not the arm — a resume re-evaluates the predicate against the
> restored state bag and lands back in the same arm, which is what makes it work.

Conditions may read **only** `ctx.State`, `ctx.Input` and prior step results
(`FLOWX1011`). A condition that reads a clock, a static, or an external service
is a determinism violation and fails the build in `Durable` flows. In `Ephemeral`
flows it is a warning — see [FLOWX1011](diagnostics/FLOWX1011.md) for what the rule
detects, what it provably cannot, and why `ctx.UtcNow` is permitted where
`DateTime.UtcNow` is not.

**The rule is not only about `When`.** It applies to every builder method that takes
the flow context — the `Switch` and `ForEach` selectors, the `Return` projection, the
`Emit` and `EmitOnFailure` payload maps, and the `Step<TCapability, TStepIn>` and
`SubFlow` input mappings — and the analyzer checks all of them, naming the construct
it found, so a message reads "the `Return` projection in flow 'X'…" rather than "the
condition". [The list is on the rule's page](diagnostics/FLOWX1011.md#which-delegates-it-covers).

### 3.2 Branch on a value

```csharp
flow.Switch(ctx => ctx.Get<ValidatedOrder>().Channel)
    .Case(Channel.Retail,    b => b.Step<ApplyRetailPricing>())
    .Case(Channel.Wholesale, b => b.Step<ApplyWholesalePricing>().Step<RequireCreditCheck>())
    .Default(b => b.Fail(OrderErrors.UnsupportedChannel));
```

The selector obeys the same determinism rule as a `When` predicate: context,
input and prior step results only — enforced by `FLOWX1011`, which reports it as
"the `Switch` selector". It is evaluated **exactly once**, and the
cases are then tested against the value it produced, in declaration order, with
`EqualityComparer<TValue>.Default` — so an `enum`, an `int` and a `string` all
mean what you expect and none of them is boxed. The first match wins; a second
case with the same value is unreachable rather than an error, exactly as a
duplicated `When` would be.

`TValue` is inferred from the selector, so a `.Case(...)` whose value is of the
wrong type is a C# compile error rather than an arm that silently never matches.

**A value that matches no case, in a switch with no `Default`, continues after
the switch.** It is not an error and there is no diagnostic. Requiring a
`Default` would force `.Default(b => { })` onto every switch that legitimately
special-cases two channels out of five, and it still would not make the switch
exhaustive — an `enum` can hold a value no member declares, so exhaustiveness is
not a property the compiler can check for the general case. The rule is therefore
the one `When` already uses: a branch nobody took does nothing. Where doing
nothing is wrong, say so:

```csharp
.Default(b => b.Fail(OrderErrors.UnsupportedChannel))
```

That arm compiles to a real step and really rejects — see [§3.8](#38-failing)
for what `Fail` costs a flow that has already had effects.

`Switch` compiles into the same flat step array as everything else — one `Switch`
node carrying a target per case plus a default target, and a `Jump` closing each
case block. See [06 §3](06-Execution-Engine.md#3-the-execution-loop). The manifest
publishes the *shape* — that the flow branches, and what is in each arm — and
never the selector or the case values, because
[a manifest is structure, never values](adr/ADR-0005-manifest-as-build-artifact.md).

### 3.3 Parallel

```csharp
flow.Parallel(p => p
        .Branch<CheckCredit>()
        .Branch<CheckFraud>()
        .Branch<CheckSanctions>(),
     merge: MergeStrategy.AllMustSucceed)
    .Step<Decide>();
```

Branches write to disjoint context slots ([`FLOWX1013`](diagnostics/FLOWX1013.md)); they
share the flow's deadline; failure semantics per `MergeStrategy` — see
[06 §9](06-Execution-Engine.md#9-concurrency-and-parallel-steps).

A branch is either a single capability, `Branch<T>()`, or a whole chain,
`Branch(b => b.Step<A>().Step<B>())`; the two are the same concept with two spellings and
compile to the same shape. A `Parallel` with fewer than two branches is laid out **inline**,
with no fork node at all — running one thing concurrently is running it, and publishing a
decision the flow does not make would put a lie in the manifest.

```csharp
flow.Parallel(p => p
        .Branch<CheckCredit>()
        .Branch<CheckFraud>()
        .Branch(sanctions => sanctions
            .Step<LoadWatchlist>()
            .Step<CheckSanctions>()),
     merge: MergeStrategy.Quorum(2))
    .Step<Decide>();
```

`MergeStrategy` is a struct rather than an enum, so `Quorum(n)` can carry its number;
`default(MergeStrategy)` is `AllMustSucceed`. Under `AllSettled` the step after the join
reads each branch's outcome with `ctx.Get<ParallelOutcome>()`.

Two things are worth knowing before reaching for it. Branches are as concurrent as their
steps are — a branch whose steps all complete synchronously finishes before its sibling
starts, so a fork buys nothing for CPU-bound work. And a fork allocates, roughly 240 B per
branch; the zero-allocation budget covers the linear, conditional and switch paths and
deliberately does not cover this one.

### 3.4 Iteration

```csharp
flow.ForEach(ctx => ctx.Get<ValidatedOrder>().Lines,
        line => line.Step<ReserveLine>().CompensateWith<ReleaseLine>(),
        options: new ForEachOptions { MaxDegreeOfParallelism = 4, ContinueOnError = false })
    .Step<ConfirmReservation>();
```

Bounded by construction: `MaxDegreeOfParallelism` is required and capped by the
runtime — at 64, a constant rather than something derived from the build machine, so
identical source compiles to an identical graph everywhere. A bound of zero is a build
failure rather than a loop that never runs; a bound of a thousand is clamped rather than
refused, because asking for it is reasonable and refusing to run a correct flow is not.

`ForEach` compiles into the same flat step array as everything else — one `ForEach` node
carrying its join, and the body laid out immediately after it. **The body appears once,
however many elements the collection holds**, and the engine re-enters that span per
element. So the plan, the manifest and a rendered diagram are all independent of the size
of the data: a flow that reserves one line and one that reserves a thousand compile to the
same graph. What it costs is a pass over the body's step loop per element, which is the
price of not unrolling something the compiler cannot count.

The selector is typed `IReadOnlyList<TItem>` rather than `IEnumerable<TItem>` on purpose.
It is evaluated **exactly once**, before the first element, and the count it reports is
what bounds the loop — every other shape terminates because targets only ever point
forward, and this is the one that needs a second reason.

**The current element is scoped to its iteration.** It is not written into the flow's state
bag: that bag is keyed by type, so every element would take the same slot — a race above a
bound of one, and a leftover visible to every step after the loop even at one. Each pass
instead runs under a view of the context in which the element resolves by its own type, and
reads fall through to the flow for everything else. Writes do not: what a body step
*returns* is a value the flow produced, and goes in the shared bag. The consequence is worth
knowing before raising the bound above one — two elements writing the same output type
still race, and the winner is whichever finished last. That is the same modelling question
[`FLOWX1013`](diagnostics/FLOWX1013.md) asks of a `Parallel`'s branches; the runtime
guarantees only that the failure mode is a wrong value and never a corrupted dictionary.

Compensation is per element and unwinds in **strict reverse**. Each completed step is
recorded with the scope it completed in, so `ReleaseLine` undoes the line its own
`ReserveLine` reserved rather than whichever line the loop ended on. With
`ContinueOnError = false` — the default — the first failing element stops the iteration and
fails the flow with that element's own error; the elements that already succeeded are not
undone by the loop, they are undone by the flow's own unwind, in order, along with
everything before it. With `ContinueOnError = true` every element runs, the flow continues,
and the step after the loop reads `ctx.Get<ForEachOutcome>()` to decide what a partial
success means — the same bargain `MergeStrategy.AllSettled` offers a fork. A selector that
*throws* is not covered by `ContinueOnError`: a collection that was never read is not
something to continue past.

A loop allocates, roughly **32 bytes per element** — one scope object, and nothing per step
of the body. The zero-allocation budget covers the linear, conditional and switch paths and
deliberately does not cover this one, exactly as it does not cover a fork.

> **Not yet true: the journal entry.** "In `Durable` flows each iteration commits its own
> journal entry, so a crash resumes mid-collection rather than restarting it" describes P2.
> There is no journal yet, so today a crash restarts the flow. The shape is the one that
> makes it possible — each element is a bounded range with a scope of its own — but nothing
> persists it.

### 3.5 Waiting

> [!WARNING]
> **One of these three works, and the snippet below still does not compile clean.**
> *This box read "none of this works" until WP-63 (2026-08-01).* `.AwaitSignal<T>(timeout)`
> is honoured; `.Delay(...)` and `.OnTimeout(...)` are reported by
> [`FLOWX1031`](diagnostics/FLOWX1031.md), because there is no scheduler and no timer table.

```csharp
flow.AwaitSignal<PaymentConfirmed>(timeout: TimeSpan.FromMinutes(30))  // honoured
        .OnTimeout(f => f.Step<CancelPendingOrder>())                  // FLOWX1031, warning
    .Delay(TimeSpan.FromHours(1))            // FLOWX1031, warning — no step is produced
    .Step<SendFollowUp>();
```

| Construct | Intended | What the compiler and the engine do with it today |
|---|---|---|
| `.AwaitSignal<T>(timeout)` | suspends until the signal arrives, holding no thread, no memory and no lease | **This.** The instance is sealed `Suspended` at its resume frontier, the lease is given back, and `FlowHost.SignalAsync` resumes it through the same step loop a recovery scan uses. The plan carries the duration the author declared — it used to carry `TimeSpan.FromHours(1)` however long they wrote — and **nothing arms it**, because there is no timer |
| `.OnTimeout(block)` | the branch taken when the signal never arrives | The block is **discarded** — its steps reach no plan, no dispatcher and no `flowx.manifest.json`. A **warning** |
| `.Delay(duration)` | a durable timer holding no resources while it waits | **No step at all**, so the flow continues without waiting. A **warning** |

`AwaitSignal` and `Delay` are declared `Durable`-only, and using `AwaitSignal` in an
`Ephemeral` flow is [`FLOWX1017`](diagnostics/FLOWX1017.md) (error), because an
in-memory wait cannot survive a deployment. **`Durable` buys the wait**, which it did not
until WP-63 — [06 §6](06-Execution-Engine.md#6-suspension-waiting-without-holding-resources)
is the account, and `samples/workflow`'s `offer.accept` is the running example. A `Delay`
still has to be expressed outside the flow: a scheduled trigger replaces it, and a scheduled
sweep over instances that have been waiting too long replaces an `OnTimeout` branch.

**What the signal delivers reaches the steps after the wait.** The payload is seeded into the
state bag under the contract named in `.AwaitSignal<T>(...)`, so the next step binds it with
`ctx.Get<T>()` exactly as it binds an earlier step's output — and it is journaled by the
commit that records the suspension point, so a second crash does not lose it. That makes `T`
a state-bag contract in the sense [`FLOWX1006`](diagnostics/FLOWX1006.md) checks: it must be
declared by a source-generated `JsonSerializerContext`.

**A flow with an `[HttpTrigger]` may suspend, and gets two routes for it.** *This paragraph
opened "A flow with an `[HttpTrigger]` should **not** suspend — the generated endpoint answers
`200` with the flow's projected output and a suspended flow has none" until WP-64
(2026-08-01).* That was true of the transport and not of the flow, and it is what kept
`samples/workflow`'s `offer.accept` — the one flow in the repository that demonstrates durable
suspension — from declaring an address at all. The endpoint now answers `202` with the instance
and where to continue it, and the compiler emits one delivery route per signal the flow waits
for, read off the `.AwaitSignal<T>` calls in this `Define` body:

```
POST /api/v1/offers                                                 -> 202 { instanceId, awaiting }
POST /api/v1/offers/{instanceId:guid}/signals/offer.countersigned   -> 202 { instanceId, status }
```

The consequence is worth knowing before you add a wait to a flow that already has a trigger:
**its HTTP surface follows its body.** Adding an `.AwaitSignal<T>` adds routes and changes what
the existing route answers, which `flowx diff` reports as `FLOWX-DIFF-022`, Breaking. See
[ADR-0022](adr/ADR-0022-http-shape-of-a-suspending-flow.md).

**Two limits that have not moved.** An **inline** composed child may not suspend, because the
parent's composition row is written only when the child finishes, so a parent resumed past a
waiting child would compose a second child instance; it is refused as
`flow.suspension_inside_composition`, and a `Detached` child may wait. And the only budget
enforced on a waiting instance is the flow's own `[FlowDeadline]`, checked at the boundary
that decides whether to suspend.

**The wait reaches `flowx.manifest.json`.** An `AwaitSignal` step publishes `signal` — the
identity a sender addresses, which is the same string the generated delivery route carries —
and `timeout`, the declared wait folded to an ISO-8601 duration. The folding is deliberately
small: `TimeSpan.Zero`, the five `TimeSpan.From…` factories with a constant argument, and one
level of indirection through a named constant like `Waits.Countersignature`. Anything else — a
method call, a conditional, a configuration lookup — publishes **no** `timeout` rather than a
guess, because the plan carries the expression verbatim and a symbol name is not a duration to
a tool that has never seen the assembly ([ADR-0021](adr/ADR-0021-manifest-publishes-the-wait.md)).

### 3.6 Emitting events

```csharp
flow.Emit<OrderPlaced>(ctx => new OrderPlaced(
        ctx.Get<OrderId>(), ctx.Get<Capture>().Reference, ctx.Clock.UtcNow))
    .EmitOnFailure<OrderRejected>(ctx => new OrderRejected(ctx.Input.OrderId, ctx.Error!.Code));
```

Emission is transactional-outbox based in `Durable` flows: the event row is
written in the same transaction as the step commit, then published by the Event
Engine. At-least-once, never lost, never published before the step is durable. The
`partition_key` is the flow instance, so one instance's events reach a consumer in the
order it staged them; no global order is offered.

A member the contract declares `[Sensitive]` is redacted in the event body, by the same
structural rule that redacts it in a journal row — the payload is a `JournalPayload` and
its only exit replaces every marked member.

> **True for a `Durable` flow, with two conditions and one gap.** The chain is whole:
> the generated dispatcher's `DescribeStep` builds the body from the expression above,
> `FlowEngine.CommitStepAsync` puts it in `StepCommit.Outbox`, `plugins/FlowX.Postgres`
> writes it in the step's own transaction (WP-53), and `PostgresOutboxPublisher` drains
> it at-least-once in `partition_key` order (WP-56). A refused commit discards the event
> with the step.
>
> **Two conditions.** The flow must declare `Profile = Durable` — an ephemeral execution
> keeps no journal, so there is no transaction for the event to join — and `TEvent` must
> be declared by exactly one source-generated `JsonSerializerContext` in the compilation,
> because the body is written through it and never by reflection. Miss either and
> [`FLOWX1024`](diagnostics/FLOWX1024.md) says which at build time.
>
> **The gap is the broker.** `IEventPublisher` is declared and no plugin implements it, so
> "published" today means "handed to a publisher"
> ([ADR-0018](adr/ADR-0018-outbox-publication-and-ordering.md)).
>
> **`EmitOnFailure` is not distinguished yet.** It compiles to the same `Emit` node in the
> same position, so it publishes where it is written rather than on the failure path.

### 3.7 Sub-flows

```csharp
flow.Step<ValidateOrder>()
    .SubFlow<FulfilOrderFlow, FulfilOrder>(ctx => new FulfilOrder(ctx.Get<OrderId>()))
    .Step<NotifyCustomer>();
```

| Mode | Semantics | Ships |
|---|---|---|
| `SubFlow<T>` | synchronous; child shares parent's deadline and correlation; child failure fails the parent | yes |
| `SubFlow<T>(Detached)` | fire-and-forget; child gets its own deadline and lifecycle | yes |
| `SubFlow<T>(AwaitCompletion)` | parent suspends until child completes (durable only) | no — [`FLOWX1026`](diagnostics/FLOWX1026.md) |

`AwaitCompletion` is refused at build time under **every** profile. It needs a durable
**suspension point**, and there is none: WP-52 gave the runtime a journal, but a durable
flow still runs to completion inside one invocation — suspension is WP-63. A sub-flow has
no truthful degenerate form either: running it inline instead would change the parent's
deadline and failure semantics, and skipping it would drop business logic.
[`FLOWX1026`](diagnostics/FLOWX1026.md) says so rather than the compiler guessing.

> *This paragraph used to contrast `AwaitCompletion` with `AwaitSignal`, "which degenerates
> honestly into a step that completes". It did not degenerate honestly — a flow written to
> wait ran straight past the wait with a clean journal and a successful result, and the plan
> carried a timeout the author never wrote — so `AwaitSignal` was refused too, by
> [`FLOWX1031`](diagnostics/FLOWX1031.md), and the two constructs were in the same position.*
> **They are on opposite sides of a line again, and the line has moved.** WP-63 made
> `AwaitSignal` suspend; `AwaitCompletion` is still refused, and now for a reason that can be
> stated concretely rather than as "there is no suspension point". A parent records a
> composition as **one** journal row, written when the child finishes. A parent that waited
> for a child would have no row for the composition while it waited, so a parent resumed in
> the meantime would compose a *second* child instance and repeat every effect the first one
> had. The runtime refuses an inline child that suspends for exactly that reason —
> `flow.suspension_inside_composition` — and `AwaitCompletion` needs the parent's row to be
> splittable into "started" and "finished", which is a schema question and not an engine one.

**A sub-flow is one node, and the child's steps are not in the parent's graph.** The parent
compiles to a single `StepKind.SubFlow` with no target; the child has its own
`ExecutionPlan`, its own dispatcher, its own pooled context and its own compensation stack.
So the flat step array survives intact — the graph validation and the termination proof for
the parent are unchanged — but one array stops describing one execution. The manifest is
the same shape: the parent's entry names the child by id and does not inline its steps, so
a flow that composes a hundred-step child is the size of the flow its author wrote, and one
edit to a shared flow is one diff.

**Deadline and correlation.** An inline child inherits the parent's correlation, tenant and
idempotency key — one operation, one trace, one deduplication identity — and its budget is
`min(parent's remaining, child's own declared deadline)`. Composition can therefore only
ever *shorten*: a child cannot buy time its parent does not have, and a parent cannot buy
the child more than its own author allowed. A detached child keeps the correlation, which
is identity rather than lifetime, and starts its own budget from now.

**Compensation crosses the boundary in one direction.** If an inline child succeeds and the
*parent* then fails, the child's completed steps are undone — anything else would mean that
extracting steps into a sub-flow silently weakened the saga, which is exactly what
[`FLOWX1005`](diagnostics/FLOWX1005.md) advises people to do. Strict reverse survives: the
parent records the composition as one entry in its own stack, so a parent that completed
`A`, then a child that completed `X` and `Y`, then `B`, unwinds `B, Y, X, A` — the same
order the steps would have had written inline. A detached child compensates only itself; it
has its own lifecycle, so the parent's failure says nothing about it.

**The child's result does not flow into the parent's context, and that is a stated
limit rather than an oversight.** A sub-flow is composed for its *effects*: the parent
sees that it succeeded or failed, and the steps after it bind to what the *parent's* own
steps produced. Two ways to pass the child's answer up were considered and both were
refused. Calling the child's `.Return(...)` projection would mean the parent's generator
depending on a member of the child's *generated* partial class — which does not exist yet
when the parent is analysed in the same compilation, so the rule would work across an
assembly boundary and not within one. Copying the child's declared output contract out of
its context instead would work everywhere and would silently deliver a *different* value
from the one the child says it returns, in exactly the case where an author computed
something in `Return`. Neither is worth two behaviours where the DSL documents one. Until
there is a way to do it that is the same in both directions, a value the parent needs is a
value the parent's own steps should produce.

**A detached child is drained.** It is started from inside a step, so `FlowHost` never
counted it — `DrainAsync` waits for the engine's detached count as well as its own, because
a drain that reported success while a fire-and-forget saga was mid-way through reserving
inventory would leave it reserved when the kill arrived.

Sub-flow **cycles are a compile error** ([`FLOWX1021`](diagnostics/FLOWX1021.md)). The flow
graph is a DAG, always — within a compilation, which is the honest scope: an edge into a
referenced assembly cannot be followed, so `FlowEngine.MaxSubFlowDepth` bounds at run time
what the analyzer cannot see at build time. The rule's page says exactly what it proves and
what it does not.

### 3.8 Failing

```csharp
flow.Step<ValidateOrder>()
    .Step<ReserveInventory>().CompensateWith<ReleaseInventory>()
    .Switch(ctx => ctx.Input.Channel)
        .Case(Channel.Retail,    b => b.Step<ApplyRetailPricing>())
        .Case(Channel.Wholesale, b => b.Step<ApplyWholesalePricing>())
        .Default(b => b.Fail(OrderErrors.UnsupportedChannel))
    .Step<CapturePayment>();
```

`.Fail(error)` **terminates the flow with a business error**. It is the only
terminal step: control does not continue past it, so the flow's result is that
error and the steps after it in the same block are unreachable — the compiler
reports [`FLOWX1027`](diagnostics/FLOWX1027.md) and does not compile them, because
a plan, a manifest and a diagram listing work the flow can never do are three
contracts that lie.

**The completed compensable steps unwind, exactly as they would on a capability
failure.** A rejection is a decision rather than an accident, and it is tempting
to read that as a clean exit with nothing to undo. It is not: by the time the
`Default` arm above rejects the request, `inventory.reserve` has already reserved
stock, and the reservation is just as real as it would be after a declined
payment. A saga's guarantee is about what *happened*, not about who decided it.
The point is sharper than an analogy — the advice while `Fail` was unimplemented
was "spell an unsupported value out as a capability that returns
`Result.Fail(...)`", and if `Fail` did not unwind, replacing that workaround with
the feature it stood in for would silently weaken every saga that took it.

Mechanically there is no special case at all: the generated dispatcher hands the
engine `StepOutcome.Failed(error)` from the same call a capability's own failure
comes back on, so the step loop cannot tell the two apart.

**The error is a value, so it stays in compiled code.** The expression you write
becomes a `static readonly Error` field on the generated partial class — built
once, so rejecting a request allocates nothing at the moment the flow is already
about to unwind — with a `#line` directive back to the line you wrote it on.
`flowx.manifest.json` records `"kind": "Fail"` and nothing else: an `Error`
carries a message, the messages in real systems interpolate order numbers and
SKUs, and
[a manifest is structure, never values](adr/ADR-0005-manifest-as-build-artifact.md).
That is the same line the capability error catalogue draws when it publishes a
code and a category and never a message; publishing the *code* here would need a
field the committed schema's step object does not have.

A rendered diagram draws a `Fail` as a double circle — a final state — with no
edge out of it.

---

## 4. The full builder surface

| Method | Purpose | Profiles |
|---|---|---|
| `.Step<TCapability>()` | invoke a capability, binding its input from the context | all |
| `.Step<TCapability, TStepIn>(map)` | invoke with an explicit input mapping | all |
| `.CompensateWith<T>()` | register the inverse of the previous step | all — but weak outside `Durable`, and [`FLOWX1012`](diagnostics/FLOWX1012.md) warns when it is |
| `.WithPolicy(policy)` | attach a policy set to the previous step | all |
| `.When(pred, then).Otherwise(else)` | conditional | all |
| `.Switch(sel).Case(v, b).Default(b)` | value branch | all |
| `.Parallel(branches, merge)` | concurrent branches | all |
| `.ForEach(sel, body, options)` | bounded iteration | all |
| `.SubFlow<TFlow>(map, mode)` | compose flows | all |
| `.Emit<TEvent>(map)` | publish a domain event | all |
| `.EmitOnFailure<TEvent>(map)` | publish on failure path | all |
| `.AwaitSignal<T>(timeout)` | external wait | Durable, and **honoured**: the instance suspends and a signal resumes it. The declared timeout reaches the plan and nothing arms it |
| `.OnTimeout(b)` | the branch taken when the signal never arrives | Durable — but **not honoured**: [`FLOWX1031`](diagnostics/FLOWX1031.md) warns, and the block is discarded |
| `.Delay(duration)` | durable timer | Durable — but **not honoured**: [`FLOWX1031`](diagnostics/FLOWX1031.md) warns, and no step is produced |
| `.Window(spec)` / `.Aggregate(...)` | stream windowing | Streaming |
| [`.Fail(error)`](#38-failing) | terminate with a business error, unwinding what completed | all |
| `.Return(projection)` | produce the flow output | all |

Deliberately **absent**: `.Do(lambda)`. Arbitrary inline code inside a flow would
be invisible to the manifest, untestable in isolation and undetectable by
determinism analysis. If it is worth executing, it is worth naming — make it a
capability.

---

## 5. What the flow compiles into

```mermaid
flowchart TB
    subgraph src["Source"]
        A["PlaceOrderFlow.Define()"]
    end
    subgraph gen["Generated (obj/generated/FlowX/)"]
        B["PlaceOrderFlow.Plan.g.cs<br/>static ExecutionPlan"]
        C["PlaceOrderFlow.Dispatch.g.cs<br/>switch over capability slots"]
        D["PlaceOrderFlow.Http.g.cs<br/>endpoint + binder + OpenAPI"]
        E["PlaceOrderFlow.Kafka.g.cs<br/>consumer registration"]
        F["PlaceOrderFlow.Json.g.cs<br/>STJ serializer context"]
        G["flowx.manifest.json fragment"]
    end
    A --> B & C & D & E & F & G
```

Generated code is written to disk in readable form (`EmitCompilerGeneratedFiles`
is on by default in FlowX projects). Debugging a FlowX application means reading
ordinary C#, not guessing at a black box — a direct mitigation for risk R1 in
[05 §11](05-Architecture.md#11-risks-and-technical-debt).

---

## 6. Visualising a flow

```bash
flowx graph --flow order.place --format mermaid
```

```mermaid
flowchart TD
    T1(["HTTP POST /api/v1/orders"]) --> S1
    T2(["Kafka orders.requested"]) --> S1
    S1["order.validate"] --> S2["inventory.reserve<br/><i>retry×3</i>"]
    S2 --> S3["payment.capture<br/><i>timeout 2s · breaker</i>"]
    S3 --> E1[["emit order.placed"]]
    E1 --> R(["OrderPlacedResult"])
    S2 -. "on failure" .-> C2["inventory.release"]
    S3 -. "on failure" .-> C2
    C2 --> F(["Compensated"])

    style S3 fill:#c62828,color:#fff
    style C2 fill:#ef6c00,color:#fff
```

This diagram is **generated from the manifest**, so it cannot be out of date.
Every diagram in every FlowX application's documentation is produced this way.

---

## 7. Why C#, not YAML

The natural instinct for a flow engine is a YAML or JSON DSL. FlowX rejects it
as the *source of truth* — see [ADR-0010](adr/ADR-0010-csharp-dsl-over-yaml.md).

| Concern | C# DSL | YAML DSL |
|---|---|---|
| Type safety between steps | compile error | runtime error, in production |
| Refactoring (rename a contract) | IDE-wide, safe | find-and-replace and hope |
| Go-to-definition, find-references | native | none |
| Debugging | breakpoints in generated code | engine internals |
| Determinism analysis | Roslyn analyzers | none |
| Diffing in code review | ordinary diff | ordinary diff (tie) |
| Editable by non-developers | no | yes |
| Hot-reload without deploy | no | yes |

The last two rows are real advantages, and FlowX serves them differently:
`flowx graph --format yaml` exports the graph, and FlowX Studio renders and
scaffolds from it. But the compiled C# remains authoritative. **Configuration
selects adapters; it never redefines business meaning.**

---

## 8. Flow anti-patterns

| Anti-pattern | Why it is wrong | Do instead |
|---|---|---|
| Business rules inside `.When(...)` predicates | invisible to the manifest, untestable | a capability returning a decision type |
| A flow with 20 steps | unreadable, one failure domain | sub-flows by bounded context |
| Inheriting from another flow | hidden control flow (`FLOWX1005`) | extract shared steps into a sub-flow |
| A flow that only calls one capability | ceremony with no benefit | expose the capability directly with a trigger |
| Using `ctx.Trigger` to branch | breaks P3 transport agnosticism (`FLOWX1003`) | put the difference in the input contract |
| `Durable` for a read query | 1000× the cost for zero benefit | `Ephemeral` |
| `Ephemeral` for a payment saga | data loss on deploy | `Durable` |

---

**Next:** [09 — Trigger Model](09-Trigger-Model.md)
