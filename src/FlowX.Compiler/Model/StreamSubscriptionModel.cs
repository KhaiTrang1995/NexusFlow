namespace FlowX.Compiler.Model;

/// <summary>
/// One stream subscription to register on a host, as the emitter needs it.
/// </summary>
/// <param name="FlowId">The windowing flow's business identity, for the doc comment.</param>
/// <param name="FlowTypeName">The flow's fully-qualified type name, whose plan and dispatcher are registered.</param>
/// <param name="MethodName">The extension method this registration is emitted as.</param>
/// <param name="Source">The stream, exactly as the manifest published it as <c>topic</c>.</param>
/// <param name="Window">The declared window, e.g. <c>tumbling:1m</c>.</param>
/// <param name="Lateness">The declared lateness, an ISO-8601 duration.</param>
/// <param name="Checkpoint">The declared checkpoint interval, an ISO-8601 duration.</param>
/// <param name="Parallelism">How many closed windows may have flows running at once.</param>
/// <remarks>
/// <see cref="ChangeSubscriptionModel"/>'s shape with the window declaration added. The source is
/// copied off the <see cref="TriggerModel"/> the manifest published, because it is one of the
/// terms every node derives a window's instance id from; the other four are read from
/// <see cref="StreamDeclaration"/>, which the manifest deliberately does not carry.
/// </remarks>
public sealed record StreamSubscriptionModel(
    string FlowId,
    string FlowTypeName,
    string MethodName,
    string Source,
    string Window,
    string Lateness,
    string Checkpoint,
    int Parallelism);
