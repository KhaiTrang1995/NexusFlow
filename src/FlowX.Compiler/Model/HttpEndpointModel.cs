using System;
using System.Collections.Generic;

namespace FlowX.Compiler.Model;

/// <summary>
/// One flow's <c>[HttpTrigger]</c>, reduced to everything the endpoint registration
/// needs and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Every address field here — <see cref="Method"/>, <see cref="Route"/>,
/// <see cref="RequiresIdempotencyKey"/> — is copied from the same
/// <see cref="TriggerModel"/> the manifest publishes, produced by the same
/// <c>TriggerReader</c> run. That is deliberate and it is the whole answer to "how do
/// the endpoint and the manifest stay in agreement": they are not two readings of one
/// attribute, they are two writers of one reading. A route that reached the manifest
/// and not the endpoint would need the two to have been read separately, and they
/// cannot be.
/// </para>
/// <para>
/// The rest is the generated surface of the flow's own partial class — its plan, its
/// dispatcher, its projection, its sensitive members — named rather than restated, so
/// the endpoint carries no copy of anything the flow declared.
/// </para>
/// </remarks>
public sealed class HttpEndpointModel
{
    /// <summary>Creates a model of one flow's HTTP endpoint.</summary>
    /// <param name="flowId">The flow's business id, e.g. <c>order.place</c>.</param>
    /// <param name="flowTypeName">The flow's fully qualified type name.</param>
    /// <param name="methodName">The C# name of the generated extension method.</param>
    /// <param name="inputTypeName">The flow's input contract, fully qualified.</param>
    /// <param name="outputTypeName">The flow's output contract, fully qualified.</param>
    /// <param name="method">HTTP method, as the trigger declared it.</param>
    /// <param name="route">Route template, as the trigger declared it.</param>
    /// <param name="requiresIdempotencyKey">Whether admission demands an idempotency key.</param>
    /// <param name="jsonContextTypeName">
    /// The fully qualified serialiser context declaring both contracts, or <c>null</c>
    /// when this compilation has no single unambiguous one.
    /// </param>
    /// <param name="signals">
    /// Every signal this flow can suspend at, deduplicated by identity in step order. Empty
    /// for a flow that never waits, which is most of them.
    /// </param>
    public HttpEndpointModel(
        string flowId,
        string flowTypeName,
        string methodName,
        string inputTypeName,
        string outputTypeName,
        string method,
        string route,
        bool requiresIdempotencyKey,
        string? jsonContextTypeName,
        IReadOnlyList<SignalEndpointModel>? signals = null)
    {
        Signals = signals ?? Array.Empty<SignalEndpointModel>();
        FlowId = flowId;
        FlowTypeName = flowTypeName;
        MethodName = methodName;
        InputTypeName = inputTypeName;
        OutputTypeName = outputTypeName;
        Method = method;
        Route = route;
        RequiresIdempotencyKey = requiresIdempotencyKey;
        JsonContextTypeName = jsonContextTypeName;
    }

    /// <summary>The flow's business id.</summary>
    public string FlowId { get; }

    /// <summary>The flow's fully qualified type name.</summary>
    public string FlowTypeName { get; }

    /// <summary>The C# name of the generated extension method, e.g. <c>MapPlaceOrderFlow</c>.</summary>
    public string MethodName { get; }

    /// <summary>The flow's input contract, fully qualified.</summary>
    public string InputTypeName { get; }

    /// <summary>The flow's output contract, fully qualified.</summary>
    public string OutputTypeName { get; }

    /// <summary>HTTP method, exactly as the manifest states it.</summary>
    public string Method { get; }

    /// <summary>Route template, exactly as the manifest states it.</summary>
    public string Route { get; }

    /// <summary>Whether an <c>Idempotency-Key</c> header is demanded at admission.</summary>
    public bool RequiresIdempotencyKey { get; }

    /// <summary>
    /// The serialiser context to read both contracts' metadata from, or <c>null</c>.
    /// </summary>
    /// <remarks>
    /// <c>null</c> means the compilation offered no single <c>JsonSerializerContext</c>
    /// declaring <c>[JsonSerializable]</c> for both contracts — none, or several. The
    /// endpoint is still generated; only the no-argument overload that would have had to
    /// pick one is not. See <c>EndpointEmitter</c>.
    /// </remarks>
    public string? JsonContextTypeName { get; }

    /// <summary>
    /// Every signal this flow can suspend at, and therefore every route that continues it.
    /// </summary>
    /// <remarks>
    /// This is the one field on this model that does not come from the trigger attribute — it
    /// comes from the flow's <c>Define</c> body, which is where a wait is declared. The
    /// consequence is worth stating: a flow's HTTP surface depends on its body and not only on
    /// its attributes, so adding an <c>.AwaitSignal&lt;T&gt;</c> adds routes. That coupling is
    /// accepted in
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0022-http-shape-of-a-suspending-flow.md">ADR-0022</a>
    /// because the alternative is a second declaration that can disagree with the plan, and it
    /// is visible: <c>FLOWX-DIFF-022</c> reports the flow gaining the wait.
    /// </remarks>
    public IReadOnlyList<SignalEndpointModel> Signals { get; }
}

/// <summary>One signal a flow waits for, as a route the transport can serve.</summary>
/// <param name="SignalType">
/// The identity the plan carries and a sender addresses, e.g. <c>offer.countersigned</c>. It
/// becomes a literal segment of the route, so an identity nothing waits for is a routing miss
/// rather than a comparison in a handler.
/// </param>
/// <param name="ContractTypeName">
/// The fully-qualified contract the flow declared in <c>.AwaitSignal&lt;T&gt;(...)</c>. It
/// becomes the generic argument of the emitted <c>MapFlowSignal</c> call, which is what lets
/// the plugin deserialise a payload it has never heard of with no reflection.
/// </param>
/// <remarks>
/// Read from the flow's own <c>Define</c> body rather than from an attribute, because the
/// body is where the wait is declared and an attribute would be a second declaration that
/// could name a signal no step waits for
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0022-http-shape-of-a-suspending-flow.md">ADR-0022</a>,
/// rejected option F).
/// </remarks>
public sealed record SignalEndpointModel(string SignalType, string ContractTypeName);

/// <summary>
/// A <c>JsonSerializerContext</c> found in the compilation, and the contracts it
/// declares.
/// </summary>
/// <remarks>
/// Read from <c>[JsonSerializable]</c> attributes rather than from the properties the
/// System.Text.Json generator produces from them. The attributes are the author's
/// declaration and are readable whether or not the other generator has run; the
/// properties are another generator's output, and a generator that reads a second
/// generator's output has an ordering problem it cannot fix.
/// </remarks>
public sealed class JsonContextModel : IEquatable<JsonContextModel>
{
    /// <summary>Creates a model of one serialiser context.</summary>
    /// <param name="typeName">The context's fully qualified type name.</param>
    /// <param name="serializableTypes">The contracts it declares, fully qualified.</param>
    public JsonContextModel(string typeName, IReadOnlyList<string> serializableTypes)
    {
        TypeName = typeName;
        SerializableTypes = serializableTypes;
    }

    /// <summary>The context's fully qualified type name.</summary>
    public string TypeName { get; }

    /// <summary>The contracts it declares <c>[JsonSerializable]</c>, fully qualified.</summary>
    public IReadOnlyList<string> SerializableTypes { get; }

    /// <summary>Whether this context declares every one of the given contracts.</summary>
    public bool Declares(params string[] contracts)
    {
        if (contracts is null)
        {
            throw new ArgumentNullException(nameof(contracts));
        }

        foreach (var contract in contracts)
        {
            if (!Contains(contract))
            {
                return false;
            }
        }

        return true;
    }

    private bool Contains(string contract)
    {
        for (var i = 0; i < SerializableTypes.Count; i++)
        {
            if (string.Equals(SerializableTypes[i], contract, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public bool Equals(JsonContextModel? other)
    {
        if (other is null || !string.Equals(TypeName, other.TypeName, StringComparison.Ordinal) ||
            SerializableTypes.Count != other.SerializableTypes.Count)
        {
            return false;
        }

        for (var i = 0; i < SerializableTypes.Count; i++)
        {
            if (!string.Equals(SerializableTypes[i], other.SerializableTypes[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as JsonContextModel);

    /// <inheritdoc />
    public override int GetHashCode() => TypeName.GetHashCode();
}
