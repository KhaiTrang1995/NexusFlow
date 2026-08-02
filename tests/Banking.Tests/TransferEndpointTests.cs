using System.Net;
using System.Security.Claims;
using System.Net.Http.Json;
using System.Text.Json;
using Banking;
using FlowX;
using FlowX.Conformance.InMemory;
using FlowX.Generated;
using FlowX.Hosting;
using FlowX.Http;
using FlowX.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Banking.Tests;

/// <summary>
/// The reference application end to end: HTTP request, durable flow, journal.
/// </summary>
/// <remarks>
/// <para>
/// Asserted on the wire — status codes, media types and JSON bodies — because those are the
/// contract a caller depends on and none of them is observable by invoking the flow
/// directly. The route is registered by the generated <c>MapFlowX()</c>, so what is under
/// test is the endpoint the <c>[HttpTrigger]</c> produced rather than one this file wrote.
/// </para>
/// <para>
/// <strong>This is also the <c>trigger → flow → journal</c> row of
/// <c>docs/23-Testing-Strategy.md §3</c>.</strong> That document says the path "is no longer
/// blocked — nothing walks it". <see cref="ATransferWalksFromTheRequestToTheJournal"/>
/// walks it: a POST arrives, <c>FlowHost</c> takes a lease, the engine journals every step
/// boundary, and the assertions read the rows back off the store the endpoint wrote to.
/// </para>
/// </remarks>
public sealed class TransferEndpointTests
{
    private const string Route = "/api/v1/transfers";
    private const string Debtor = "GB33BUKB20201555555555";
    private const string Creditor = "DE89370400440532013000";
    private const string Unknown = "XX00NOSUCHACCOUNT0000000";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ASettledTransferReturnsTheFlowsDeclaredOutput()
    {
        await using var app = await StartAsync();

        var response = await app.PostAsync("wire-1", Transfer(120m, TransferChannel.Sepa));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");

        var body = await ReadAsync<TransferResult>(response);

        // Derived from what the steps produced, not invented by the endpoint.
        body.TransferId.ShouldBe("wire-1");
        body.DebitEntryId.ShouldBe("wire-1:ledger.post_debit");
        body.CreditEntryId.ShouldBe("wire-1:ledger.post_credit");
        body.Amount.ShouldBe(120m);
    }

    [Fact]
    public async Task TheWireContractIsCamelCase()
    {
        await using var app = await StartAsync();

        var response = await app.PostAsync("wire-2", Transfer(1m, TransferChannel.Book));
        var json = await response.Content.ReadAsStringAsync(Cancellation);

        // Pinned because the failure is silent in the other direction: a client sending
        // "amount" against a PascalCase contract gets a zero, not an error.
        json.ShouldContain("\"transferId\"");
        json.ShouldContain("\"debitEntryId\"");
        json.ShouldContain("\"creditEntryId\"");
    }

    /// <summary>
    /// The <c>.Fail(...)</c> arm arrives at the caller as a <c>409</c>.
    /// </summary>
    /// <remarks>
    /// No capability and no part of the flow names a status code. The error's
    /// <c>ErrorCategory.Conflict</c> is the contract, and the mapping from category to status
    /// lives in one place in <c>FlowX.Http</c>.
    /// </remarks>
    [Fact]
    public async Task AShortBalanceMapsToAConflictProblemDetails()
    {
        await using var app = await StartAsync();

        var response = await app.PostAsync("wire-3", Transfer(5_000m, TransferChannel.Book));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancellation));

        problem.RootElement.GetProperty("code").GetString().ShouldBe("transfer.insufficient_funds");
        problem.RootElement.GetProperty("status").GetInt32().ShouldBe(409);
    }

    [Fact]
    public async Task AnUnknownDebtorMapsToNotFound()
    {
        await using var app = await StartAsync();

        var response = await app.PostAsync(
            "wire-4", new ExecuteTransfer(Unknown, Creditor, 10m, "EUR", TransferChannel.Book));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancellation));

        problem.RootElement.GetProperty("code").GetString().ShouldBe("ledger.account_unknown");

        // The account the caller asked about is not echoed. An endpoint that named it would
        // be an account-enumeration oracle for anyone who can reach the route.
        (await response.Content.ReadAsStringAsync(Cancellation)).ShouldNotContain(Unknown);
    }

    [Fact]
    public async Task AZeroAmountMapsToBadRequest()
    {
        await using var app = await StartAsync();

        var response = await app.PostAsync("wire-5", Transfer(0m, TransferChannel.Book));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancellation));

        problem.RootElement.GetProperty("code").GetString().ShouldBe("transfer.invalid_amount");
    }

    [Fact]
    public async Task AMissingIdempotencyKeyIsRefused()
    {
        await using var app = await StartAsync();

        var response = await app.Client.PostAsync(
            Route,
            JsonContent.Create(Transfer(10m, TransferChannel.Book)),
            Cancellation);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancellation));

        problem.RootElement.GetProperty("code").GetString().ShouldBe("http.idempotency_key_required");
    }

    [Fact]
    public async Task TheHealthProbeReportsReady()
    {
        await using var app = await StartAsync();

        var response = await app.Client.GetAsync("/health", Cancellation);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Cancellation)).ShouldBe("Healthy");
    }

    /// <summary>
    /// One POST, and the journal holds the whole transfer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The conformance row of the testing pyramid, walked: <c>MapFlowX</c>'s handler resolves
    /// <c>FlowHost</c>, the host acquires a lease and opens an instance, the engine commits a
    /// row per step boundary, and the <c>Emit</c> step's commit stages the event. Nothing in
    /// this test constructs any of that — it posts JSON and reads a store.
    /// </para>
    /// <para>
    /// The stores are the reference implementations rather than PostgreSQL, so this asserts
    /// the path and not the adapter; <c>tests/FlowX.Postgres.Tests</c> holds the adapter to
    /// the same conformance suite.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ATransferWalksFromTheRequestToTheJournal()
    {
        await using var app = await StartAsync();

        var response = await app.PostAsync("wire-6", Transfer(120m, TransferChannel.Swift));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var instance = app.Journal.Instances.ShouldHaveSingleItem();

        instance.FlowId.ShouldBe("transfer.execute");
        instance.State.ShouldBe(FlowInstanceState.Completed);

        var frontier = await app.Journal.ReadResumeFrontierAsync(instance.InstanceId, Cancellation);

        frontier.Value.Committed.Select(s => s.CapabilityId).ShouldBe(
            [
                "transfer.validate",
                "compliance.screen_sanctions",
                "correspondent.resolve",
                "ledger.post_debit",
                "ledger.post_credit",
                "settlement.record",
                "transfer.completed",
            ]);

        var outbox = await app.Journal.ReadOutboxAsync(instance.InstanceId, Cancellation);
        var staged = outbox.Value.ShouldHaveSingleItem();

        staged.Type.ShouldBe("transfer.completed");

        // The account numbers the caller sent are in neither the response nor the journal.
        var everything = await response.Content.ReadAsStringAsync(Cancellation) + staged.PayloadJson;

        everything.ShouldNotContain(Debtor, Case.Sensitive);
        everything.ShouldNotContain(Creditor, Case.Sensitive);
    }

    /// <summary>
    /// A durable flow on a host with no journal is refused, and says which.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reason this sample's <c>Program.cs</c> refuses to start without a connection
    /// string. The platform's own answer is a refusal per request, which is correct and is a
    /// poor way to discover that a deployment forgot its database — so the sample fails at
    /// start-up instead, and this is what it would otherwise do.
    /// </para>
    /// <para>
    /// <strong>A <c>500</c>, not a <c>503</c>.</strong>
    /// <c>FlowErrors.DurabilityNotConfigured</c> carries <c>ErrorCategory.Internal</c>, and
    /// the category is what the status is mapped from. That is arguably the wrong category —
    /// a missing registration is a misconfiguration a load balancer should route around, and
    /// <c>Unavailable</c> is the category that says so — but it is what the runtime declares,
    /// and a sample asserting <c>503</c> here would be documenting a behaviour that does not
    /// exist.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task WithoutAJournalTheEndpointRefusesEveryTransfer()
    {
        await using var app = await StartAsync(withJournal: false);

        var response = await app.PostAsync("wire-7", Transfer(10m, TransferChannel.Book));

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);

        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancellation));

        problem.RootElement.GetProperty("code").GetString()
            .ShouldBe("flow.durability_not_configured");
    }

    private static ExecuteTransfer Transfer(decimal amount, TransferChannel channel) =>
        new(Debtor, Creditor, amount, "EUR", channel);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync(Cancellation);

        return JsonSerializer.Deserialize<T>(json, BankingJsonContext.Default.Options)
            ?? throw new InvalidOperationException("The response body was null: " + json);
    }

    /// <summary>
    /// Composes the same graph the sample's <c>Program.cs</c> does, with the journal in
    /// memory instead of in PostgreSQL.
    /// </summary>
    /// <summary>
    /// The caller every request in this file is made as.
    /// </summary>
    /// <remarks>
    /// The four permissions <c>ExecuteTransferFlow</c>'s capabilities name between them, and
    /// no more — so a capability added with a fifth fails here naming the grant it needs,
    /// rather than being waved through by a caller who holds everything.
    /// </remarks>
    private static ClaimsPrincipal Caller { get; } = TestPrincipal.Holding(
        "compliance:screen",
        "correspondent:read",
        "ledger:post",
        "settlement:write");

    private static async Task<Application> StartAsync(bool withJournal = true)
    {
        var journal = new InMemoryFlowJournal();

        var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddFlowX(options => options.ApplicationName = "Banking");

                    if (withJournal)
                    {
                        // Both halves or neither: AddFlowX builds a FlowDurability only when
                        // it can resolve a journal and a lease store together.
                        services.AddSingleton<IFlowJournal>(journal);
                        services.AddSingleton<ILeaseStore>(new InMemoryLeaseStore());
                    }

                    // The first step declares a RateLimit, and a step declaring one with no
                    // IRateLimiterStore registered is refused rather than admitted — so every
                    // test in this file would answer 503 policy.ratelimit_unavailable without
                    // this line, which is exactly the loudness ADR-0040 §2.2 asks for. The
                    // budget is generous because these are tests about an endpoint, not about
                    // admission; TransferPolicyTests is where a narrow one bites.
                    services.AddSingleton<IRateLimiterStore>(new FixedBudgetLimiter(int.MaxValue));

                    services.AddSingleton<ILedger, InMemoryLedger>();
                    services.AddSingleton<ISanctionsScreening, InMemorySanctionsScreening>();
                    services.AddSingleton<ICorrespondentDirectory, InMemoryCorrespondentDirectory>();
                    services.AddSingleton<ISettlementRegister, InMemorySettlementRegister>();

                    services.AddSingleton<ValidateTransfer>();
                    services.AddSingleton<ScreenSanctions>();
                    services.AddSingleton<ResolveCorrespondent>();
                    services.AddSingleton<PostDebit>();
                    services.AddSingleton<PostCredit>();
                    services.AddSingleton<ReverseDebit>();
                    services.AddSingleton<ReverseCredit>();
                    services.AddSingleton<RecordSettlement>();
                    services.AddSingleton<ExecuteTransferFlow.Dispatcher>();
                })
                .Configure(app =>
                {
                    // Stands where an authentication scheme stands, because the flow's
                    // capabilities declare four permissions and the engine decides each
                    // stance against HttpContext.User before the step is dispatched. Without
                    // it every request here is anonymous, every assertion below reads 403,
                    // and none of them is about what it says it is about.
                    //
                    // A middleware rather than a scheme: this file is testing the endpoint
                    // the [HttpTrigger] generated, not how a token becomes a principal, and
                    // samples/banking has no authentication of its own to mirror.
                    // samples/ecommerce is where the token half is demonstrated.
                    app.Use(async (context, next) =>
                    {
                        context.User = Caller;
                        await next(context).ConfigureAwait(false);
                    });

                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapHealthChecks("/health");

                        // The generated registration, exactly as Program.cs calls it. This
                        // file names no route, no method and no contract.
                        endpoints.MapFlowX();
                    });
                }))
            .StartAsync(Cancellation);

        return new Application(host, journal);
    }

    /// <summary>A started server, its client, and the journal behind it.</summary>
    private sealed class Application(IHost host, InMemoryFlowJournal journal) : IAsyncDisposable
    {
        public HttpClient Client { get; } = host.GetTestClient();

        public InMemoryFlowJournal Journal { get; } = journal;

        public Task<HttpResponseMessage> PostAsync(string key, ExecuteTransfer transfer)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, Route)
            {
                Content = JsonContent.Create(transfer, BankingJsonContext.Default.ExecuteTransfer),
            };

            request.Headers.Add(FlowXHeaders.IdempotencyKey, key);

            return Client.SendAsync(request, Cancellation);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await host.StopAsync(Cancellation).ConfigureAwait(false);
            host.Dispose();
        }
    }
}
