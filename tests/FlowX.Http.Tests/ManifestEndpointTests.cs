using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace FlowX.Http.Tests;

/// <summary>
/// The manifest, served as it was written.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists beside the OpenAPI document.</strong> OpenAPI describes an HTTP
/// surface: paths, bodies, status codes. It cannot describe a flow's execution profile, and it
/// omits every trigger that is not a path — the bus subscriptions, the schedules, the agent tools.
/// A screen listing what an application does was hand-written from memory because there was
/// nothing to read; five of the eleven flows on it named flows that did not exist.
/// </para>
/// <para>
/// <strong>Unchanged is the claim.</strong> Anything that reformatted, re-ordered or re-encoded
/// the manifest would produce a document that is nearly the compiler's, and "nearly" is the
/// property that costs an afternoon.
/// </para>
/// </remarks>
public sealed class ManifestEndpointTests : IAsyncLifetime
{
    private const string Manifest = """
        {
          "schemaVersion": "0.1.0",
          "application": { "name": "Crm", "version": "2.1.0" },
          "flows": [
            {
              "id": "crm.lead.capture",
              "profile": "Durable",
              "deadline": "PT15S",
              "triggers": [
                { "kind": "Http", "method": "POST", "route": "/api/v1/crm/leads" },
                { "kind": "Bus", "topic": "lead.created" }
              ]
            }
          ]
        }
        """;

    private IHost _host = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services => services.AddRouting())
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapFlowXManifest(Manifest));
                }))
            .StartAsync(TestContext.Current.CancellationToken);

        _client = _host.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();

        if (_host is not null)
        {
            await _host.StopAsync(TestContext.Current.CancellationToken);
            _host.Dispose();
        }
    }

    /// <summary>The manifest comes back byte for byte, as JSON.</summary>
    [Fact]
    public async Task TheManifestIsServedUnchanged()
    {
        var response = await _client.GetAsync(
            OpenApiEndpointExtensions.DefaultManifestRoute, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();

        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");

        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldBe(Manifest, "anything that reformats it is not the compiler's document.");
    }

    /// <summary>
    /// It carries what OpenAPI cannot: the profile, and the triggers that are not paths.
    /// </summary>
    /// <remarks>
    /// The two reasons a reader asks for this instead of the derived document. A flow that stages
    /// an event and a flow that reads a report look identical over HTTP until the process dies
    /// halfway through, and a subscription has no route to appear under at all.
    /// </remarks>
    [Fact]
    public async Task ItCarriesTheProfileAndTheTriggersWithNoRoute()
    {
        var body = await _client.GetStringAsync(
            OpenApiEndpointExtensions.DefaultManifestRoute, TestContext.Current.CancellationToken);

        body.ShouldContain("\"profile\": \"Durable\"");
        body.ShouldContain("\"kind\": \"Bus\"");
        body.ShouldContain("\"topic\": \"lead.created\"");
    }

    /// <summary>Served on a route of its own, not over the OpenAPI document.</summary>
    /// <remarks>
    /// They are different documents about the same application, and a build that served one at
    /// the other's address would hand a client generator a manifest it cannot read.
    /// </remarks>
    [Fact]
    public void ItsRouteIsNotTheDocumentsRoute()
    {
        OpenApiEndpointExtensions.DefaultManifestRoute
            .ShouldNotBe(OpenApiEndpointExtensions.DefaultRoute);
    }
}
