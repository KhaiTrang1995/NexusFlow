# 23 — Testing Strategy

> **Status:** Accepted · **Audience:** contributors, application authors
> **Answers:** what does a FlowX application test look like at each level, and which of those levels does the shipped kit actually support?

---

> **This document did not exist while the testing model was being taught.** The pyramid
> below lived in [07 §8](07-Capability-Model.md), the kit lived in
> [19 §6](19-SDK.md), and `CONTRIBUTING.md` taught a third shape against an API that had
> never been written. Three partial accounts in three places is how a documented API
> nobody had built survived for two phases. This is the one account; 07 and 19 point here.

---

## 1. The rule

> **A test's level is decided by what it is a statement about, not by how convenient the
> tooling is.**

A capability is a business rule. A flow is order, condition and recovery. An endpoint is
a status code, a media type and a wire shape. Those are three different claims, and a
test that needs a web server to observe the second one is not testing the second one — it
is testing the third and hoping.

That rule is what this document is for, and until WP-49 it was unenforceable: there was no
way to run a flow without a host, so every flow-level property was asserted through HTTP
or not at all.

---

## 2. What ships

`FlowX.Testing`, referenced from a test project:

| Type | For |
|---|---|
| `TestCapabilityContext` | testing a capability as a plain class |
| `TestFlowContext` | testing a step dispatcher or a `.Return(…)` projection |
| `FlowTestHost` | running a compiled flow with capabilities substituted |
| `FlowTestClock` | a clock a test moves by hand |
| `FlowTestRun`, `FlowTestTrace` | what a run produced, and what it did |

Nothing else. In particular there is no mock framework, no journal, no transport and no
virtual time — [§5](#5-what-is-deliberately-not-here) says why for each.

---

## 3. The pyramid

| Level | Subject | Infrastructure | Kit | Target share |
|---|---|---|---|---|
| Unit | one capability | none | `TestCapabilityContext` | ~70 % |
| Flow | one flow, capabilities substituted | none | `FlowTestHost` | ~20 % |
| Integration | a capability against a real adapter | Testcontainers | — | ~8 % |
| Conformance | trigger → flow → journal | Testcontainers | — | ~2 % |

The bottom two rows are supported today. The top two are not, and are not claimed to be:
there is no integration harness in this package
([§5](#5-what-is-deliberately-not-here)), and nothing runs the `trigger → flow → journal`
path the conformance row names.

*This paragraph used to say "there is no journal to conform against", and that half is now
wrong.* `tests/FlowX.Conformance.Tests` (WP-51) defines what an `IFlowJournal` and an
`ILeaseStore` must do, and a store either passes it or fails it. What does not exist is
anything for this pyramid's conformance row to run against: no store outside the in-memory
reference sitting beside the suite, and no Testcontainers. *A second half of this paragraph
also expired, at WP-52 (2026-07-31): there **is** now an execution path that reaches a
journal — `FlowX.Runtime` reads `ExecutionProfile` and a `Durable` flow commits a step
boundary.* `trigger → flow → journal` still cannot run end to end, because the trigger end
has one transport and the journal end has no store. Defined and unreachable is a different
state from absent, and the row stays unsupported either way.

### 3.1 Unit — a capability is a class with a method

```csharp
var result = await new ReserveInventory(fakeStore).ExecuteAsync(
    new ValidatedOrder("SKU-1", 2, 40m),
    new TestCapabilityContext(idempotencyKey: "key-1"),
    ct);

result.Value.ReservationId.ShouldBe("key-1");
```

No host, no container, no attribute a runner has to understand. Every value the context
returns is fixed — the clock is `DateTimeOffset.UnixEpoch`, `Random` is seeded, `NewId()`
returns a distinct-but-reproducible sequence — because the clock, the identifiers and the
randomness are the three things a capability is allowed to reach for. Pinning them is what
makes the test deterministic, and it is the same property durable replay depends on.

### 3.2 Flow — the real engine, substituted capabilities

```csharp
var host = FlowTestHost
    .For(PlaceOrderFlow.Plan, new PlaceOrderFlow.Dispatcher(capture, release, reserve, validate))
    .Substitute("payment.capture", OrderErrors.PaymentDeclined("insufficient funds"))
    .Build();

var run = await host.RunAsync(new PlaceOrder("SKU-1", 4, "tok"), ct);

run.Error!.Code.ShouldBe("payment.declined");
run.Compensation.ShouldBe(CompensationOutcome.Succeeded);
run.Trace.Executed.ShouldBe(["order.validate", "inventory.reserve", "payment.capture"]);
run.Trace.Compensated.ShouldBe(["inventory.release"]);
```

**It is the real runtime.** The engine, the compiled plan, the pooled context, the
compensation stack, the deadline check, the merge strategies and the sub-flow recursion
are the production ones. The host adds two things and no more: an `IStepDispatcher` in
front of the generated one, and a trace.

### 3.3 Endpoint — on the wire

Status codes, media types, `Problem Details` bodies and header requirements are properties
of the transport. They need a real server, and `Ecommerce.Tests/PlaceOrderEndpointTests`
is what that looks like. Anything asserted there that is not about the wire belongs one
level down.

---

## 4. `FlowTestHost` in detail

### 4.1 Why the plan and the dispatcher are named

The documented shape was `FlowTestHost.For<PlaceOrderFlow>()`. **It does not survive
contact with what the compiler emits**, and this is the one place the design changed
rather than the code.

The generator emits `PlaceOrderFlow.Plan` as a static property and `PlaceOrderFlow.Dispatcher`
as a nested class whose constructor takes each capability *as its own concrete sealed
type*. So `For<TFlow>()` would have to (a) find those generated members by reflection,
which constraint C2 forbids — `FlowX.Testing` is trim- and AOT-analysed like every other
shipped package — and (b) resolve the capabilities to construct the dispatcher, which
means a container, which is the mock framework this deliberately is not.

Naming them costs one line, keeps the host reflection-free, and makes visible exactly
which capabilities the test decided to construct for real:

```csharp
FlowTestHost.For(PlaceOrderFlow.Plan, new PlaceOrderFlow.Dispatcher(…))
```

### 4.2 Substitution is by capability id

```csharp
.Substitute("payment.capture", error)                              // fails
.Substitute("inventory.reserve", ctx => Result.Ok(new Reservation(…)))   // succeeds
.Substitute("payment.capture", async (ctx, ct) => …)               // succeeds, asynchronously
```

Keyed by the id the plan carries, not by the capability's class name. That is what the
plan is keyed by, it is what the manifest publishes, and it is what survives a capability
being renamed, moved or replaced by a different implementation of the same contract.

A substitution applies **wherever that capability appears** — as a step, as another step's
compensation, and inside a flow this one composes.

Two guards, because a substitution that silently does not happen is a test that asserts on
the real capability while claiming otherwise:

- `Build()` throws when a substituted id appears nowhere in the plan, listing the ids that
  do. (Skipped when the flow composes a sub-flow, whose capabilities live in a graph this
  plan does not carry.)
- `FlowTestRun.UnusedSubstitutions` names the substitutions the run never reached — the
  branch that was not taken, or the step the flow failed before.

### 4.3 The trace

`FlowTestTrace` holds strings and integers, never a `FlowContext`: contexts are pooled and
reset the instant a flow returns, so a trace that captured one would read as empty at best
and as the next test's data at worst.

| Member | Answers |
|---|---|
| `Executed` | which steps ran, in order |
| `Compensated` | which compensations ran, in unwind order |
| `Entries` | everything, including branch, switch, iteration and sub-flow decisions |
| `TimesExecuted(id)`, `DidExecute(id)` | order-insensitive questions |
| `ToString()` | the whole run, one entry per line, for an assertion message |

Names are stable and greppable: a capability step is the capability id verbatim, and every
other kind carries a prefix — `emit:`, `signal:`, `fail`, `branch:then`, `branch:otherwise`,
`switch:0`, `switch:default`, `foreach:3`, `subflow:payment.settle:Inline`. A capability id
is `<domain>.<verb>` and cannot contain a colon, so no prefixed name can collide with one.

**Order is exact for sequential control flow and arbitrary inside a fork.** A `Parallel`'s
branches and a `ForEach` above concurrency 1 genuinely interleave, so their relative order
is the thread pool's answer, not the flow's — assert `TimesExecuted` there. Compensation is
always sequential and strictly reverse, so `Compensated` is exact even for a flow that
forked.

### 4.4 What it handles

| Shape | Covered |
|---|---|
| Linear steps, `Emit` | yes |
| `When`/`Otherwise` | yes — the decision is in the trace |
| `Switch` | yes — the arm, and `switch:default` for no match |
| `Parallel`, all four merge strategies | yes |
| `ForEach`, including bounded concurrency and `ContinueOnError` | yes |
| `SubFlow` — `Inline` | yes — the child's steps join the parent's trace, under the child's flow id |
| `SubFlow` — `Detached` | yes — the run waits for detached children before reporting, and throws rather than reporting a trace it knows is incomplete |
| `Fail` | yes |
| Compensation, strict reverse order | yes |
| Compensation across a sub-flow boundary | yes — a child's completed steps unwind when the parent later fails |
| Deadline | yes — advance a `FlowTestClock` |
| `AwaitSignal` | **no.** It suspends into a journal that does not exist ([20 §P2](20-Roadmap.md)) |
| `SubFlowMode.AwaitCompletion` | **no.** Unrepresentable in a plan at all, for the same reason |

### 4.5 Isolation

One host owns one `FlowEngine`, and therefore one context pool. A host built per test
cannot hand another test the first one's data, and repeated runs on a single host exercise
the pool's reset rather than avoiding it — `TwoRunsOnOneHostDoNotSeeEachOthersContext` is
the check. A **trace**, by contrast, is created per run: two runs on one host must not see
each other's.

---

## 5. What is deliberately not here

| Absent | Why |
|---|---|
| A mock framework | The roadmap's P0 *Should* said "substitution only". A stand-in is a delegate; there is no `Verify`, no argument matcher and no call-order DSL, because the trace answers those questions about the flow rather than about a proxy object |
| `WithVirtualTime()` | It accelerates retries, timeouts and breaker windows. **None of those executes**: a step's policy chain reaches the plan and the runtime never reads it ([10, header](10-Policy-Framework.md)), so virtual time would have nothing to make elapse. `FlowTestClock` is the honest subset — a clock a test advances, which is enough for the deadline, the one time-dependent behaviour that does run |
| Crash, resume and replay | Needs the journal (P2). `RunUntilStep`, `SimulateNodeCrash` and `ResumeOnNewNode` were documented in [19 §6](19-SDK.md) and none of them exists |
| `IntegrationTestHost`, Kafka, Testcontainers | A different kind of test with a different failure mode and a different runtime cost. It is not this package's job, and putting it here would make every unit test project restore a container runtime |
| Assertion extensions (`HaveCompensated<T>()`) | They would pin `FlowX.Testing` to one assertion library for everybody. `FlowTestRun` exposes plain collections instead, which Shouldly, FluentAssertions and `Assert.Equal` all read equally well |

---

## 6. Choosing a level

| The question | Where it belongs |
|---|---|
| "Does this rule reject a zero quantity?" | Unit, `TestCapabilityContext` |
| "Does a declined payment release the hold?" | Flow, `FlowTestHost` |
| "Does the hold get released *before* the refund?" | Flow — nothing above can see ordering |
| "Does a rejected order return 400 with `application/problem+json`?" | Endpoint, a real server |
| "Does the `[Sensitive]` token stay out of the error body?" | Endpoint — redaction happens in the transport |
| "Does a step commit survive a node loss?" | Conformance, and not yet buildable |

---

## 7. Rules

1. **A flow-level property is asserted at flow level.** If a test stands up a server to
   observe compensation, it is asserting the wrong thing at the wrong cost.
2. **A test that pins an ordering must be able to fail on the ordering.** Two compensable
   steps, not one: with one, forward and reverse unwind look identical.
3. **A substituted capability must be shown not to have run.** Otherwise the test passes
   equally well when the substitution is ignored and the real capability happens to fail.
4. **Tests do not sleep.** Advance a `FlowTestClock`. A test that sleeps in real time will
   be asked to change.
5. **`UnusedSubstitutions` is empty, or the test says why.** A stand-in the run never
   reached means the assertion is about a path the test did not exercise.
