using FlowX.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace FlowX.Hosting;

/// <summary>Registers the FlowX runtime with a dependency-injection container.</summary>
public static class FlowXServiceCollectionExtensions
{
    /// <summary>Adds the FlowX runtime, validating its configuration at startup.</summary>
    /// <param name="services">The container.</param>
    /// <param name="configure">Optional configuration callback.</param>
    /// <remarks>
    /// <para>
    /// Idempotent. A library registered twice — once by the application, once by a
    /// plugin that also depends on it — must not produce two engines with two
    /// independent context pools, which would silently double the process's memory
    /// floor and make pooling metrics meaningless.
    /// </para>
    /// <para>
    /// <c>ValidateOnStart</c> is the load-bearing call. Without it the same validation
    /// runs lazily, on the first request after a deploy, when traffic is already
    /// routed to the new pod (OWASP A05).
    /// </para>
    /// </remarks>
    public static IServiceCollection AddFlowX(
        this IServiceCollection services,
        Action<FlowXOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = services.AddOptions<FlowXOptions>();

        if (configure is not null)
        {
            builder.Configure(configure);
        }

        // TryAdd throughout: see the idempotency note above.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<FlowXOptions>, FlowXOptionsValidator>());

        builder.ValidateOnStart();

        services.TryAddSingleton<IClock>(SystemClock.Instance);

        services.TryAddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<FlowXOptions>>().Value;
            return new FlowEngine(provider.GetRequiredService<IClock>(), options.MaxPooledContexts);
        });

        services.TryAddSingleton(provider => new FlowHost(
            provider.GetRequiredService<FlowEngine>(),
            provider.GetRequiredService<IOptions<FlowXOptions>>().Value));

        services.TryAddSingleton<FlowXHealthCheck>();

        // Registering the type is not the same as registering the check. Before this,
        // FlowXHealthCheck was resolvable and never ran: an application that called
        // MapHealthChecks got an exception for the missing service, and one that also
        // called AddHealthChecks got a probe that reported healthy while draining. The
        // sample found it on its first startup, which is what samples are for.
        services.AddHealthChecks();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigureOptions<HealthCheckServiceOptions>, FlowXHealthCheckRegistration>());

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, FlowXLifecycleService>());

        return services;
    }
}

/// <summary>
/// Adds <see cref="FlowXHealthCheck"/> to the health-check set.
/// </summary>
/// <remarks>
/// Done through <see cref="IConfigureOptions{TOptions}"/> rather than
/// <c>AddHealthChecks().AddCheck&lt;T&gt;()</c> so that <c>TryAddEnumerable</c> can
/// deduplicate it by implementation type. Calling <c>AddCheck</c> twice registers the
/// probe twice, and <c>AddFlowX</c> promises to be idempotent.
/// </remarks>
internal sealed class FlowXHealthCheckRegistration : IConfigureOptions<HealthCheckServiceOptions>
{
    /// <summary>The probe's name, as it appears in the health report.</summary>
    internal const string Name = "flowx";

    /// <summary>
    /// Tagged <c>ready</c>, not <c>live</c>. A liveness probe wired to this would restart
    /// a pod that is draining correctly — see <see cref="FlowXHealthCheck"/>.
    /// </summary>
    internal const string ReadyTag = "ready";

    public void Configure(HealthCheckServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Registrations.Add(new HealthCheckRegistration(
            Name,
            provider => provider.GetRequiredService<FlowXHealthCheck>(),
            failureStatus: HealthStatus.Unhealthy,
            tags: [ReadyTag]));
    }
}

/// <summary>
/// Marks the host ready on start, and drains in-flight flows on stop.
/// </summary>
/// <remarks>
/// Registered as an <see cref="IHostedService"/> so the drain happens inside the
/// host's own shutdown sequence, before the process exits. Doing it from a
/// <c>ProcessExit</c> handler instead — the other obvious place — gives no
/// cancellation token, no ordering guarantee against other services, and a hard
/// two-second limit on some runtimes.
/// </remarks>
internal sealed class FlowXLifecycleService : IHostedService
{
    private readonly FlowHost _host;

    public FlowXLifecycleService(FlowHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _host.MarkReady();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // The return value is deliberately not thrown on. A drain that ran out of
        // budget is a fact to report, not a reason to fail shutdown — failing here
        // would leave the process in a worse state than the abandoned work does.
        await _host.DrainAsync(cancellationToken).ConfigureAwait(false);
    }
}
