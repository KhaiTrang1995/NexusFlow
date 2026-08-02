using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Banking;
using FlowX;
using FlowX.Conformance.InMemory;
using FlowX.Generated;
using FlowX.Hosting;
using FlowX.Http;
using FlowX.Runtime;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Banking.Tests;

/// <summary>
/// The multi-tenancy this sample declares, over the wire, with the tenant arriving where a
/// deployment's tenants arrive: on a validated claim.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Composed as <c>Program.cs</c> composes it.</strong> The isolation level, the five
/// fairness bounds and the sample's own <c>BankTokenHandler</c> are the ones the application
/// registers — the only substitutions are the reference journal in place of PostgreSQL and a
/// limiter whose budget a test can exhaust. Asserting these against a host this file invented
/// would prove the runtime works and say nothing about whether the sample uses it.
/// </para>
/// <para>
/// <strong>Isolation and fairness are two guarantees and this file keeps them apart.</strong>
/// The first stops one bank reading another's rows; it does nothing whatever about one bank
/// consuming every slot on the node, which is what the second is for. A test that only showed
/// tenant ids landing in rows would leave the more common production failure undemonstrated.
/// </para>
/// </remarks>
public sealed class TransferTenancyTests
{
    private const string Route = "/api/v1/transfers";
    private const string Debtor = "GB33BUKB20201555555555";
    private const string Creditor = "DE89370400440532013000";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>
    /// A caller whose claims name no tenant is refused before the first step.
    /// </summary>
    /// <remarks>
    /// <c>TenantIsolation.Row</c> makes a tenant mandatory rather than defaulted, and the
    /// distinction is the whole point: a default tenant is the precise shape of a cross-tenant
    /// read, so an unnamed tenant has to be a refusal. The ledger assertion is what separates
    /// "refused" from "refused after moving the money".
    /// </remarks>
    [Fact]
    public async Task ACallerWhoseClaimsNameNoTenantIsRefusedBeforeAnyStepRuns()
    {
        await using var app = await StartAsync();

        using var response = await app.PostAsync("t-untenanted", Transfer(10m), token: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancellation));

        problem.RootElement.GetProperty("code").GetString().ShouldBe("tenant.required");

        // Nothing was opened, so nothing was journalled and no ledger entry exists to unwind.
        app.Journal.Instances.ShouldBeEmpty();
    }

    /// <summary>Two banks' transfers are journalled under their own tenants.</summary>
    /// <remarks>
    /// Neither token names a tenant anywhere a caller controls — <c>tid</c> is a claim the
    /// scheme minted, and <c>ClaimTenantResolver</c> reads claims and no header at all
    /// (ADR-0046). So the two rows differ because the two principals do, which is the property
    /// a header-derived tenant would not have.
    /// </remarks>
    [Fact]
    public async Task EachBanksTransferIsJournalledUnderItsOwnTenant()
    {
        await using var app = await StartAsync();

        using var frankfurt = await app.PostAsync("t-de-1", Transfer(20m), BankTokens.Frankfurt);
        using var london = await app.PostAsync("t-uk-1", Transfer(30m), BankTokens.London);

        frankfurt.StatusCode.ShouldBe(HttpStatusCode.OK);
        london.StatusCode.ShouldBe(HttpStatusCode.OK);

        app.Journal.Instances
            .Select(instance => instance.TenantId)
            .OrderBy(static tenant => tenant, StringComparer.Ordinal)
            .ShouldBe([BankTokens.FrankfurtTenant, BankTokens.LondonTenant]);
    }

    /// <summary>
    /// A bank past its fair share is refused, and the other bank is untouched by it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The second half is the assertion. A bound that refused every tenant once any tenant
    /// exhausted it would satisfy the first half and would be an outage rather than fairness —
    /// which is exactly the failure <c>docs/16 §4</c> describes as one tenant's burst becoming
    /// everyone's incident.
    /// </para>
    /// <para>
    /// This is not <c>Policies.Admission</c>'s rate limit. That one is declared on a step and
    /// counts principals; this one is declared on the deployment and counts tenants, and they
    /// are separate keys in the same store — which is why the budget here has to be small
    /// enough to bite before the step limit does.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ABankPastItsFairShareIsRefusedAndTheOtherBankIsNot()
    {
        // Two permits per key. Each tenant gets its own key, so Frankfurt's third transfer is
        // refused while London has spent none of its own.
        await using var app = await StartAsync(permitsPerKey: 2);

        using var first = await app.PostAsync("t-de-a", Transfer(1m), BankTokens.Frankfurt);
        using var second = await app.PostAsync("t-de-b", Transfer(1m), BankTokens.Frankfurt);
        using var third = await app.PostAsync("t-de-c", Transfer(1m), BankTokens.Frankfurt);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        // 503 rather than 429, and that is the category doing the mapping rather than a
        // transport deciding: a refusal a caller should retry is ErrorCategory.Unavailable, and
        // ErrorCategory is a closed set with no rate-limit member. The *code* is what tells an
        // operator which bound was hit, which is the fact worth pinning.
        third.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        using var problem = JsonDocument.Parse(await third.Content.ReadAsStringAsync(Cancellation));

        problem.RootElement.GetProperty("code").GetString().ShouldBe("tenant.rate_limited");

        using var neighbour = await app.PostAsync("t-uk-a", Transfer(1m), BankTokens.London);

        neighbour.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// The sample's tokens carry exactly the grants its capabilities name, and one of them
    /// deliberately does not.
    /// </summary>
    /// <remarks>
    /// The pinned set is what makes <c>BankTokens.FrankfurtClerk</c> a demonstration rather than
    /// an accident: it is authenticated, so it reaches the third step, and it holds no
    /// <c>ledger:post</c>, so the refusal happens where a permission model is supposed to bite.
    /// A token quietly granted every scope would turn the sample's most useful transcript into
    /// a second copy of its happy path.
    /// </remarks>
    [Fact]
    public async Task TheClerksTokenIsAuthenticatedAndCannotPostToTheLedger()
    {
        await using var app = await StartAsync();

        using var response = await app.PostAsync("t-clerk", Transfer(10m), BankTokens.FrankfurtClerk);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancellation));

        // Not `not_authenticated`: the clerk is signed in, and the deployment resolved its
        // tenant. What it lacks is one grant, and the refusal names both it and the capability.
        problem.RootElement.GetProperty("code").GetString().ShouldBe("authorization.permission_denied");
        problem.RootElement.GetProperty("permission").GetString().ShouldBe("ledger:post");
        problem.RootElement.GetProperty("capabilityId").GetString().ShouldBe("ledger.post_debit");

        // The instance was opened and it carries the clerk's tenant, so the refusal happened
        // inside the flow rather than in front of it.
        app.Journal.Instances.ShouldHaveSingleItem()
            .TenantId.ShouldBe(BankTokens.FrankfurtTenant);
    }

    /// <summary>
    /// Every permission this bank's capabilities name is one the operator tokens carry.
    /// </summary>
    /// <remarks>
    /// Read out of the manifest rather than listed here, so a capability added with a fifth
    /// permission fails this test naming the grant it needs — which is the failure mode that put
    /// the sample in this state to begin with, discovered by running it rather than by a build.
    /// </remarks>
    [Fact]
    public void BothOperatorsHoldEveryPermissionThisBankNames()
    {
        using var manifest = JsonDocument.Parse(FlowXManifest.Json);

        var required = manifest.RootElement.GetProperty("capabilities").EnumerateArray()
            .Select(capability => capability.GetProperty("authorization"))
            .Where(authorization => authorization.TryGetProperty("value", out _))
            .Select(authorization => authorization.GetProperty("value").GetString()!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        required.ShouldNotBeEmpty();

        foreach (var token in new[] { BankTokens.Frankfurt, BankTokens.London })
        {
            var granted = BankTokens.Claims[token]
                .Single(claim => claim.Type == "scope")
                .Value
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);

            foreach (var permission in required)
            {
                granted.ShouldContain(permission);
            }
        }
    }

    private static ExecuteTransfer Transfer(decimal amount) =>
        new(Debtor, Creditor, amount, "EUR", TransferChannel.Book);

    /// <summary>
    /// The sample's composition, with the reference journal in place of PostgreSQL.
    /// </summary>
    /// <remarks>
    /// <c>TenantIsolation.Row</c> and the five fairness bounds are the values
    /// <c>samples/banking/Program.cs</c> sets. Schema-per-tenant is the other level the sample
    /// offers and it cannot be exercised here, because a schema per tenant is a statement about
    /// a database and the reference journal is a dictionary —
    /// <c>tests/FlowX.Postgres.Tests/TenantSchemaIsolationTests</c> is where that level is held
    /// to the same conformance suite.
    /// </remarks>
    private static async Task<Application> StartAsync(int permitsPerKey = int.MaxValue)
    {
        var journal = new InMemoryFlowJournal();

        var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddFlowX(options =>
                    {
                        options.ApplicationName = "Banking";
                        options.TenantIsolation = TenantIsolation.Row;

                        options.Fairness.PermitsPerWindow = 50;
                        options.Fairness.Window = TimeSpan.FromSeconds(1);
                        options.Fairness.QuotaPerWindow = 5_000;
                        options.Fairness.QuotaWindow = TimeSpan.FromHours(1);
                        options.Fairness.MaxConcurrency = 16;
                        options.Fairness.PerTenantScanShare = 8;
                        options.Fairness.Weights[BankTokens.FrankfurtTenant] = 2;
                    });

                    // The sample's own scheme, because what is under test includes whether its
                    // tokens carry the tenant the isolation level demands.
                    services
                        .AddAuthentication(BankTokenHandler.SchemeName)
                        .AddScheme<AuthenticationSchemeOptions, BankTokenHandler>(
                            BankTokenHandler.SchemeName, null);

                    services.AddSingleton<IFlowJournal>(journal);
                    services.AddSingleton<ILeaseStore>(new InMemoryLeaseStore());
                    services.AddSingleton<IRateLimiterStore>(new TenantBudgetLimiter(permitsPerKey));

                    services.AddSingleton<ILedger, InMemoryLedger>();
                    services.AddSingleton<ISanctionsScreening, InMemorySanctionsScreening>();
                    services.AddSingleton<ICorrespondentDirectory, InMemoryCorrespondentDirectory>();
                    services.AddSingleton<ISettlementRegister, InMemorySettlementRegister>();

                    services.AddSingleton<InMemoryAuditTrail>();
                    services.AddSingleton<IAuditSink>(
                        sp => sp.GetRequiredService<InMemoryAuditTrail>());

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
                    app.UseAuthentication();
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapFlowX());
                }))
            .StartAsync(Cancellation);

        return new Application(host, journal);
    }

    /// <summary>
    /// A limiter that bounds a tenant's admissions and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The step's own rate limit shares this store and must not be what refuses.</strong>
    /// <c>Policies.Admission</c> declares <c>RateLimit(20, PT1S, Principal)</c>, and a limiter
    /// with one budget for every key it is handed refuses the second bank at
    /// <c>transfer.validate</c> — a green test about a fairness bound that was never reached.
    /// So the budget applies only to the tenant bucket, and every other key is unlimited.
    /// </para>
    /// <para>
    /// The prefix is spelled out because <c>PolicyKeys</c> is internal to the runtime. It is
    /// pinned by <c>PolicyKeyTests</c> there; if it changes, this double stops bounding anything
    /// and <see cref="ABankPastItsFairShareIsRefusedAndTheOtherBankIsNot"/> goes red on its
    /// first assertion rather than passing vacuously.
    /// </para>
    /// </remarks>
    private sealed class TenantBudgetLimiter(int budget) : IRateLimiterStore
    {
        private const string TenantRatePrefix = "flowx:tenant:rate";

        private readonly Dictionary<string, int> _taken = new(StringComparer.Ordinal);

        public ValueTask<Result<RateLimitVerdict>> TryAcquireAsync(
            string key,
            int permits,
            TimeSpan window,
            CancellationToken cancellationToken)
        {
            if (!key.StartsWith(TenantRatePrefix, StringComparison.Ordinal))
            {
                return new ValueTask<Result<RateLimitVerdict>>(
                    Result.Ok(new RateLimitVerdict(true, int.MaxValue, TimeSpan.Zero)));
            }

            _taken.TryGetValue(key, out var used);
            _taken[key] = ++used;

            var verdict = used <= budget
                ? new RateLimitVerdict(true, budget - used, TimeSpan.Zero)
                : new RateLimitVerdict(false, 0, window);

            return new ValueTask<Result<RateLimitVerdict>>(Result.Ok(verdict));
        }
    }

    private sealed class Application(IHost host, InMemoryFlowJournal journal) : IAsyncDisposable
    {
        public HttpClient Client { get; } = host.GetTestClient();

        public InMemoryFlowJournal Journal { get; } = journal;

        public Task<HttpResponseMessage> PostAsync(string key, ExecuteTransfer transfer, string? token)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, Route)
            {
                Content = JsonContent.Create(transfer, BankingJsonContext.Default.ExecuteTransfer),
            };

            request.Headers.Add(FlowXHeaders.IdempotencyKey, key);

            if (!string.IsNullOrEmpty(token))
            {
                request.Headers.Add("Authorization", "Bearer " + token);
            }

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
