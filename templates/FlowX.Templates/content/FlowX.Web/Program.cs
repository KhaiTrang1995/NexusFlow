using FlowX.Generated;
using FlowX.Hosting;
using FlowXStarter;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddRouting();

// ApplicationName has no default: a manifest and a trace stream that cannot say which
// application produced them are worth noticeably less than ones that can. The options
// are validated at start-up, so a bad value is a pod that never becomes ready.
builder.Services.AddFlowX(options => options.ApplicationName = "FlowXStarter");

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

app.MapHealthChecks("/health");

// Every endpoint this application declares, generated from the [HttpTrigger] on the flow.
// The method, the route and the Idempotency-Key rule come from that attribute — the same
// reading of it that produced flowx.manifest.json — and the plan, the dispatcher, the
// projection and the sensitive-member list are the flow's own generated members. Change
// the route on the flow and this line still serves it. There is nothing here to keep in
// step, because there is nothing here that restates the flow.
app.MapFlowX();

await app.RunAsync().ConfigureAwait(false);
