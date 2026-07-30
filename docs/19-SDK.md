# 19 — SDK and Developer Experience

> **Status:** Accepted · **Audience:** application engineers
> **Answers:** what does using FlowX actually feel like, minute to minute?

---

## 1. First five minutes

```bash
dotnet new install FlowX.Templates
dotnet new flowx --name Ordering
cd Ordering
flowx dev up          # Postgres + Redpanda + OTel + Studio, one command
dotnet run
```

```
FlowX 1.0.0 · Ordering v1.0.0 · role=all
  Flows        3   (2 ephemeral, 1 durable)
  Capabilities 7
  Triggers     5   (3 http, 1 kafka, 1 cron)
  Manifest     .artifacts/flowx.manifest.json
  Studio       http://localhost:5055/flowx
  Ready in 148 ms
```

The scaffold is one working vertical slice — a flow, its capabilities, its
contracts and its tests — not an empty folder tree. New engineers learn the model
by modifying something that runs.

---

## 2. The package surface

| Package | Contains | Referenced by |
|---|---|---|
| `FlowX.Abstractions` | `ICapability`, `Flow<,>`, attributes, `Result<T>` | your application, plugins |
| `FlowX.Sdk` | metapackage: Abstractions + Compiler + Hosting | your application |
| `FlowX.Compiler` | analyzers + generators (analyzer asset, not a runtime dependency) | via Sdk |
| `FlowX.Runtime` | engines | via Sdk |
| `FlowX.Runtime.Durable` | journal, leases, replay | apps with durable flows |
| `FlowX.Testing` | `FlowTestHost`, fakes, virtual time, assertions | test projects |
| `FlowX.Http` / `.Kafka` / `.Cron` / … | trigger + publisher plugins | as needed |
| `FlowX.Cli` | dotnet tool | developer machines, CI |

Deliberately small. A new user references `FlowX.Sdk` plus the transports they
use, and nothing else.

---

## 3. Composition root

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddFlowX(flowx =>
{
    flowx.UseHttp()                                        // generated endpoints
         .UseKafka(builder.Configuration.GetSection("Kafka"))
         .UseCron()
         .UseDurableJournal(j => j.UsePostgreSql(builder.Configuration.GetConnectionString("Journal")))
         .UseLeases(l => l.UseRedis(builder.Configuration.GetConnectionString("Redis")))
         .UseTelemetry(t => t.UseOtlp());

    flowx.AddApplication<OrderingAssembly>();              // generated registration, no scanning
});

var app = builder.Build();
app.MapFlowX();                                            // generated endpoint mapping
app.Run();
```

There is no `AddMediatR(typeof(X).Assembly)`-style reflection scan. `AddApplication<T>`
resolves to generated static registration code. This is what makes cold start
148 ms and NativeAOT possible.

---

## 4. The inner loop

```mermaid
flowchart LR
    A["Write capability<br/>+ its unit test"] --> B["Write flow"]
    B --> C{"Build"}
    C -- "FLOWX diagnostic" --> D["Fix — the message names<br/>the symbol and the fix"]
    D --> C
    C -- ok --> E["Flow test with<br/>substituted capabilities"]
    E --> F["flowx graph — see it"]
    F --> G["Run locally against<br/>flowx dev up"]
    G --> H["PR: benchmarks + fitness<br/>functions + flowx diff"]
```

### Diagnostics that teach

```
error FLOWX1014: Retry policy on step 2 requires capability 'payment.capture'
                 to be idempotent, but it declares Idempotent = false.
                 Retrying a non-idempotent capability risks duplicate side effects.

                 Fix one of:
                   1. If capture is safe to repeat with the same idempotency key,
                      set [Capability(..., Idempotent = true)] on CapturePayment.cs:12
                   2. Remove .WithPolicy(Policies.PaymentGateway) from PlaceOrderFlow.cs:19
                   3. Add .CompensateWith<RefundPayment>() and drop the retry.

                 See https://flowx.dev/diag/FLOWX1014
```

Every `FLOWX*` diagnostic carries: what is wrong, why it matters, the exact
location, concrete alternatives, and a link. `EveryDiagnosticIsHelpful` is a CI
test — a diagnostic that fails to explain itself fails the build (P12).

---

## 5. The CLI

| Command | Purpose |
|---|---|
| `flowx new flow <name>` / `capability <name>` | scaffold with tests |
| `flowx graph [--flow x] [--format mermaid\|dot\|json\|yaml] [--live]` | visualise |
| `flowx diff [--baseline <ref>]` | breaking-change detection — **the CI gate** |
| `flowx verify [--complete] [--cost] [--runtime]` | manifest completeness, profile sanity, deployed-vs-built |
| `flowx query "<expression>"` | query the knowledge graph |
| `flowx replay --instance <id> [--mode inspect\|simulate\|resume\|fork]` | incident analysis and recovery |
| `flowx run <flow> --input <json>` | invoke a flow from the CLI (a trigger like any other) |
| `flowx signal <instance> <name> --payload <json>` | deliver a signal to a suspended flow |
| `flowx cancel <instance>` | cancel with compensation |
| `flowx generate openapi\|asyncapi\|alerts\|dashboard\|mcp\|c4\|tests` | derived artifacts |
| `flowx tenant provision\|suspend\|migrate\|purge` | tenant lifecycle |
| `flowx bench [--compare <baseline>]` | run the benchmark suite |
| `flowx dev up\|down\|graph` | local infrastructure |
| `flowx ai review\|document\|test\|explain\|optimize\|impact` | optional AI layer |

---

## 6. Testing kit

```csharp
// 1 — capability: a pure function. No host, no DI, no infrastructure.
var result = await new ReserveInventory(fakeStore)
    .ExecuteAsync(new ReserveRequest("SKU-1", 2), CapabilityContext.ForTest(), default);

// 2 — flow: real orchestration, substituted capabilities.
var host = FlowTestHost.For<PlaceOrderFlow>()
    .Substitute<CapturePayment>(_ => Result.Fail<Capture>(PaymentErrors.Declined("insufficient_funds")))
    .WithVirtualTime()
    .Build();

var outcome = await host.RunAsync(AnOrder());

outcome.Should().HaveFailedWith("payment.declined");
outcome.Should().HaveCompensated<ReleaseInventory>();
outcome.Trace.Should().HaveExecutedInOrder(
    "order.validate", "inventory.reserve", "payment.capture", "inventory.release");

// 3 — durable semantics: crash and resume, deterministically.
var durable = FlowTestHost.For<PlaceOrderFlow>().WithJournal(InMemoryJournal.Create()).Build();
await durable.RunUntilStep(2);
await durable.SimulateNodeCrash();
var resumed = await durable.ResumeOnNewNode();
resumed.Should().HaveCompleted();
resumed.Should().NotHaveReexecuted("inventory.reserve");   // the property that matters

// 4 — trigger: end to end through the real transport.
await using var it = await IntegrationTestHost.CreateAsync(c => c.UseKafka().UsePostgresJournal());
await it.Kafka.PublishAsync("orders.requested", AnOrder());
await it.WaitForFlow("order.place").ToComplete(TimeSpan.FromSeconds(10));
```

`WithVirtualTime()` is what makes resilience testing practical: retries,
timeouts, breaker windows and multi-day durable timers all execute instantly and
deterministically. Tests that sleep are the reason resilience is usually
untested; FlowX removes the excuse.

---

## 7. IDE experience

| Feature | Delivered by |
|---|---|
| Diagnostics with code fixes | Roslyn analyzers + fix providers |
| Flow graph in a tool window | FlowX extension (VS, Rider, VS Code) reading the manifest |
| Navigate step → capability | generated code with `SourceLink` |
| CodeLens: "used by 3 flows" | manifest-backed |
| Debug generated code | `EmitCompilerGeneratedFiles` on by default |
| Snippets: `flowcap`, `flowflow`, `flowtest` | template package |
| Live topology in the editor | Studio embedded view |

Debuggability of generated code is a deliberate mitigation for risk R1
([05 §11](05-Architecture.md#11-risks-and-technical-debt)): the generated plan is
ordinary, readable, breakpoint-able C# on disk — not an opaque build artifact.

---

## 8. Migration from existing code

Incremental adoption, in the order that keeps a system shippable throughout
(mitigation for risk R4):

```mermaid
flowchart TD
    S0["Existing ASP.NET Core app"] --> S1["1 · Add FlowX.Sdk<br/>coexists; nothing changes"]
    S1 --> S2["2 · Wrap one existing service<br/>method as a capability"]
    S2 --> S3["3 · One flow, one endpoint,<br/>behind a feature flag"]
    S3 --> S4["4 · Compare metrics; expand<br/>flow by flow"]
    S4 --> S5["5 · Add a Kafka trigger to an<br/>existing flow — no logic change"]
    S5 --> S6["6 · Convert sagas to Durable;<br/>retire the hand-written state machine"]
    S6 --> S7["7 · Delete controllers, consumers,<br/>hosted services"]
```

| Coming from | Bridge |
|---|---|
| MediatR | `FlowX.Bridge.MediatR` — an `IRequestHandler` is exposed as a capability; migrate call sites gradually |
| MassTransit | keep the consumer, have it dispatch to a flow; swap to `[KafkaTrigger]` later |
| Hangfire / Quartz | `[CronTrigger]` on the flow; the job body becomes a capability |
| Temporal | activities → capabilities, workflows → durable flows; the mental model transfers directly |
| Plain services | wrap as capabilities; compose in flows; delete the orchestrating service |

You never rewrite the system to try FlowX. You wrap one method, ship it, and
measure.

---

## 9. What a use case costs

```
src/Ordering.Application/PlaceOrder/
├── PlaceOrderFlow.cs           28 lines   ← orchestration + triggers + policies
├── ValidateOrder.cs            24 lines   ← capability
├── ReserveInventory.cs         21 lines   ← capability
├── ReleaseInventory.cs         18 lines   ← compensation
├── CapturePayment.cs           26 lines   ← capability
└── PlaceOrderTests.cs          61 lines   ← unit + flow tests
                               ───────────
                                178 lines, 6 files
```

Against the ~17-file baseline in [01-Vision §1](01-Vision.md#1-the-observation),
and with the HTTP endpoint, Kafka consumer, OpenAPI document, retry policy, saga
compensation, distributed tracing, metrics, alerts and architecture diagram all
generated. That is quality criterion V1, and it is the promise the whole platform
is built to keep.

---

**Next:** [20 — Roadmap](20-Roadmap.md)
