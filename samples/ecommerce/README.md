# Sample — E-commerce order placement

A three-step ephemeral flow behind one HTTP endpoint: validate an order, hold the
stock, take the money. Small on purpose, and every part of it is load-bearing
somewhere in this repository's claims.

> **This file described a durable, two-transport, policy-driven flow before the
> sample existed.** It now describes the sample that does. The larger version is not
> deleted so much as deferred: durable execution, Kafka, and the policy pipeline
> arrive in P1–P4, and this file will grow with them. A sample README that documents
> features the sample does not have is worse than no sample.

```bash
dotnet run --project samples/ecommerce

curl -X POST http://localhost:5000/api/v1/orders \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: order-1' \
  -d '{"sku":"SKU-1","quantity":2,"paymentToken":"tok_test"}'
```

```json
{ "orderId": "order-1", "receiptId": "receipt-order-1" }
```

---

## What is here

| File | What it holds |
|---|---|
| `Contracts.cs` | The records on the wire and between steps. No behaviour. |
| `Capabilities.cs` | Four capabilities and the two ports they depend on. All the business rules. |
| `PlaceOrderFlow.cs` | The control flow: order, compensation, the event, the answer. |
| `Program.cs` | Composition. Registrations and one route. |
| `Infrastructure.cs` | In-memory adapters and the JSON context. |

The flow:

```csharp
[Flow("order.place", Version = "1.0.0", Profile = ExecutionProfile.Ephemeral, Owner = "orders")]
[FlowDeadline("PT30S")]
public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
{
    protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<ValidateOrder>()
            .Step<ReserveInventory>().CompensateWith<ReleaseInventory>()
            .Step<CapturePayment>()
            .Emit<OrderPlaced>(ctx => new OrderPlaced(/* … */))
            .Return(ctx => new OrderPlacedResult(/* … */));
    }
}
```

There is no business rule in that method and no mention of HTTP. It expresses
**order, condition and recovery** and nothing else, which is why moving this flow
behind another transport would change the attribute above it and nothing below it.

---

## What the sample is actually for

### It is the test of the generator

`Ecommerce.csproj` references `FlowX.Compiler` as an **analyzer**, not as a library:

```xml
<ProjectReference Include="../../src/FlowX.Compiler/FlowX.Compiler.csproj"
                  OutputItemType="Analyzer"
                  ReferenceOutputAssembly="false" />
```

So the generator runs inside this project's build, against this project's source,
exactly as it would for anyone else. Its output is on disk under
`obj/generated/FlowX.Compiler/`:

- `Ecommerce.PlaceOrderFlow.Flow.g.cs` — the compiled `ExecutionPlan`, the step
  dispatcher, and the output projection
- `FlowXManifest.g.cs` — the manifest for the whole application

Open the first one. Every step carries a `#line` directive back to the line of
`PlaceOrderFlow.cs` that declared it, so a breakpoint on generated code lands in your
own file. If it is hard to read, that is a bug in the generator.

### It is the proof of NativeAOT

```bash
dotnet publish samples/ecommerce -c Release -r linux-x64
```

A self-contained ~11 MB binary, no trim or AOT warnings. Constraint C2 says nothing
that ships may reflect; a reference application that could not publish this way would
make that a claim rather than a property. CI publishes it **and then runs it** —
linking successfully and serving a request are different facts.

### It is where quality goal Q2 is checked

`tests/Ecommerce.Tests/CapabilityTests.cs` has no host in it. No
`WebApplicationFactory`, no service provider, no in-memory server — a capability is a
class with a method, and the tests construct one and call it.

`tests/Ecommerce.Tests/PlaceOrderEndpointTests.cs` covers what that structurally
cannot: status codes, media types and JSON bodies over a real server, including the
compensation path.

---

## The things worth reading the code for

**The idempotency key comes from the context.** `ReserveInventory` reads
`ctx.IdempotencyKey` and passes it downstream, so replaying a request reserves once:

```bash
curl … -H 'Idempotency-Key: order-1' …   # 200, stock 10 → 8
curl … -H 'Idempotency-Key: order-1' …   # 200, stock still 8
```

A capability that generated its own key would reserve twice on the second attempt and
nothing else in the system would notice.

**`CapturePayment` declares `Idempotent = false`.** That single declaration is what
makes attaching a retry policy a build error (FLOWX1014). Retrying a capture is a
duplicate charge, and the compiler refusing is cheaper than the refund process.

**Compensation runs in reverse.** If the capture fails, `ReleaseInventory` gives the
hold back. Covered by `AFailedPaymentReleasesTheReservation`, which is the only way to
reach that path — no happy-path run does.

**Errors are values, and they map themselves.** `OrderErrors.OutOfStock` carries
`ErrorCategory.Conflict`, and that is the whole reason the endpoint answers `409`:

```json
{
  "type": "https://flowx.dev/errors/inventory.out_of_stock",
  "title": "The request conflicts with the current state",
  "status": 409,
  "detail": "'SKU-1' has 8 in stock.",
  "code": "inventory.out_of_stock",
  "correlationId": "0HNNE5LSTBR72:00000001",
  "sku": "SKU-1",
  "available": 8
}
```

No capability names a status code. The category is the contract, and the mapping
lives in one place.

**The wire contract is the flow's own types.** There is no request DTO and no
response DTO — `PlaceOrder` and `OrderPlacedResult` go on the wire directly, and the
endpoint returns what the `.Return(...)` clause projected. There is nothing to keep in
step.

---

## Rendering the graph

```bash
dotnet run --project src/FlowX.Cli -- manifest \
  --assembly samples/ecommerce/bin/Debug/net10.0/Ecommerce.dll \
  --output flowx.manifest.json

dotnet run --project src/FlowX.Cli -- graph \
  --manifest flowx.manifest.json --output order-place.mmd
```

```mermaid
flowchart TD
    subgraph f0["order.place@1.0.0 (Ephemeral)"]
    direction TB
        f0s0["order.validate"]
        f0s1["inventory.reserve ⚡"]
        f0s2["payment.capture ⚡ ⚠"]
        f0s3(["emit order.placed"])
        f0s0 --> f0s1
        f0s1 --> f0s2
        f0s2 --> f0s3
        f0s1c[/"undo inventory.release"/]
        f0s1 -.-> f0s1c
    end
```

`⚡` marks a declared side effect, `⚠` a capability that is not idempotent. Both come
from the `[Capability]` attributes, through the manifest, without anyone drawing a
diagram.

---

## Things to try

1. Add `.WithPolicy(Policies.Retry)` to `CapturePayment` — the build fails with
   **FLOWX1014**, because the capability declares `Idempotent = false`.
2. Remove the `partial` keyword from `PlaceOrderFlow` — **FLOWX1001**, because the
   generated plan has nowhere to live.
3. Delete the `.Return(...)` clause — the generated `Projection` field disappears and
   `Program.cs` stops compiling, rather than quietly returning a default.
4. Reorder the two `.Step<>` calls — the generated dispatcher's `ctx.Get<T>()` types
   change with them, because steps bind by contract, not by position.

---

## What the sample does not do yet

**`.Emit<OrderPlaced>()` publishes nothing.** The step is compiled into the plan and
recorded in the manifest, so a consumer reading the manifest will expect the event —
but transactional outbox publication is not implemented in this release. The compiler
says so as **FLOWX1024**, and the sample suppresses it with an explicit `FLOWX-DEBT`
marker rather than hiding it. See
[docs/diagnostics/FLOWX1024.md](../../docs/diagnostics/FLOWX1024.md).

**The endpoint is registered by hand.** `[HttpTrigger]` will generate the `MapFlow`
call in a later phase. It is written out in `Program.cs` so the sample runs against
what exists today.

**Authorisation is declared, not enforced.** The capabilities carry
`Authorization.Authenticated` and `Authorization.Permission`, and those reach the
manifest, but the policy pipeline that acts on them arrives with P4.

**The flow is ephemeral.** Durable execution — the journal, the crash-and-resume
guarantee — is P2. Until then a node that dies mid-flow loses the flow, and the
compensation that would have run with it.

---

**See also:** [Capability model](../../docs/07-Capability-Model.md) ·
[Flow definition](../../docs/08-Flow-Definition.md) ·
[Diagnostics](../../docs/diagnostics/README.md) ·
[Quality gates](../../docs/21-Quality-Gates.md)
