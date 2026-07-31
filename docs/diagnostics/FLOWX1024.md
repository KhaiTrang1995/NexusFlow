# FLOWX1024 — Emit step is recorded but not published

> **Severity:** Warning · **Category:** FlowX · **Since:** 0.1.0
> **Status revisited at WP-56 (2026-07-31): still raised, for a narrower reason.**

## What it means

`.Emit<TEvent>(...)` compiles into a real step: it appears in the flow's
`ExecutionPlan`, and it appears in `flowx.manifest.json` as a published event.
Anyone reading that manifest — a consumer team, `flowx diff`, a generated
contract — will reasonably conclude the event is published.

In this release it is not. The step reaches the plan and the manifest, and
nothing anywhere turns it into an event.

This is the only FlowX diagnostic with `Warning` severity. The gap it reports is
between what the manifest promises and what the process does, and that is a
class of defect that is otherwise found in production, by the consumer who was
waiting.

### What changed at WP-56, and what did not

This page used to say the reason was that "transactional outbox publication
([design principle P8](../03-Design-Principles.md#p8--event-native)) is not
implemented, so the generated dispatcher records the step and does nothing
else". **That sentence has stopped being true and the diagnostic is still
correct**, which is the kind of drift a status revisit exists to catch. The
pipeline has four links; three of them now exist.

| Link | Before WP-56 | After WP-56 |
|---|---|---|
| `.Emit<T>()` reaches the plan and the manifest | yes | yes |
| The engine stages the event into `StepCommit.Outbox` | **no** | **no** |
| A store commits it atomically with the step row | yes (WP-53) | yes |
| A publisher drains it to a broker | **no** | yes (`PostgresOutboxPublisher`) |

The missing link moved rather than closed. `FlowEngine.CommitStepAsync` builds a
`StepCommit` for every step and never sets `Outbox`; `IStepDispatcher.DescribeStep`
— the only thing that can name a `JsonTypeInfo<T>` for a user's event type, which
is what `JournalPayload.Of<T>` requires — returns a `StepJournalEntry` carrying a
result and a state bag and no event. So an `.Emit` step stages no outbox row, and
a publisher draining an empty table publishes nothing.

The gap is unchanged in kind and one link narrower: it is in the engine and the
generator now, not in a store.

**Severity is unchanged, and that is a decision rather than an omission.**
`Warning` was chosen because the source is not wrong — the artifact is
incomplete. That is still exactly the case. `Error` would fail builds of correct
code for a gap the author cannot close, and `Info` is where `FLOWX1007`–`1009`
sat unraised for two phases: a severity that never reaches a build log
([WP-58](../../PLAN.md)).

## Example that triggers it

```csharp
protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) => flow
    .Step<ValidateOrder>()
    .Emit<OrderPlaced>(ctx => new OrderPlaced(...))   // FLOWX1024
    .Return(ctx => new OrderPlacedResult(...));
```

## How to fix it

There is still no fix in user code — the link this waits on is in the engine.
Your options are:

- **Remove the `.Emit`** until the engine stages it, if a consumer would
  otherwise be built against an event that never arrives. This is the honest
  default.
- **Publish the event from a capability** in the meantime, accepting that it is
  outside the flow's transaction and can be lost. WP-56 adds a second version of
  this that is *not* outside the transaction: a host that commits through
  `IFlowJournal` itself can populate `StepCommit.Outbox`, and
  `PostgresOutboxPublisher` will deliver it at-least-once. That is more work than
  `.Emit` and it is atomic, which `.Emit` currently is not.
- **Suppress it**, if nothing downstream depends on delivery yet and you want the
  declaration in place for when the engine catches up.

## When to suppress

When the event has no consumer, and the `.Emit` is there to declare intent.
Scope the suppression to the single step, never the file or the project:

```csharp
#pragma warning disable FLOWX1024 // FLOWX-DEBT(orders, 2026-12-31): no consumer yet;
                                  //   remove once .Emit reaches the outbox.
    .Emit<OrderPlaced>(ctx => new OrderPlaced(...))
#pragma warning restore FLOWX1024
```

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A
suppression without one fails the build.

## When this page is deleted

When `IStepDispatcher.DescribeStep` returns the emitted event alongside the
result and the state bag, and `FlowEngine.CommitStepAsync` puts it in
`StepCommit.Outbox`. The chain is connected at that point, the crash and
double-publish tests in `tests/FlowX.Postgres.Tests/OutboxPublisherTests.cs`
become reachable from a flow rather than from a hand-built commit, and this rule
has nothing left to report.

---

**Back to:** [diagnostics index](README.md) · [Quality gates](../21-Quality-Gates.md)
