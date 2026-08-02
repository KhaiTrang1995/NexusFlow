using FlowX.Generated;
using FlowX.Hosting;
using FlowX.Http;
using FlowX.Mcp;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AiAgent.Tests;

/// <summary>
/// The sample's composition root, with the test server in place of Kestrel.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This file composes what <c>Program.cs</c> composes, including the lifetimes.</strong>
/// The scoped registrations are not a testing convenience: <c>ReviewApplication</c> takes
/// <c>IAgentSampler</c>, which is the channel back to one call's client, so it and its dispatcher
/// must be per request. Registering them as singletons here would pass every test that does not
/// sample and fail the two that do, in a way that looks like a transport bug.
/// </para>
/// <para>
/// Only the two adapters are substituted, and only so a test can count what the flow did. The
/// stances, the flows, the manifest, the reviewer and the MCP options are the sample's own.
/// </para>
/// </remarks>
internal static class AiAgentHost
{
    /// <summary>The route the agent surface is served on.</summary>
    public const string McpRoute = "/mcp";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>Starts the sample.</summary>
    /// <param name="gateway">The payment provider, so a test can count reversals.</param>
    /// <param name="confirmation">
    /// The deployment's confirmation stance, defaulting to the sample's own
    /// <see cref="ConfirmationPolicy.Elicit"/>. A test that wants the platform default passes it.
    /// </param>
    public static async Task<IHost> StartAsync(
        CountingGateway? gateway = null,
        ConfirmationPolicy confirmation = ConfirmationPolicy.Elicit) =>
        await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddFlowX(options => options.ApplicationName = "AiAgent");

                    services
                        .AddAuthentication(DemoTokenHandler.SchemeName)
                        .AddScheme<AuthenticationSchemeOptions, DemoTokenHandler>(
                            DemoTokenHandler.SchemeName, null);

                    services.AddSingleton<ITicketDesk, InMemoryTicketDesk>();
                    services.AddSingleton<IPaymentGateway>(gateway ?? new CountingGateway());

                    services.AddSingleton<IApplicationReviewer>(
                        new ManifestReviewer(FlowX.Generated.FlowXManifest.Json));

                    services.AddSingleton<LoadTicket>();
                    services.AddSingleton<RefundPayment>();
                    services.AddSingleton<SearchTicketDesk>();
                    services.AddSingleton<IssueRefundFlow.Dispatcher>();
                    services.AddSingleton<SearchTicketsFlow.Dispatcher>();

                    services.AddScoped<ReviewApplication>();
                    services.AddScoped<ReviewApplicationFlow.Dispatcher>();

                    services.AddFlowXAgentTools();
                })
                .Configure(app =>
                {
                    app.UseAuthentication();
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapHealthChecks("/health");
                        endpoints.MapFlowX();
                        endpoints.MapFlowXMcp(McpRoute, new McpOptions
                        {
                            Confirmation = confirmation,

                            // Short, so the timeout arm is a test rather than a coffee break. The
                            // sample's own default is thirty seconds, which is a human's reading
                            // speed and not a machine's.
                            ClientRequestTimeout = TimeSpan.FromSeconds(2),
                        });
                    });
                }))
            .StartAsync(Cancellation);
}

/// <summary>A payment provider that always reverses, and counts.</summary>
/// <remarks>
/// The counter is the whole point. Every assertion in this project about a refusal checks it:
/// "the agent was told no" is a claim about a message, and "no money moved" is a claim about the
/// system, and only the second one is the property the sample exists to demonstrate.
/// </remarks>
internal sealed class CountingGateway : IPaymentGateway
{
    private int _refunds;

    /// <summary>How many reversals reached the provider.</summary>
    public int Refunds => Volatile.Read(ref _refunds);

    public ValueTask<string?> RefundAsync(
        string ticketId, decimal amount, string idempotencyKey, CancellationToken ct)
    {
        Interlocked.Increment(ref _refunds);

        return ValueTask.FromResult<string?>("rf_" + idempotencyKey);
    }
}
