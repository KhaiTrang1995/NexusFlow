using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;

namespace FlowX.Hosting;

/// <summary>One address a flow declared, as the manifest published it.</summary>
/// <param name="FlowId">The flow's business identity.</param>
/// <param name="Version">The version the declaration belongs to.</param>
/// <param name="Kind">The trigger kind, e.g. <c>Bus</c>.</param>
/// <param name="Address">What the declaration named — a topic, an event type, a cron, a source.</param>
public sealed record DeclaredTrigger(string FlowId, string Version, string Kind, string Address);

/// <summary>
/// What this application's flows declared, so the host can tell whether anything serves it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This exists because a declared address nothing serves is silent.</strong> A
/// <c>[BusTrigger]</c> whose subscription was never registered compiles, reaches the manifest,
/// is published, is diffed — and never runs. Every symptom points at the broker: no message
/// arrives, no error is logged, and the flow is reachable in every way a reader can check
/// without starting it. The repository has found that shape three times, and once in the CRM
/// sample after the flows were written.
/// </para>
/// <para>
/// <strong>Collected in the service collection rather than read back out of the manifest.</strong>
/// The generated <c>AddFlowXCapabilities</c> knows every declaration already — it is emitted from
/// the same reading of the attributes — so parsing JSON at start-up would be a second answer to a
/// question the build has settled, and a reflection-free host would have to grow a parser to ask
/// it.
/// </para>
/// </remarks>
public sealed class FlowXDeclaredTriggers
{
    private readonly List<DeclaredTrigger> _declared = [];

    /// <summary>Everything declared, in declaration order.</summary>
    public IReadOnlyList<DeclaredTrigger> All => _declared;

    /// <summary>Records one declaration.</summary>
    /// <param name="declaration">What the flow declared.</param>
    /// <exception cref="System.ArgumentNullException"><paramref name="declaration"/> is null.</exception>
    public void Add(DeclaredTrigger declaration)
    {
        System.ArgumentNullException.ThrowIfNull(declaration);

        _declared.Add(declaration);
    }
}

/// <summary>
/// What generated capability registration calls to say an address exists.
/// </summary>
/// <remarks>
/// The generator knows this assembly only by the string
/// <c>"FlowX.Hosting.FlowXTriggerDeclaration"</c>, which it looks up in the user's own compilation
/// before emitting anything — <see cref="FlowBusSubscriptionRegistration"/>'s arrangement, for its
/// reasons.
/// </remarks>
public static class FlowXTriggerDeclaration
{
    /// <summary>Declares one trigger a flow in this compilation carries.</summary>
    /// <param name="services">The collection being built.</param>
    /// <param name="flowId">The flow's business identity.</param>
    /// <param name="version">The version the declaration belongs to.</param>
    /// <param name="kind">The trigger kind, e.g. <c>Bus</c>.</param>
    /// <param name="address">What the declaration named.</param>
    /// <returns>The same collection, so registrations chain.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <remarks>
    /// The collector is an instance in the collection rather than a factory, because this is
    /// called before anything is built and each call has to reach the same list. A second
    /// <c>AddFlowXCapabilities</c> from a second flow library therefore adds to it rather than
    /// replacing it.
    /// </remarks>
    public static IServiceCollection Declare(
        IServiceCollection services,
        string flowId,
        string version,
        string kind,
        string address)
    {
        System.ArgumentNullException.ThrowIfNull(services);

        Collector(services).Add(new DeclaredTrigger(flowId, version, kind, address));

        return services;
    }

    private static FlowXDeclaredTriggers Collector(IServiceCollection services)
    {
        if (services.FirstOrDefault(static d => d.ServiceType == typeof(FlowXDeclaredTriggers))
            is { ImplementationInstance: FlowXDeclaredTriggers existing })
        {
            return existing;
        }

        var collector = new FlowXDeclaredTriggers();

        services.AddSingleton(collector);

        return collector;
    }
}
