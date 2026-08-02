using Banking;
using FlowX;
using FlowX.Generated;
using FlowX.Hosting;
using FlowX.Postgres;
using Microsoft.AspNetCore.Authentication;

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

// How far apart this deployment keeps its tenants' data, and what each of them is bounded to.
//
// Row is docs/16 §2's L1: one database, one tenant column, and PostgreSQL's own row-level
// security deciding — the journal narrows every connection to the unprivileged `flowx_tenant`
// role, so a capability with a bug still cannot read another bank's rows. Schema is L2: a schema
// per tenant, a connection pool per tenant, and the registry provisioning one on first use.
//
// The level is read from the environment rather than compiled in, because the claim docs/16 §2
// makes is that a tenant moves between levels *without a code change*. That claim is worth
// exactly as much as the demonstration of it, so:
//
//   FLOWX_SAMPLE_TENANCY=schema dotnet run --project samples/banking
//
// changes this line's value and the one PostgresJournalOptions below, and nothing else in the
// application — not a flow, not a capability, not a contract.
var isolation =
    string.Equals(
        Environment.GetEnvironmentVariable("FLOWX_SAMPLE_TENANCY"), "schema", StringComparison.OrdinalIgnoreCase)
        ? TenantIsolation.Schema
        : TenantIsolation.Row;

builder.Services.AddRouting();
builder.Services.AddFlowX(options =>
{
    options.ApplicationName = "Banking";

    // A tenant becomes mandatory here: an invocation naming none is refused at admission with
    // `tenant.required` rather than defaulted, because a default tenant is the precise shape of
    // a cross-tenant read. The tenant is derived from the caller's `tid` claim and from nothing
    // else — never a header, never the payload (ADR-0046).
    options.TenantIsolation = isolation;

    // And what one tenant may cost the others. Isolation and fairness are separate guarantees:
    // the first stops a bank reading another bank's rows and does nothing at all about a bank
    // consuming every slot on the node.
    //
    // Five mechanisms, and this sample declares all five so that a reader can see what each one
    // bounds. They are deliberately generous — a sample that refused its own second curl would
    // be demonstrating a typo rather than a control.
    options.Fairness.PermitsPerWindow = 50;              // admissions per tenant per second
    options.Fairness.Window = TimeSpan.FromSeconds(1);
    options.Fairness.QuotaPerWindow = 5_000;             // and per hour, which a burst cannot buy back
    options.Fairness.QuotaWindow = TimeSpan.FromHours(1);
    options.Fairness.MaxConcurrency = 16;                // in flight at once, per tenant
    options.Fairness.PerTenantScanShare = 8;             // how much of a recovery page one tenant may fill

    // Weighted, because "fair" is not "equal" when one tenant pays for more. A weight multiplies
    // that tenant's share of the four bounds above; an absent tenant weighs 1.
    options.Fairness.Weights[BankTokens.FrankfurtTenant] = 2;

    // The tenants a per-tenant schedule would fan out over. This flow has no [CronTrigger], so
    // nothing reads it at Row — it is here because at Schema the registry answers instead, and
    // the difference between "a list a deployment maintains" and "a list the store already
    // holds" is the one thing about the two levels that is not a single line.
    options.Tenants.Add(BankTokens.FrankfurtTenant);
    options.Tenants.Add(BankTokens.LondonTenant);
});

// Authentication, which is the only reason this application has a principal — or a tenant — at
// all. Both come off the same validated claims: the stances on the capabilities are decided
// against HttpContext.User, and the tenant is the `tid` claim on the same principal.
//
// Nothing here names a route or a permission. A rule attached to the endpoint would hold over
// HTTP and not over a broker or an agent, which is exactly the transport-attached authorisation
// the capability stance exists to replace (docs/15-Security.md §4, ADR-0004).
//
// BankTokenHandler is a stand-in for an OIDC handler and says so at length. A real deployment
// replaces this one call with AddJwtBearer and changes nothing else.
builder.Services
    .AddAuthentication(BankTokenHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, BankTokenHandler>(BankTokenHandler.SchemeName, null);

// Durability is opted into by registering stores, not by a flag. This registers the journal,
// the lease store and the recovery index; AddFlowX resolves all three as optional services,
// so a host that omitted this line would build and would refuse every durable flow.
// The second of the two lines the isolation level changes, and the reason it is not one line:
// where the tables live is the store's decision and how far apart the tenants are is the
// deployment's, and a store that quietly served L2 out of one shared table would be exactly the
// silent downgrade the level exists to prevent. FlowDurability.IsolationEnforced refuses to
// start when these two disagree.
builder.Services.AddFlowXPostgres(
    connectionString,
    new PostgresJournalOptions
    {
        TenantSchemas = new TenantSchemaOptions { IsEnabled = isolation is TenantIsolation.Schema },
    });

// The rate limit on the first step is enforced against this, and against nothing if this line
// is deleted — a step declaring a RateLimit with no IRateLimiterStore registered is refused
// rather than admitted, which is deliberate and is ADR-0040 §2.2. There is no in-memory
// default, because a limiter counting in a process admits twenty transfers per second *per
// replica* behind a declaration that reads as twenty for the deployment, and the multiplier is
// the replica count, which nothing declares and nothing reports.
//
// PostgreSQL here because this sample already has one. A deployment that wanted the bucket
// somewhere hotter would call AddFlowXRedisPolicyStores instead and change nothing else: the
// engine reads a seam, and RateLimiterConformance is what makes the two interchangeable.
//
// This registers an IIdempotencyStore too. Nothing in this flow declares an Idempotency window
// — FLOWX1040 refuses one here, see Policies.Admission — so the store is registered and unused,
// which is the honest state rather than a line to delete: the next flow this application gains
// may well be one whose contracts mark nothing.
builder.Services.AddFlowXPostgresPolicyStores();

// Infrastructure. In memory here; the capabilities do not know or care, because they depend
// on the four interfaces below and never on these classes.
builder.Services.AddSingleton<ILedger, InMemoryLedger>();
builder.Services.AddSingleton<ISanctionsScreening, InMemorySanctionsScreening>();
builder.Services.AddSingleton<ICorrespondentDirectory, InMemoryCorrespondentDirectory>();
builder.Services.AddSingleton<ISettlementRegister, InMemorySettlementRegister>();

// Where this bank's audit records go. Registered rather than defaulted, and the flow does not
// start without it: three steps declare an Audit, and the engine refuses an audited step it
// cannot record — an unwritten financial audit record is a real loss rather than a
// conservative default. Deleting this line does not make the transfer cheaper; it makes it
// fail at the debit and unwind, naming the missing sink.
//
// In memory, which no bank would ship. A real deployment writes to an append-only table, a
// WORM bucket or a SIEM, and IAuditSink has no default implementation for exactly that reason:
// where an audit record is kept is a decision about a compliance regime rather than about
// FlowX, and a default would be a control that reads as configured and survives no restart.
builder.Services.AddSingleton<InMemoryAuditTrail>();
builder.Services.AddSingleton<IAuditSink>(sp => sp.GetRequiredService<InMemoryAuditTrail>());

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

// Runs the scheme above, so HttpContext.User carries the token's claims — and its tenant — by
// the time the generated endpoint reads them. Without this line the handler is registered and
// never invoked, every request is anonymous, and every transfer is refused at the first step,
// which looks exactly like a broken token.
app.UseAuthentication();

app.MapHealthChecks("/health");

// Every endpoint this application declares, generated from the [HttpTrigger] on the flow that
// declares it. The method, the route and the idempotency rule come from the same reading of
// that attribute which produced flowx.manifest.json, so the address served and the address
// published cannot disagree. Nothing in this file mentions transfer.execute.
app.MapFlowX();

await app.RunAsync().ConfigureAwait(false);
