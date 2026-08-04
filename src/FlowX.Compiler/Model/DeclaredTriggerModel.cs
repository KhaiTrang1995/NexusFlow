namespace FlowX.Compiler.Model;

/// <summary>
/// One address a flow declared and the host must serve, as the emitted registration states it.
/// </summary>
/// <remarks>
/// Only the four kinds whose absence is silent are modelled — <c>Bus</c>, <c>Change</c>,
/// <c>Schedule</c> and <c>Stream</c>. A missing HTTP mapping is a 404 at the first request and an
/// agent tool that was never bound is absent from <c>tools/list</c>; both are visible without a
/// start-up refusal, and refusing on them would make a legitimate composition — a worker that
/// serves the subscriptions and no routes — impossible to write.
/// </remarks>
/// <param name="FlowId">The flow's business identity.</param>
/// <param name="Version">The version the declaration belongs to.</param>
/// <param name="Kind">The trigger kind, exactly as the manifest spells it.</param>
/// <param name="Address">The topic, event type, cron expression or stream source declared.</param>
public sealed record DeclaredTriggerModel(
    string FlowId,
    string Version,
    string Kind,
    string Address);
