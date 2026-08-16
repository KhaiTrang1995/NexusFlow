# FLOWX1053 — Fallback requires a capability with no side effects

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0

## What it means

A fallback answers with a declared constant when the step has failed for the last time, which
returns a success **without performing the effect** — [FLOWX1018](FLOWX1018.md)'s objection to
caching a write, reaching the same capability by the other door.

There is a second half, and it is why the rule is an error rather than a warning: a degraded step
registers **no compensation**. The capability produced nothing, so an undo would be an undo of
nothing ([ADR-0078](../adr/ADR-0078-stage-four-nests-six-kinds.md) §2.7). On a capability that
does change the world, an effect that half happened before the failure would be left with
nothing pointing at it — [10 §2](../10-Policy-Framework.md#why-rigidity-is-the-feature)'s
"compensating something that never happened" row, read backwards.

## It asks the same question of both capabilities

Since WP-80 a fallback may answer with a second capability rather than a constant, and the rule
covers that capability too. The two halves are different questions with one repair:

* **The step.** A degraded success stands in for an effect the step was supposed to make and did
  not. That is the paragraph above, and it is unchanged.
* **The fallback.** A fallback runs *because* a dependency has just failed, so it is the
  least-exercised path in the system running at the worst moment. An effect made there sits under
  a step whose own capability made none, and nothing on the unwind stack points at it — the stack
  deliberately leaves a degraded step off, and putting it on would run the *step's* undo for work
  the *fallback* did.

This is where [ADR-0078](../adr/ADR-0078-stage-four-nests-six-kinds.md) §3's third blocker is
answered rather than engineered around. That record argued a step answered by its fallback
capability "really did produce an effect, so it must be compensable"; refused here, it does not,
so §2.7's rule that a degraded step registers no compensation survives word for word
([ADR-0079](../adr/ADR-0079-a-fallback-capability-is-a-dispatch-of-its-own.md) §2.3).

The message names whichever capability is at fault, because that is the declaration the author
has to change.

## Example that triggers it

```csharp
[Capability("inventory.reserve", Version = "1.0.0",
    Authorization = Authorization.Authenticated,
    SideEffects = new[] { "inventory-ledger" })]
...
flow.Step<ReserveInventory>().WithPolicy(Policies.Degradable)   // FLOWX1053 — the step writes
```

```csharp
// And the same rule over the answer. The step is a read, so the paragraph above is
// satisfied; what is refused is asking a capability that writes to stand in for it.
public static readonly PolicySet Degradable = PolicySet
    .Named("degradable")
    .Fallback<ReserveInventory>();

flow.Step<ReadInventoryLevel>().WithPolicy(Policies.Degradable)  // FLOWX1053 — the fallback writes
```

## How to fix it

```csharp
// Declare the fallback on the read that precedes the write, where a stale or default
// answer is a degraded mode rather than a lost write:
flow.Step<ReadInventoryLevel>().WithPolicy(Policies.Degradable)
    .Step<ReserveInventory>()
```

Or handle the failure in the flow: a write that may be skipped is a branch, and a branch is
where a compensable effect belongs.

## When to suppress

None. If the capability genuinely has no side effects, remove them from its declaration and the
policy becomes legal. `PolicyChain.Create` refuses the same pairing at plan-composition time, so
a suppressed build would fail at start-up instead.

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A
suppression without one fails the build.

---

**Back to:** [diagnostics index](README.md) · [FLOWX1018](FLOWX1018.md) · [Policy framework §3](../10-Policy-Framework.md#3-the-policy-catalogue)
