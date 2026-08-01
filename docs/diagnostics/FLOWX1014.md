# FLOWX1014 — Retry requires an idempotent capability

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0
> **Applies to:** `Retry` on `.Step<T>()`, and `CompensationRetry` on the capability named by
> the step's `.CompensateWith<T>()`.

> [!NOTE]
> **This rule had a hole on the compensation side, and it is closed rather than papered
> over.** From 0.1.0 the analyzer asked one question — is the *step* idempotent, and does the
> declared set contain `Retry`. The string `CompensationRetry` appeared nowhere in
> `FlowAnalyzer`, so a retry declared over a non-idempotent *compensating* capability was
> reported by nothing. `PolicyChain.ForCompensation` carried the same rule and could not
> enforce it either: `FlowEmitter` wrote the compensation's descriptor with `idempotent:`
> hardcoded to `true`, so the runtime check was answering a question about a literal this
> compiler had invented rather than about anything the author declared.
>
> **It cost nothing until it suddenly did.** No DSL surface could declare a
> `CompensationRetry` at all — `.WithPolicy` stored the argument's source text and stopped —
> so the gap was theoretical for as long as nothing could reach it. Making `.WithPolicy` reach
> the plan made it reachable, and made both halves live on the same day.
>
> **No new id.** This is one rule reaching the case it always meant to cover, not a second
> rule about compensation. A separate id would let a team suppress half a safety rule
> believing they had suppressed a different one.

## What it means

Retrying a non-idempotent operation duplicates its effect. For a payment capture that is a
duplicate charge — the single most expensive bug this platform can prevent structurally.

**The capability judged is the one the policy would re-dispatch**, and one `.WithPolicy(...)`
may describe two:

| Policy | What it wraps | Whose `Idempotent` decides | If it runs twice |
|---|---|---|---|
| `Retry` | the step | the step's capability | a second charge |
| `CompensationRetry` | the step's compensation | the **compensating** capability | a second reversal |

That split is not a technicality; it is what makes the ordinary saga expressible. A
`payment.capture` is not idempotent and never will be, and its reversal is, because the
reversal is written against `CapabilityContext.IdempotencyKey`. Reading the step's
declaration for both would refuse every real saga; reading the compensation's for both would
permit the duplicate charge. So the rule reads each policy against the call it actually
governs, and the message names which policy and which capability it means.

## Example that triggers it

```csharp
[Capability("payment.capture", Version = "2.1.0",
    Authorization = Authorization.Permission)]   // Idempotent defaults to false
...
flow.Step<CapturePayment>().WithPolicy(Policies.WithRetry)
```

And the compensation case, which reads the *reversal's* declaration and not the capture's:

```csharp
[Capability("payment.refund", Version = "1.0.0",
    Authorization = Authorization.Permission)]   // Idempotent defaults to false
public sealed class RefundPayment : ICapability<Payment, Refund> { ... }

public static readonly PolicySet Undo = PolicySet
    .Named("payment-undo")
    .CompensationRetry(attempts: 3);

flow.Step<CapturePayment>().CompensateWith<RefundPayment>()
    .WithPolicy(Policies.Undo)                   // FLOWX1014 — names payment.refund
```

`payment.capture` being non-idempotent is not what is wrong here, and the report says so by
naming `payment.refund`. The step is allowed to be non-idempotent; the undo the retry would
re-dispatch is not.

`.CompensateWith` and `.WithPolicy` both return `IStepBuilder`, so either order is legal C#
and both are reported — the diagnostic lands on whichever of the two calls completed the
pairing.

## How to fix it

```csharp
// Either make the capability idempotent and declare it —
[Capability("payment.capture", Version = "2.1.0",
    Authorization = Authorization.Permission, Idempotent = true)]

// — or handle the failure in the flow rather than retrying it.
```

For the compensation case, the same two options, applied to the reversal:

```csharp
// Either write the reversal against the idempotency key and declare it honestly —
[Capability("payment.refund", Version = "1.0.0",
    Authorization = Authorization.Permission, Idempotent = true)]

// — or drop the CompensationRetry and accept a single attempt, and with it the
//   CompensationFailed the engine raises when that attempt does not succeed.
```

Dropping it is a real choice, not a defeat. A reversal that cannot be safely repeated is
better left to a human than dispatched twice by a policy.

## When to suppress

None. `CapabilityContext.IdempotencyKey` is stable across retries and replays; pass it
downstream so the provider can deduplicate, then declare `Idempotent = true` honestly. That
applies to a compensation exactly as it applies to a step — the compensation runs with an
idempotency key of its own.

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A
suppression without one fails the build.

Suppressing it no longer disables the check entirely, which it effectively did before. The
compiled plan now carries the compensation's real `Idempotent` in its descriptor, so
`PolicyChain.ForCompensation` refuses the pairing at plan construction — a
`InvalidFlowPlanException` at type initialisation rather than a duplicate reversal in
production.

---

**Back to:** [diagnostics index](README.md) · [Quality gates](../21-Quality-Gates.md)
