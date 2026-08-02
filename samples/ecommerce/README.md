# Sample — E-commerce order placement

A three-step ephemeral flow behind two transports — one HTTP endpoint and one MCP tool —
and three durable flows beside it that show the same declarations over a broker and over a
change feed: validate an order, hold the stock, take the money.

> **This file described a durable, two-transport, policy-driven flow before the sample
> existed, and then described a smaller sample than the one on disk.** Both are corrected
> below rather than quietly rewritten: the paragraphs that said durable execution, a broker
> and the policy pipeline were still to come are gone, because all three are here, and the
> sections that said this sample could not demonstrate a broker, published no event and
> enforced no authorisation are retracted at the point each was made.

```bash
dotnet run --project samples/ecommerce

curl -X POST http://localhost:5000/api/v1/orders \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: order-1' \
  -H 'Authorization: Bearer cashier-token' \
  -d '{"sku":"SKU-1","quantity":2,"paymentToken":"tok_test"}'
```

```json
{ "orderId": "order-1", "receiptId": "receipt-order-1" }
```

**The token is not decoration and this request is refused without it.** `order.validate`
declares `Authorization.Authenticated` and `payment.capture` requires `payment.write`, and
the engine decides both against `HttpContext.User` before the step is dispatched. Drop the
header and the answer is `403 authorization.not_authenticated`; send `shopper-token`
instead and the answer is `403 authorization.permission_denied` — *after* the inventory hold
is taken, so the compensation gives it back. The three tokens are in `Authentication.cs`.

---

## What is here

| File | What it holds |
|---|---|
| `Contracts.cs` | The records on the wire, between steps, and on the outbox. No behaviour. |
| `Capabilities.cs` | Four capabilities and the two ports they depend on. All the business rules. |
| `PlaceOrderFlow.cs` | The control flow: order, compensation, the event, the answer — HTTP and agent. |
| `ConfirmOrderFlow.cs` | The same announcement, `Durable`, so the event is actually staged. |
| `RepriceOrderFlow.cs` + `RepriceBasket.cs` | The consuming half, started by a broker. |
| `ProjectOrderFlow.cs` | The same event observed with no broker at all, off the outbox. |
| `Authentication.cs` | Three demonstration tokens. A stand-in for an OIDC handler, and it says so. |
| `Telemetry.cs` | Spans to the console and a Prometheus `/metrics`, hand-written for constraint C2. |
| `Program.cs` | Composition. `MapFlowX()`, `MapFlowXMcp()`, and both generated subscription registrations. |
| `Infrastructure.cs` | In-memory adapters and the two JSON contexts. |

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
  dispatcher, and the output projection. One per flow, so there are four.
- `FlowXEndpoints.g.cs` — the route registration `app.MapFlowX()` calls, from the
  `[HttpTrigger]` on the flow. Emitted only because this project references
  `FlowX.Http`; a project that does not gets no such file.
- `FlowXAgentTools.g.cs` — `AddFlowXAgentTools()`, one binding per `[AgentTrigger]`,
  emitted only because this project references `FlowX.Mcp`. Same rule, second plugin.
- `FlowXSubscriptions.g.cs` and `FlowXChangeSubscriptions.g.cs` — the bus and change
  registrations, from the `[BusTrigger]` and `[ChangeTrigger]` on two of the flows.
- `FlowXManifest.g.cs` — the manifest for the whole application

Open the first one. Every step carries a `#line` directive back to the line of
`PlaceOrderFlow.cs` that declared it, so a breakpoint on generated code lands in your
own file. If it is hard to read, that is a bug in the generator.

### It is the proof of NativeAOT

```bash
dotnet publish samples/ecommerce -c Release -r linux-x64
```

A self-contained ~18 MB binary, no trim or AOT warnings. Constraint C2 says nothing
that ships may reflect; a reference application that could not publish this way would
make that a claim rather than a property. CI publishes it **and then runs it** —
linking successfully and serving a request are different facts.

**It is also the only statement this repository can make about `plugins/FlowX.Mcp`.**
`Directory.Build.props` sets `IsAotCompatible=false` on every project whose name ends
`.Tests`, deliberately — the fitness functions reflect by design — so no test can say
whether the agent surface publishes natively. This project references it and is the one
assembly published that way, which turns the question into a build failure or nothing.

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

**`PaymentToken` is marked `[Sensitive]`, and that does two things.** It reaches the
manifest — the contract's `input` carries `"sensitive": ["PaymentToken"]`, so a reviewer
or an agent can see which field holds the secret — and it reaches the endpoint as
`PlaceOrderFlow.SensitiveMembers`, which strips matching structured detail out of an
error response. A capability that attached the token to an `Error` would not send it to
the caller. That is the only sink this release serialises such a value on; see
[PLAN.md WP-12a](../../PLAN.md).

**The wire contract is the flow's own types.** There is no request DTO and no
response DTO — `PlaceOrder` and `OrderPlacedResult` go on the wire directly, and the
endpoint returns what the `.Return(...)` clause projected. There is nothing to keep in
step.

**The address is declared once.** `[HttpTrigger("POST", "/api/v1/orders", Idempotent =
true)]` is read once, and that one reading produces both the `triggers` block of the
manifest and the route `app.MapFlowX()` registers — `FlowXEndpoints.g.cs`, next to the
plan under `obj/generated`. `Program.cs` names no method, no route and no contract, so
the address the sample publishes and the address it serves cannot disagree.

---

## Rendering the graph

```bash
dotnet run --project src/FlowX.Cli -- manifest \
  --assembly samples/ecommerce/bin/Debug/net10.0/Ecommerce.dll \
  --output flowx.manifest.json

dotnet run --project src/FlowX.Cli -- graph \
  --manifest flowx.manifest.json --flow order.place --output order-place.mmd
```

`--flow` because this application declares four of them now. Without it the render is all
four subgraphs, which is the right default for reading an application and the wrong one for
reading a flow.

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
5. Delete the `[AgentTrigger]` from `PlaceOrderFlow` — `FlowXAgentTools.g.cs` disappears,
   `Program.cs` stops compiling on `AddFlowXAgentTools()`, and `tools/list` cannot silently
   go empty. Removing the *last* one is a compile error rather than a quieter surface.
6. Swap `RepriceOrderFlow`'s `[BusTrigger("order.placed", …)]` for
   `[ChangeTrigger("order.placed", …)]` — the flow body, its input and its capability are
   unchanged, and the broker leaves the path. That is quality goal Q4 as a one-line edit.

---

## What the sample does not do yet

**`.Emit<OrderPlaced>()` publishes nothing, and the reason is now entirely this sample's
profile.** The step is compiled into the plan and recorded in the manifest, so a consumer
reading the manifest will expect the event — and nothing delivers it. *This paragraph said
at WP-56 that "the engine still stages nothing into `StepCommit.Outbox` for an `Emit` step".
That expired immediately afterwards, and the rest of the chain expired at WP-56b.* Every
other link is built and exercised: the generated `DescribeStep` builds the event body, the
engine stages it in the step's own transaction, `PostgresOutboxPublisher` drains it
at-least-once in per-`partition_key` order, and `RedisStreamEventPublisher` puts it on a
broker. What is missing here is the **first** link — an `Ephemeral` flow keeps no
transaction to stage into. The compiler says so as **FLOWX1024**, and the sample suppresses
it with an explicit `FLOWX-DEBT` marker rather than hiding it. See
[docs/diagnostics/FLOWX1024.md](../../docs/diagnostics/FLOWX1024.md).

**"This sample cannot demonstrate the broker plugin" was this file's flattest false
sentence, and three flows replaced it rather than an edit.** `PlaceOrderFlow` stays
`Ephemeral` for the reason above, so a *second* flow takes DEBT-0001's other resolution:
`ConfirmOrderFlow` declares the same `.Emit<OrderPlaced>` under `Durable`, stages it in the
step's own transaction, and `RepriceOrderFlow` consumes it from a broker under one
`[BusTrigger]`. `ProjectOrderFlow` then observes the *same* staged rows with no broker in
the path at all — one attribute's difference, `[ChangeTrigger]` for `[BusTrigger]`, and
nothing else in the flow, its input or its capabilities moves. Neither consumer names the
emitter and the emitter names neither of them: the topic is the contract's identity.

`dotnet run` still needs no infrastructure, and that is why both consumers are registered
and consume nothing — this host wires no `IBusConsumer`, no journal and no change feed. The
wiring is one call each, and `tests/Ecommerce.Tests/EmitStartsAFlowTests` and
`ChangeStartsAFlowTests` are where it is made, against a real PostgreSQL and a real Redis.

**Service registration is written by hand, and stays that way.** The endpoints and both
subscription registrations are generated — `app.MapFlowX()`, `app.MapFlowXMcp()`,
`AddFlowXSubscriptions()` and `AddFlowXChangeSubscriptions()` are the whole of them, from
the attributes on the flows — but the `AddSingleton` lines above them are not. The generator
knows exactly which types each dispatcher needs, because it wrote that constructor; it does
not know what lifetime any of them should have, and nothing in a flow declares one. A
generated lifetime would be a guess, and a captive dependency is the kind of failure that is
worst in a file nobody wrote.

The cost of that is real and this sample paid it: `ProjectOrderFlow.Dispatcher` was missing
from `Program.cs`, `AddFlowXChangeSubscriptions` resolves a dispatcher *while it registers*,
and the application therefore threw during start-up — for as long as nothing ran it.
`AgentSurfaceTests.TheApplicationStartsAndServesBothTransports` starts this composition root
rather than a parallel one, which is what makes the missing line a red test rather than a
red deployment.

**"Redaction covers one sink" is no longer true either.** `[Sensitive]` reached only
Problem Details bodies when that sentence was written. It now reaches every exit a
`JournalPayload` has — the journal's stored request, each step result, the emitted event
body and `FlowXLogBridge`'s log state — through one redaction pass, and the tool schema an
agent reads publishes the member names as `x-flowx-sensitive`. What it does not reach is
this sample's own console spans, which carry no payload at all.

**"Authorisation is declared, not enforced" is false and was the most consequential
sentence here.** The capabilities carry `Authorization.Authenticated` and
`Authorization.Permission`, those reach the manifest, and the engine now decides each stance
against the invocation's principal before the step is dispatched — which is why this file's
first `curl` carries a token and did not before. `Authentication.cs` is what turns one into
a principal, and it is a stand-in for an OIDC handler rather than a security control.

**And the same decision serves the agent.** `PlaceOrderFlow` declares an `[AgentTrigger]`,
so `order.place` is also the MCP tool `order_place` at `POST /mcp` — `tools/list` projects
the name, the description, the required permissions and the declared side effects out of
`flowx.manifest.json`, and `tools/call` builds its invocation with the same
`HttpTriggerReader` an HTTP route uses. So `shopper-token` is refused at `payment.capture`
with `authorization.permission_denied` over both transports and the hold is released on
both, which is `AgentSurfaceTests.OneStanceRefusesTheSameCallerOnBothTransports` rather than
a claim about two code paths nobody ran together.

**Multi-tenancy is not here, and that is the one gap left.** The tokens carry a `tid` claim
and the spans are tagged with it, but this deployment declares `TenantIsolation.None`: a
tenant is resolved from nothing and isolates nothing, because an ephemeral flow with a
dictionary for a store has no rows to keep apart. [`samples/banking`](../banking) is where
`Row` and `Schema` are demonstrated, one environment variable apart.

**The flow is ephemeral, and that is now a choice rather than a wait.** This paragraph used
to say durable execution was P2 and would arrive. It has: the runtime journals a `Durable`
flow's step boundaries, `plugins/FlowX.Postgres` is a store that passes the conformance
suite unmodified, and a recovered instance puts each completed compensable step back on its
unwind stack as it replays. The compiler says so too — **FLOWX1012** fires on this flow,
because a compensable saga on `Ephemeral` loses its pending unwind when the node dies, and
the hold `ReserveInventory` took is then owned by nobody.

The sample keeps `Ephemeral` and suppresses the rule with a stated reason rather than a
`FLOWX-DEBT` marker: [docs/DEBT.md](../../docs/DEBT.md) is explicit that a trade recorded in
an ADR is a *decision* rather than debt, and this one is
[ADR-0003](../../docs/adr/ADR-0003-execution-profiles.md). The reason is this sample's whole
value — `dotnet run` serves an order with nothing behind it, and the only journal FlowX
ships is PostgreSQL, so `Durable` here would mean a reference application that cannot place
an order without a database, buying a crash-safe unwind for an inventory store that is a
dictionary and a payment gateway that always approves.

So, plainly: **a node that dies between the reservation and the end of this flow leaves the
hold standing, and nothing anywhere records that it should have been released.** That is
what the profile costs. The full argument, including what would change the answer, is on
[docs/diagnostics/FLOWX1012.md](../../docs/diagnostics/FLOWX1012.md#the-reference-sample-fires-this-rule).

---

**See also:** [Capability model](../../docs/07-Capability-Model.md) ·
[Flow definition](../../docs/08-Flow-Definition.md) ·
[Diagnostics](../../docs/diagnostics/README.md) ·
[Quality gates](../../docs/21-Quality-Gates.md)
