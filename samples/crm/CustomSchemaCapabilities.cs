using FlowX;

namespace Crm;

/// <summary>
/// Declares an entity this build has never heard of.
/// </summary>
/// <remarks>
/// <strong><c>crm.admin</c> and not <c>crm.write</c>, and the split is the point.</strong>
/// Writing a lead and changing what a lead <em>is</em> are different acts with different blast
/// radii: a required field declared here is a field every future record must carry, including
/// ones written by flows that have never heard of it. A representative may do the first and not
/// the second, and the refusal is on the step, so it holds over a broker and from an agent too.
/// </remarks>
[Capability("crm.custom.define_object", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.admin",
    Idempotent = true)]
public sealed class DefineCustomObject : ICapability<DefineObject, ObjectDefined>
{
    private readonly CustomSchemaStore _store;

    /// <summary>Creates the capability.</summary>
    /// <param name="store">Writes the declaration.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public DefineCustomObject(CustomSchemaStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <inheritdoc />
    public async ValueTask<Result<ObjectDefined>> ExecuteAsync(
        DefineObject input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (!CustomValues.IsUsableName(input.Name))
        {
            return Result.Fail<ObjectDefined>(CustomSchemaErrors.NameIsNotUsable(input.Name));
        }

        var id = await _store
            .DeclareObjectAsync(ctx.TenantId, ctx.NewId(), input, ctx.UtcNow, ct)
            .ConfigureAwait(false);

        return id is null
            ? Result.Fail<ObjectDefined>(CustomSchemaErrors.NameIsTaken(input.Name))
            : Result.Ok(new ObjectDefined(id.Value, input.Name));
    }
}

/// <summary>
/// Declares a field on a built-in entity kind or on a custom object.
/// </summary>
/// <remarks>
/// <strong>The owner is checked here and constrained by the schema.</strong> Migration
/// <c>0005</c>'s <c>CHECK ((applies_to IS NULL) &lt;&gt; (object_id IS NULL))</c> is what makes
/// a field with two owners or none impossible; this is what turns that into an error naming the
/// problem. The pair is the pattern migration <c>0003</c> established for the activity trigger.
/// </remarks>
[Capability("crm.custom.define_field", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.admin",
    Idempotent = true)]
public sealed class DefineCustomField : ICapability<DefineField, FieldDefined>
{
    private readonly CustomSchemaStore _store;

    /// <summary>Creates the capability.</summary>
    /// <param name="store">Writes the declaration.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public DefineCustomField(CustomSchemaStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <inheritdoc />
    public async ValueTask<Result<FieldDefined>> ExecuteAsync(
        DefineField input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (input.AppliesTo is null == input.Target is null)
        {
            return Result.Fail<FieldDefined>(CustomSchemaErrors.FieldHasNoOwner());
        }

        if (!CustomValues.IsUsableName(input.Name))
        {
            return Result.Fail<FieldDefined>(CustomSchemaErrors.NameIsNotUsable(input.Name));
        }

        if (input.Target is { } objectId &&
            !await _store.HasObjectAsync(ctx.TenantId, objectId, ct).ConfigureAwait(false))
        {
            return Result.Fail<FieldDefined>(CustomSchemaErrors.ObjectNotFound(objectId));
        }

        var id = await _store
            .DeclareFieldAsync(ctx.TenantId, ctx.NewId(), input, ctx.UtcNow, ct)
            .ConfigureAwait(false);

        return id is null
            ? Result.Fail<FieldDefined>(CustomSchemaErrors.NameIsTaken(input.Name))
            : Result.Ok(new FieldDefined(id.Value, input.Name));
    }
}

/// <summary>
/// Declares a named edge between two custom objects.
/// </summary>
[Capability("crm.custom.define_relationship", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.admin",
    Idempotent = true)]
public sealed class DefineCustomRelationship : ICapability<DefineRelationship, RelationshipDefined>
{
    private readonly CustomSchemaStore _store;

    /// <summary>Creates the capability.</summary>
    /// <param name="store">Writes the declaration.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public DefineCustomRelationship(CustomSchemaStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <inheritdoc />
    public async ValueTask<Result<RelationshipDefined>> ExecuteAsync(
        DefineRelationship input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (!CustomValues.IsUsableName(input.Name))
        {
            return Result.Fail<RelationshipDefined>(CustomSchemaErrors.NameIsNotUsable(input.Name));
        }

        foreach (var end in new[] { input.From, input.To })
        {
            if (!await _store.HasObjectAsync(ctx.TenantId, end, ct).ConfigureAwait(false))
            {
                return Result.Fail<RelationshipDefined>(CustomSchemaErrors.ObjectNotFound(end));
            }
        }

        var id = await _store
            .DeclareRelationshipAsync(ctx.TenantId, ctx.NewId(), input, ct)
            .ConfigureAwait(false);

        return id is null
            ? Result.Fail<RelationshipDefined>(CustomSchemaErrors.NameIsTaken(input.Name))
            : Result.Ok(new RelationshipDefined(id.Value, input.Name));
    }
}

/// <summary>
/// Writes a row of an object an administrator invented.
/// </summary>
/// <remarks>
/// <strong><c>crm.write</c>, because this is data.</strong> Whoever may write a lead may write a
/// row of a custom object; what they may not do is decide what its columns are.
/// </remarks>
[Capability("crm.custom.create_record", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.write",
    Idempotent = true)]
public sealed class CreateCustomRecord : ICapability<CreateRecord, RecordCreated>
{
    private readonly CustomSchemaStore _store;

    /// <summary>Creates the capability.</summary>
    /// <param name="store">Reads the declarations and writes the row.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public CreateCustomRecord(CustomSchemaStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <inheritdoc />
    public async ValueTask<Result<RecordCreated>> ExecuteAsync(
        CreateRecord input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (!await _store.HasObjectAsync(ctx.TenantId, input.Target, ct).ConfigureAwait(false))
        {
            return Result.Fail<RecordCreated>(CustomSchemaErrors.ObjectNotFound(input.Target));
        }

        var declared = await _store
            .FieldsForAsync(ctx.TenantId, input.Target, ct)
            .ConfigureAwait(false);

        // A whole record, so a required field with no value is a fault. Reported one at a time:
        // the first is what the caller has to fix, and a list of every fault would still be
        // read top-down.
        if (CustomValues.Validate(declared, input.Values, requireComplete: true) is { Count: > 0 } faults)
        {
            return Result.Fail<RecordCreated>(faults[0]);
        }

        var id = ctx.NewId();

        await _store
            .WriteRecordAsync(
                ctx.TenantId, id, input.Target, CustomValues.ToJson(declared, input.Values), ctx.UtcNow, ct)
            .ConfigureAwait(false);

        return Result.Ok(new RecordCreated(id));
    }
}

/// <summary>
/// Joins two records along a relationship an administrator declared.
/// </summary>
/// <remarks>
/// <strong>Both ends are checked to be of the objects the edge joins</strong>, because the
/// foreign keys of migration <c>0005</c> hold a record to <em>a</em> record and cannot say which
/// object it belongs to. Without this a <c>site</c> could be linked as though it were a
/// <c>contract</c>, and every reader of the edge would have to re-check.
/// </remarks>
[Capability("crm.custom.link_records", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.write",
    Idempotent = true)]
public sealed class LinkCustomRecords : ICapability<LinkRecords, RecordsLinked>
{
    private readonly CustomSchemaStore _store;

    /// <summary>Creates the capability.</summary>
    /// <param name="store">Reads the relationship and writes the link.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public LinkCustomRecords(CustomSchemaStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <inheritdoc />
    public async ValueTask<Result<RecordsLinked>> ExecuteAsync(
        LinkRecords input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (await _store.ReadRelationshipAsync(ctx.TenantId, input.Relationship, ct).ConfigureAwait(false)
            is not { } edge)
        {
            return Result.Fail<RecordsLinked>(
                CustomSchemaErrors.RelationshipNotFound(input.Relationship));
        }

        if (await _store.ObjectOfAsync(ctx.TenantId, input.From, ct).ConfigureAwait(false) != edge.From)
        {
            return Result.Fail<RecordsLinked>(CustomSchemaErrors.RecordNotFound(input.From));
        }

        if (await _store.ObjectOfAsync(ctx.TenantId, input.To, ct).ConfigureAwait(false) != edge.To)
        {
            return Result.Fail<RecordsLinked>(CustomSchemaErrors.RecordNotFound(input.To));
        }

        var id = ctx.NewId();

        return await _store.LinkAsync(ctx.TenantId, id, input, ctx.UtcNow, ct).ConfigureAwait(false)
            ? Result.Ok(new RecordsLinked(id))
            : Result.Fail<RecordsLinked>(
                CustomSchemaErrors.CardinalityWouldBreak(edge.Cardinality));
    }
}

/// <summary>
/// Sets the custom values of a built-in entity.
/// </summary>
/// <remarks>
/// <strong>A merge and not a replacement.</strong> The values a caller sends are written over
/// the ones with the same names and the rest are left alone, because a partial update that
/// erased what it did not mention would make two clients editing different fields of the same
/// lead destroy each other's work.
/// </remarks>
[Capability("crm.custom.set_fields", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.write",
    Idempotent = true)]
public sealed class SetEntityCustomFields : ICapability<SetCustomFields, CustomFieldsSet>
{
    private readonly CustomSchemaStore _store;

    /// <summary>Creates the capability.</summary>
    /// <param name="store">Reads the declarations and merges the values.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public SetEntityCustomFields(CustomSchemaStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <inheritdoc />
    public async ValueTask<Result<CustomFieldsSet>> ExecuteAsync(
        SetCustomFields input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var declared = await _store.FieldsForAsync(ctx.TenantId, input.Kind, ct).ConfigureAwait(false);

        // requireComplete: false — see the remarks on the class. A field the caller did not
        // mention keeps whatever the row already held, so it is not missing.
        if (CustomValues.Validate(declared, input.Values, requireComplete: false) is { Count: > 0 } faults)
        {
            return Result.Fail<CustomFieldsSet>(faults[0]);
        }

        var merged = await _store
            .MergeCustomFieldsAsync(
                ctx.TenantId, input.Kind, input.Id, CustomValues.ToJson(declared, input.Values), ct)
            .ConfigureAwait(false);

        return merged is null
            ? Result.Fail<CustomFieldsSet>(CustomSchemaErrors.EntityNotFound(input.Kind, input.Id))
            : Result.Ok(new CustomFieldsSet(input.Id, merged));
    }
}
