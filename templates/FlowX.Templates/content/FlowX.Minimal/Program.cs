using FlowX.Generated;
using FlowX.Hosting;
using FlowXMinimal;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddRouting();

// ApplicationName has no default: a manifest and a trace stream that cannot say which
// application produced them are worth noticeably less than ones that can.
builder.Services.AddFlowX(options => options.ApplicationName = "FlowXMinimal");

// Yours. The capabilities depend on this, not on a database.
builder.Services.AddSingleton<TicketBook>();

// Every capability the flows step through, and every generated dispatcher. Read off the
// constructors the generator wrote, so this line does not grow when you add a capability.
builder.Services.AddFlowXCapabilities();

var app = builder.Build();

// Every route the flows declared, and — the moment you add a [BusTrigger], a [CronTrigger] or a
// [ChangeTrigger] — its subscription or schedule too. This line does not change when they do.
app.UseFlowX();

await app.RunAsync().ConfigureAwait(false);
