using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EventDriven;
using FlowX;
using FlowX.Generated;
using FlowX.Hosting;
using FlowX.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace EventDriven.Tests;

/// <summary>
/// The sample's two HTTP routes, on a real server, over the journal the other three transports
/// use.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The routes come from <c>MapFlowX()</c> and are not restated here.</strong> That method
/// is generated from the same reading of the <c>[HttpTrigger]</c> attributes which produced
/// <c>flowx.manifest.json</c>, so a test that named <c>/api/v1/invoices</c> in its own route
/// table would be asserting against an address the application does not necessarily serve.
/// </para>
/// <para>
/// <strong>The stores are registered as instances rather than through
/// <c>AddFlowXPostgres</c>.</strong> The portability fixture owns the schema, the migration and
/// the lifetime; a second registration over the same connection string would build a second data
/// source and a second lease store, and the HTTP arm would then be running against stores the
/// other three arms cannot see.
/// </para>
/// </remarks>
internal sealed class InvoiceWebHost : IAsyncDisposable
{
    private readonly IHost _host;

    /// <summary>Builds the server. Nothing is listening until <see cref="StartAsync"/>.</summary>
    /// <param name="journal">The journal every transport commits to.</param>
    /// <param name="leases">Where exclusive ownership comes from.</param>
    /// <param name="validate">The shared validating capability.</param>
    /// <param name="tax">The shared tax capability.</param>
    /// <param name="persist">The shared persisting capability.</param>
    /// <param name="void">The shared compensation.</param>
    public InvoiceWebHost(
        IFlowJournal journal,
        ILeaseStore leases,
        ValidateInvoice validate,
        CalculateTax tax,
        PersistInvoice persist,
        VoidInvoice @void) =>
        _host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddFlowX(options =>
                    {
                        options.ApplicationName = "EventDriven";
                        options.NodeName = "test-node";
                    });

                    services.AddSingleton(journal);
                    services.AddSingleton(leases);
                    services.AddSingleton(validate);
                    services.AddSingleton(tax);
                    services.AddSingleton(persist);
                    services.AddSingleton(@void);
                    services.AddSingleton<IssueInvoiceOverHttpFlow.Dispatcher>();
                    services.AddSingleton<RequestInvoiceFlow.Dispatcher>();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapFlowX());
                }))
            .Build();

    /// <summary>Starts listening.</summary>
    /// <param name="ct">Cancels the start.</param>
    public Task StartAsync(CancellationToken ct) => _host.StartAsync(ct);

    /// <summary>Posts a request and reads the flow's declared output back off the wire.</summary>
    /// <typeparam name="TOut">What the flow returns.</typeparam>
    /// <param name="route">The generated route.</param>
    /// <param name="request">The body.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>The deserialised response.</returns>
    public async Task<TOut> PostAsync<TOut>(string route, IssueInvoice request, CancellationToken ct)
    {
        using var client = _host.GetTestClient();
        using var content = JsonContent.Create(request, EventDrivenJsonContext.Default.IssueInvoice);

        // Both routes declare Idempotent = true, so the header is part of their contract rather
        // than an option: without it the endpoint answers 400 before the flow starts.
        content.Headers.Add(FlowXHeaders.IdempotencyKey, route + "-" + request.Reference);

        using var response = await client.PostAsync(new Uri(route, UriKind.Relative), content, ct);

        var body = await response.Content.ReadAsStringAsync(ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);

        return JsonSerializer.Deserialize<TOut>(
                body, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<TOut>)
                    EventDrivenJsonContext.Default.GetTypeInfo(typeof(TOut))!)
            ?? throw new InvalidOperationException($"{route} returned no body.");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();

        _host.Dispose();
    }
}
