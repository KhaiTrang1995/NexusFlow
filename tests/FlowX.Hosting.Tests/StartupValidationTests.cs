using FlowX.Hosting;
using FlowX.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace FlowX.Hosting.Tests;

/// <summary>
/// Startup validation. OWASP A05: a misconfigured node must be a <em>dead</em> node,
/// never a quietly insecure one.
/// </summary>
/// <remarks>
/// The distinction that matters is <strong>when</strong> validation runs. Options
/// validated on first use fail on the first request after a deploy — which is a
/// production incident, at 3am, with traffic already routed to the new pod. Validated
/// at startup, the same mistake is a pod that never becomes ready and a rollout that
/// halts on its own.
/// </remarks>
public sealed class StartupValidationTests
{
    private static IHost BuildHost(Action<FlowXOptions> configure)
    {
        return new HostBuilder()
            .ConfigureServices(services => services.AddFlowX(configure))
            .Build();
    }

    [Fact]
    public async Task AValidConfigurationStarts()
    {
        using var host = BuildHost(options => options.ApplicationName = "Sample.App");

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RefusesToStartWithoutAnApplicationName()
    {
        using var host = BuildHost(options => options.ApplicationName = "   ");

        var error = await Should.ThrowAsync<OptionsValidationException>(
            async () => await host.StartAsync(TestContext.Current.CancellationToken));

        error.Message.Contains(nameof(FlowXOptions.ApplicationName), StringComparison.Ordinal)
            .ShouldBeTrue(
                "The message must name the setting. 'Configuration is invalid' sends an " +
                $"operator to read source code at the worst possible moment.\n{error.Message}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task RefusesToStartWithANonPositivePoolSize(int pooled)
    {
        using var host = BuildHost(options =>
        {
            options.ApplicationName = "Sample.App";
            options.MaxPooledContexts = pooled;
        });

        var error = await Should.ThrowAsync<OptionsValidationException>(
            async () => await host.StartAsync(TestContext.Current.CancellationToken));

        error.Message.ShouldContain(nameof(FlowXOptions.MaxPooledContexts));
    }

    [Fact]
    public async Task RefusesToStartWithANonPositiveDefaultDeadline()
    {
        using var host = BuildHost(options =>
        {
            options.ApplicationName = "Sample.App";
            options.DefaultDeadline = TimeSpan.Zero;
        });

        var error = await Should.ThrowAsync<OptionsValidationException>(
            async () => await host.StartAsync(TestContext.Current.CancellationToken));

        error.Message.ShouldContain(nameof(FlowXOptions.DefaultDeadline));
        // A zero deadline means every step starts already expired, which presents as
        // "the service mysteriously does nothing" rather than as a configuration error.
    }

    [Fact]
    public async Task RefusesToStartWithANegativeDrainTimeout()
    {
        using var host = BuildHost(options =>
        {
            options.ApplicationName = "Sample.App";
            options.ShutdownDrainTimeout = TimeSpan.FromSeconds(-1);
        });

        await Should.ThrowAsync<OptionsValidationException>(
            async () => await host.StartAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A ceiling that is present and not positive fails the pod rather than the customer.
    /// </summary>
    /// <remarks>
    /// Zero is the interesting one: it is the spelling somebody reaches for meaning "no limit",
    /// and it means the opposite — every item this node is ever offered is shed. Absent is the
    /// unbounded spelling, and <see cref="AnAbsentCeilingIsTheUnboundedDefault"/> is what says so.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task RefusesToStartWithANonPositiveAdmissionCeiling(int ceiling)
    {
        using var host = BuildHost(options =>
        {
            options.ApplicationName = "Sample.App";
            options.MaxInFlightAdmissions = ceiling;
        });

        var error = await Should.ThrowAsync<OptionsValidationException>(
            async () => await host.StartAsync(TestContext.Current.CancellationToken));

        error.Message.ShouldContain(nameof(FlowXOptions.MaxInFlightAdmissions));
    }

    /// <summary>
    /// The default is no ceiling, and the host that starts under it counts nothing.
    /// </summary>
    /// <remarks>
    /// <strong>The rule this option was written under.</strong> A hosting option added to a
    /// release must not change what a running deployment does, so the default has to be absence
    /// rather than a number somebody guessed for other people's hardware.
    /// </remarks>
    [Fact]
    public async Task AnAbsentCeilingIsTheUnboundedDefault()
    {
        using var host = BuildHost(options => options.ApplicationName = "Sample.App");

        await host.StartAsync(TestContext.Current.CancellationToken);

        var gate = host.Services.GetRequiredService<FlowHost>().AdmissionGate;

        gate.IsBounded.ShouldBeFalse();
        gate.Ceiling.ShouldBeNull();

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReportsEveryProblemAtOnceRatherThanTheFirst()
    {
        using var host = BuildHost(options =>
        {
            options.ApplicationName = string.Empty;
            options.MaxPooledContexts = 0;
            options.DefaultDeadline = TimeSpan.Zero;
        });

        var error = await Should.ThrowAsync<OptionsValidationException>(
            async () => await host.StartAsync(TestContext.Current.CancellationToken));

        error.Failures.Count().ShouldBeGreaterThanOrEqualTo(3,
            "Fixing configuration one error per deploy cycle is a miserable loop. " +
            "Report everything wrong in one pass.");
    }

    [Fact]
    public void RejectsANullServiceCollection()
        => Should.Throw<ArgumentNullException>(
            () => FlowXServiceCollectionExtensions.AddFlowX(null!, _ => { }));

    [Fact]
    public async Task CallingAddFlowXTwiceDoesNotDuplicateRegistrations()
    {
        var services = new ServiceCollection();
        services.AddFlowX(o => o.ApplicationName = "Sample.App");
        services.AddFlowX(o => o.ApplicationName = "Sample.App");

        using var provider = services.BuildServiceProvider();

        provider.GetServices<FlowHost>().Count().ShouldBe(1,
            "A library registered twice — once by the app, once by a plugin — must not " +
            "produce two engines with two independent context pools.");

        await Task.CompletedTask;
    }

    [Fact]
    public async Task AddFlowXRegistersTheHealthProbeAndNotJustItsType()
    {
        // Registering the type is not registering the check. AddFlowX did the first and
        // not the second: MapHealthChecks threw at startup, and an application that also
        // called AddHealthChecks got a probe that never ran. The sample found it on its
        // first run; this keeps it found.
        var services = new ServiceCollection();
        services.AddLogging();   // HealthCheckService takes an ILogger; a real host has one.
        services.AddFlowX(o => o.ApplicationName = "Sample.App");

        using var provider = services.BuildServiceProvider();

        var registrations = provider
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations;

        registrations.Count.ShouldBe(1);
        registrations.Single().Name.ShouldBe("flowx");
        registrations.Single().Tags.ShouldContain("ready");

        var report = await provider
            .GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(TestContext.Current.CancellationToken);

        // Unhealthy because MarkReady runs from the hosted service, which has not
        // started here. That it reports at all is the point.
        report.Entries.ShouldContainKey("flowx");
    }

    /// <summary>
    /// An isolation level this runtime does not implement is refused at startup, and the
    /// refusal says why.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The refusal has to be reachable, and until this test nothing checked that it
    /// was.</strong> The whole point of naming a level in <c>TenantIsolation</c> and not
    /// building it is that a deployment configuring it gets a pod that never becomes ready
    /// instead of one silently serving less separation than it asked for — and a validator
    /// branch nobody exercises is exactly how that guarantee is lost to a refactor.
    /// </para>
    /// <para>
    /// The message is asserted as well as the failure. <c>Database</c> is refused for a reason
    /// an operator can act on — it is a deployment per tenant, so the pod serving one declares
    /// <c>None</c> against that tenant's own store — and a refusal that only said "not
    /// supported" would send them looking for a version of FlowX that supports it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RefusesToStartWithAnIsolationLevelItDoesNotImplement()
    {
        using var host = BuildHost(options =>
        {
            options.ApplicationName = "Sample.App";
            options.TenantIsolation = TenantIsolation.Database;
        });

        var error = await Should.ThrowAsync<OptionsValidationException>(
            async () => await host.StartAsync(TestContext.Current.CancellationToken));

        error.Message.Contains(nameof(TenantIsolation.Database), StringComparison.Ordinal)
            .ShouldBeTrue($"the message must name the level that is refused.\n{error.Message}");

        error.Message.Contains("dedicated deployment", StringComparison.Ordinal)
            .ShouldBeTrue(
                "and say why, because the repair is a topology rather than a newer build.\n" +
                error.Message);
    }

    /// <summary>
    /// The levels this runtime does implement start, including the one that needs a store to
    /// cooperate.
    /// </summary>
    /// <remarks>
    /// The other half of the assertion above, and it is not redundant: a validator that refused
    /// every level would satisfy the first test and break every multi-tenant deployment. Schema
    /// starts here with no journal registered at all, which is the case the constructor's own
    /// check deliberately lets through — there is no store to be weaker than the declaration.
    /// </remarks>
    [Theory]
    [InlineData(TenantIsolation.None)]
    [InlineData(TenantIsolation.Row)]
    [InlineData(TenantIsolation.Schema)]
    public async Task AcceptsAnIsolationLevelItImplements(TenantIsolation isolation)
    {
        using var host = BuildHost(options =>
        {
            options.ApplicationName = "Sample.App";
            options.TenantIsolation = isolation;
        });

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A per-tenant bound on a deployment that resolves no tenant is refused at startup.
    /// </summary>
    /// <remarks>
    /// It has no key to spend under, so nothing would be applied and nothing would say so — the
    /// "declared and inert" shape arriving through the very setting meant to remove it. An
    /// operator who configured a noisy-neighbour bound and got none is the failure this class
    /// exists to convert into a pod that never becomes ready.
    /// </remarks>
    [Fact]
    public async Task RefusesToStartWithAPerTenantBoundAndNoTenants()
    {
        using var host = BuildHost(options =>
        {
            options.ApplicationName = "Sample.App";
            options.Fairness.MaxConcurrency = 4;
        });

        var error = await Should.ThrowAsync<OptionsValidationException>(
            async () => await host.StartAsync(TestContext.Current.CancellationToken));

        error.Message.Contains(nameof(FlowXOptions.TenantIsolation), StringComparison.Ordinal)
            .ShouldBeTrue($"the message must name the setting that is wrong.\n{error.Message}");
    }

    /// <summary>A tenant weight of zero is refused rather than honoured.</summary>
    /// <remarks>
    /// It would express "never schedule this tenant", which is a suspension —
    /// <c>docs/16 §7</c>'s lifecycle state, whose contract is that in-flight durable flows still
    /// complete — and it would be delivered as parked instances that are silently never woken.
    /// That is the starvation the setting exists to prevent, arriving through the setting.
    /// </remarks>
    [Fact]
    public async Task RefusesToStartWithATenantWeightOfZero()
    {
        using var host = BuildHost(options =>
        {
            options.ApplicationName = "Sample.App";
            options.TenantIsolation = TenantIsolation.Row;
            options.Fairness.PerTenantScanShare = 4;
            options.Fairness.Weights["acme"] = 0;
        });

        await Should.ThrowAsync<OptionsValidationException>(
            async () => await host.StartAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A per-tenant rate limit with no shared limiter registered is refused at startup.
    /// </summary>
    /// <remarks>
    /// <strong>Not an options failure, because the options cannot see a registration.</strong>
    /// A budget each node kept for itself would be the declared limit multiplied by the replica
    /// count (ADR-0040), so the alternative to failing here is a commercial promise the
    /// deployment silently cannot keep. The bulkhead is deliberately not covered by this check:
    /// it is per node and needs no store.
    /// </remarks>
    [Fact]
    public async Task RefusesToStartWithAPerTenantRateLimitAndNoSharedLimiter()
    {
        using var host = BuildHost(options =>
        {
            options.ApplicationName = "Sample.App";
            options.TenantIsolation = TenantIsolation.Row;
            options.Fairness.PermitsPerWindow = 100;
        });

        var error = await Should.ThrowAsync<InvalidOperationException>(
            async () => await host.StartAsync(TestContext.Current.CancellationToken));

        error.Message.Contains("IRateLimiterStore", StringComparison.Ordinal).ShouldBeTrue(
            $"the message must name the registration that is missing.\n{error.Message}");
    }

    /// <summary>
    /// A journal write budget the block size does not divide is refused rather than rounded
    /// down.
    /// </summary>
    /// <remarks>
    /// The shared bucket is denominated in blocks of credit, so 100 rows per window drawn 32 at
    /// a time would enforce 96 while the configuration said 100. A limit that does not mean what
    /// it says is the one failure this mechanism has to be free of to be worth having over the
    /// per-process budget ADR-0040 refuses, so the arithmetic is made exact at startup rather
    /// than approximate at run time.
    /// </remarks>
    [Fact]
    public async Task RefusesToStartWithAWriteBudgetItsBlockSizeDoesNotDivide()
    {
        using var host = BuildHost(options =>
        {
            options.ApplicationName = "Sample.App";
            options.TenantIsolation = TenantIsolation.Row;
            options.Fairness.JournalWritesPerWindow = 100;
            options.Fairness.JournalWriteBlock = 32;
        });

        var error = await Should.ThrowAsync<OptionsValidationException>(
            async () => await host.StartAsync(TestContext.Current.CancellationToken));

        error.Message.Contains("96", StringComparison.Ordinal).ShouldBeTrue(
            $"the message must name what would actually have been enforced.\n{error.Message}");
    }

    /// <summary>A journal write budget with no shared limiter is refused at startup.</summary>
    /// <remarks>
    /// The same check as the rate limit's and for a sharper reason: a write budget each node
    /// kept for itself would multiply by the replica count the very thing it exists to protect —
    /// the shared durable store — so the mechanism would be at its least effective exactly where
    /// it is most needed.
    /// </remarks>
    [Fact]
    public async Task RefusesToStartWithAWriteBudgetAndNoSharedLimiter()
    {
        using var host = BuildHost(options =>
        {
            options.ApplicationName = "Sample.App";
            options.TenantIsolation = TenantIsolation.Row;
            options.Fairness.JournalWritesPerWindow = 64;
            options.Fairness.JournalWriteBlock = 32;
        });

        var error = await Should.ThrowAsync<InvalidOperationException>(
            async () => await host.StartAsync(TestContext.Current.CancellationToken));

        error.Message.Contains("IRateLimiterStore", StringComparison.Ordinal).ShouldBeTrue(
            $"the message must name the registration that is missing.\n{error.Message}");
    }

    /// <summary>A bulkhead alone needs no store, and starts.</summary>
    /// <remarks>
    /// The other half of the check above. Concurrency is a property of one process's threads and
    /// sockets, so there is nothing shared to consult and requiring a limiter for it would make
    /// the cheapest of the three bounds the most expensive to adopt.
    /// </remarks>
    [Fact]
    public async Task ABulkheadAloneNeedsNoSharedLimiter()
    {
        using var host = BuildHost(options =>
        {
            options.ApplicationName = "Sample.App";
            options.TenantIsolation = TenantIsolation.Row;
            options.Fairness.MaxConcurrency = 4;
        });

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A registered flow declaring a <c>Quota</c> with no <c>IQuotaStore</c> refuses to start.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The step policy's version of the check above, and the earliest moment anything
    /// can make it.</strong> A DI registration is invisible to the compiler, so there is no
    /// build-time shape available for "no store is registered" — FLOWX1056 is the build-time
    /// rule <c>Validate</c> gets, and it is about the contract rather than the container. What
    /// is available is the plans this node actually carries, read after the composition root has
    /// finished registering them.
    /// </para>
    /// <para>
    /// The alternative is a node that becomes ready and refuses every call to that step with
    /// <c>policy.quota_unavailable</c> — which is loud, and still worse than a rollout that
    /// halts, because the traffic is already routed by then.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RefusesToStartWhenAFlowDeclaresAQuotaAndNoStoreIsRegistered()
    {
        using var host = BuildHost(options => options.ApplicationName = "Sample.App");

        host.Services.GetRequiredService<FlowCatalog>().Add(QuotaPlan(), new SucceedingDispatcher());

        var error = await Should.ThrowAsync<InvalidOperationException>(
            async () => await host.StartAsync(TestContext.Current.CancellationToken));

        error.Message.Contains("IQuotaStore", StringComparison.Ordinal).ShouldBeTrue(
            $"the message must name the registration that is missing.\n{error.Message}");

        error.Message.Contains("order.metered", StringComparison.Ordinal).ShouldBeTrue(
            $"and the flow, so an author knows which declaration to look at.\n{error.Message}");
    }

    /// <summary>The same flow starts once a store is registered.</summary>
    /// <remarks>
    /// The other half, and the one that keeps the check from being a refusal nobody can satisfy.
    /// It also pins the resolution: the store is found through the container, so a deployment
    /// that registers one anywhere — <c>AddFlowXPostgresPolicyStores()</c>, its own — is served.
    /// </remarks>
    [Fact]
    public async Task StartsWhenTheQuotaHasAStore()
    {
        using var host = new HostBuilder()
            .ConfigureServices(services => services
                .AddFlowX(options => options.ApplicationName = "Sample.App")
                .AddSingleton<IQuotaStore>(new AlwaysAdmittingQuota()))
            .Build();

        host.Services.GetRequiredService<FlowCatalog>().Add(QuotaPlan(), new SucceedingDispatcher());

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>A flow that declares no quota starts with no store, which is every other flow.</summary>
    [Fact]
    public async Task AFlowWithNoQuotaNeedsNoStore()
    {
        using var host = BuildHost(options => options.ApplicationName = "Sample.App");

        host.Services.GetRequiredService<FlowCatalog>().Add(
            ExecutionPlan.Create(
                FlowDescriptor.Create(
                    "order.plain", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
                StepGraph.Create([StepNode.ForCapability(0, Metered)])),
            new SucceedingDispatcher());

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    private static readonly CapabilityDescriptor Metered =
        CapabilityDescriptor.Create("billing.meter", "1.0.0", isIdempotent: true);

    private static ExecutionPlan QuotaPlan() => ExecutionPlan.Create(
        FlowDescriptor.Create("order.metered", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
        StepGraph.Create([
            StepNode.ForCapability(
                0,
                Metered,
                policies: PolicyChain.ForStep(
                    PolicySet.Named("plan").Quota(budget: 100, TimeSpan.FromDays(1)),
                    Metered)),
        ]));

    private sealed class AlwaysAdmittingQuota : IQuotaStore
    {
        public ValueTask<Result<QuotaVerdict>> TryConsumeAsync(
            string key, int budget, TimeSpan period, CancellationToken cancellationToken) =>
            new(Result.Ok(new QuotaVerdict(true, budget - 1, TimeSpan.Zero)));
    }

    private sealed class SucceedingDispatcher : IStepDispatcher
    {
        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct) =>
            ValueTask.FromResult(StepOutcome.Success);

        public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct) =>
            ValueTask.FromResult(StepOutcome.Success);

        public bool Evaluate(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException("This double runs plans with no branch step.");

        public int Select(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException("This double runs plans with no switch step.");

        public IterationSource BeginIteration(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to begin.");

        public FlowContext EnterIteration(
            int stepIndex, in IterationSource source, int iteration, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to enter.");
    }

    [Fact]
    public void CallingAddFlowXTwiceRegistersTheHealthProbeOnce()
    {
        var services = new ServiceCollection();
        services.AddFlowX(o => o.ApplicationName = "Sample.App");
        services.AddFlowX(o => o.ApplicationName = "Sample.App");

        using var provider = services.BuildServiceProvider();

        provider
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations.Count.ShouldBe(1,
                "A duplicated probe reports the same condition twice and doubles its cost.");
    }
}
