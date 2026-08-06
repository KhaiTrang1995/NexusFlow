using FlowX;

namespace Crm;

/// <summary>Renames one thing, in this tenant only.</summary>
/// <remarks>
/// <strong>Durable, because a rename is what everybody in the tenant then sees.</strong> A lost
/// rename is a settings screen that says one thing and every other screen another.
/// </remarks>
[Flow("crm.label.set", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/labels", Idempotent = true)]
public sealed partial class SetLabelFlow : Flow<SetLabel, LabelSet>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<SetLabel, LabelSet> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<SetCrmLabel>()
            .Return(ctx => ctx.Get<LabelSet>());
    }
}
