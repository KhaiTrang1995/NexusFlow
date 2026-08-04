using System.Globalization;
using FlowX;
using FlowX.Generated;
using FlowX.Hosting;
using FlowX.Postgres;
using FlowX.Runtime;
using Microsoft.AspNetCore.Authentication;
using Polling;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddRouting();
builder.Services.AddFlowX(options =>
{
    options.ApplicationName = "Polling";

    if (Environment.GetEnvironmentVariable("FLOWX_SAMPLE_NODE") is { Length: > 0 } node)
    {
        options.NodeName = node;
    }

    // The resolution of every wait this application declares, and the number a demonstration
    // has to move. A poll's five-second gap under a ten-second sweep is between five and
    // fifteen seconds — a wait is a lower bound and never an upper one, which is the only
    // promise a sweep can keep — so a run that wants to watch a poll make six attempts has to
    // wind this down or it spends the demonstration inside the sweep interval.
    if (TimeSpan.TryParse(
            Environment.GetEnvironmentVariable("FLOWX_SAMPLE_TIMER_SCAN"),
            CultureInfo.InvariantCulture,
            out var sweep) && sweep > TimeSpan.Zero)
    {
        options.TimerScanInterval = sweep;
    }
});

builder.Services
    .AddAuthentication(DocumentTokenHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, DocumentTokenHandler>(
        DocumentTokenHandler.SchemeName, null);

// The journal, and the reason this application needs a database at all.
//
// `document.process` declares Durable, and a durable flow on a host that registered no journal
// and no lease store is refused with `flow.durability_not_configured` before its first step.
// AddFlowXPostgres registers all four contracts over one data source, and the fourth —
// ITimerIndex — is the one this sample lives or dies on: a poll parks between attempts, and a
// host with no timer index parks instances correctly and never comes back for them. That is
// not a hang, it is a document that is polled once and then waits for its flow deadline.
builder.Services.AddFlowXPostgres(
    builder.Configuration.GetConnectionString("FlowX")
    ?? Environment.GetEnvironmentVariable("FLOWX_POSTGRES_CONNECTION")
    ?? "Host=localhost;Port=5432;Database=postgres;Username=postgres");

// The OCR provider. In memory here; the capabilities do not know or care.
//
// How long a job takes is read from the environment so that a demonstration does not have to
// find a document that takes four hours. It is a property of the *stand-in*, not of the flow:
// the poll's own schedule and budget are declared in `Waits` and reach the plan and the
// manifest, and nothing here can change them.
builder.Services.AddSingleton<IOcrService>(_ => new InMemoryOcrService(
    TimeProvider.System,
    TimeSpan.TryParse(
        Environment.GetEnvironmentVariable("FLOWX_SAMPLE_OCR_PER_PAGE"),
        CultureInfo.InvariantCulture,
        out var perPage) && perPage > TimeSpan.Zero
        ? perPage
        : TimeSpan.FromSeconds(20)));

// The capabilities, generated from the constructor the generator wrote — samples/ecommerce
// gives the reason they are singletons.
builder.Services.AddFlowXCapabilities();

var app = builder.Build();

// Applying DDL is a decision, not a consequence of building a container. A real deployment
// calls this from wherever it runs schema changes; a sample calls it here, once, so that
// `dotnet run` against an empty database works.
await app.Services.GetRequiredService<PostgresMigrator>()
    .MigrateAsync(CancellationToken.None)
    .ConfigureAwait(false);

// What makes a parked instance resumable by a sweep rather than only by a request, and the one
// line without which this sample makes exactly one OCR call per document.
//
// An instance a sweep finds is a row carrying a flow id and a version and nothing else, so
// something has to turn those two strings back into an ExecutionPlan and an IStepDispatcher.
// Without this registration FlowTimerScan finds the parked document, cannot run it, and counts
// it NotRunnable — which is the honest answer to "this node was not deployed with that flow"
// and is indistinguishable, from the outside, from an OCR provider that never answers.
app.Services.GetRequiredService<FlowCatalog>().Add(
    ProcessDocumentFlow.Plan,
    app.Services.GetRequiredService<ProcessDocumentFlow.Dispatcher>());

app.UseAuthentication();

app.MapHealthChecks("/health");

// The one generated route, from the [HttpTrigger] on the flow.
//
//   POST /api/v1/documents  -> 202 { instanceId, status: "suspended" }
//
// 202 with no `awaiting` array, and that is exactly right: the array lists the signals a caller
// could deliver to continue the instance, and a poll is not waiting for anybody — it is waiting
// for a clock. There is nothing to address, which is the difference between this sample and
// samples/workflow's, and the reason no signal route is published here.
app.MapFlowX();

await app.RunAsync().ConfigureAwait(false);
