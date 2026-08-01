using FlowX.Generated;
using FlowX.Hosting;
using FlowX.Postgres;
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

// Both dispatchers. The parent's takes the child's, because composing a flow is a typed call
// the generator emits into the parent's dispatcher — which is what keeps the engine free of
// reflection even across a sub-flow boundary.
builder.Services.AddSingleton<ProvisionWorkspaceFlow.Dispatcher>();
builder.Services.AddSingleton<OnboardEmployeeFlow.Dispatcher>();

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

await app.RunAsync().ConfigureAwait(false);
