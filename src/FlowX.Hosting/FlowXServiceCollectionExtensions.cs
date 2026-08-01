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
    /// <para>
    /// <strong>Durability is opted into by registering stores, not by a flag here.</strong>
    /// An application that registers an <see cref="IFlowJournal"/> and an
    /// <see cref="ILeaseStore"/> gets a host that runs <c>Durable</c> flows; one that also
    /// registers an <see cref="IRecoveryIndex"/> gets a node that picks up instances a dead
    /// node left behind. One that registers neither keeps the behaviour WP-52 landed — a
    /// <c>Durable</c> flow refused with <c>flow.durability_not_configured</c> — which is
    /// what an unconfigured host should say. A store implementing more than one of the three
    /// must be registered under each interface it implements: the container matches on the
    /// service type, not on what the instance turns out to be.
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

        // The seam ICompensationAlertSink was left for. Registered by default rather than
        // opted into: docs/12-Observability.md §7 pages immediately on any occurrence of
        // flowx_flow_compensation_failed_total, and a counter an application has to remember to
        // wire is a counter that reads zero on the deployment that needed it. An application
        // that registers its own sink — a pager, a dead-letter writer — wins, because TryAdd
        // does not replace it.
        services.TryAddSingleton<ICompensationAlertSink, CompensationFailureCounter>();

        services.TryAddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<FlowXOptions>>().Value;

            // The two policy stores are resolved rather than required, and there is deliberately
            // no default for either — TryAdd's bargain above does not transfer. A default alert
            // sink that counts leaves a deployment degraded about a state the instance row still
            // records; a default in-memory rate limiter would leave one admitting n × the
            // declared rate across n nodes behind a declaration that reads as a deployment-wide
            // bound, and a default in-memory idempotency store would deduplicate one caller in n.
            // A step that declares either policy with no store registered is refused rather than
            // run (ADR-0035 §2.2), which is loud, and one registration fixes it.
            return new FlowEngine(
                provider.GetRequiredService<IClock>(),
                options.MaxPooledContexts,
                provider.GetService<ICompensationAlertSink>(),
                provider.GetService<IRateLimiterStore>(),
                provider.GetService<IIdempotencyStore>());
        });

        // The catalogue is registered whether or not anything is put in it. It is only read
        // by the recovery scan, and a host with no flows registered simply finds no candidate
        // it can run — which is the same answer as an empty backlog and needs no branch.
        services.TryAddSingleton<FlowCatalog>();

        services.TryAddSingleton(provider => new FlowHost(
            provider.GetRequiredService<FlowEngine>(),
            provider.GetRequiredService<IOptions<FlowXOptions>>().Value,
            ResolveDurability(provider)));

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, FlowRecoveryService>(
            static provider => new FlowRecoveryService(
                ResolveScan(provider),
                provider.GetRequiredService<IOptions<FlowXOptions>>().Value)));

        // A second loop rather than a second query on the first, because they are two sweeps
        // over disjoint sets of rows on two intervals a deployment may reasonably set apart —
        // and because a host that can wake parked instances but cannot take over abandoned
        // ones, or the reverse, is a configuration each store decides for itself.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, FlowTimerService>(
            static provider => new FlowTimerService(
                ResolveTimerScan(provider),
                provider.GetRequiredService<IOptions<FlowXOptions>>().Value)));

        // Registered whether or not anything is put in it, for FlowCatalog's reason: the sweep
        // over an empty catalogue is not enabled, which is the same answer as a node that fires
        // nothing and needs no branch.
        services.TryAddSingleton<FlowScheduleCatalog>();

        // A third loop, and not a query on either of the first two. A schedule occurrence is
        // computed rather than read off a row, so this sweep touches no index at all — folding
        // it into FlowTimerService would mean one loop whose interval means two different
        // things, and a deployment that wants schedules at one-second resolution and durable
        // timers at ten cannot say so.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, FlowScheduleService>(
            static provider => new FlowScheduleService(
                ResolveScheduleScan(provider),
                provider.GetRequiredService<IOptions<FlowXOptions>>().Value)));

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

    /// <summary>
    /// The stores this host was given, or null when it was given none.
    /// </summary>
    /// <remarks>
    /// Resolved through <see cref="ServiceProviderServiceExtensions.GetService{T}"/> rather
    /// than required, because an unconfigured host is a supported configuration and not a
    /// mistake. An application that wants to build the bundle itself — a store that plays two
    /// roles, or one wrapped in decorators — registers a <see cref="FlowDurability"/> and
    /// that wins.
    /// </remarks>
    private static FlowDurability? ResolveDurability(IServiceProvider provider)
    {
        if (provider.GetService<FlowDurability>() is { } configured)
        {
            return configured;
        }

        var journal = provider.GetService<IFlowJournal>();
        var leases = provider.GetService<ILeaseStore>();

        // Both or neither. A journal with no lease store would write under a token nothing
        // issued; a lease store with no journal would fence nothing.
        //
        // The journal is wrapped here and nowhere else, which is what makes
        // flowx_journal_commit_seconds a property of the contract rather than of one adapter.
        // The wrap is a no-op unless something is listening, and an application that supplies
        // its own FlowDurability above is left exactly as it built it.
        return journal is not null && leases is not null
            ? new FlowDurability(
                JournalTelemetry.Wrap(journal),
                leases,
                provider.GetService<IRecoveryIndex>(),
                provider.GetService<ITimerIndex>())
            : null;
    }

    /// <summary>The recovery sweep, or null when this host has nothing to sweep with.</summary>
    private static FlowRecoveryScan? ResolveScan(IServiceProvider provider)
    {
        if (ResolveDurability(provider) is not { CanScan: true } durability)
        {
            return null;
        }

        return new FlowRecoveryScan(
            provider.GetRequiredService<FlowHost>(),
            provider.GetRequiredService<FlowCatalog>(),
            durability,
            provider.GetRequiredService<IOptions<FlowXOptions>>().Value,
            provider.GetRequiredService<IClock>());
    }

    /// <summary>The timer sweep, or null when this host has nothing to sweep with.</summary>
    /// <remarks>
    /// Null is the state a deployment is in when its journal implements no
    /// <see cref="ITimerIndex"/>, and it is a supported one: durable flows still run and still
    /// park, and a <c>.Delay(...)</c> waits for whatever else resumes the instance. It is
    /// reported by <c>FlowTimerScan.IsEnabled</c> rather than refused, because a host that
    /// runs no flow with a timer in it is not misconfigured.
    /// </remarks>
    private static FlowTimerScan? ResolveTimerScan(IServiceProvider provider)
    {
        if (ResolveDurability(provider) is not { CanWake: true } durability)
        {
            return null;
        }

        return new FlowTimerScan(
            provider.GetRequiredService<FlowHost>(),
            provider.GetRequiredService<FlowCatalog>(),
            durability,
            provider.GetRequiredService<IOptions<FlowXOptions>>().Value,
            provider.GetRequiredService<IClock>());
    }

    /// <summary>The schedule sweep, or null when this host has no journal to fire into.</summary>
    /// <remarks>
    /// <para>
    /// Null when nothing registered a journal, which for a schedule is not a supported
    /// configuration in the way it is for a timer: without a primary key to refuse a second
    /// node's firing, a schedule fires once per node per occurrence and nothing records that it
    /// did. A host in that state fires nothing, and <c>FlowScheduleCatalog.Add</c> is where an
    /// application that meant to schedule something finds out.
    /// </para>
    /// <para>
    /// <strong>Whether anything is registered is deliberately not decided here.</strong>
    /// Schedules reach the catalogue from the composition root <em>after</em> the container is
    /// built, by the generated <c>AddFlowXSchedules</c>, exactly as a flow reaches
    /// <see cref="FlowCatalog"/>. So the sweep is constructed over a catalogue that may still be
    /// empty and reads <see cref="FlowScheduleScan.IsEnabled"/> at each tick.
    /// </para>
    /// </remarks>
    private static FlowScheduleScan? ResolveScheduleScan(IServiceProvider provider)
    {
        if (ResolveDurability(provider) is not { } durability)
        {
            return null;
        }

        return new FlowScheduleScan(
            provider.GetRequiredService<FlowHost>(),
            provider.GetRequiredService<FlowScheduleCatalog>(),
            durability,
            provider.GetRequiredService<IOptions<FlowXOptions>>().Value,
            provider.GetRequiredService<IClock>());
    }
}

/// <summary>
/// What generated schedule registration code calls, and the only thing it knows about this
/// assembly.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A named type with a stable signature, for <c>FlowEndpointExtensions</c>'s
/// reason.</strong> The generator links against nothing — it is a netstandard2.0 analyzer —
/// and knows this transport only as the string
/// <c>"FlowX.Hosting.FlowScheduleRegistration"</c>, which it looks up in the user's own
/// compilation before emitting anything. An application that does not reference
/// <c>FlowX.Hosting</c> gets no file, no type and no IL.
/// </para>
/// <para>
/// <strong>It takes primitives and not a <see cref="FlowSchedule"/>.</strong> The generated
/// call site is C# the compiler writes from attribute data, and attribute data is strings and
/// enum members; asking it to construct a parsed schedule would mean emitting the parse, which
/// belongs here where it can fail loudly at startup.
/// </para>
/// </remarks>
public static class FlowScheduleRegistration
{
    /// <summary>Registers one declared schedule on this node.</summary>
    /// <param name="services">The built container, which is where the dispatcher comes from.</param>
    /// <param name="plan">The compiled flow.</param>
    /// <param name="dispatcher">Resolves the flow's generated dispatcher.</param>
    /// <param name="cron">The five-field expression, exactly as the manifest published it.</param>
    /// <param name="timeZone">The IANA zone the expression is read in.</param>
    /// <param name="missedFire">Behaviour after downtime.</param>
    /// <returns>The same provider, so registrations chain.</returns>
    /// <exception cref="ArgumentException">
    /// The expression or the zone could not be read, or the flow does not declare
    /// <c>Durable</c>. Both are startup failures on purpose — see
    /// <see cref="FlowScheduleCatalog.Add"/>.
    /// </exception>
    public static IServiceProvider Add(
        IServiceProvider services,
        ExecutionPlan plan,
        Func<IServiceProvider, IStepDispatcher> dispatcher,
        string cron,
        string timeZone,
        MissedFirePolicy missedFire)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(dispatcher);

        services.GetRequiredService<FlowScheduleCatalog>().Add(
            FlowSchedule.Create(plan.Flow.Id, plan.Flow.Version, cron, timeZone, missedFire),
            plan,
            dispatcher(services));

        // A scheduled instance is a durable instance like any other: a node that dies holding
        // one has abandoned it, and a recovery sweep can only take it over if this node can
        // turn its (flow_id, flow_version) back into a plan. Registering here rather than
        // asking the application to remember means a fired schedule is recoverable by
        // construction.
        services.GetRequiredService<FlowCatalog>().Add(plan, dispatcher(services));

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
