using FlowX;
using FlowX.Generated;
using FlowX.Hosting;
using FlowX.Postgres;
using Healthcare;
using Microsoft.AspNetCore.Authentication;

var builder = WebApplication.CreateSlimBuilder(args);

// The journal's connection string. Required rather than defaulted, and the failure is here
// rather than at the first request, for samples/banking's reason and one of this sample's own:
// patient.intake declares Durable, and a host that registers no journal answers every intake
// with `flow.durability_not_configured`. On top of that, the handle a patient's records are
// erased by lives on the instance row — so a deployment with no journal has no erasure either,
// and discovering that when somebody exercises the right is not a thing anyone should have to do.
var connectionString =
    builder.Configuration["FlowX:Postgres"]
    ?? Environment.GetEnvironmentVariable("FLOWX_POSTGRES_CONNECTION")
    ?? throw new InvalidOperationException(
        "This sample journals every step of an admission and erases by reading that journal, " +
        "so it needs PostgreSQL. Set FLOWX_POSTGRES_CONNECTION (or FlowX:Postgres in " +
        "configuration) to a connection string, e.g. " +
        "\"Host=localhost;Port=5432;Database=postgres;Username=postgres\". " +
        "See samples/healthcare/README.md.");

// How far apart this deployment keeps its clinics' data.
//
// Row is docs/16 §2's L1: one database, one tenant column, and PostgreSQL's own row-level
// security deciding under a role that cannot bypass it. Schema is L2: a schema and a connection
// pool per clinic, provisioned on first use.
//
// This line and the PostgresJournalOptions below are the whole of the difference between the two
// levels. No flow, no capability, no contract and no policy changes:
//
//   FLOWX_SAMPLE_TENANCY=schema dotnet run --project samples/healthcare
//
// L3 and L4 are not here and are not coming. docs/16 §2 offers them as "a dedicated deployment"
// and "a region-pinned dedicated deployment", and ADR-0051 holds that a deployment per tenant is
// a topology rather than a runtime level: the pod serving one clinic's database points its
// connection string at that database and declares None, and there is no second tenant in the
// process to be kept apart from. `TenantIsolation.Database` is refused at startup, by name.
var isolation =
    string.Equals(
        Environment.GetEnvironmentVariable("FLOWX_SAMPLE_TENANCY"), "schema", StringComparison.OrdinalIgnoreCase)
        ? TenantIsolation.Schema
        : TenantIsolation.Row;

// Where this deployment is. Configuration, never a claim — a caller that could name the region
// its data may be processed in could name this one, and residency would become a control the
// caller configures. Overridable so that one machine can run the fleet's two halves.
var region =
    Environment.GetEnvironmentVariable("FLOWX_SAMPLE_REGION") is { Length: > 0 } declared
        ? declared
        : "eu-central-1";

builder.Services.AddRouting();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddFlowX(options =>
{
    options.ApplicationName = "Healthcare";

    // A clinic becomes mandatory here: an invocation naming none is refused at admission with
    // `tenant.required` rather than defaulted, because a default tenant is the precise shape of a
    // cross-tenant read. The tenant is derived from the caller's `tid` claim and from nothing
    // else — never a header, never the payload (ADR-0046).
    options.TenantIsolation = isolation;

    // What one clinic may cost the others. Isolation and fairness are separate guarantees, and
    // samples/banking declares all six mechanisms with the argument for each; this sample
    // declares the three that bound admission, because what it is here to show is elsewhere.
    options.Fairness.PermitsPerWindow = 50;
    options.Fairness.Window = TimeSpan.FromSeconds(1);
    options.Fairness.MaxConcurrency = 16;

    // Data residency, which is a refusal and not a placement.
    //
    // This deployment declares where it is; each pinned clinic declares where its data may be
    // processed. A call whose clinic is pinned somewhere else is refused at admission with
    // `tenant.residency_refused` — before a lease, before a row, before a step — and is not
    // forwarded, because forwarding would be the platform carrying the data across the boundary
    // the pin exists to hold.
    //
    // It does not reopen ADR-0051 and it is not L4. Nothing here selects a store: the regional
    // deployment is still the topology, placed by whoever writes the manifests, and this is the
    // check that the caller reached the fleet its clinic is allowed to be served by. It is one
    // half of a residency guarantee; the other half is that the data was never in the wrong
    // region to begin with, which no runtime check can provide.
    options.Residency.Region = region;
    options.Residency.Requirements[ClinicTokens.BerlinTenant] = "eu-central-1";
    options.Residency.Requirements[ClinicTokens.MunichTenant] = "eu-central-1";
    options.Residency.Requirements[ClinicTokens.DublinTenant] = "eu-west-1";

    // The clinics a per-tenant schedule would fan out over. This flow has no [CronTrigger], so
    // nothing reads it at Row — it is here because at Schema the registry answers instead.
    options.Tenants.Add(ClinicTokens.BerlinTenant);
    options.Tenants.Add(ClinicTokens.MunichTenant);
});

builder.Services
    .AddAuthentication(ClinicTokenHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ClinicTokenHandler>(ClinicTokenHandler.SchemeName, null);

// The journal, the lease store, the recovery index — and ISubjectErasure, which is registered
// by the same call because it is the same store answering a different question. The second line
// the isolation level changes: FlowDurability.IsolationEnforced refuses to start when the level
// the deployment declares and the level the store can serve disagree.
builder.Services.AddFlowXPostgres(
    connectionString,
    new PostgresJournalOptions
    {
        TenantSchemas = new TenantSchemaOptions { IsEnabled = isolation is TenantIsolation.Schema },
    });

// What reads this deployment's outbox: nothing.
//
// The flow emits patient.admitted and this sample wires no broker, so every staged event sits
// in the outbox unread. Saying so is not tidiness — it is the difference between an erasure
// that completes and one that withholds every instance for a publisher that does not exist.
// Retention and erasure ask the same question of the same rows, and neither can discover the
// answer: an undrained row looks identical whether the publisher is missing or merely down.
//
// A deployment that adds a broker deletes this line, and both sweeps start holding rows again
// until the publisher has taken them.
builder.Services.AddFlowXPostgresRetentionConsumers(new RetentionConsumers { Publisher = false });

// The rate limit on the first step is enforced against this, and against nothing if this line is
// deleted — a step declaring a RateLimit with no IRateLimiterStore registered is refused rather
// than admitted (ADR-0040 §2.2). The per-tenant permits declared above spend against it too.
builder.Services.AddFlowXPostgresPolicyStores();

// The clinic's own systems. In memory here; the capabilities depend on the three interfaces and
// never on these classes.
builder.Services.AddSingleton<IConsentRegister, InMemoryConsentRegister>();
builder.Services.AddSingleton<IPatientIndex, InMemoryPatientIndex>();
builder.Services.AddSingleton<IRecordStore, InMemoryRecordStore>();

// Where the clinical audit records go. Registered rather than defaulted, and the flow does not
// start without it: three steps declare an Audit, and the engine refuses an audited step it
// cannot record. In memory, which no clinic would ship — where a clinical audit record is kept
// is a decision about a regulatory regime rather than about FlowX.
builder.Services.AddSingleton<InMemoryClinicalAudit>();
builder.Services.AddSingleton<IAuditSink>(sp => sp.GetRequiredService<InMemoryClinicalAudit>());

// The capabilities. The generated dispatcher takes them as constructor parameters, so a missing
// registration is a startup failure naming the type rather than a null reference on the first
// request.
builder.Services.AddSingleton<ValidateIntake>();
builder.Services.AddSingleton<VerifyConsent>();
builder.Services.AddSingleton<DeduplicatePatient>();
builder.Services.AddSingleton<StoreRecord>();
builder.Services.AddSingleton<PurgeRecord>();
builder.Services.AddSingleton<PatientIntakeFlow.Dispatcher>();

var app = builder.Build();

// Migrating is a decision, not a consequence of building a container. One process, one sample,
// so it is done here; a real deployment runs its schema changes as a deployment step.
await app.Services.GetRequiredService<PostgresMigrator>()
    .MigrateAsync(app.Lifetime.ApplicationStopping)
    .ConfigureAwait(false);

app.UseAuthentication();

app.MapHealthChecks("/health");

// Every endpoint the flow declares, generated from its [HttpTrigger]. Nothing in this file
// mentions patient.intake or its route.
app.MapFlowX();

// And the one endpoint no flow declares. See Erasure.cs for why an erasure is not a flow, not a
// CLI verb, and does not get the platform's admission for free.
app.MapPatientErasure();

await app.RunAsync().ConfigureAwait(false);
