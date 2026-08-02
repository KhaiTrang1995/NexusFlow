using FlowX.Hosting;
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
