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

        // Projected, not passed straight through. The scopes come off the principal the trigger
        // authenticated; a `scopes` field on the request would let anybody claim any grant, which
        // is the argument ADR-0046 makes about the tenant.
        flow
            .Step<CreateCustomRecord, WriteObjectRecord>(
                ctx => new WriteObjectRecord(
                    ctx.Input.Target,
                    ctx.Input.Values,
                    CustomFieldPolicy.Scopes(ctx.Principal)))
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
            .Step<SetEntityCustomFields, WriteEntityFields>(
                ctx => new WriteEntityFields(
                    ctx.Input.Kind,
                    ctx.Input.Id,
                    ctx.Input.Values,
                    CustomFieldPolicy.Scopes(ctx.Principal),
                    Approvers.Of(ctx.Principal)))
            .Return(ctx => ctx.Get<CustomFieldsSet>());
    }
}

/// <summary>Declares a rule that refuses a record.</summary>
[Flow("crm.custom.validation_rule", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/custom/validation-rules", Idempotent = true)]
public sealed partial class DefineValidationRuleFlow
    : Flow<DefineValidationRule, ValidationRuleDefined>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<DefineValidationRule, ValidationRuleDefined> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<DefineCustomValidationRule>()
            .Return(ctx => ctx.Get<ValidationRuleDefined>());
    }
}

/// <summary>Declares a field whose value is an aggregate over a parent's children.</summary>
[Flow("crm.custom.rollup", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/custom/rollups", Idempotent = true)]
public sealed partial class DefineRollupFlow : Flow<DefineRollup, RollupDefined>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<DefineRollup, RollupDefined> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<DefineCustomRollup>()
            .Return(ctx => ctx.Get<RollupDefined>());
    }
}

/// <summary>Saves a named query over a custom object.</summary>
[Flow("crm.custom.list_view", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/custom/list-views", Idempotent = true)]
public sealed partial class DefineListViewFlow : Flow<DefineListView, ListViewDefined>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<DefineListView, ListViewDefined> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<DefineCrmListView>()
            .Return(ctx => ctx.Get<ListViewDefined>());
    }
}

/// <summary>Reads records of a custom object.</summary>
/// <remarks>
/// <strong><c>Ephemeral</c>, unlike every other flow here, because it writes nothing.</strong> A
/// read journaled into <c>flow_instance</c> would put a row in the durable store for every list
/// anybody opened, and a replay of a read has nothing to make idempotent.
/// </remarks>
[Flow("crm.custom.query", Version = "1.0.0", Profile = ExecutionProfile.Ephemeral, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/custom/queries")]
public sealed partial class QueryRecordsFlow : Flow<QueryRecords, RecordPage>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<QueryRecords, RecordPage> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        // Projected, so the grants come off the principal the trigger authenticated. A `scopes`
        // field on the request would let anybody read anything.
        flow
            .Step<QueryCustomRecords, ReadObjectRecords>(
                ctx => new ReadObjectRecords(ctx.Input, CustomFieldPolicy.Scopes(ctx.Principal)))
            .Return(ctx => ctx.Get<RecordPage>());
    }
}
