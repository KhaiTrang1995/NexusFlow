using FlowX;
using FlowX.Generated;
using FlowX.Hosting;
using FlowX.Postgres;
using FlowX.Runtime;
using Microsoft.AspNetCore.Authentication;
using Workflow;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddRouting();
builder.Services.AddFlowX(options =>
{
    options.ApplicationName = "Workflow";

    // Which node this is, as a lease records its owner. The default is the machine name, which
    // is right under an orchestrator and useless when three replicas are three processes on one
    // laptop — and three replicas on one laptop is exactly how "a schedule fires once across
    // every node" is demonstrated without a cluster.
    if (Environment.GetEnvironmentVariable("FLOWX_SAMPLE_NODE") is { Length: > 0 } node)
    {
        options.NodeName = node;
    }

    // The resolution of every timer this application declares: a `.Delay` of one second under
    // a ten-second sweep waits between one and eleven. A wait is a lower bound and never an
    // upper one, which is the same promise a scheduled trigger makes and the only one a sweep
    // can keep — so a sample that winds its waits down to seconds has to wind this down too,
    // or it spends most of a demonstration inside the sweep interval rather than inside the
    // wait it is demonstrating.
    if (TimeSpan.TryParse(
            Environment.GetEnvironmentVariable("FLOWX_SAMPLE_TIMER_SCAN"),
            System.Globalization.CultureInfo.InvariantCulture,
            out var sweep) && sweep > TimeSpan.Zero)
    {
        options.TimerScanInterval = sweep;
    }

    // And the resolution of every schedule, for the same reason and with the same shape. A
    // `0 2 * * *` under a ten-second sweep fires between 02:00:00 and 02:00:10; a demonstration
    // that wants to watch a schedule fire needs both this and a denser expression, which is
    // registered below rather than declared on the flow.
    if (TimeSpan.TryParse(
            Environment.GetEnvironmentVariable("FLOWX_SAMPLE_SCHEDULE_SCAN"),
            System.Globalization.CultureInfo.InvariantCulture,
            out var scheduleSweep) && scheduleSweep > TimeSpan.Zero)
    {
        options.ScheduleScanInterval = scheduleSweep;
    }
});

// Authentication, which is the only reason this application has a principal at all.
//
// The stances are on the capabilities — offer.validate admits any authenticated caller, the six
// writes each name a permission — and the engine decides them against HttpContext.User before
// each step is dispatched. Nothing here names a route or a permission: a rule attached to an
// endpoint would hold over HTTP and not over the cron schedule below, which is exactly the
// transport-attached authorisation a capability stance exists to replace.
//
// offer.window.close needs none of this. It is started by an occurrence, its one capability
// declares Authorization.Internal, and it therefore fires and settles with no principal in the
// path at all — which is what makes a scheduled flow deployable under an enforced authorisation
// model rather than an exception to it.
builder.Services
    .AddAuthentication(PeopleOpsTokenHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, PeopleOpsTokenHandler>(
        PeopleOpsTokenHandler.SchemeName, null);

// The journal, and the whole reason this sample has a dependency samples/ecommerce does not.
//
// `employee.onboard` declares Durable. A durable flow on a host that registered no journal
// and no lease store is refused with `flow.durability_not_configured` before its first step —
// not run ephemerally — so this line is not optional configuration, it is the difference
// between an application that starts and one that answers every request with a refusal.
// AddFlowX resolves IFlowJournal, ILeaseStore, IRecoveryIndex and ITimerIndex out of the
// container; this registers all four over one data source. The last of them is what makes
// `offer.accept`'s `.Delay` come due and its `.OnTimeout` fire — a host that registered no
// ITimerIndex would park instances correctly and never wake them.
builder.Services.AddFlowXPostgres(
    builder.Configuration.GetConnectionString("FlowX")
    ?? Environment.GetEnvironmentVariable("FLOWX_POSTGRES_CONNECTION")
    ?? "Host=localhost;Port=5432;Database=postgres;Username=postgres");

// Infrastructure. In memory here; the capabilities do not know or care.
builder.Services.AddSingleton<IPeopleDirectory, InMemoryPeopleDirectory>();
builder.Services.AddSingleton<IAssetRegistry, InMemoryAssetRegistry>();
builder.Services.AddSingleton<IAccessControl, InMemoryAccessControl>();
builder.Services.AddSingleton<IFacilities, InMemoryFacilities>();
builder.Services.AddSingleton<IOfferDesk, InMemoryOfferDesk>();

// The capabilities themselves. The generated dispatchers take them as constructor parameters,
// so a missing registration is a startup failure that names the type rather than a null
// reference on the first request.
builder.Services.AddFlowXCapabilities();

// offer.accept's three. It is the flow that waits, and none of its capabilities knows that:
// a suspension point is the engine's business, so `onboarding.start` simply binds the
// contract the signal delivered, exactly as it would bind an earlier step's output.

// offer.window.close's one. Nothing calls it; a cron expression starts it.

// Both dispatchers. The parent's takes the child's, because composing a flow is a typed call
// the generator emits into the parent's dispatcher — which is what keeps the engine free of
// reflection even across a sub-flow boundary.

var app = builder.Build();

// Applying DDL is a decision, not a consequence of building a container — every replica of a
// rolling update would otherwise race to migrate as it starts, and a failure would surface as
// a pod that will not start rather than as a deployment step that did not pass. A real
// deployment calls this from wherever it runs schema changes. A sample calls it here, once,
// so that `dotnet run` against an empty database works.
await app.Services.GetRequiredService<PostgresMigrator>()
    .MigrateAsync(CancellationToken.None)
    .ConfigureAwait(false);

// What makes a parked instance resumable by a sweep rather than only by a request.
//
// A trigger arrives with its plan and its dispatcher in hand; an instance a sweep finds is a
// row carrying a flow id and a version and nothing else, so something has to turn those two
// strings back into an ExecutionPlan and an IStepDispatcher. That is the catalogue, and this
// is the registration `offer.accept` needs in order for its `.Delay` to ever come due and its
// `.OnTimeout` to ever fire — without it FlowTimerScan finds the instance, cannot run it, and
// counts it as NotRunnable, which is the honest answer to "this node was not deployed with
// that flow".
//
// Only the flow that waits. The other two are started by a request and finish inside it, so
// nothing ever looks for them by id.
app.Services.GetRequiredService<FlowCatalog>().Add(
    AcceptOfferFlow.Plan,
    app.Services.GetRequiredService<AcceptOfferFlow.Dispatcher>());

// Every schedule this application declares, generated from the [CronTrigger] on the flow that
// declares it — the same arrangement `app.MapFlowX()` has with [HttpTrigger], and the reason
// there is no hosted service, no timer and no mention of 02:00 anywhere in this file.
//
// One line registers `offer.window.close` at `0 2 * * *` in Europe/Berlin. From here on every
// node running this application sweeps for it, every node computes the same occurrence, and
// every node derives the same instance id from it — so the fire happens once, and the eight
// replicas that lost the race are refused by the lease store and then by the journal's primary
// key (docs/adr/ADR-0031-an-occurrence-names-the-instance-it-starts.md).
//
// It also puts `offer.window.close` in the FlowCatalog, so a node that dies mid-firing leaves an
// instance the recovery sweep can take over. That is done inside the registration rather than
// asked of this file, because a scheduled instance is a durable instance like any other and
// forgetting it would be a silent loss rather than a startup failure.
app.Services.AddFlowXSchedules();

// And a second, denser schedule for a demonstration — registered rather than declared.
//
// `0 2 * * *` is the business number and is what flowx.manifest.json publishes; overriding it
// would mean the declaration a reader sees is not the one that runs, which is the whole failure
// the generated registration exists to prevent. So this adds a schedule beside it, under its own
// expression, and therefore under its own instance ids. `FLOWX_SAMPLE_SCHEDULE_CRON="* * * * *"`
// with `FLOWX_SAMPLE_SCHEDULE_SCAN=00:00:01` fires it once a minute.
if (Environment.GetEnvironmentVariable("FLOWX_SAMPLE_SCHEDULE_CRON") is { Length: > 0 } demonstration)
{
    app.Services.GetRequiredService<FlowScheduleCatalog>().Add(
        FlowSchedule.Create(
            CloseOfferWindowFlow.Plan.Flow.Id,
            CloseOfferWindowFlow.Plan.Flow.Version,
            demonstration,
            "UTC",
            MissedFirePolicy.RunOnce),
        CloseOfferWindowFlow.Plan,
        app.Services.GetRequiredService<CloseOfferWindowFlow.Dispatcher>());
}

// Runs the scheme above, so HttpContext.User carries the token's claims by the time the
// generated endpoints read them. Without this line the handler is registered and never invoked,
// every request is anonymous, and every onboarding is refused at its first step.
app.UseAuthentication();

app.MapHealthChecks("/health");

// Every endpoint this application declares, generated from the [HttpTrigger] on the flow that
// declares it. `workspace.provision` has no trigger and therefore no route: it is reached by
// being composed, which is the whole difference between a flow and an endpoint.
//
// `offer.accept` publishes two of them, and until WP-64 it published none. It suspends, and
// the generated endpoint answered 200 with the flow's projected output — which a suspended
// flow does not have, because its `.Return(...)` reads values the steps after the wait were
// going to produce. So it carried no [HttpTrigger] and this file mapped two routes by hand,
// with a `RequestDelegate` because minimal-API delegate binding reflects over handler
// parameters and constraint C2 forbids it.
//
// Both routes are generated now (ADR-0022):
//
//   POST /api/v1/offers
//        -> 202 { instanceId, status: "suspended", awaiting: [ { signal, deliverTo } ] }
//           and a Location header, because this flow declares exactly one wait
//
//   POST /api/v1/offers/{instanceId:guid}/signals/offer.countersigned
//        -> 202 { instanceId, status: "completed" }
//
// The identity in the second route is read off the flow's own `.AwaitSignal<T>` call, so
// `Signals.OfferCountersigned` is now a constant this application uses to *send* against a
// route the compiler wrote — which is exactly what
// `SuspensionTests.TheSignalIdentityThisApplicationSendsIsTheOneThePlanWaitsFor` checks.
app.MapFlowX();

await app.RunAsync().ConfigureAwait(false);
