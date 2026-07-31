# FLOWX1021 — Sub-flow composition forms a cycle

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0

## What it means

A flow composes itself, directly or through a chain of other flows.

[08 §3.7](../08-Flow-Definition.md#37-sub-flows) has said since before anything could
declare a sub-flow that "cycles are a **compile error** (`FLOWX1021`). The flow graph is a
DAG, always." Until `SubFlowCycleAnalyzer` existed, the id was reserved and the sentence
was a promise the compiler was not keeping.

### Why a cycle is worse than an infinite loop

Every other repetition the DSL can express is bounded by construction. A branch target must
point strictly forward, so a step runs at most once. A `ForEach` reads its element count
once, from a materialised list, before the first iteration.

A composition cycle is bounded by nothing at all. Each level rents a pooled context, opens
a compensation scope and shortens the deadline, so the recursion ends in one of two places:

- a **stack overflow**, which takes the process down rather than failing one flow, and names
  a hundred identical frames rather than the mistake; or
- a **deadline breach** several levels below the flow that caused it, reported against a
  capability that is entirely innocent.

Neither failure points at the `.SubFlow<T>()` call that has to change. A build error does.

## Example that triggers it

```csharp
[Flow("order.place")]
public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlaced>
{
    protected override void Define(IFlowBuilder<PlaceOrder, OrderPlaced> flow) => flow
        .Step<ValidateOrder>()
        .SubFlow<FulfilOrderFlow, FulfilOrder>(ctx => new FulfilOrder(ctx.Get<OrderId>()))
        .Return(ctx => new OrderPlaced(...));
}

[Flow("order.fulfil")]
public sealed partial class FulfilOrderFlow : Flow<FulfilOrder, Fulfilled>
{
    protected override void Define(IFlowBuilder<FulfilOrder, Fulfilled> flow) => flow
        .Step<ReserveInventory>()
        // "if the reservation failed, re-place the order" — which is this flow's caller.
        .SubFlow<PlaceOrderFlow, PlaceOrder>(ctx => new PlaceOrder(...))
        .Return(ctx => new Fulfilled(...));
}
```

```
error FLOWX1021: Flow 'order.place' composes itself: order.place → order.fulfil → order.place
error FLOWX1021: Flow 'order.fulfil' composes itself: order.fulfil → order.place → order.fulfil
```

The **path** is in the message on purpose. The useful fact about a cycle is never that
there is one — it is which edge to cut, and in a four-flow cycle that edge may be three
files away from the flow the error is reported against.

One diagnostic per flow **on** the cycle, reported at that flow's declaration. Not one per
edge: a three-flow cycle has three edges, and printing the same loop three times with three
different starting points helps nobody. A flow that merely *reaches* a cycle is not itself
on one and is not reported — blaming it would send you to a file with nothing wrong in it.

## How to fix it

**Extract what the two flows share into a third flow that composes neither.**

```csharp
[Flow("order.reserve")]
public sealed partial class ReserveOrderFlow : Flow<ReserveOrder, Reserved> { … }
```

`order.place` and `order.fulfil` then both compose `order.reserve`, and the graph is a tree
rather than a loop.

**Or make the repeated work a capability.** Capabilities form a *set*, not a graph
([FLOWX1004](FLOWX1004.md) is the rule that keeps it that way), so the question cannot
arise. If the shared thing is one operation rather than an orchestration, it was a
capability all along.

**Or invert the direction.** A cycle almost always means one of the two edges is really a
*result* rather than a call: the child does not need to re-run its parent, it needs to
return something the parent acts on. Deleting that edge and moving the decision up into the
parent is usually both the smaller change and the more honest model.

## What it proves, and what it cannot

**Within one compilation, for edges written as a direct `.SubFlow<T, …>()` in a `Define`
chain, the answer is exact.** The graph is built from resolved symbols; nothing is matched
on a name and nothing is guessed. Nested calls count — a composition inside a `When` block
or a `ForEach` body is exactly as much of a cycle as one at the top level.

Outside that it is **silent**, and the silences matter more than the guarantee:

- **An edge into a referenced assembly is not followed.** This is the largest gap. If flow
  `A` in this project composes flow `B` from a NuGet package, and `B` composes `A`, the
  build sees `A → B` and stops: `B`'s `Define` body is not in this compilation. Only its
  compiled plan is, and a plan carries the child's own sub-flow *ids* — not the symbols
  needed to resolve them back to types. **The cycle closes and no build reports it.**
- **A flow reached through anything but a type argument is invisible.** `TFlow` is a type
  parameter today, so there is no way to name a flow through a variable — but a helper
  method that composed on a caller's behalf would not be followed.
- **A `Define` body that is not a chain is skipped**, which is the same limit
  [`FlowChainWalker`](FLOWX1020.md) already documents: a method group or an `Action` in a
  variable has no body at the call site.
- **Anything that does not bind is skipped.** An unresolved flow type means the file does
  not compile, and that message is better than this one.

**The cross-assembly gap is why `FlowEngine.MaxSubFlowDepth` exists.** A cycle this rule
cannot see is bounded at run time instead: composition thirty-two levels deep fails that one
flow with `flow.subflow_too_deep`, compensating everything it completed, rather than
overflowing the stack and taking the process with it. That is a worse diagnostic than a
build error and a much better outcome than a crash — and it is the honest position, because
no build-time analysis can see across a boundary its inputs do not cross.

## When to suppress

**Never.** There is no correct flow this rule refuses. Unlike [FLOWX1003](FLOWX1003.md),
which matches a list of namespaces, or [FLOWX1011](FLOWX1011.md), which is not
interprocedural, this rule reports only cycles it has proved by walking resolved symbols —
so a report is never a false positive. Suppressing it does not make the composition
terminate; it moves the failure from your build to somebody's production stack trace.

If the build is reporting a cycle you believe is not there, the interesting question is
which of the named edges you did not expect. The path in the message is the answer.
