using EventDriven;
using FlowX;
using FlowX.Generated;
using FlowX.Hosting;
using FlowX.Postgres;
using FlowX.Redis;
using FlowX.Runtime;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddRouting();
builder.Services.AddFlowX(options =>
{
    options.ApplicationName = "EventDriven";

    // Which node this is, as a lease records its owner. The default is the machine name, which
    // is right under an orchestrator and useless when three replicas are three processes on one
    // laptop — and that is how "a schedule fires once across every node" is demonstrated without
    // a cluster.
    if (Environment.GetEnvironmentVariable("FLOWX_SAMPLE_NODE") is { Length: > 0 } node)
    {
        options.NodeName = node;
    }

    // The resolution of every schedule. `0 3 * * *` under a ten-second sweep fires between
    // 03:00:00 and 03:00:10; a demonstration that wants to watch the cron transport work needs
    // both this and a denser expression, which is registered below rather than declared.
    if (TimeSpan.TryParse(
            Environment.GetEnvironmentVariable("FLOWX_SAMPLE_SCHEDULE_SCAN"),
            System.Globalization.CultureInfo.InvariantCulture,
            out var scheduleSweep) && scheduleSweep > TimeSpan.Zero)
    {
        options.ScheduleScanInterval = scheduleSweep;
    }
});

// The journal, and the reason this sample states its infrastructure in the README rather than
// running against nothing. Every flow here declares Durable, and a durable flow on a host with no
// journal is refused with `flow.durability_not_configured` before its first step — so this is not
// optional configuration, it is the difference between an application that answers and one that
// refuses every request.
builder.Services.AddFlowXPostgres(
    builder.Configuration.GetConnectionString("FlowX")
    ?? Environment.GetEnvironmentVariable("FLOWX_POSTGRES_CONNECTION")
    ?? "Host=localhost;Port=5432;Database=postgres;Username=postgres");

// The broker, in both directions. The publisher is what `PostgresOutboxPublisher` drains staged
// events into; the consumer is what `FlowBusScan` reads them back out of. Neither is named by a
// flow: `[BusTrigger]` says what is consumed and never on what, which is the whole of why the
// same chain moves between transports.
var redis = Environment.GetEnvironmentVariable("FLOWX_REDIS_CONNECTION") ?? "localhost:6379";

builder.Services.AddFlowXRedisStreams(redis);
builder.Services.AddFlowXRedisStreamConsumer(redis);

// The outbox, and the change feed over the same table. One drains to the broker for
// `invoice.issue.bus`; the other offers the identical rows straight to `invoice.issue.change`,
// with no broker anywhere in the path.
builder.Services.AddFlowXPostgresOutbox();
builder.Services.AddFlowXPostgresChangeFeed();
builder.Services.AddHostedService<OutboxPump>();

// Infrastructure. In memory here; the capabilities do not know or care.
builder.Services.AddSingleton<IInvoiceLedger, InMemoryInvoiceLedger>();
builder.Services.AddSingleton<InMemoryBillingCalendar>();
builder.Services.AddSingleton<IBillingCalendar>(
    provider => provider.GetRequiredService<InMemoryBillingCalendar>());

// The capabilities, and the four business ones are registered once and shared by every flow.
// That is the sample's claim expressed as a container registration: there is one
// `invoice.validate`, one `invoice.tax`, one `invoice.persist` and one `invoice.void`, and the
// transport a flow was started by never reaches them.
builder.Services.AddSingleton<ValidateInvoice>();
builder.Services.AddSingleton<CalculateTax>();
builder.Services.AddSingleton<PersistInvoice>();
builder.Services.AddSingleton<VoidInvoice>();
builder.Services.AddSingleton<ReadInvoiceRequest>();
builder.Services.AddSingleton<DueInvoice>();

builder.Services.AddSingleton<RequestInvoiceFlow.Dispatcher>();
builder.Services.AddSingleton<IssueInvoiceOverHttpFlow.Dispatcher>();
builder.Services.AddSingleton<IssueInvoiceOverBusFlow.Dispatcher>();
builder.Services.AddSingleton<IssueInvoiceOverChangeFlow.Dispatcher>();
builder.Services.AddSingleton<IssueInvoiceOverScheduleFlow.Dispatcher>();

var app = builder.Build();

// Applying DDL is a decision, not a consequence of building a container — every replica of a
// rolling update would otherwise race to migrate as it starts. A real deployment calls this from
// wherever it runs schema changes; a sample calls it here so `dotnet run` against an empty
// database works.
await app.Services.GetRequiredService<PostgresMigrator>()
    .MigrateAsync(CancellationToken.None)
    .ConfigureAwait(false);

app.MapHealthChecks("/health");

// Both HTTP routes, generated from the [HttpTrigger] on the flows that declare them.
app.MapFlowX();

// The bus subscription, the change subscription and the schedule, each generated from the one
// attribute that declares it and from the same reading of that attribute which produced
// `flowx.manifest.json`. Nothing in this file names `invoice.requested`, `billing` or 03:00.
app.Services.AddFlowXSubscriptions();
app.Services.AddFlowXChangeSubscriptions();
app.Services.AddFlowXSchedules();

// A denser schedule beside the declared one, for watching the cron transport work.
// `0 3 * * *` is the business number and is what the manifest publishes; overriding it would
// mean the declaration a reader sees is not the one that runs. `FLOWX_SAMPLE_SCHEDULE_CRON="* * * * *"`
// with `FLOWX_SAMPLE_SCHEDULE_SCAN=00:00:01` fires it once a minute — and `invoice.due` then
// needs the calendar to hold something at that occurrence, which is what the seed below does.
if (Environment.GetEnvironmentVariable("FLOWX_SAMPLE_SCHEDULE_CRON") is { Length: > 0 } demonstration)
{
    app.Services.GetRequiredService<FlowScheduleCatalog>().Add(
        FlowSchedule.Create(
            IssueInvoiceOverScheduleFlow.Plan.Flow.Id,
            IssueInvoiceOverScheduleFlow.Plan.Flow.Version,
            demonstration,
            "UTC",
            MissedFirePolicy.RunOnce),
        IssueInvoiceOverScheduleFlow.Plan,
        app.Services.GetRequiredService<IssueInvoiceOverScheduleFlow.Dispatcher>());

    // What that firing bills. A real deployment reads a billing calendar; this one seeds the
    // next sixty minutes so the demonstration has something due whenever it is started.
    var calendar = app.Services.GetRequiredService<InMemoryBillingCalendar>();
    var from = DateTimeOffset.UtcNow;

    for (var minute = 0; minute < 60; minute++)
    {
        calendar.Add(
            from.AddMinutes(minute),
            new IssueInvoice("NIGHTLY-" + minute.ToString(System.Globalization.CultureInfo.InvariantCulture), "acme", 100m, "GBP"));
    }
}

await app.RunAsync().ConfigureAwait(false);
