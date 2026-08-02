using Ecommerce;
using FlowX.Generated;
using FlowX.Hosting;
using Microsoft.AspNetCore.Authentication;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddRouting();
builder.Services.AddFlowX(options => options.ApplicationName = "Ecommerce");

// Authentication, which is the only reason this application has a principal at all.
//
// The stances are on the capabilities — order.validate and inventory.reserve are
// Authenticated, payment.capture requires the payment.write permission — and the engine
// decides them against HttpContext.User, which is what this line populates. Nothing here
// names a route or a permission: an endpoint-level rule would hold over HTTP and not over a
// broker or an agent, which is exactly the transport-attached authorisation the capability
// stance exists to replace (docs/15-Security.md §4, ADR-0004).
//
// DemoTokenHandler is a stand-in for an OIDC handler and says so at length. A real
// deployment replaces this one call with AddJwtBearer and changes nothing else.
builder.Services
    .AddAuthentication(DemoTokenHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, DemoTokenHandler>(DemoTokenHandler.SchemeName, null);

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
builder.Services.AddSingleton<RepriceBasket>();
builder.Services.AddSingleton<PlaceOrderFlow.Dispatcher>();
builder.Services.AddSingleton<ConfirmOrderFlow.Dispatcher>();
builder.Services.AddSingleton<RepriceOrderFlow.Dispatcher>();

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

// Runs the scheme above, so HttpContext.User carries the token's claims by the time the
// generated endpoint reads it. Without this line the handler is registered and never
// invoked, every request is anonymous, and the first step of every order is refused —
// which is a failure mode worth naming, because it looks exactly like a broken token.
app.UseAuthentication();

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

// Every bus subscription this application declares, generated from the [BusTrigger] on the flow
// that declares it. The topic and the group come from the same reading of that attribute which
// produced flowx.manifest.json, so the address published and the address consumed cannot
// disagree — and both are terms every node derives a delivery's instance id from, which is what
// makes one message start one flow rather than one per delivery.
//
// This host registers no IBusConsumer and no journal, so the subscription is registered and
// consumes nothing: `dotnet run` still serves POST /api/v1/orders against an in-memory inventory
// and needs no infrastructure at all, which is this sample's whole value. Wiring a broker is one
// AddFlowXRedisStreamConsumer call and one AddFlowXPostgres call — see the README, and see
// tests/Ecommerce.Tests/EmitStartsAFlowTests, which makes both against real servers and is where
// the journal rows in the README come from.
app.Services.AddFlowXSubscriptions();

// Every change subscription this application declares, generated from the [ChangeTrigger] on the
// flow that declares it. Same arrangement as the line above and the same guarantee about the
// address; what differs is what serves it — an IChangeFeed over this deployment's own outbox
// rather than a broker, so the transport needs no infrastructure the journal did not already
// require (ADR-0047). This host registers neither, so it is registered and observes nothing.
app.Services.AddFlowXChangeSubscriptions();

// `await RunAsync()` rather than `Run()`. Identical behaviour — top-level statements compile
// to an async entry point, so the process still blocks here until shutdown — and it is the
// form that stays correct if a reader lifts the line into a method of their own. The
// `ConfigureAwait(false)` is what CA2007 asks for repository-wide; nothing runs after this
// await, so it changes nothing at this call site beyond satisfying the rule.
await app.RunAsync().ConfigureAwait(false);
