using FlowX;

namespace Crm;

/// <summary>What a tenant has declared, of one kind.</summary>
/// <remarks>
/// <c>Ephemeral</c>: a read that journals nothing. One route for twelve tables, because what
/// varies between them is a statement and every one of them answers the same four columns.
/// </remarks>
[Flow("crm.config.list", Version = "1.0.0", Profile = ExecutionProfile.Ephemeral, Owner = "crm-platform")]
[FlowDeadline("PT30S")]
[HttpTrigger("POST", "/api/v1/crm/config")]
public sealed partial class ConfigListFlow : Flow<ReadConfig, ConfigList>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<ReadConfig, ConfigList> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<ReadCrmConfig, ReadConfig>(ctx => ctx.Input)
            .Return(ctx => ctx.Get<ConfigList>());
    }
}
