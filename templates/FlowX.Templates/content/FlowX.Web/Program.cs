using FlowX.Hosting;
using FlowX.Http;
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
builder.Services.AddSingleton<ValidateTicket>();
builder.Services.AddSingleton<RecordTicket>();
builder.Services.AddSingleton<OpenTicketFlow.Dispatcher>();

var app = builder.Build();

app.MapHealthChecks("/health");

// One endpoint, from the compiled plan. Plan, Dispatcher, Projection and
// SensitiveMembers are all generated from OpenTicketFlow.Define — nothing here restates
// the flow, so the two cannot drift. Keep the method and route equal to the
// [HttpTrigger] on the flow until the endpoint generator emits this call itself.
app.MapFlow(
    "POST",
    "/api/v1/tickets",
    OpenTicketFlow.Plan,
    services => services.GetRequiredService<OpenTicketFlow.Dispatcher>(),
    OpenTicketFlow.Projection,
    AppJsonContext.Default.OpenTicket,
    AppJsonContext.Default.TicketOpened,
    requireIdempotencyKey: true,
    sensitiveMembers: OpenTicketFlow.SensitiveMembers);

await app.RunAsync().ConfigureAwait(false);
