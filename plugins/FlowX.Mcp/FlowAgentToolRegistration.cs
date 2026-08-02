using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FlowX.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace FlowX.Mcp;

/// <summary>
/// What generated agent-tool registration code calls, and the only thing it knows about
/// this assembly.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A named type with a stable signature, for <c>FlowScheduleRegistration</c>'s
/// reason.</strong> <c>FlowX.Compiler</c> links against nothing and knows this package only
/// by the string <c>"FlowX.Mcp.FlowAgentToolRegistration"</c>, which it looks up in the
/// user's own compilation before emitting anything. An application that does not reference
/// <c>FlowX.Mcp</c> gets no file, no type and no IL.
/// </para>
/// <para>
/// <strong>Taken on <see cref="IServiceCollection"/>, unlike the schedule and subscription
/// registrations.</strong> Those need the built provider because a registration resolves
/// the flow's dispatcher there and then; a tool resolves its dispatcher per call, from the
/// scope the call arrived in, so nothing here needs a provider yet. Registering rather than
/// mutating also means <see cref="McpServer"/> is constructed once by the container, which
/// is where its start-up check against the manifest belongs.
/// </para>
/// </remarks>
public static class FlowAgentToolRegistration
{
    /// <summary>
    /// Registers the manifest the agent surface projects its tools from.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="manifestJson">
    /// <c>FlowX.Generated.FlowXManifest.Json</c> — the compiled-in copy of the document the
    /// build published, which is what makes <c>tools/list</c> a projection of an artifact
    /// rather than a description assembled at start-up.
    /// </param>
    /// <returns>The same collection, so registrations chain.</returns>
    public static IServiceCollection UseManifest(IServiceCollection services, string manifestJson)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(manifestJson);

        services.AddSingleton(new McpManifest(manifestJson));
        services.TryAddSingletonServer();

        return services;
    }

    /// <summary>Binds one flow to the tool the manifest publishes for it.</summary>
    /// <typeparam name="TIn">The flow's input contract, read from the arguments document.</typeparam>
    /// <typeparam name="TOut">The flow's output contract.</typeparam>
    /// <param name="services">The container.</param>
    /// <param name="flowId">The flow's business identity, exactly as the manifest states it.</param>
    /// <param name="plan">The compiled flow.</param>
    /// <param name="dispatcher">Resolves the flow's generated dispatcher, per call.</param>
    /// <param name="projection">
    /// The generated <c>Projection</c> field on the flow's partial class. Passing it rather
    /// than a hand-written lambda is what makes the agent's result the same thing the flow
    /// declared, instead of a second copy that can drift.
    /// </param>
    /// <param name="json">
    /// A source-generated context declaring <c>[JsonSerializable]</c> for both contracts.
    /// </param>
    /// <returns>The same collection, so registrations chain.</returns>
    /// <exception cref="InvalidOperationException">
    /// The context carries no metadata for one of the contracts, which is a tool that could
    /// never have served a call.
    /// </exception>
    public static IServiceCollection Add<TIn, TOut>(
        IServiceCollection services,
        string flowId,
        ExecutionPlan plan,
        Func<IServiceProvider, IStepDispatcher> dispatcher,
        Func<FlowContext, TOut> projection,
        JsonSerializerContext json)
        where TIn : notnull
    {
        ArgumentNullException.ThrowIfNull(json);

        return Add(
            services,
            flowId,
            plan,
            dispatcher,
            projection,
            FlowAgentTool<TIn, TOut>.TypeInfoFor<TIn>(json),
            FlowAgentTool<TIn, TOut>.TypeInfoFor<TOut>(json));
    }

    /// <summary>Binds one flow, with both contracts' metadata supplied directly.</summary>
    /// <typeparam name="TIn">The flow's input contract.</typeparam>
    /// <typeparam name="TOut">The flow's output contract.</typeparam>
    /// <param name="services">The container.</param>
    /// <param name="flowId">The flow's business identity, exactly as the manifest states it.</param>
    /// <param name="plan">The compiled flow.</param>
    /// <param name="dispatcher">Resolves the flow's generated dispatcher, per call.</param>
    /// <param name="projection">The generated <c>Projection</c> field.</param>
    /// <param name="input">Source-generated metadata for <typeparamref name="TIn"/>.</param>
    /// <param name="output">Source-generated metadata for <typeparamref name="TOut"/>.</param>
    /// <returns>The same collection, so registrations chain.</returns>
    /// <remarks>
    /// Public and supported, like <c>FlowEndpointExtensions</c>'s hand-written overloads: a
    /// project that never runs the generator binds its tools by calling this.
    /// </remarks>
    public static IServiceCollection Add<TIn, TOut>(
        IServiceCollection services,
        string flowId,
        ExecutionPlan plan,
        Func<IServiceProvider, IStepDispatcher> dispatcher,
        Func<FlowContext, TOut> projection,
        JsonTypeInfo<TIn> input,
        JsonTypeInfo<TOut> output)
        where TIn : notnull
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowId);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        services.AddSingleton<IFlowAgentTool>(
            new FlowAgentTool<TIn, TOut>(flowId, plan, dispatcher, projection, input, output));

        services.TryAddSingletonServer();

        return services;
    }

    /// <summary>
    /// Registers <see cref="McpServer"/> once, whichever registration runs first.
    /// </summary>
    /// <remarks>
    /// Both entry points call this rather than one of them owning it, because the order the
    /// generated file writes them in is the generator's business and should not be a
    /// correctness condition here.
    /// </remarks>
    private static void TryAddSingletonServer(this IServiceCollection services)
    {
        if (services.Any(static d => d.ServiceType == typeof(McpServer)))
        {
            return;
        }

        services.AddSingleton<McpServer>();
    }
}
