using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FlowX.Generated;
using FlowX.Hosting;
using FlowX.Http;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlowX.Mcp.Tests;

/// <summary>
/// A real ASP.NET Core host serving this assembly's flows down both transports at once.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Both surfaces are registered from generated code, not by hand.</strong>
/// <c>MapFlowX()</c> comes from <c>FlowXEndpoints.g.cs</c> and
/// <c>AddFlowXAgentTools()</c> from <c>FlowXAgentTools.g.cs</c>, both written by the real
/// generator during this project's build. So what these tests exercise is the composition a
/// consumer's application gets, including the manifest constant the emitted registration
/// hands to the agent surface.
/// </para>
/// <para>
/// <strong>One process, one plan, two transports</strong> — which is what makes the refusal
/// comparison meaningful. <c>booking.book</c> is reachable at <c>POST /api/v1/bookings</c>
/// and as the agent tool <c>booking_book</c>, and both run the same
/// <c>FlowEngine.ExecuteAsync</c> over the same <c>ChargeCard</c> and its
/// <c>booking:write</c> stance.
/// </para>
/// <para>
/// The authentication scheme reads the caller's permissions out of a test header. That is a
/// substitute for a token issuer and for nothing else: the claims it produces are ordinary
/// validated claims on <c>HttpContext.User</c>, which is the only thing
/// <c>HttpTriggerReader</c> and then <c>StepAuthorization</c> ever look at.
/// </para>
/// </remarks>
internal sealed class AgentHost : IAsyncDisposable
{
    /// <summary>The header the test scheme reads a caller's permissions from.</summary>
    public const string PermissionsHeader = "X-Test-Permissions";

    /// <summary>The route the agent surface is served on.</summary>
    public const string McpRoute = "/mcp";

    /// <summary>The route <c>booking.book</c>'s HTTP trigger declares.</summary>
    public const string BookingRoute = "/api/v1/bookings";

    private readonly IHost _host;

    private AgentHost(IHost host)
    {
        _host = host;
        Client = host.GetTestClient();
    }

    /// <summary>A client bound to the in-memory server.</summary>
    public HttpClient Client { get; }

    /// <summary>The surface the endpoint resolved, for assertions that need no wire.</summary>
    public McpServer Server => _host.Services.GetRequiredService<McpServer>();

    /// <summary>Starts the host.</summary>
    /// <param name="ct">The test's cancellation token.</param>
    public static async Task<AgentHost> StartAsync(CancellationToken ct)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddFlowX(o => o.ApplicationName = "Bookings");

                    services.AddAuthentication(HeaderScheme.Name)
                        .AddScheme<AuthenticationSchemeOptions, HeaderScheme>(HeaderScheme.Name, null);

                    services.AddSingleton<ChargeCard>();
                    services.AddSingleton<QuoteRoom>();
                    services.AddSingleton<BookRoomFlow.Dispatcher>();
                    services.AddSingleton<QuoteRoomFlow.Dispatcher>();

                    // Generated. The manifest and both bindings come from FlowXAgentTools.g.cs.
                    services.AddFlowXAgentTools();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseEndpoints(endpoints =>
                    {
                        // Generated. booking.book's [HttpTrigger], so the same flow is
                        // reachable both ways in one process.
                        endpoints.MapFlowX(AgentJsonContext.Default);
                        endpoints.MapFlowXMcp(McpRoute);
                    });
                }))
            .StartAsync(ct);

        return new AgentHost(host);
    }

    /// <summary>Posts one JSON-RPC message and parses the response.</summary>
    /// <param name="body">The request document.</param>
    /// <param name="permissions">
    /// The permissions the caller holds, or <c>null</c> for an anonymous agent.
    /// </param>
    /// <param name="ct">The test's cancellation token.</param>
    public async Task<(HttpResponseMessage Response, JsonDocument Body)> RpcAsync(
        string body, string? permissions, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, McpRoute)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        if (permissions is not null)
        {
            request.Headers.Add(PermissionsHeader, permissions);
        }

        var response = await Client.SendAsync(request, ct);

        return (response, JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)));
    }

    /// <summary>Posts one request to <c>booking.book</c>'s HTTP trigger.</summary>
    /// <param name="body">The request body.</param>
    /// <param name="permissions">The permissions the caller holds, or <c>null</c>.</param>
    /// <param name="ct">The test's cancellation token.</param>
    public async Task<(HttpResponseMessage Response, JsonDocument Body)> PostBookingAsync(
        string body, string? permissions, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BookingRoute)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        if (permissions is not null)
        {
            request.Headers.Add(PermissionsHeader, permissions);
        }

        var response = await Client.SendAsync(request, ct);

        return (response, JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)));
    }

    /// <summary>This assembly's manifest, as the generated registration hands it over.</summary>
    public static string Manifest => FlowXManifest.Json;

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _host.StopAsync(TestContext.Current.CancellationToken);
        _host.Dispose();
    }

    /// <summary>
    /// Turns a test header into validated claims.
    /// </summary>
    /// <remarks>
    /// A stand-in for a token issuer and nothing more. The permissions are emitted as one
    /// space-delimited <c>scope</c> claim, which is the shape an OAuth 2.0 access token
    /// carries and the shape <c>StepAuthorization.Grants</c> splits — so the authorisation
    /// path under test is the real one rather than a convenient one.
    /// </remarks>
    internal sealed class HeaderScheme : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        /// <summary>The scheme's name.</summary>
        public const string Name = "Test";

        /// <summary>Creates the handler.</summary>
        /// <param name="options">The scheme's options.</param>
        /// <param name="logger">The host's logger factory.</param>
        /// <param name="encoder">The host's URL encoder.</param>
        public HeaderScheme(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            Microsoft.Extensions.Logging.ILoggerFactory logger,
            System.Text.Encodings.Web.UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(PermissionsHeader, out var granted))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity([new Claim("scope", granted.ToString())], Name);

            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Name)));
        }
    }
}
