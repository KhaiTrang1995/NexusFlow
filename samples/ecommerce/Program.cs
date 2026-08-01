using Ecommerce;
using FlowX.Generated;
using FlowX.Hosting;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddRouting();
builder.Services.AddFlowX(options => options.ApplicationName = "Ecommerce");

// Infrastructure. In-memory here; the capabilities do not know or care.
builder.Services.AddSingleton<IInventoryStore, InMemoryInventoryStore>();
builder.Services.AddSingleton<IPaymentGateway, AlwaysApprovesGateway>();

// The capabilities themselves. The generated dispatcher takes them as constructor
// parameters, so a missing registration is a startup failure rather than a null
// reference on the first request.
//
// These stay hand-written on purpose. The generator knows exactly which types the
// dispatcher needs — it wrote that constructor — but nothing in the flow model declares
// a service lifetime, so a generated AddSingleton would be the generator inventing a
// fact rather than publishing one. A missing line here fails at start-up and names the
// type; a wrong lifetime would be a captive dependency in a file nobody wrote.
builder.Services.AddSingleton<ValidateOrder>();
builder.Services.AddSingleton<ReserveInventory>();
builder.Services.AddSingleton<ReleaseInventory>();
builder.Services.AddSingleton<CapturePayment>();
builder.Services.AddSingleton<PlaceOrderFlow.Dispatcher>();

// Spans to the console, metrics at /metrics. Hand-written rather than an OpenTelemetry SDK
// reference because this is the repository's only NativeAOT-published assembly (constraint
// C2), and the thing being demonstrated is that FlowX emits through an ActivitySource and a
// Meter named "FlowX" — which is exactly the seam an SDK attaches to. A real deployment
// deletes this line and adds AddSource("FlowX") and AddMeter("FlowX") instead.
//
// Registered as a singleton so the host disposes it, and started before the app runs so the
// very first request is observed. FlowX allocates nothing per step until this exists, which
// is budget B6 and is asserted in TelemetryCostTests rather than claimed here.
var telemetry = SampleTelemetry.Start();

builder.Services.AddSingleton(telemetry);

var app = builder.Build();

app.MapHealthChecks("/health");

// The scrape endpoint. Prometheus text format, and deliberately not behind the same routing
// as the flow endpoints: an operator's scraper is not a caller of this application's API.
app.MapGet("/metrics", (SampleTelemetry collected) =>
    Results.Text(collected.Scrape(), "text/plain; version=0.0.4"));

// Every endpoint this application declares, generated from the [HttpTrigger] on the flow
// that declares it. The method, the route and the idempotency rule come from the same
// reading of that attribute which produced flowx.manifest.json, so the address served and
// the address published cannot disagree — and the plan, the dispatcher, the projection
// and the sensitive-member list are the flow's own generated members, named rather than
// restated. Nothing in this file mentions order.place, and nothing in it can drift from
// the flow.
app.MapFlowX();

// `await RunAsync()` rather than `Run()`. Identical behaviour — top-level statements compile
// to an async entry point, so the process still blocks here until shutdown — and it is the
// form that stays correct if a reader lifts the line into a method of their own. The
// `ConfigureAwait(false)` is what CA2007 asks for repository-wide; nothing runs after this
// await, so it changes nothing at this call site beyond satisfying the rule.
await app.RunAsync().ConfigureAwait(false);
