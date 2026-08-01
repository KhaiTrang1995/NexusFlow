using Banking;
using FlowX.Generated;
using FlowX.Hosting;
using FlowX.Postgres;

var builder = WebApplication.CreateSlimBuilder(args);

// The journal's connection string, and the one piece of configuration this application has.
//
// It is required rather than defaulted, and the failure is here rather than at the first
// request. `transfer.execute` declares Durable, and a host that registers no journal answers
// every transfer with `flow.durability_not_configured` — which is a correct refusal and a
// terrible way to find out. Money movement that is not journaled is the thing this sample
// exists to argue against; starting without one would be the sample undermining itself.
var connectionString =
    builder.Configuration["FlowX:Postgres"]
    ?? Environment.GetEnvironmentVariable("FLOWX_POSTGRES_CONNECTION")
    ?? throw new InvalidOperationException(
        "This sample journals every step of a transfer, so it needs PostgreSQL. Set " +
        "FLOWX_POSTGRES_CONNECTION (or FlowX:Postgres in configuration) to a connection " +
        "string, e.g. \"Host=localhost;Port=5432;Database=postgres;Username=postgres\". " +
        "See samples/banking/README.md.");

builder.Services.AddRouting();
builder.Services.AddFlowX(options => options.ApplicationName = "Banking");

// Durability is opted into by registering stores, not by a flag. This registers the journal,
// the lease store and the recovery index; AddFlowX resolves all three as optional services,
// so a host that omitted this line would build and would refuse every durable flow.
builder.Services.AddFlowXPostgres(connectionString);

// Infrastructure. In memory here; the capabilities do not know or care, because they depend
// on the four interfaces below and never on these classes.
builder.Services.AddSingleton<ILedger, InMemoryLedger>();
builder.Services.AddSingleton<ISanctionsScreening, InMemorySanctionsScreening>();
builder.Services.AddSingleton<ICorrespondentDirectory, InMemoryCorrespondentDirectory>();
builder.Services.AddSingleton<ISettlementRegister, InMemorySettlementRegister>();

// The capabilities themselves. The generated dispatcher takes them as constructor
// parameters, so a missing registration is a startup failure naming the type rather than a
// null reference on the first request.
//
// These stay hand-written on purpose. The generator knows exactly which types the dispatcher
// needs — it wrote that constructor — but nothing in the flow model declares a service
// lifetime, so a generated AddSingleton would be the generator inventing a fact rather than
// publishing one.
builder.Services.AddSingleton<ValidateTransfer>();
builder.Services.AddSingleton<ScreenSanctions>();
builder.Services.AddSingleton<ResolveCorrespondent>();
builder.Services.AddSingleton<PostDebit>();
builder.Services.AddSingleton<PostCredit>();
builder.Services.AddSingleton<ReverseDebit>();
builder.Services.AddSingleton<ReverseCredit>();
builder.Services.AddSingleton<RecordSettlement>();
builder.Services.AddSingleton<ExecuteTransferFlow.Dispatcher>();

var app = builder.Build();

// Migrating is a decision, not a consequence of building a container: AddFlowXPostgres
// deliberately does not apply DDL, because every replica of a rolling update would then race
// to migrate at start-up. One process, one sample, so it is done here — a real deployment
// runs its schema changes as a deployment step.
await app.Services.GetRequiredService<PostgresMigrator>()
    .MigrateAsync(app.Lifetime.ApplicationStopping)
    .ConfigureAwait(false);

app.MapHealthChecks("/health");

// Every endpoint this application declares, generated from the [HttpTrigger] on the flow that
// declares it. The method, the route and the idempotency rule come from the same reading of
// that attribute which produced flowx.manifest.json, so the address served and the address
// published cannot disagree. Nothing in this file mentions transfer.execute.
app.MapFlowX();

await app.RunAsync().ConfigureAwait(false);
