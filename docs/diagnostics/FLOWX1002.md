# FLOWX1002 — Step type is not a capability

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0

## What it means

A step invokes a capability. The named type does not implement `ICapability<TIn, TOut>`, so there is nothing for the engine to call.

## Example that triggers it

```csharp
flow.Step<EmailSender>()   // EmailSender is a plain class
```

## How to fix it

```csharp
public sealed class EmailSender : ICapability<EmailRequest, EmailSent>
{
    public ValueTask<Result<EmailSent>> ExecuteAsync(
        EmailRequest input, CapabilityContext ctx, CancellationToken ct) => ...;
}
```

## When to suppress

None. If the work is worth a step it is worth a contract, and the contract is what makes the step testable alone and visible in the manifest.

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A
suppression without one fails the build.

---

**Back to:** [diagnostics index](README.md) · [Quality gates](../21-Quality-Gates.md)
