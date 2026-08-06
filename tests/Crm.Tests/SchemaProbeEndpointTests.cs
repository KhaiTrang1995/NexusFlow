using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlowX;
using FlowX.Generated;
using FlowX.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// The sample end to end: an HTTP request, the generated endpoint, a capability, a scoped
/// connection, and the policies of migration <c>0002</c> deciding what comes back.
/// </summary>
/// <remarks>
/// <para>
/// The route is registered by the generated <c>MapFlowX()</c>, so what is under test is the
/// endpoint the <c>[HttpTrigger]</c> produced rather than one this file wrote. Nothing here
/// names <c>crm.schema.probe</c> either.
/// </para>
/// <para>
/// <strong>The isolation is asserted over HTTP as well as over the tables</strong>, and the two
/// are not the same claim. <c>TenantIsolationTests</c> proves the database refuses a
/// cross-tenant read; this proves the tenant that reaches the database is the one the caller's
/// <c>tid</c> claim resolved to, over a request nobody can put a tenant into.
/// </para>
/// </remarks>
public sealed class SchemaProbeEndpointTests
{
    private const string Route = "/api/v1/crm/schema-probes";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A probe reports the schema this build writes against.</summary>
    [Fact]
    public async Task AProbeReportsTheSchemaVersionAndAnEmptyTenant()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        await using var app = await StartAsync(crm);

        var response = await app.ProbeAsync(CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");

        var report = await ReadAsync(response);

        report.SchemaVersion.ShouldBe(CrmMigrator.TargetVersion);
        report.Tables.Count.ShouldBe(22, "one row per CRM table.");
        report.Tables.ShouldAllBe(static table => table.Rows == 0);
    }

    /// <summary>
    /// One tenant's probe counts one tenant's rows, and the other tenant's counts none of them.
    /// </summary>
    /// <remarks>
    /// This is §10's "cross-tenant read is refused against a real PostgreSQL, in both
    /// directions", reached the way a caller reaches it. The two requests differ only in the
    /// bearer token; the body is <c>{}</c> both times, because <see cref="CrmSchemaProbe"/> has
    /// no members for a caller to name somebody else's tenant in.
    /// </remarks>
    [Fact]
    public async Task AProbeCountsTheCallersOwnRowsAndNobodyElses()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        await using var app = await StartAsync(crm);

        await crm.AccountAsync(CrmSchemaHarness.Northwind, Lifecycle.Customer, Cancellation);
        await crm.AccountAsync(CrmSchemaHarness.Northwind, Lifecycle.Prospect, Cancellation);
        await crm.LeadAsync(CrmSchemaHarness.Contoso, Cancellation);

        var northwind = await ReadAsync(await app.ProbeAsync(CrmTokens.Northwind));
        var contoso = await ReadAsync(await app.ProbeAsync(CrmTokens.Contoso));

        Rows(northwind, "account").ShouldBe(2);
        Rows(northwind, "lead").ShouldBe(
            0, "Northwind counted Contoso's lead. The tenant reaching the database is not the " +
               "one the token resolved to.");

        Rows(contoso, "lead").ShouldBe(1);
        Rows(contoso, "account").ShouldBe(
            0, "Contoso counted Northwind's accounts, which is the other direction.");
    }

    /// <summary>An anonymous caller is refused, because it has no tenant to be narrowed to.</summary>
    /// <remarks>
    /// <c>crm.schema.count</c> declares <c>Authorization.Authenticated</c>. The refusal happens
    /// at the step rather than at the route, which is the point of the stance being on the
    /// capability: the same refusal holds when the same capability is reached over a broker or
    /// by an agent.
    /// </remarks>
    [Fact]
    public async Task AnAnonymousProbeIsRefused()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        await using var app = await StartAsync(crm);

        var response = await app.ProbeAsync(token: null);

        response.IsSuccessStatusCode.ShouldBeFalse(
            "an anonymous caller was served a tenant's row counts.");

        response.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    private static long Rows(CrmSchemaReport report, string table) =>
        report.Tables.Single(row => row.Table == table).Rows;

    private static async Task<CrmSchemaReport> ReadAsync(HttpResponseMessage response)
    {
        var report = await response.Content.ReadFromJsonAsync<CrmSchemaReport>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web),
            Cancellation);

        return report!;
    }

    private static async Task<Application> StartAsync(CrmSchemaHarness crm)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddFlowX(options =>
                    {
                        options.ApplicationName = "Crm";
                        options.TenantIsolation = TenantIsolation.Row;
                        options.Tenants.Add(CrmTokens.NorthwindTenant);
                        options.Tenants.Add(CrmTokens.ContosoTenant);
                    });

                    services
                        .AddAuthentication(CrmTokenHandler.SchemeName)
                        .AddScheme<AuthenticationSchemeOptions, CrmTokenHandler>(
                            CrmTokenHandler.SchemeName, null);

                    // The reader rather than the data source, so nothing here is registered
                    // that the container would dispose out from under the harness that owns it.
                    services.AddSingleton(new CrmSchemaReader(crm.DataSource));
                    services.AddSingleton<CountCrmRows>();
                    services.AddSingleton<ProbeCrmSchemaFlow.Dispatcher>();
                })
                .Configure(app =>
                {
                    app.UseAuthentication();
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapFlowX());
                }))
            .StartAsync(Cancellation);

        return new Application(host);
    }

    private sealed class Application(IHost host) : IAsyncDisposable
    {
        public Task<HttpResponseMessage> ProbeAsync(string? token)
        {
            var client = host.GetTestClient();
            var request = new HttpRequestMessage(HttpMethod.Post, Route)
            {
                Content = JsonContent.Create(new CrmSchemaProbe()),
            };

            if (token is not null)
            {
                request.Headers.Add("Authorization", "Bearer " + token);
            }

            return client.SendAsync(request, Cancellation);
        }

        public async ValueTask DisposeAsync()
        {
            await host.StopAsync(Cancellation);
            host.Dispose();
        }
    }
}
