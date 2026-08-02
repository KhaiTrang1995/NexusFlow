using FlowX.Generated;
using FlowX.Hosting;
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

// The capabilities, and the dispatcher the generator emitted for the flow. The
// dispatcher takes them as constructor parameters, so a missing registration is a
// start-up failure rather than a null reference on the first request. There is no
// assembly scan.
//
// These three lines are the only wiring the generator does not write, and deliberately:
// it knows which types the dispatcher needs, but nothing in the flow declares a service
// lifetime, so choosing one for you would be inventing a fact. Forgetting a line here
// fails at start-up and names the type.
builder.Services.AddSingleton<ValidateTicket>();
builder.Services.AddSingleton<RecordTicket>();
builder.Services.AddSingleton<OpenTicketFlow.Dispatcher>();

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

await app.RunAsync().ConfigureAwait(false);
