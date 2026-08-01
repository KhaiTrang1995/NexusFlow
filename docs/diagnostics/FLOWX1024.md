# FLOWX1024 — Emit step stages no event to publish

> **Severity:** Warning · **Category:** FlowX · **Since:** 0.1.0
> **Re-scoped once `.Emit<T>()` reached the outbox: two narrow cases, both fixable in
> your own code.**

## What it means

`.Emit<TEvent>(...)` compiles into a real step: it appears in the flow's
`ExecutionPlan`, and it appears in `flowx.manifest.json` as a published event.
Anyone reading that manifest — a consumer team, `flowx diff`, a generated
contract — will reasonably conclude the event is published.

**Usually it now is.** The generated dispatcher's `DescribeStep` builds the event
body from your own `.Emit` expression, `FlowEngine.CommitStepAsync` stages it into
`StepCommit.Outbox`, the store writes it in the same transaction as the step row,
and `PostgresOutboxPublisher` drains it to an `IEventPublisher` at-least-once in
`partition_key` order.

This warning fires on the two flows where that chain cannot start. The message
says which:

| Reason | What is missing | Fix |
|---|---|---|
| `Profile = Ephemeral` | An ephemeral execution keeps no journal, so there is no transaction for the event to be part of. Staging it anywhere else would be the dual write the outbox exists to remove | Declare `Profile = Durable` on the flow |
| No serialiser context declares `TEvent` | The body is written through a source-generated `JsonSerializerContext`. `JournalPayload.Of` takes a `JsonTypeInfo<T>` and has no overload that reflects over a type, which is what keeps the write path trim- and NativeAOT-safe (constraint C2, [ADR-0008](../adr/ADR-0008-serialization-and-schema.md)) | Add `[JsonSerializable(typeof(TEvent))]` to one context |

**Exactly one context, not at least one.** Two contexts declaring the same
contract is the same answer as none: picking the first would make the event's wire
shape depend on file order, which is the reason `EndpointEmitter` declines to pick
one for a request body either.

This is the only FlowX diagnostic with `Warning` severity. The gap it reports is
between what the manifest promises and what the process does, and that is a
class of defect that is otherwise found in production, by the consumer who was
waiting.

### What changed, and what did not

This page has twice recorded a reason that later stopped being true. The first
said transactional outbox publication was not implemented. The second said the
engine staged nothing. Both are now false, and the table is how that drift stays
visible rather than being quietly overwritten.

| Link | Before WP-56 | After WP-56 | Now |
|---|---|---|---|
| `.Emit<T>()` reaches the plan and the manifest | yes | yes | yes |
| The generated dispatcher describes the event | **no** | **no** | yes |
| The engine stages it into `StepCommit.Outbox` | **no** | **no** | yes |
| A store commits it atomically with the step row | yes (WP-53) | yes | yes |
| A publisher drains it to a broker | **no** | yes (`PostgresOutboxPublisher`) | yes |
| A broker plugin implements `IEventPublisher` | **no** | yes (`RedisStreamEventPublisher`) | yes |

*That last row read **no** in all three columns until WP-56b, and the paragraph
under it called `IEventPublisher` "a declared contract with a recording test
double behind it".* `plugins/FlowX.Redis` now implements it over Redis Streams,
one stream per `partition_key`, and `PublisherConformance` holds it and the double
to one contract ([ADR-0018](../adr/ADR-0018-outbox-publication-and-ordering.md)).
The **first** column is still **no**, and it is still not what this diagnostic
reports: what this rule is about is a flow that cannot stage, and which broker a
deployment wires behind the seam is a composition decision, not something the
author of a flow can fix. Warning about it on every `.Emit` would be a warning
with no action attached — which is how a diagnostic gets suppressed project-wide
and stops being read.

**Severity is unchanged, and that is a decision rather than an omission.**
`Warning` was chosen because the source is not wrong — the artifact is
incomplete. That is still exactly the case, and it is now *more* defensible than
it was: both reasons have a one-line fix, so an author who wants the event
published can have it. `Error` would fail builds of code that is deliberately
ephemeral, and `Info` is where `FLOWX1007`–`1009` sat unraised for two phases: a
severity that never reaches a build log
([WP-58](../../PLAN.md)).

## Example that triggers it

```csharp
// Ephemeral: the flow keeps no journal, so there is no transaction to stage into.
[Flow("order.place", Profile = ExecutionProfile.Ephemeral)]
public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
{
    protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) => flow
        .Step<ValidateOrder>()
        .Emit<OrderPlaced>(ctx => new OrderPlaced(...))   // FLOWX1024
        .Return(ctx => new OrderPlacedResult(...));
}
```

```csharp
// Durable, but nothing in this compilation can serialise OrderPlaced.
[JsonSerializable(typeof(PlaceOrder))]          // OrderPlaced is missing
internal sealed partial class OrderingJson : JsonSerializerContext;
```

## Example that does not

```csharp
[Flow("order.place", Profile = ExecutionProfile.Durable)]
public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
{
    protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) => flow
        .Step<ValidateOrder>()
        .Emit<OrderPlaced>(ctx => new OrderPlaced(ctx.Input.Sku))
        .Return(ctx => new OrderPlacedResult(...));
}

[JsonSerializable(typeof(PlaceOrder))]
[JsonSerializable(typeof(OrderPlaced))]
[JsonSerializable(typeof(OrderPlacedResult))]
internal sealed partial class OrderingJson : JsonSerializerContext;
```

The event is staged by the step's own commit, keyed on the flow instance so one
instance's events reach a consumer in the order it staged them, and any member the
contract declares `[Sensitive]` is redacted before the row is written.

## How to fix it

- **Declare `Profile = Durable`**, if the event has a consumer. This is a real fix
  and not a paper one: a durable flow journals its step boundaries, and a host
  with no journal wired refuses it at start-up rather than running it silently.
  It is also a decision with consequences — see
  [`FLOWX1012`](FLOWX1012.md) for the argument the reference sample made in the
  other direction.
- **Add `[JsonSerializable(typeof(TEvent))]`** to the serialiser context your
  application already declares. If it declares none, the same attribute is what an
  HTTP-triggered flow needs for its request body.
- **Remove the `.Emit`**, if a consumer would otherwise be built against an event
  that never arrives.
- **Suppress it**, if nothing downstream depends on delivery yet and you want the
  declaration in place.

## When to suppress

When the event has no consumer, and the `.Emit` is there to declare intent.
Scope the suppression to the single step, never the file or the project:

```csharp
#pragma warning disable FLOWX1024 // FLOWX-DEBT(orders, 2026-12-31): no consumer yet;
                                  //   this flow is deliberately ephemeral.
    .Emit<OrderPlaced>(ctx => new OrderPlaced(...))
#pragma warning restore FLOWX1024
```

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A
suppression without one fails the build.

## When this page is deleted

When both remaining reasons stop being possible. The serialiser-context one goes
if `IPayloadSerializer` (WP-59) ever gives an event contract a route to the wire
that does not need a generated context. The ephemeral one goes only if `.Emit`
becomes an error on an ephemeral flow, which is a severity decision this page has
already declined once.

Neither is close, and the rule is now a warning that names its own fix — which is
the state a diagnostic is supposed to end in rather than the state it was stuck in.

---

**Back to:** [diagnostics index](README.md) · [Quality gates](../21-Quality-Gates.md)
