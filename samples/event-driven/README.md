# Sample — Transport portability

**Claim it is meant to prove:** quality goal **Q4** and criterion
[**V2**](../../docs/01-Vision.md#7-measurable-success-criteria) — moving a flow from
HTTP to Kafka to cron changes **zero lines of business logic**, asserted mechanically
in CI.

> [!NOTE]
> **Two of the three transports have since been built, and the warning box after
> this one is kept as written rather than edited.** *"`plugins/` holds three plugins
> and `FlowX.Http` is the only transport among them"* is false: there are four, and
> `plugins/FlowX.Redis` serves a bus — `RedisStreamEventPublisher` drains the outbox
> onto a stream and `RedisStreamBusConsumer` starts a flow per delivery, deriving the
> instance id so a redelivery starts nothing new
> ([ADR-0035](../../docs/adr/ADR-0035-a-delivery-names-the-instance-it-starts.md)).
> Cron is served too — see [scheduler](../scheduler/).
>
> **And the zero-lines claim is demonstrated one attribute at a time.**
> `samples/ecommerce` runs the same event chain over a broker and over the outbox
> itself: `RepriceOrderFlow` and `ProjectOrderFlow` take the same input, the same
> capability shape and the same profile, and differ by `[BusTrigger]` against
> `[ChangeTrigger]` and by nothing else. Both are consumers of one
> `.Emit<OrderPlaced>` that names neither of them.
>
> **What is left is Kafka specifically, and WP-71** — the mechanical CI assertion
> this page is built around, which is still unwritten and is the reason the claim
> above is demonstrated rather than *asserted*.

> [!WARNING]
> **This sample has no code.** `samples/event-driven/` is this file and nothing
> else: no project, no flow, no capability, no test, and no CI assertion. The
> mechanical check the whole page is built around is **WP-71**, unwritten.
>
> **This is the most misleading of the code-less samples, and the reason is worth
> stating precisely: the attribute is real.** `KafkaTriggerAttribute` ships
> in `src/FlowX.Abstractions/Triggers/TriggerAttributes.cs`. A flow that applies
> `[KafkaTrigger("billing.invoice_requested", Group = "invoicing")]` **compiles**,
> and `EveryTriggerKindTheAbstractionShipsIsRecognised` asserts it reaches
> `flowx.manifest.json` as `"kind": "Bus"` carrying `transport: kafka`, its topic
> and its consumer group, where `flowx diff` will report a breaking change to it.
>
> **Nothing serves it.** `plugins/` holds three plugins and `FlowX.Http` is the
> only *transport* among them; there is no Kafka client anywhere in this
> repository, and no `ITriggerSource` contract for one to implement
> ([17](../../docs/17-Plugin-System.md)). `TriggerKind` is switched on in no file
> under `src/FlowX.Runtime`, `src/FlowX.Hosting` or `plugins/`, and
> `ANonHttpTriggerProducesNoEndpoint` is the test that pins a bus trigger to
> nothing on purpose. So an author who writes the commit-2 flow gets a green
> build, a manifest entry downstream consumers can read — and a topic no process
> is subscribed to. **A silent success is a worse outcome than a build error**,
> which is why this box is longer than the others.
>
> `[CronTrigger]` sits one row down in exactly the same position: it compiles,
> publishes `"kind": "Schedule"` with its cron expression and time zone, and there
> is no scheduler — see [scheduler](../scheduler/).
>
> | What has to exist first | Where it comes from |
> |---|---|
> | `ITriggerSource` and a published transport conformance suite | **WP-70**, [P3](../../PLAN.md#6-p3--transport-breadth). `PluginsPassConformance` has been blocked on it since P1 |
> | The unchanged-file CI assertion this page is built around | **WP-71**, P3 — it is to run against `samples/ecommerce` first, where it must pass trivially |
> | A Kafka transport: consumer loop, offset commit after completion, dead-lettering | **WP-72**, P3 |
> | Cron as the third transport, fired once across N nodes | **WP-75**, P3 — leader election *is* a lease, which is why it follows P2 |
> | A broker on the far side of `.Emit<T>()` | `IEventPublisher` is declared and the only implementation anywhere is a recording test double ([11 §5](../../docs/11-Distributed-Runtime.md#5-the-transactional-outbox)) |
>
> Read the rest as the design a P3 implementer is held to. Outside the HTTP
> commit, do not read any sentence below as describing behaviour you can observe
> today.

## The experiment

Three git commits, three transports, one unchanged flow body.

```csharp
// commit 1 — HTTP
[Flow("invoice.issue", Profile = ExecutionProfile.Durable)]
[HttpTrigger("POST", "/api/v1/invoices")]
public sealed partial class IssueInvoiceFlow : Flow<IssueInvoice, Invoice>
{
    protected override void Define(IFlowBuilder<IssueInvoice, Invoice> flow) => flow
        .Step<ValidateInvoice>()
        .Step<CalculateTax>()
        .Step<PersistInvoice>().CompensateWith<VoidInvoice>()
        .Emit<InvoiceIssued>()
        .Return(ctx => ctx.Get<Invoice>());
}

// commit 2 — Kafka. One attribute line differs.
[KafkaTrigger("billing.invoice_requested", Group = "invoicing")]

// commit 3 — all three at once.
[HttpTrigger("POST", "/api/v1/invoices")]
[KafkaTrigger("billing.invoice_requested", Group = "invoicing")]
[CronTrigger("0 3 * * *", TimeZone = "UTC")]
```

> **All three commits compile; only the first one runs.** Commit 1 is served —
> `FlowXEndpoints.g.cs` maps the route. Commits 2 and 3 build clean, publish
> `"kind": "Bus"` and `"kind": "Schedule"` into the manifest, and are activated by
> nothing. `.Emit<InvoiceIssued>()` reaches `IEventPublisher` and stops there.
> `Profile = ExecutionProfile.Durable` is honoured since WP-52 and needs a
> journal registered, or the flow is refused with `flow.durability_not_configured`
> ([11](../../docs/11-Distributed-Runtime.md)).

## The CI assertion

> **This test does not exist and neither does `GitFixture`.** Nothing under
> `tests/` reads git history. It is **WP-71**, whose first job is to run against
> `samples/ecommerce` — a sample that does exist — and pass there trivially.

```csharp
[Fact]
public void Business_logic_is_byte_identical_across_transport_commits()
{
    var http  = GitFixture.FileAt("commit-1-http",  "IssueInvoiceFlow.cs");
    var kafka = GitFixture.FileAt("commit-2-kafka", "IssueInvoiceFlow.cs");

    StripAttributes(http).Should().Be(StripAttributes(kafka));

    GitFixture.ChangedFiles("commit-1-http", "commit-2-kafka")
        .Should().BeEquivalentTo("IssueInvoiceFlow.cs");   // no capability touched
}
```

Q4 is not a claim in a README here; it is a failing test if it stops being true.

## Event chaining

```mermaid
flowchart LR
    T1(["HTTP"]) --> F1["invoice.issue"]
    T2(["Kafka billing.invoice_requested"]) --> F1
    T3(["Cron 03:00 UTC"]) --> F1
    F1 --> E1[["invoice.issued"]]
    E1 --> F2["notification.send"]
    E1 --> F3["ledger.record"]
    E1 --> F4["analytics.ingest"]
    F3 --> E2[["ledger.recorded"]]
    E2 --> F5["reconciliation.check"]
```

Drawn by hand, for now. `flowx graph` renders one manifest as a flowchart and has
no `--events` switch; the CLI has four verbs — `graph`, `manifest`, `diff`,
`verify` ([22-CLI](../../docs/22-CLI.md)). The estate-wide topology above is what
those manifests make *possible*, not something any command assembles today, so
this diagram can and does drift.

## Outbox guarantee

> **Not runnable.** `IntegrationTestHost`, `UseKafka()` and
> `KillDuringOutboxPublish()` exist nowhere in `tests/`. What does exist is the
> half of the path below the broker: `PostgresOutboxPublisher` drains the outbox
> at-least-once in per-`partition_key` order, and `EmitReachesTheBrokerTests`
> exercises `.Emit<T>()` end to end — into a recording test double, because no
> plugin implements `IEventPublisher`. The republish-and-deduplicate guarantee
> stated below is therefore proved on this side of the interface and unproved on
> the far one.

```csharp
[Fact]
public async Task Event_is_published_exactly_once_even_if_the_process_dies_mid_publish()
{
    await using var host = await IntegrationTestHost.CreateAsync(c => c.UseKafka().UsePostgresJournal());

    await host.RunFlow<IssueInvoiceFlow>(AnInvoice());
    await host.KillDuringOutboxPublish();          // crash between publish and mark-published
    await host.RestartAndDrainOutbox();

    var events = await host.Kafka.ConsumeAll("invoice.issued");
    events.Should().HaveCount(2);                            // at-least-once: republished
    events.Select(e => e.Key).Distinct().Should().HaveCount(1);
    host.Consumer.ProcessedDistinct.Should().Be(1);          // idempotency deduplicated it
}
```

This test states the honest guarantee: FlowX republishes, the consumer
deduplicates, and the *effect* happens once
([11 §4](../../docs/11-Distributed-Runtime.md#4-exactly-once-honestly)).

## Things to try

*Nothing here can be tried yet — there is no project to run them against. Kept as
the acceptance list this sample is written to, with what each one currently needs.*

1. Add `[MqttTrigger("devices/+/invoice")]` — a fourth transport, still zero
   logic changes. *A plugin author can already declare one: carry
   `[TriggerKind(TriggerKind.Bus)]` on the attribute class and the compiler reads
   the kind out of a referenced assembly. That half of **WP-70** shipped three
   phases early. The consumer behind it is **WP-76**.*
2. Break the `InvoiceIssued` contract and run `flowx diff` — CI fails, naming the
   downstream consumers that would break. *`flowx diff` is the one item on this
   list that works today, against `flowx.manifest.json`.*
3. Stop the broker and run the flow — the outbox accumulates, flows keep
   completing, and events drain when the broker returns. *The accumulating half is
   real and tested against PostgreSQL; there is no broker to stop.*
