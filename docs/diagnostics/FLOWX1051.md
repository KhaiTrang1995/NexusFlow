# FLOWX1051 — Hedge requires an idempotent capability

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0

## What it means

A `Hedge` issues a **second call while the first is still outstanding**, under the same
`ctx.IdempotencyKey`, and keeps whichever answers first. So the effect can happen twice at
once — and, unlike a retry, the answer the flow keeps may be the losing call's: the loser is
cancelled after it may already have written its result into the state bag
([ADR-0078](../adr/ADR-0078-stage-four-nests-six-kinds.md) §2.4).

`Idempotent = true` is the declaration that both calls are **one request**, which is what makes
the two answers interchangeable. This is [FLOWX1014](FLOWX1014.md)'s requirement asked of a
concurrent repeat rather than a sequential one.

## Example that triggers it

```csharp
[Capability("payment.capture", Version = "2.1.0", Idempotent = false, ...)]
public sealed class CapturePayment : ICapability<CaptureRequest, Capture> { … }

public static readonly PolicySet Fast = PolicySet.Named("fast")
    .Timeout(TimeSpan.FromSeconds(2))
    .Hedge(afterDelay: TimeSpan.FromMilliseconds(300));

flow.Step<CapturePayment>().WithPolicy(Policies.Fast)   // FLOWX1051
```

## How to fix it

```csharp
// Either the capability is safe to ask twice at once, and says so:
[Capability("rating.score", Version = "1.0.0", Idempotent = true, ...)]

// Or bound the tail with a policy that refuses a slow call rather than duplicating it:
public static readonly PolicySet Fast = PolicySet.Named("fast")
    .Timeout(TimeSpan.FromSeconds(2))
    .Retry(attempts: 2);
```

A hedge is for a dependency that is **up and slow**. If the tail is a dependency that is up and
*wrong*, the policy wanted is a retry, and it asks for the same declaration.

## When to suppress

None. The declaration is a claim about the capability, not about this flow — so if it is not
true, the repair is a capability that is safe to ask twice, never an attribute added to make a
build pass. `PolicyChain.Create` refuses the same pairing at plan-composition time, so a
suppressed build would fail at start-up instead.

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A
suppression without one fails the build.

---

**Back to:** [diagnostics index](README.md) · [Policy framework §3](../10-Policy-Framework.md#3-the-policy-catalogue)
