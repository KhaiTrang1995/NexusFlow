using FlowX;

namespace Crm;

/// <summary>A page of a built-in entity.</summary>
/// <remarks>
/// <c>Ephemeral</c>: a read that journals nothing. The scopes come from the caller's claims
/// through the projection, never from the body — a page that took its own grants in the request
/// would be a page anybody could read as anybody.
/// </remarks>
[Flow("crm.entity.page", Version = "1.0.0", Profile = ExecutionProfile.Ephemeral, Owner = "crm-platform")]
[FlowDeadline("PT30S")]
[HttpTrigger("POST", "/api/v1/crm/entities")]
public sealed partial class EntityPageFlow : Flow<ReadEntityPage, RecordPage>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<ReadEntityPage, RecordPage> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<ReadCrmEntityPage, ReadEntityRecords>(
                ctx => new ReadEntityRecords(ctx.Input, CustomFieldPolicy.Scopes(ctx.Principal)))
            .Return(ctx => ctx.Get<RecordPage>());
    }
}
