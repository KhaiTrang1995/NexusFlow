using FlowX;

namespace Crm;

/// <summary>
/// Declares a field whose value is an aggregate over a parent's children.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every check here is a publish-time check</strong>, and each one exists because the
/// alternative is an aggregate that is silently always zero. A roll-up whose field belongs to the
/// wrong object walks an edge no child of that parent is on; one whose source belongs to the
/// wrong object reads a key no child has. Both read exactly like a parent that genuinely has no
/// children, which is the worst kind of wrong number — nobody investigates a zero.
/// </para>
/// <para>
/// <strong><c>crm.admin</c>, and it also makes the field read-only.</strong> Declaring a roll-up
/// takes a field away from every writer, which is a bigger act than writing one.
/// </para>
/// </remarks>
[Capability("crm.custom.define_rollup", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.admin",
    Idempotent = true)]
public sealed class DefineCustomRollup : ICapability<DefineRollup, RollupDefined>
{
    private readonly CustomSchemaStore _schema;
    private readonly RollupStore _rollups;

    /// <summary>Creates the capability.</summary>
    /// <param name="schema">Reads the relationship's two ends.</param>
    /// <param name="rollups">Reads what a field belongs to, and writes the declaration.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public DefineCustomRollup(CustomSchemaStore schema, RollupStore rollups)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(rollups);

        _schema = schema;
        _rollups = rollups;
    }

    /// <inheritdoc />
    public async ValueTask<Result<RollupDefined>> ExecuteAsync(
        DefineRollup input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if ((input.Aggregate == RollupAggregate.Count) != (input.SourceField is null))
        {
            return Result.Fail<RollupDefined>(
                RollupErrors.SourceDoesNotMatchTheAggregate(input.Aggregate));
        }

        if (await _schema.ReadRelationshipAsync(ctx.TenantId, input.Relationship, ct)
                .ConfigureAwait(false) is not { } edge)
        {
            return Result.Fail<RollupDefined>(
                CustomSchemaErrors.RelationshipNotFound(input.Relationship));
        }

        if (await _rollups.FieldOwnerAsync(ctx.TenantId, input.Field, ct).ConfigureAwait(false)
            is not { } field)
        {
            return Result.Fail<RollupDefined>(RollupErrors.FieldNotFound(input.Field));
        }

        if (field.IsComputed)
        {
            return Result.Fail<RollupDefined>(RollupErrors.FieldIsAlreadyComputed(input.Field));
        }

        // `from` is the parent. Migration 0006's cardinality trigger is what makes that true —
        // it constrains the `to` end to one link — and the roll-up has to agree with it.
        if (field.Object != edge.From)
        {
            return Result.Fail<RollupDefined>(RollupErrors.FieldIsNotOnTheParent(input.Field));
        }

        if (input.SourceField is { } source)
        {
            if (await _rollups.FieldOwnerAsync(ctx.TenantId, source, ct).ConfigureAwait(false)
                is not { } sourceField)
            {
                return Result.Fail<RollupDefined>(RollupErrors.FieldNotFound(source));
            }

            if (sourceField.Object != edge.To)
            {
                return Result.Fail<RollupDefined>(RollupErrors.SourceIsNotOnTheChild(source));
            }
        }

        var id = await _rollups
            .DeclareAsync(ctx.TenantId, ctx.NewId(), input, ctx.UtcNow, ct)
            .ConfigureAwait(false);

        return id is null
            ? Result.Fail<RollupDefined>(RollupErrors.FieldIsAlreadyComputed(input.Field))
            : Result.Ok(new RollupDefined(id.Value, input.Field));
    }
}
