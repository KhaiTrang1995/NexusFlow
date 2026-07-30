using Ecommerce;
using FlowX.Hosting;
using FlowX.Http;
using FlowX.Runtime;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddRouting();
builder.Services.AddFlowX(options => options.ApplicationName = "Ecommerce");

// Infrastructure. In-memory here; the capabilities do not know or care.
builder.Services.AddSingleton<IInventoryStore, InMemoryInventoryStore>();
builder.Services.AddSingleton<IPaymentGateway, AlwaysApprovesGateway>();

// The capabilities themselves. The generated dispatcher takes them as constructor
// parameters, so a missing registration is a startup failure rather than a null
// reference on the first request.
builder.Services.AddSingleton<ValidateOrder>();
builder.Services.AddSingleton<ReserveInventory>();
builder.Services.AddSingleton<ReleaseInventory>();
builder.Services.AddSingleton<CapturePayment>();
builder.Services.AddSingleton<PlaceOrderFlow.Dispatcher>();

var app = builder.Build();

app.MapHealthChecks("/health");

// One endpoint, from the compiled plan. Every piece of it is generated: the plan, the
// dispatcher and the projection all come from PlaceOrderFlow.Define. The generator will
// emit this registration too, from the [HttpTrigger] attribute, in a later phase — it is
// written by hand here so the sample runs against what exists today.
app.MapFlow(
    "POST",
    "/api/v1/orders",
    PlaceOrderFlow.Plan,
    services => services.GetRequiredService<PlaceOrderFlow.Dispatcher>(),
    PlaceOrderFlow.Projection,
    EcommerceJsonContext.Default.PlaceOrder,
    EcommerceJsonContext.Default.OrderPlacedResult,
    requireIdempotencyKey: true,
    sensitiveMembers: PlaceOrderFlow.SensitiveMembers);

app.Run();
