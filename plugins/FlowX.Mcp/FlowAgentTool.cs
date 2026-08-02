using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FlowX.Hosting;
using FlowX.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace FlowX.Mcp;

/// <summary>
/// One flow, bound to the tool name an agent calls it by.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A binding, never a description.</strong> Everything an agent is <em>told</em>
/// about a tool comes from <see cref="McpToolCatalog"/> and therefore from the manifest;
/// this side supplies only what the manifest cannot — the compiled plan, the dispatcher and
/// the two <see cref="JsonTypeInfo{T}"/> that turn an argument document into the flow's
/// input contract and its output back into JSON. Keeping the two apart is what makes the
/// published surface a projection: a registration cannot add a tool the manifest does not
/// publish, and <see cref="McpServer"/> refuses one at start-up if it tries.
/// </para>
/// <para>
/// <strong>It reaches <c>FlowEngine.ExecuteAsync</c> through <see cref="FlowHost"/> and by
/// no other route.</strong> There is no agent-shaped entry into a flow; a
/// <c>tools/call</c> is <c>FlowHost.RunAsync</c> with the agent's principal on the
/// invocation, which is the same call <c>FlowEndpointExtensions.MapFlow</c> makes with a
/// request's principal on it.
/// </para>
/// </remarks>
public interface IFlowAgentTool
{
    /// <summary>The flow's business identity, which is what the manifest keys a tool on.</summary>
    string FlowId { get; }

    /// <summary>Runs the flow with the agent's arguments and the agent's claims.</summary>
    /// <param name="name">The tool name, so a refusal about the arguments can name it.</param>
    /// <param name="arguments">
    /// The <c>params.arguments</c> object, or <c>null</c> when the call carried none.
    /// </param>
    /// <param name="invocation">
    /// Correlation, tenant, idempotency and the agent's principal — built by the transport
    /// exactly as it builds one for any other caller.
    /// </param>
    /// <param name="services">The request's scope, which is where the dispatcher comes from.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    ValueTask<McpToolOutcome> InvokeAsync(
        string name,
        JsonElement? arguments,
        FlowInvocation invocation,
        IServiceProvider services,
        CancellationToken ct);
}

/// <summary>What one <c>tools/call</c> produced.</summary>
/// <remarks>
/// Three states and not two. A flow can complete, fail, or stop at a wait — and the third
/// is neither of the first two, exactly as <c>FlowExecutionResult</c> has said since
/// ADR-0022. An agent told "done" about a suspended instance would report a countersigned
/// offer that nobody has countersigned.
/// </remarks>
public readonly struct McpToolOutcome
{
    private McpToolOutcome(Error? error, byte[]? content, Guid? instanceId, bool suspended)
    {
        Error = error;
        Content = content;
        InstanceId = instanceId;
        IsSuspended = suspended;
    }

    /// <summary>The refusal, or <c>null</c> when the call was not refused.</summary>
    public Error? Error { get; }

    /// <summary>The flow's projected output as UTF-8 JSON, or <c>null</c>.</summary>
    public byte[]? Content { get; }

    /// <summary>The journaled instance, when the flow had one.</summary>
    public Guid? InstanceId { get; }

    /// <summary>Whether the flow stopped at a wait rather than finishing.</summary>
    public bool IsSuspended { get; }

    /// <summary>Whether the flow ran to completion and produced its output.</summary>
    public bool IsSuccess => Error is null && !IsSuspended;

    /// <summary>The call was refused, by this surface or by the flow.</summary>
    /// <param name="error">The refusal.</param>
    /// <param name="instanceId">The instance, when one was opened before the refusal.</param>
    public static McpToolOutcome Failed(Error error, Guid? instanceId = null) =>
        new(error, content: null, instanceId, suspended: false);

    /// <summary>The flow completed.</summary>
    /// <param name="content">The projected output, as UTF-8 JSON.</param>
    /// <param name="instanceId">The instance, when the flow was journaled.</param>
    public static McpToolOutcome Completed(byte[] content, Guid? instanceId) =>
        new(error: null, content, instanceId, suspended: false);

    /// <summary>The flow stopped at a wait.</summary>
    /// <param name="instanceId">The instance to deliver the signal to.</param>
    public static McpToolOutcome Suspended(Guid? instanceId) =>
        new(error: null, content: null, instanceId, suspended: true);
}

/// <summary>
/// The binding for one flow, closed over its two contracts.
/// </summary>
/// <typeparam name="TIn">The flow's input contract, read from the arguments document.</typeparam>
/// <typeparam name="TOut">The flow's output contract, from its <c>.Return(...)</c> clause.</typeparam>
/// <remarks>
/// Generic and instantiated by generated code in the user's assembly, which is how this
/// plugin serialises contracts it has never heard of with no reflection at all — the same
/// arrangement <c>FlowEndpointExtensions.MapFlow&lt;TRequest, TResponse&gt;</c> uses, and
/// for the same constraint (C2).
/// </remarks>
internal sealed class FlowAgentTool<TIn, TOut> : IFlowAgentTool
    where TIn : notnull
{
    private readonly ExecutionPlan _plan;
    private readonly Func<IServiceProvider, IStepDispatcher> _dispatcherFactory;
    private readonly Func<FlowContext, TOut> _projection;
    private readonly JsonTypeInfo<TIn> _input;
    private readonly JsonTypeInfo<TOut> _output;

    public FlowAgentTool(
        string flowId,
        ExecutionPlan plan,
        Func<IServiceProvider, IStepDispatcher> dispatcherFactory,
        Func<FlowContext, TOut> projection,
        JsonTypeInfo<TIn> input,
        JsonTypeInfo<TOut> output)
    {
        FlowId = flowId;
        _plan = plan;
        _dispatcherFactory = dispatcherFactory;
        _projection = projection;
        _input = input;
        _output = output;
    }

    public string FlowId { get; }

    public async ValueTask<McpToolOutcome> InvokeAsync(
        string name,
        JsonElement? arguments,
        FlowInvocation invocation,
        IServiceProvider services,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);

        var contract = typeof(TIn).FullName ?? typeof(TIn).Name;

        if (arguments is not { ValueKind: not JsonValueKind.Null } document)
        {
            return McpToolOutcome.Failed(McpErrors.MissingArguments(name, contract));
        }

        TIn? input;

        try
        {
            input = document.Deserialize(_input);
        }
        catch (JsonException exception)
        {
            return McpToolOutcome.Failed(
                McpErrors.MalformedArguments(name, contract, exception.Message));
        }

        if (input is null)
        {
            return McpToolOutcome.Failed(McpErrors.MissingArguments(name, contract));
        }

        var host = services.GetRequiredService<FlowHost>();

        // The one entry into a flow. The invocation carries the agent's principal, so the
        // step loop decides every capability's stance against the agent's claims — which is
        // the whole of the authorisation story for this transport (ADR-0027, ADR-0028).
        var result = await host
            .RunAsync(_plan, _dispatcherFactory(services), invocation, input, _projection, ct)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return McpToolOutcome.Failed(result.Error!, result.InstanceId);
        }

        return result.IsSuspended
            ? McpToolOutcome.Suspended(result.InstanceId)
            : McpToolOutcome.Completed(Serialise(result.Value), result.InstanceId);
    }

    private byte[] Serialise(TOut value)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            JsonSerializer.Serialize(writer, value, _output);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Reads one contract's metadata out of a source-generated context.
    /// </summary>
    /// <remarks>
    /// Fails when the tool is registered rather than on the first call, and names the
    /// attribute to add — <c>FlowEndpointExtensions.TypeInfoFor</c>'s reasoning, for the
    /// same reason: a contract missing from the context is a tool that could never have
    /// served a call, so the useful moment to say so is before the process reports ready.
    /// </remarks>
    internal static JsonTypeInfo<T> TypeInfoFor<T>(JsonSerializerContext json) =>
        json.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
        ?? throw new InvalidOperationException(
            $"{json.GetType().Name} carries no metadata for {typeof(T)}. Add " +
            $"[JsonSerializable(typeof({typeof(T).Name}))] to it — the flow declares that " +
            "contract as an agent tool's arguments or result, so the serialiser has to know it.");
}
