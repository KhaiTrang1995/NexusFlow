using System.Text.Json;
using FlowX;
using FlowX.Generated;
using FlowX.Hosting;
using FlowX.Postgres;
using FlowX.Runtime;
using Workflow;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddRouting();
builder.Services.AddFlowX(options => options.ApplicationName = "Workflow");

// The journal, and the whole reason this sample has a dependency samples/ecommerce does not.
//
// `employee.onboard` declares Durable. A durable flow on a host that registered no journal
// and no lease store is refused with `flow.durability_not_configured` before its first step —
// not run ephemerally — so this line is not optional configuration, it is the difference
// between an application that starts and one that answers every request with a refusal.
// AddFlowX resolves IFlowJournal, ILeaseStore and IRecoveryIndex out of the container; this
// registers all three over one data source.
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
//
// These stay hand-written for the reason samples/ecommerce gives: the generator knows exactly
// which types each dispatcher needs — it wrote that constructor — but nothing in the flow
// model declares a service lifetime, so a generated AddSingleton would be the generator
// inventing a fact rather than publishing one.
builder.Services.AddSingleton<ValidateOffer>();
builder.Services.AddSingleton<OpenPayrollRecord>();
builder.Services.AddSingleton<ClosePayrollRecord>();
builder.Services.AddSingleton<SignSupplierAgreement>();
builder.Services.AddSingleton<VoidSupplierAgreement>();
builder.Services.AddSingleton<CreateIdentity>();
builder.Services.AddSingleton<DisableIdentity>();
builder.Services.AddSingleton<OrderLaptop>();
builder.Services.AddSingleton<CancelLaptopOrder>();
builder.Services.AddSingleton<GrantSystemAccess>();
builder.Services.AddSingleton<RevokeSystemAccess>();
builder.Services.AddSingleton<ScheduleInduction>();
builder.Services.AddSingleton<RecordEquipmentApproval>();
builder.Services.AddSingleton<AutoClearEquipment>();
builder.Services.AddSingleton<AssignEquipment>();
builder.Services.AddSingleton<ReturnEquipment>();
builder.Services.AddSingleton<StartBackgroundCheck>();
builder.Services.AddSingleton<WaiveBackgroundCheck>();
builder.Services.AddSingleton<SendWelcomePack>();
builder.Services.AddSingleton<AllocateDesk>();
builder.Services.AddSingleton<ReleaseDesk>();
builder.Services.AddSingleton<IssueBuildingPass>();
builder.Services.AddSingleton<CancelBuildingPass>();

// offer.accept's three. It is the flow that waits, and none of its capabilities knows that:
// a suspension point is the engine's business, so `onboarding.start` simply binds the
// contract the signal delivered, exactly as it would bind an earlier step's output.
builder.Services.AddSingleton<SendOfferForSignature>();
builder.Services.AddSingleton<WithdrawOffer>();
builder.Services.AddSingleton<StartOnboarding>();

// Both dispatchers. The parent's takes the child's, because composing a flow is a typed call
// the generator emits into the parent's dispatcher — which is what keeps the engine free of
// reflection even across a sub-flow boundary.
builder.Services.AddSingleton<ProvisionWorkspaceFlow.Dispatcher>();
builder.Services.AddSingleton<OnboardEmployeeFlow.Dispatcher>();
builder.Services.AddSingleton<AcceptOfferFlow.Dispatcher>();

var app = builder.Build();

// Applying DDL is a decision, not a consequence of building a container — every replica of a
// rolling update would otherwise race to migrate as it starts, and a failure would surface as
// a pod that will not start rather than as a deployment step that did not pass. A real
// deployment calls this from wherever it runs schema changes. A sample calls it here, once,
// so that `dotnet run` against an empty database works.
await app.Services.GetRequiredService<PostgresMigrator>()
    .MigrateAsync(CancellationToken.None)
    .ConfigureAwait(false);

app.MapHealthChecks("/health");

// Every endpoint this application declares, generated from the [HttpTrigger] on the flow that
// declares it. `workspace.provision` has no trigger and therefore no route: it is reached by
// being composed, which is the whole difference between a flow and an endpoint.
app.MapFlowX();

// ---------------------------------------------------------------------------------------
// offer.accept — the two routes a flow that waits needs, and why they are hand-written
// ---------------------------------------------------------------------------------------
//
// `MapFlowX` generates an endpoint per [HttpTrigger] and answers 200 with the flow's
// projected output. A flow that suspends has no output to project — its `.Return(...)` reads
// values the steps after the wait were going to produce — and the answer that shape needs is
// 202 with the instance id, so that the caller can address the countersignature to it. The
// HTTP plugin has no 202 path yet, which is why `offer.accept` declares no [HttpTrigger] and
// these two routes are here instead. It is a gap in the transport, named in the README.
//
// Everything below the two `host` calls is the real runtime: the same FlowHost, the same
// lease, the same journal, the same step loop. Delivering a signal is `ResumeAsync` carrying
// a value — the call FlowRecoveryScan already makes — and not a second way of running a flow.

// `Map` with a RequestDelegate rather than `MapPost` with a bound handler, for the reason
// FlowEndpointExtensions gives: minimal-API delegate binding reflects over the handler's
// parameters, which constraint C2 forbids. A RequestDelegate plus the generated
// JsonTypeInfo does the same job with no reflection at all.
app.Map("/api/v1/offers", async (HttpContext http) =>
{
    var offer = await JsonSerializer
        .DeserializeAsync(http.Request.Body, WorkflowJsonContext.Default.OfferToAccept, http.RequestAborted)
        .ConfigureAwait(false);

    if (offer is null)
    {
        http.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var host = http.RequestServices.GetRequiredService<FlowHost>();
    var dispatcher = http.RequestServices.GetRequiredService<AcceptOfferFlow.Dispatcher>();

    var result = await host.RunAsync(
            AcceptOfferFlow.Plan,
            dispatcher,
            new FlowInvocation(http.TraceIdentifier, offer.CandidateId),
            offer,
            AcceptOfferFlow.Projection,
            http.RequestAborted)
        .ConfigureAwait(false);

    if (result.IsSuspended)
    {
        // The whole point of the sample. The offer has gone out, the instance is one row in
        // the journal, and this process is holding nothing on its behalf — no thread, no
        // pooled context, no lease.
        http.Response.StatusCode = StatusCodes.Status202Accepted;
        http.Response.ContentType = "application/json";

        await JsonSerializer.SerializeAsync(
                http.Response.Body,
                new OfferPending(result.InstanceId!.Value, Signals.OfferCountersigned),
                WorkflowJsonContext.Default.OfferPending,
                http.RequestAborted)
            .ConfigureAwait(false);

        return;
    }

    http.Response.StatusCode = result.IsSuccess
        ? StatusCodes.Status200OK
        : StatusCodes.Status409Conflict;
}).WithMetadata(new HttpMethodMetadata(["POST"]));

// The signal endpoint, shaped the way `docs/06-Execution-Engine.md §6` draws it: the instance
// in the path, the signal's identity in the path, and the payload in the body. The identity is
// what the plan carries, so a transport can route a signal without knowing a contract type.
app.Map("/api/v1/offers/{instanceId:guid}/signals/{signalType}", async (HttpContext http) =>
{
    if (!Guid.TryParse((string?)http.Request.RouteValues["instanceId"], out var instanceId) ||
        !string.Equals(
            (string?)http.Request.RouteValues["signalType"],
            Signals.OfferCountersigned,
            StringComparison.Ordinal))
    {
        http.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    var signed = await JsonSerializer
        .DeserializeAsync(
            http.Request.Body, WorkflowJsonContext.Default.OfferCountersigned, http.RequestAborted)
        .ConfigureAwait(false);

    if (signed is null)
    {
        http.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var host = http.RequestServices.GetRequiredService<FlowHost>();
    var dispatcher = http.RequestServices.GetRequiredService<AcceptOfferFlow.Dispatcher>();

    // The same call FlowRecoveryScan makes to pick up an instance a dead node left behind,
    // carrying a value. There is no second resume path.
    var result = await host.SignalAsync(
            instanceId,
            new FlowRegistration(AcceptOfferFlow.Plan, dispatcher),
            FlowSignal.Of(Signals.OfferCountersigned, signed),
            http.RequestAborted)
        .ConfigureAwait(false);

    http.Response.StatusCode = result.IsSuccess
        ? StatusCodes.Status200OK
        : StatusCodes.Status409Conflict;
}).WithMetadata(new HttpMethodMetadata(["POST"]));

await app.RunAsync().ConfigureAwait(false);
