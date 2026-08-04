using System.Globalization;
using FlowX;
using FlowX.Generated;
using FlowX.Hosting;
using FlowX.Postgres;
using FlowX.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Scheduler;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddFlowX(options =>
{
    options.ApplicationName = "Scheduler";

    // Which node this is, as a lease records its owner. The default is the machine name, which
    // is right under an orchestrator and useless when three replicas are three processes on one
    // laptop — and three replicas on one laptop is how "one occurrence fires once, however many
    // nodes are sweeping" is shown without a cluster.
    if (Environment.GetEnvironmentVariable("FLOWX_SAMPLE_NODE") is { Length: > 0 } node)
    {
        options.NodeName = node;
    }

    // The resolution of every schedule on this node, and the lower bound on how late a firing
    // is: `0 2 * * *` under a ten-second sweep fires between 02:00:00 and 02:00:10. A
    // demonstration that wants to watch a schedule fire needs this wound down as well as a
    // denser expression, which is registered below rather than declared on the flow.
    if (TimeSpan.TryParse(
            Environment.GetEnvironmentVariable("FLOWX_SAMPLE_SCHEDULE_SCAN"),
            CultureInfo.InvariantCulture,
            out var sweep) && sweep > TimeSpan.Zero)
    {
        options.ScheduleScanInterval = sweep;
    }

    // Who the fan-out visits. `PerTenant = true` means "one firing per tenant", which cannot be
    // evaluated without the list — and at row isolation there is no registry to derive it from,
    // because a tenant exists the moment a token carrying its claim arrives. So the deployment
    // states it. An empty list fires nothing, which is the truthful behaviour for a host that
    // was never told who it serves.
    options.TenantIsolation = TenantIsolation.Row;

    foreach (var tenant in (Environment.GetEnvironmentVariable("FLOWX_SAMPLE_TENANTS")
                 ?? "acme,globex,initech").Split(',', StringSplitOptions.RemoveEmptyEntries
                 | StringSplitOptions.TrimEntries))
    {
        options.Tenants.Add(tenant);
    }
});

// The journal, and the reason this sample cannot start without a database.
//
// `reconciliation.daily` declares Durable. A durable flow on a host that registered no journal
// and no lease store is refused before its first step — and, more to the point here,
// FlowScheduleScan.IsEnabled is false without one, so the node would sweep for nothing at all
// and never say so. The primary key on flow_instance is what makes one occurrence one run
// across the whole fleet; there is no leader and nothing to elect.
builder.Services.AddFlowXPostgres(
    Environment.GetEnvironmentVariable("FLOWX_POSTGRES_CONNECTION")
    ?? "Host=localhost;Port=5432;Database=postgres;Username=postgres");

// The outside world, in memory. The capabilities do not know or care.
builder.Services.AddSingleton<InMemoryLedger>();
builder.Services.AddSingleton<InMemoryBank>();
builder.Services.AddSingleton<ILedger>(p => p.GetRequiredService<InMemoryLedger>());
builder.Services.AddSingleton<IBank>(p => p.GetRequiredService<InMemoryBank>());
builder.Services.AddSingleton<IReportDesk, InMemoryReportDesk>();

// The capabilities, generated from the constructor the generator wrote — samples/ecommerce
// gives the reason they are singletons.
builder.Services.AddFlowXCapabilities();

var app = builder.Build();

// Applying DDL is a decision, not a consequence of building a container: every replica of a
// rolling update would otherwise race to migrate as it starts. A real deployment calls this
// from wherever it runs schema changes; a sample calls it here so `dotnet run` against an
// empty database works.
await app.Services.GetRequiredService<PostgresMigrator>()
    .MigrateAsync(CancellationToken.None)
    .ConfigureAwait(false);

// A day's worth of books for each tenant, so the first firing has something to reconcile.
// Nothing about this is part of the sample's claim — it is the fixture a real deployment
// already has in its own database.
Seed(app.Services);

// Every schedule this application declares, generated from the [CronTrigger] on the flow that
// declares it. One line registers `reconciliation.daily` at `0 2 * * *` in Europe/Berlin, with
// its overlap policy, its jitter and its fan-out — all four read off the same attribute the
// manifest was written from, so a declared schedule and a fired one cannot disagree.
//
// It also puts the flow in the FlowCatalog, so a node that dies mid-reconciliation leaves an
// instance the recovery sweep can take over.
app.Services.AddFlowXSchedules();

// And a second, denser schedule for a demonstration — registered rather than declared.
//
// `0 2 * * *` is the business number and is what flowx.manifest.json publishes; overriding it
// would mean the declaration a reader sees is not the one that runs. So this adds a schedule
// beside it, under its own expression and therefore its own instance ids.
//
//   FLOWX_SAMPLE_SCHEDULE_CRON="* * * * *" FLOWX_SAMPLE_SCHEDULE_SCAN=00:00:01 dotnet run
//
// Set FLOWX_SAMPLE_BANK_LATENCY to something longer than the interval to watch OverlapPolicy
// do its job: the second minute's occurrence is skipped rather than stacked on the first.
if (Environment.GetEnvironmentVariable("FLOWX_SAMPLE_SCHEDULE_CRON") is { Length: > 0 } dense)
{
    app.Services.GetRequiredService<FlowScheduleCatalog>().Add(
        FlowSchedule.Create(
            DailyReconciliationFlow.Plan.Flow.Id,
            DailyReconciliationFlow.Plan.Flow.Version,
            dense,
            "UTC",
            MissedFirePolicy.RunOnce,
            perTenant: true,
            overlap: OverlapPolicy.Skip,
            jitter: Environment.GetEnvironmentVariable("FLOWX_SAMPLE_JITTER") ?? "PT2S"),
        DailyReconciliationFlow.Plan,
        app.Services.GetRequiredService<DailyReconciliationFlow.Dispatcher>());
}

await app.RunAsync().ConfigureAwait(false);

static void Seed(IServiceProvider services)
{
    var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<FlowXOptions>>().Value;
    var ledger = services.GetRequiredService<InMemoryLedger>();
    var bank = services.GetRequiredService<InMemoryBank>();

    if (TimeSpan.TryParse(
            Environment.GetEnvironmentVariable("FLOWX_SAMPLE_BANK_LATENCY"),
            CultureInfo.InvariantCulture,
            out var latency) && latency > TimeSpan.Zero)
    {
        bank.Latency = latency;
    }

    var booked = DateTimeOffset.UnixEpoch;

    foreach (var tenant in options.Tenants)
    {
        // Three entries: one the bank references exactly, one it settles without a reference,
        // and one it has not settled at all — which is the number the report exists to produce.
        ledger.Book(tenant, new LedgerEntry(tenant + "-ref-1", 12_50, booked));
        ledger.Book(tenant, new LedgerEntry(tenant + "-ref-2", 99_00, booked));
        ledger.Book(tenant, new LedgerEntry(tenant + "-ref-3", 5_00, booked));

        bank.Post(tenant, new StatementLine(tenant + "-ref-1", 12_50, booked));
        bank.Post(tenant, new StatementLine(string.Empty, 99_00, booked));
    }
}
