using Crm;
using FlowX;
using FlowX.Generated;
using FlowX.Hosting;
using FlowX.Mcp;
using FlowX.Postgres;
using FlowX.RabbitMq;
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

// The outbox and the change feed, over the same table. `.Emit<T>()` stages an event in the
// step's own transaction; the outbox drains those rows to the broker for the three
// subscriptions on `lead.created`, and the change feed offers the identical rows straight to
// `crm.process.transition` with no broker in the path at all.
builder.Services.AddFlowXPostgresOutbox();
builder.Services.AddFlowXPostgresChangeFeed();
builder.Services.AddHostedService<CrmOutboxPump>();

// The broker — §9. Optional, and what it costs to leave it out is stated rather than hidden:
// the configured process still runs, because it is driven by the change feed; the three
// subscriptions on `lead.created` do not, because a [BusTrigger] with no IBusConsumer has
// nothing to read. Nothing else moves, and no flow mentions RabbitMQ.
var broker =
    builder.Configuration["FlowX:RabbitMq"]
    ?? Environment.GetEnvironmentVariable("FLOWX_RABBITMQ_CONNECTION");

if (broker is { Length: > 0 })
{
    builder.Services.AddFlowXRabbitMq(broker);
    builder.Services.AddFlowXRabbitMqConsumer(broker);
}

// What the capabilities are built out of. These are this sample's own types — the stores that
// issue the SQL, the schema reader, the stand-in enrichment provider — and nothing generated
// knows they exist, which is why they are named here and the capabilities are not.
builder.Services.AddSingleton<CrmSchemaReader>();
builder.Services.AddSingleton<ConversionStore>();
builder.Services.AddSingleton<IntakeStore>();
builder.Services.AddSingleton<ProcessStore>();
builder.Services.AddSingleton<SalesStore>();
builder.Services.AddSingleton<WorkStore>();
builder.Services.AddSingleton<EnrichmentProvider>();
builder.Services.AddSingleton<EnrichmentStore>();
builder.Services.AddSingleton<AssistantStore>();
builder.Services.AddSingleton<CustomSchemaStore>();
builder.Services.AddSingleton<ConnectorStore>();
builder.Services.AddSingleton<FieldPolicyStore>();

// The one thing in this application that talks to somebody else's system. Registered under the
// interface, so a deployment with a vault or a real Slack renderer replaces this line and
// nothing else; the registry, the queue and the sweep do not know which one they got.
builder.Services.AddHttpClient<IConnectorTransport, HttpConnectorTransport>(
    static client => client.Timeout = TimeSpan.FromSeconds(10));

// Every capability the twenty-five steps invoke, and every flow's dispatcher — generated from
// the constructors the generator itself wrote. Forty hand-written lines stood here until the
// runtime settled the lifetime question they were waiting on: the catalogues hold a resolved
// dispatcher for the life of the node, a recovery sweep resumes an instance with no scope to
// resolve another from, and singleton is therefore the only lifetime that is honest. TryAdd, so
// a capability registered above under an interface would still win.
builder.Services.AddFlowXCapabilities();

// The agent surface's tool bindings — §10 package 11. One flow reaches a model, it reads, and
// it meets the same crm.read stance a person meets over HTTP.
builder.Services.AddFlowXAgentTools();

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

// Everything this application declared, in one call: the routes from each [HttpTrigger], the
// three subscriptions on `lead.created`, the change subscription that drives the configured
// process, and the two sweeps. Nothing in this file names a route, a topic or a cron expression
// — they are read off the attributes the manifest was written from.
//
// The two sweeps are why this is one call and not six. They were declared, published, listed in
// the README's table of surfaces, and registered by nothing, because AddFlowXSchedules() was the
// one line of six that nobody wrote.
app.UseFlowX();

// The agent surface, served from the same manifest the HTTP routes are generated from — so the
// tools a model can see are exactly the flows carrying [AgentTrigger] and nothing else.
app.MapFlowXMcp("/mcp");

await app.RunAsync().ConfigureAwait(false);
