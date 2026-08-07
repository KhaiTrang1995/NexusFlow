using FlowX;

namespace Crm;

/// <summary>The active process for one entity kind.</summary>
/// <remarks>
/// <c>Ephemeral</c>: a read that journals nothing. The one surface that shows the claim this
/// sample is built to prove — a process an administrator changed, read back without a rebuild.
/// </remarks>
[Flow("crm.process.read", Version = "1.0.0", Profile = ExecutionProfile.Ephemeral, Owner = "crm-platform")]
[FlowDeadline("PT30S")]
[HttpTrigger("POST", "/api/v1/crm/processes")]
public sealed partial class ProcessViewFlow : Flow<ReadProcess, ProcessView>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<ReadProcess, ProcessView> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<ReadCrmProcess, ReadProcess>(ctx => ctx.Input)
            .Return(ctx => ctx.Get<ProcessView>());
    }
}
