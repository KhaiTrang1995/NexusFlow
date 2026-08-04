using FlowX.Generated;
using FlowX.Hosting;
using FlowX.Mcp;
using FlowXStarter;
using Microsoft.AspNetCore.Authentication;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddRouting();

// ApplicationName has no default: a manifest and a trace stream that cannot say which
// application produced them are worth noticeably less than ones that can. The options
// are validated at start-up, so a bad value is a pod that never becomes ready.
builder.Services.AddFlowX(options => options.ApplicationName = "FlowXStarter");

// Authentication, which is the only reason this application has a principal at all.
//
// The stances live on the capabilities — ticket.validate admits any authenticated caller,
// ticket.record requires the ticket.write permission — and the engine decides both against
// HttpContext.User, which is what this line populates. Nothing here names a route or a
// permission: a rule attached to the endpoint would hold over HTTP and not over a broker or
// an agent, which is exactly the transport-attached authorisation a capability stance exists
// to replace.
//
// StarterTokenHandler is a stand-in for an OIDC handler and says so at length. Replacing it
// with AddJwtBearer is a one-line change and nothing else in this project moves.
builder.Services
    .AddAuthentication(StarterTokenHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, StarterTokenHandler>(StarterTokenHandler.SchemeName, null);

// Infrastructure. In memory here; the capabilities do not know or care.
builder.Services.AddSingleton<ITicketStore, InMemoryTicketStore>();

// The capabilities the flow steps through, and the dispatcher the generator emitted for
// it — generated from the constructor the generator itself wrote, so a capability cannot
// be missing from the container. There is no assembly scan.
//
// Singleton, because that is the only lifetime the runtime can honour: the catalogues hold
// a resolved dispatcher for the life of the node, and a recovery sweep resumes an instance
// with no scope to resolve another from. Everything that varies per invocation — tenant,
// principal, idempotency key, deadline, clock, ids — arrives on CapabilityContext instead,
// which is also what lets a resumed instance replay identically.
//
// TryAdd, so registering a capability yourself — behind an interface, or decorated — wins.
builder.Services.AddFlowXCapabilities();

// Every flow carrying an [AgentTrigger], bound to the tool the manifest publishes for it.
// Generated, like the endpoint below — and like it, nothing here names the flow, its
// description or the permissions it needs. All of them are read out of the compiled-in
// manifest at run time, so what an agent is told and what your build published are one
// document rather than two that agree today.
builder.Services.AddFlowXAgentTools();

var app = builder.Build();

// Runs the scheme above, so HttpContext.User carries the token's claims by the time the
// generated endpoint reads it. Without this line the handler is registered and never
// invoked, every request is anonymous, and the first step of every flow is refused — a
// failure mode worth naming, because it looks exactly like a broken token.
app.UseAuthentication();

app.MapHealthChecks("/health");

// Every endpoint this application declares, generated from the [HttpTrigger] on the flow.
// The method, the route and the Idempotency-Key rule come from that attribute — the same
// reading of it that produced flowx.manifest.json — and the plan, the dispatcher, the
// projection and the sensitive-member list are the flow's own generated members. Change
// the route on the flow and this line still serves it. There is nothing here to keep in
// step, because there is nothing here that restates the flow.
app.MapFlowX();

// The agent surface: one route, JSON-RPC in, JSON-RPC out. It serves `initialize`,
// `tools/list` and `tools/call`, and it makes no authorisation decision of its own — a call
// builds its invocation with the same reader the HTTP route uses, so the capability stances
// decide an agent's call exactly as they decide a request's. Delete this line and the
// [AgentTrigger] on the flow and nothing else in the project moves.
app.MapFlowXMcp();

await app.RunAsync().ConfigureAwait(false);
