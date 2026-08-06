using FlowX;

namespace Crm;

/// <summary>Declares an object an administrator invented.</summary>
/// <remarks>
/// <strong>One step and no projection, which is what a flow should look like when the
/// interesting part is elsewhere.</strong> What makes these five routes worth having is the
/// stance on the capability and the rules in <see cref="CustomValues"/>; the flow's job is to
/// give them a transport and a journal.
/// </remarks>
[Flow("crm.custom.object", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/custom/objects", Idempotent = true)]
public sealed partial class DefineObjectFlow : Flow<DefineObject, ObjectDefined>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<DefineObject, ObjectDefined> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<DefineCustomObject>()
            .Return(ctx => ctx.Get<ObjectDefined>());
    }
}

/// <summary>Declares a field on a built-in entity or on a custom object.</summary>
[Flow("crm.custom.field", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/custom/fields", Idempotent = true)]
public sealed partial class DefineFieldFlow : Flow<DefineField, FieldDefined>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<DefineField, FieldDefined> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<DefineCustomField>()
            .Return(ctx => ctx.Get<FieldDefined>());
    }
}

/// <summary>Declares a relationship between two custom objects.</summary>
[Flow("crm.custom.relationship", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/custom/relationships", Idempotent = true)]
public sealed partial class DefineRelationshipFlow : Flow<DefineRelationship, RelationshipDefined>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<DefineRelationship, RelationshipDefined> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<DefineCustomRelationship>()
            .Return(ctx => ctx.Get<RelationshipDefined>());
    }
}

/// <summary>Writes a row of a custom object.</summary>
[Flow("crm.custom.record", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/custom/records", Idempotent = true)]
public sealed partial class CreateRecordFlow : Flow<CreateRecord, RecordCreated>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<CreateRecord, RecordCreated> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<CreateCustomRecord>()
            .Return(ctx => ctx.Get<RecordCreated>());
    }
}

/// <summary>Joins two records along a declared relationship.</summary>
[Flow("crm.custom.link", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/custom/links", Idempotent = true)]
public sealed partial class LinkRecordsFlow : Flow<LinkRecords, RecordsLinked>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<LinkRecords, RecordsLinked> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<LinkCustomRecords>()
            .Return(ctx => ctx.Get<RecordsLinked>());
    }
}

/// <summary>Sets the custom values of a built-in entity.</summary>
[Flow("crm.custom.entity_fields", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/custom/entity-fields", Idempotent = true)]
public sealed partial class SetCustomFieldsFlow : Flow<SetCustomFields, CustomFieldsSet>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<SetCustomFields, CustomFieldsSet> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<SetEntityCustomFields>()
            .Return(ctx => ctx.Get<CustomFieldsSet>());
    }
}
