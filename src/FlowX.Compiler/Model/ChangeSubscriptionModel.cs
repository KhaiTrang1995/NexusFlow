namespace FlowX.Compiler.Model;

/// <summary>
/// One flow's change trigger, reduced to everything the registration needs and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <strong><see cref="Source"/> and <see cref="Group"/> are copied off the
/// <see cref="TriggerModel"/> the manifest publishes, not read a second time</strong>, for
/// <see cref="BusSubscriptionModel"/>'s exact reason: they are two of the four values every node
/// derives a change's instance id from
/// (<a href="../../../docs/adr/ADR-0049-a-change-names-the-instance-it-starts.md">ADR-0049</a>)
/// and two of the four the durable cursor is keyed on, so a registration carrying a different
/// string would start one flow twice and read another subscription's position.
/// </para>
/// <para>
/// <strong>There is no transport field</strong>, unlike <see cref="BusSubscriptionModel"/>. A
/// change trigger names no feed family — the declaration says which event type it observes, and
/// which <c>IChangeFeed</c> answers is the host's registration — so there is nothing for a wired
/// feed to be checked against.
/// </para>
/// </remarks>
public sealed class ChangeSubscriptionModel
{
    /// <summary>Creates a model of one flow's change subscription.</summary>
    /// <param name="flowId">The flow's business id, e.g. <c>orders.project</c>.</param>
    /// <param name="flowTypeName">The flow's fully qualified type name.</param>
    /// <param name="methodName">The C# name of the generated extension method.</param>
    /// <param name="source">The observed event type, exactly as the manifest states it.</param>
    /// <param name="group">The subscription group, exactly as the manifest states it.</param>
    public ChangeSubscriptionModel(
        string flowId,
        string flowTypeName,
        string methodName,
        string source,
        string group)
    {
        FlowId = flowId;
        FlowTypeName = flowTypeName;
        MethodName = methodName;
        Source = source;
        Group = group;
    }

    /// <summary>The flow's business id.</summary>
    public string FlowId { get; }

    /// <summary>The flow's fully qualified type name.</summary>
    public string FlowTypeName { get; }

    /// <summary>The C# name of the generated extension method.</summary>
    public string MethodName { get; }

    /// <summary>The observed event type, exactly as the manifest states it.</summary>
    public string Source { get; }

    /// <summary>The subscription group, exactly as the manifest states it.</summary>
    public string Group { get; }
}
