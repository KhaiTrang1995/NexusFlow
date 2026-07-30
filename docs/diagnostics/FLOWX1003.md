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

## What it detects, and what it does not

`CapabilityAnalyzer` reads a capability's **dependencies** — constructor parameters,
fields and properties — and reports one whose namespace is a known transport. Generic
wrappers are unwrapped, so `Lazy<HttpContext>` is caught too.

The transport list is **a list, not a proof**:

`Microsoft.AspNetCore` · `Microsoft.Extensions.Http` · `System.Net.Http` · `Grpc.` ·
`Confluent.Kafka` · `RabbitMQ.` · `Azure.Messaging` · `Amazon.SQS` ·
`Amazon.SimpleNotificationService` · `MQTTnet` · `NATS.` · `StackExchange.Redis`

— plus any `FlowX.*` namespace that is not `FlowX`, `FlowX.Runtime` or
`FlowX.Generated`, because everything else under `FlowX.` is a plugin.

A transport not on that list is not detected. There is no general way to recognise one:
the sound alternative is an attribute applied by transport authors, which is worth
nothing until they adopt it. Treat a clean build as "no *known* transport", not as proof.

A dependency is what is checked, not every use. A transport type in a local variable is
a use, and reporting at every call site would make the rule noisy enough to disable.

## When to suppress

None inside a capability. A transport plugin is the correct place for transport types.

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A
suppression without one fails the build.

---

**Back to:** [diagnostics index](README.md) · [Quality gates](../21-Quality-Gates.md)
