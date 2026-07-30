# FLOWX1003 — Capability references a transport

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0

## What it means

A capability must not know how it was invoked. The moment it references `HttpContext`, a Kafka `ConsumeResult` or any other transport type, the same flow can no longer run behind HTTP, a bus and a cron schedule without changing — which is quality goal Q4.

## Example that triggers it

```csharp
public sealed class ReserveInventory : ICapability<ReserveRequest, Reservation>
{
    private readonly IHttpContextAccessor _http;   // transport leaked into business logic
```

## How to fix it

```csharp
// Move the transport concern into the trigger. Anything the capability genuinely
// needs — tenant, principal, correlation — is already on CapabilityContext.
public ValueTask<Result<Reservation>> ExecuteAsync(
    ReserveRequest input, CapabilityContext ctx, CancellationToken ct)
{
    var tenant = ctx.TenantId;   // not from a header
}
```

## When to suppress

None inside a capability. A transport plugin is the correct place for transport types.

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A
suppression without one fails the build.

---

**Back to:** [diagnostics index](README.md) · [Quality gates](../21-Quality-Gates.md)
