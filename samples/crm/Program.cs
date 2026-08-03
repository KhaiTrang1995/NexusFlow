using Crm;
using FlowX;
using FlowX.Generated;
using FlowX.Hosting;
using FlowX.Postgres;
using Microsoft.AspNetCore.Authentication;
using Npgsql;

var builder = WebApplication.CreateSlimBuilder(args);

// The one piece of configuration this application has, and it is required rather than
// defaulted.
//
// A CRM is its tables. Starting without a database would give a process that answers every
// probe with `crm.schema_unreachable` and looks exactly like a misconfigured network; failing
// here names what is missing.
var connectionString =
    builder.Configuration["FlowX:Postgres"]
    ?? Environment.GetEnvironmentVariable("FLOWX_POSTGRES_CONNECTION")
    ?? throw new InvalidOperationException(
        "This sample keeps its own tables in PostgreSQL, so it needs one. Set " +
        "FLOWX_POSTGRES_CONNECTION (or FlowX:Postgres in configuration) to a connection " +
        "string, e.g. \"Host=localhost;Port=5432;Database=postgres;Username=postgres\".");

builder.Services.AddRouting();
builder.Services.AddFlowX(options =>
{
    options.ApplicationName = "Crm";

    // A tenant becomes mandatory here: an invocation naming none is refused at admission with
    // `tenant.required` rather than defaulted, because a default tenant is the precise shape of
    // a cross-tenant read. The tenant is derived from the caller's `tid` claim and from nothing
    // else — never a header, never the payload (ADR-0046).
    //
    // Row rather than Schema: docs/16 §2's L1, one database, one tenant column, and
    // PostgreSQL's own row-level security deciding. That is the level migration 0002's policies
    // are written for, and the level §6 specifies.
    options.TenantIsolation = TenantIsolation.Row;

    options.Tenants.Add(CrmTokens.NorthwindTenant);
    options.Tenants.Add(CrmTokens.ContosoTenant);
});

// Authentication, which is the only reason this application has a principal — or a tenant — at
// all. Nothing here names a route or a permission: a rule attached to the endpoint would hold
// over HTTP and not over a broker or an agent, which is exactly the transport-attached
// authorisation the capability stance exists to replace.
builder.Services
    .AddAuthentication(CrmTokenHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, CrmTokenHandler>(CrmTokenHandler.SchemeName, null);

// The journal, the lease store, the recovery index — and, for this sample, the NpgsqlDataSource
// every CRM statement is issued on. `crm.schema.probe` is Ephemeral and journals nothing, so
// the first three are registered for the flows packages 4 to 12 add rather than for this one;
// the data source is what CrmSchemaReader resolves.
builder.Services.AddFlowXPostgres(connectionString);

builder.Services.AddSingleton<CrmSchemaReader>();
builder.Services.AddSingleton<CountCrmRows>();
builder.Services.AddSingleton<ProbeCrmSchemaFlow.Dispatcher>();

// The conversion saga — §8.1. Each of the three writes and each of the three undos is a
// capability the generated dispatcher takes by constructor, so a missing line here is a
// start-up failure naming the type rather than a null on the first conversion.
builder.Services.AddSingleton<ConversionStore>();
builder.Services.AddSingleton<ReadLeadForConversion>();
builder.Services.AddSingleton<CreateAccount>();
builder.Services.AddSingleton<RemoveAccount>();
builder.Services.AddSingleton<CreateContact>();
builder.Services.AddSingleton<RemoveContact>();
builder.Services.AddSingleton<CreateOpportunity>();
builder.Services.AddSingleton<RemoveOpportunity>();
builder.Services.AddSingleton<MarkLeadConverted>();
builder.Services.AddSingleton<ConvertLeadFlow.Dispatcher>();

// Intake — §8.5. One capture, one event, two subscriptions that do not know about each other.
builder.Services.AddSingleton<IntakeStore>();
builder.Services.AddSingleton<CaptureNewLead>();
builder.Services.AddSingleton<ScoreLead>();
builder.Services.AddSingleton<AssignLead>();
builder.Services.AddSingleton<CaptureLeadFlow.Dispatcher>();
builder.Services.AddSingleton<ScoreLeadFlow.Dispatcher>();
builder.Services.AddSingleton<AssignLeadFlow.Dispatcher>();

// The configurable process — §7. Five action kinds, a closed enumeration, and a definition an
// administrator changes in the database without a deployment.
builder.Services.AddSingleton<ProcessStore>();
builder.Services.AddSingleton<RunConfiguredTransition>();
builder.Services.AddSingleton<RunWorkflowTransitionFlow.Dispatcher>();

// Pipeline and sales — §5.2. The discount threshold is the sample's second authorisation
// stance: a representative may ask for any discount and a manager is who signs it off.
builder.Services.AddSingleton<SalesStore>();
builder.Services.AddSingleton<IssueQuoteForOpportunity>();
builder.Services.AddSingleton<ApproveQuoteDiscountCapability>();
builder.Services.AddSingleton<PlaceOrderForQuote>();
builder.Services.AddSingleton<ApplyOpportunityTrigger>();
builder.Services.AddSingleton<IssueQuoteFlow.Dispatcher>();
builder.Services.AddSingleton<ApproveDiscountFlow.Dispatcher>();
builder.Services.AddSingleton<PlaceOrderFlow.Dispatcher>();
builder.Services.AddSingleton<AdvanceOpportunityFlow.Dispatcher>();

var app = builder.Build();

// Migrating is a decision, not a consequence of building a container: AddFlowXPostgres
// deliberately does not apply DDL, because every replica of a rolling update would then race to
// migrate at start-up. One process, one sample, so it is done here — a real deployment runs its
// schema changes as a deployment step.
//
// Two migrators and two ledgers, in this order because the second's GRANT statements need the
// `flowx_tenant` role and the first is where it is created. Neither is a prerequisite of the
// other beyond that: `schema_migration` is the platform's history and `crm_schema_migration` is
// this sample's, and they advance independently.
await app.Services.GetRequiredService<PostgresMigrator>()
    .MigrateAsync(app.Lifetime.ApplicationStopping)
    .ConfigureAwait(false);

await new CrmMigrator(app.Services.GetRequiredService<NpgsqlDataSource>())
    .MigrateAsync(app.Lifetime.ApplicationStopping)
    .ConfigureAwait(false);

// Runs the scheme above, so HttpContext.User carries the token's claims — and its tenant — by
// the time the generated endpoint reads them. Without this line every request is anonymous and
// `crm.schema.count`, which admits any authenticated caller and no anonymous one, refuses them
// all — which looks exactly like a broken token.
app.UseAuthentication();

app.MapHealthChecks("/health");

// Every endpoint this application declares, generated from the [HttpTrigger] on the flow that
// declares it. Nothing in this file mentions crm.schema.probe.
app.MapFlowX();

await app.RunAsync().ConfigureAwait(false);
