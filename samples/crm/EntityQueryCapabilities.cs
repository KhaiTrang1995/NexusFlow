using FlowX;

namespace Crm;

/// <summary>
/// A page of a built-in entity.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The filter's field names are checked here, before anything reaches the store.</strong>
/// A field name is a caller's value, and the one thing this codebase never does is let one near a
/// statement. Checking it against the entity's own closed column list means the name is either
/// one of a handful this build wrote down, or the request is refused with the list in the message.
/// </para>
/// <para>
/// <strong>What this does not return.</strong> The entity's built-in columns and nothing else.
/// Custom fields declared on a built-in entity live in their own table and carry their own read
/// permissions; they are read through <c>custom/entity-fields</c>, which already masks them. A
/// surface that returned both would have to mask half its answer and not the other half, and the
/// caller could not tell which half they were looking at.
/// </para>
/// </remarks>
[Capability("crm.entity.page", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.read",
    Idempotent = true)]
public sealed class ReadCrmEntityPage : ICapability<ReadEntityRecords, RecordPage>
{
    private readonly EntityQueryStore _entities;

    /// <summary>Creates the capability.</summary>
    /// <param name="entities">Reads the page.</param>
    /// <exception cref="ArgumentNullException"><paramref name="entities"/> is null.</exception>
    public ReadCrmEntityPage(EntityQueryStore entities)
    {
        ArgumentNullException.ThrowIfNull(entities);

        _entities = entities;
    }

    /// <inheritdoc />
    public async ValueTask<Result<RecordPage>> ExecuteAsync(
        ReadEntityRecords input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var query = input.Query;
        var criteria = query.Filter?.Criteria ?? [];

        if (criteria.Count > EntityQueryLimits.MaxCriteria)
        {
            return Result.Fail<RecordPage>(QueryErrors.TooManyCriteria(criteria.Count));
        }

        foreach (var criterion in criteria)
        {
            if (!EntityColumns.HasReadable(query.Entity, criterion.Field))
            {
                return Result.Fail<RecordPage>(
                    EntityQueryErrors.FieldIsNotOfEntity(query.Entity, criterion.Field));
            }
        }

        // A cursor that is not an id is a cursor from another surface, or one somebody typed.
        // Refused rather than treated as "start from the beginning", which would silently hand
        // back page one to a caller who asked for page nine.
        if (query.After is { Length: > 0 } cursor && !Guid.TryParse(cursor, out _))
        {
            return Result.Fail<RecordPage>(QueryErrors.CursorIsNotUsable(cursor));
        }

        return Result.Ok(await _entities.PageAsync(ctx.TenantId, query, ct).ConfigureAwait(false));
    }
}
