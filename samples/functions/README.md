# functions — the same flows, on a host that scales to zero

**What this proves:** a flow declares a trigger once, and the deployment shape is a
build-time consequence rather than a rewrite. Nothing in [`Flows.cs`](Flows.cs)
mentions Azure Functions. `FlowX.Functions` reads the same three attributes the plan
generator reads and emits `obj/generated/…/FlowXFunctions.g.cs`: an `HttpTrigger`, a
`ServiceBusTrigger` and a `TimerTrigger`, each one call into the runtime.

| Declared | Becomes | Which runs |
|---|---|---|
| `[HttpTrigger("POST", "/api/v1/orders", Idempotent = true)]` | `HttpTrigger`, route `api/v1/orders` | `FlowHost.RunAsync`, through `FlowPushSeams.HttpAsync` |
| `[BusTrigger("order.placed", Group = "stock")]` | `ServiceBusTrigger`, `AutoCompleteMessages = false` | `FlowBusScan.AdmitAsync`, then the platform's own settle verb |
| `[CronTrigger("0 2 * * *", TimeZone = "Europe/Berlin")]` | one `TimerTrigger` at `0 * * * * *` | `FlowScheduleScan.RunOnceAsync` |

## The three decisions worth reading the code for

**The declared cron is not in the `TimerTrigger`.** The platform's timer fires every
minute and asks the sweep whether anything is due; `0 2 * * *` in `Europe/Berlin` is
answered from the declaration. Copying it into the binding would have dropped the zone —
a `TimerTrigger` has no field for one — and, worse, would have made a firing at the
wrong minute a *different* occurrence, because
[ADR-0031](../../docs/adr/ADR-0031-an-occurrence-names-the-instance-it-starts.md) derives
the instance id from the expression.

**`Sweeps = HostSweeps.None`.** The platform is the scheduler now. A background sweep
beside a `TimerTrigger` would look at the same declaration twice a minute — harmless,
because ADR-0031 makes the duplicate inert, and therefore exactly the kind of waste
nothing ever fails on. It also keeps the process working, and a host that never goes
idle never scales to zero.

**One timer for every schedule, not one per flow.** `FlowScheduleScan.RunOnceAsync` is a
pass over the whole catalogue; ten scheduled flows would otherwise be ten passes a
minute doing nine tenths of nothing.

## What is not bound here

A `[StreamTrigger]` generates a comment naming itself and its reason rather than
silence — a window is wider than an invocation, and closing one needs the open windows,
the watermark and the checkpoint that a host which scales to zero does not keep. Run
that flow on the ASP.NET host; it is the same declaration.

## Running it

Needs PostgreSQL. Every flow declares `Durable`, and on a push host that is not a
preference: the platform delivers at least once, and `flow_instance`'s primary key is
the only thing that makes a redelivery inert
([ADR-0035](../../docs/adr/ADR-0035-a-delivery-names-the-instance-it-starts.md)).

```bash
export FLOWX_POSTGRES_CONNECTION="Host=localhost;Port=5432;Database=postgres;Username=postgres"
dotnet build samples/functions
```

**The Functions local runtime run happened on 2026-08-15** — Core Tools 4.6.0 (from its GitHub release; the npm route's CDN is proxy-blocked in some environments), the Service Bus emulator and Azurite: all three trigger arms executed end to end, journal rows verified. *This sentence previously recorded the run as owed.*
`azure-functions-core-tools` installs by downloading a binary from
`cdn.functions.azure.com`, which this repository's build environment refuses (HTTP 403 at
the egress proxy), and a Service Bus end-to-end additionally needs a broker. What stands
in for it is [`tests/Functions.Tests`](../../tests/Functions.Tests), which invokes the
**generated** entry points with the arguments the platform would pass — a real
`ServiceBusReceivedMessage`, a recording `ServiceBusMessageActions` — against a real
PostgreSQL journal, and asserts the journal rows and the settle verb. What that cannot
cover is the platform half: that the host binds these signatures over its gRPC channel,
and that `AutoCompleteMessages = false` is honoured by a live Service Bus extension.
