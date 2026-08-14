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

## Example that triggers it

```csharp
[Capability("inventory.reserve", Version = "1.0.0",
    Authorization = Authorization.Authenticated,
    SideEffects = new[] { "inventory-ledger" })]
...
flow.Step<ReserveInventory>().WithPolicy(Policies.Degradable)   // FLOWX1053
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
