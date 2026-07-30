using FlowX.Hosting;
using Microsoft.Extensions.DependencyInjection;
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
}
