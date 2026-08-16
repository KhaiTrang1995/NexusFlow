using FlowX;
using FlowX.Generated;
using FlowX.Hosting;
using FlowX.Postgres;
using Functions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// The isolated worker's own builder. No ConfigureFunctionsWebApplication: that is the ASP.NET
// Core integration, and the generated HTTP entry points take HttpRequestData rather than
// HttpRequest precisely so a worker does not have to bring a second web stack.
var builder = FunctionsApplication.CreateBuilder(args);

builder.Services.AddFlowX(options =>
{
    options.ApplicationName = "Functions";

    // ==========================================================================
    // The one line that makes this deployment shape correct.
    // ==========================================================================
    //
    // The platform is the scheduler now. FlowXSchedulePass fires FlowScheduleScan on the
    // platform's timer and FlowXChangePass would fire FlowChangeScan on the same; leaving
    // the sweeps on would run both loops beside them, and a due occurrence would be looked
    // at twice a minute by two things that each believe they own the interval.
    //
    // ADR-0031 makes the *outcome* of that harmless — the derived instance id and the
    // journal's primary key refuse the second firing — which is exactly why it is worth
    // being explicit about: nothing would break, the worker would simply pay for two of
    // everything for ever, and no test anywhere would fail.
    //
    // It also matters for a reason peculiar to serverless: a background sweep keeps the
    // process doing work, and a host that never goes idle is a host that never scales to
    // zero. The sweeps are what a consumption plan is billed for.
    options.Sweeps = HostSweeps.None;
});

// The journal. Every flow here declares Durable, and on a push host the primary key on
// flow_instance is the only thing that makes an at-least-once redelivery inert.
builder.Services.AddFlowXPostgres(
    Environment.GetEnvironmentVariable("FLOWX_POSTGRES_CONNECTION")
    ?? "Host=localhost;Port=5432;Database=postgres;Username=postgres");

builder.Services.AddSingleton<InMemoryOrderBook>();

// The capabilities, from the constructor the generator wrote.
builder.Services.AddFlowXCapabilities();

var app = builder.Build();

// The catalogue the generated entry points read. AddFlowXSubscriptions is what puts
// `order.placed` as `stock` on this node, and FlowPushSeams.BusAsync looks the registration
// up by the same topic the generated [ServiceBusTrigger] declares — one reading, two places
// it is used, no second copy to drift.
app.Services.AddFlowXSubscriptions();
app.Services.AddFlowXSchedules();

await app.RunAsync().ConfigureAwait(false);
