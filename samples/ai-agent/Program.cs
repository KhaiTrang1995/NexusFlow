using AiAgent;
using FlowX.Generated;
using FlowX.Hosting;
using FlowX.Mcp;
using Microsoft.AspNetCore.Authentication;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddRouting();
builder.Services.AddFlowX(options => options.ApplicationName = "AiAgent");

// Authentication, which is the only reason this application has a principal at all. The stances
// are on the capabilities — ticket.load and ticket.search are Authenticated, payment.refund needs
// payment.refund, ops.review needs ops.read — and the engine decides them against
// HttpContext.User, which is what this line populates. Nothing here names a route, a tool or a
// permission: an endpoint-level rule would hold over HTTP and not over an agent, which is exactly
// the transport-attached authorisation a capability stance replaces.
builder.Services
    .AddAuthentication(DemoTokenHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, DemoTokenHandler>(DemoTokenHandler.SchemeName, null);

builder.Services.AddSingleton<ITicketDesk, InMemoryTicketDesk>();
builder.Services.AddSingleton<IPaymentGateway, AlwaysRefundsGateway>();

// FlowX.Ai, reading the manifest this build published and nothing else. It is the same constant
// FlowX.Mcp projects tools/list and the resource surface from, so a reader of this application
// over MCP and the reviewer running inside it are reading one document.
builder.Services.AddSingleton<IApplicationReviewer>(new ManifestReviewer(FlowXManifest.Json));

// Scoped, where the other two are singletons, and the difference is the whole of what sampling
// costs. ReviewApplication takes IAgentSampler — the channel back to *this call's* client — so
// neither it nor the dispatcher that holds it may be captured by a singleton. A capability that
// talks to its caller is a different kind of thing from one that talks to a database, and this is
// where that shows up.
//
// It works because this flow is reached over HTTP — the MCP surface is a route, so the request's
// scope is the call's. A capability behind a bus, change, schedule or stream trigger has no such
// scope and cannot be registered this way; the catalogues hold a resolved dispatcher, and a
// recovery sweep resumes an instance days later with nothing to resolve one from.
//
// Written *before* the generated registrations on purpose: those are TryAdd, so a service already
// in the collection is left exactly as it is.
builder.Services.AddScoped<ReviewApplication>();
builder.Services.AddScoped<ReviewApplicationFlow.Dispatcher>();

builder.Services.AddFlowXCapabilities();

// Every flow declaring [AgentTrigger], bound to the tool the manifest publishes for it, plus the
// compiled-in manifest the surface projects from. Generated. Nothing in this file names
// ticket.refund, its description, its permission or its confirmation requirement.
builder.Services.AddFlowXAgentTools();

var app = builder.Build();

app.UseAuthentication();

app.MapHealthChecks("/health");

// Every endpoint declared by an [HttpTrigger], generated from the same reading of the attribute
// that produced flowx.manifest.json. ticket.refund is reachable here and by an agent, which is
// what lets tests/AiAgent.Tests compare the two refusals rather than describe them.
app.MapFlowX();

// The agent surface.
//
// ConfirmationPolicy.Elicit is the one line in this application that is not the default, and it
// is what makes the sample's second claim true. Under it, a tools/call for a flow whose declared
// side effects require a human is answered by asking the client's human over
// `elicitation/create` — and the flow is not entered until an approval comes back. Declined,
// cancelled, unanswered, or a client that cannot be asked are all refusals, and in every one of
// them RefundPayment never runs.
//
// It is not an authorisation setting and cannot substitute for one. payment.refund's stance is
// decided in the step loop against the caller's own claims either way: an agent that approves its
// own prompt and holds no payment.refund is refused at the step exactly as it would have been
// unasked. See docs/adr/ADR-0060.
app.MapFlowXMcp(FlowMcpEndpointExtensions.DefaultRoute, new McpOptions
{
    Confirmation = ConfirmationPolicy.Elicit,
});

await app.RunAsync().ConfigureAwait(false);
