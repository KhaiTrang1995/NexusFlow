using FlowX;

namespace Crm;

/// <summary>
/// Saves a named query over a custom object.
/// </summary>
/// <remarks>
/// <strong><c>crm.admin</c>, because a saved view is configuration.</strong> It is the thing
/// somebody presses a button on repeatedly, and a representative who could save one could put a
/// five-hundred-row scan behind a button their whole team uses.
/// </remarks>
[Capability("crm.custom.define_list_view", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.admin",
    Idempotent = true)]
public sealed class DefineCrmListView : ICapability<DefineListView, ListViewDefined>
{
    private readonly CustomSchemaStore _schema;
    private readonly QueryStore _queries;

    /// <summary>Creates the capability.</summary>
    /// <param name="schema">Checks the object and the fields the view names.</param>
    /// <param name="queries">Writes the view.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public DefineCrmListView(CustomSchemaStore schema, QueryStore queries)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(queries);

        _schema = schema;
        _queries = queries;
    }

    /// <inheritdoc />
    public async ValueTask<Result<ListViewDefined>> ExecuteAsync(
        DefineListView input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (!CustomValues.IsUsableName(input.Name))
        {
            return Result.Fail<ListViewDefined>(CustomSchemaErrors.NameIsNotUsable(input.Name));
        }

        if (input.Limit is < 1 or > QueryLimits.Max)
        {
            return Result.Fail<ListViewDefined>(QueryErrors.LimitIsOutOfRange(input.Limit));
        }

        if (!await _schema.HasObjectAsync(ctx.TenantId, input.Target, ct).ConfigureAwait(false))
        {
            return Result.Fail<ListViewDefined>(CustomSchemaErrors.ObjectNotFound(input.Target));
        }

        var declared = await _schema.FieldsForAsync(ctx.TenantId, input.Target, ct).ConfigureAwait(false);

        if (input.Filter is { Criteria.Count: > QueryLimits.MaxCriteria } tooMany)
        {
            return Result.Fail<ListViewDefined>(
                QueryErrors.TooManyCriteria(tooMany.Criteria.Count));
        }

        // Every criterion's field and the ordering's, checked when the view is saved. A saved
        // view naming a field nobody declared would return everything or nothing for ever, and
        // whoever pressed the button would believe the answer.
        var named = (input.Filter?.Criteria ?? [])
            .Select(static criterion => criterion.Field)
            .Append(input.Order?.Field);

        foreach (var field in named)
        {
            if (field is { Length: > 0 } && !declared.ContainsKey(field))
            {
                return Result.Fail<ListViewDefined>(
                    FieldPolicyErrors.RuleNamesNoField(field, declared.Keys));
            }
        }

        var id = await _queries
            .SaveViewAsync(ctx.TenantId, ctx.NewId(), input, ctx.UtcNow, ct)
            .ConfigureAwait(false);

        return id is null
            ? Result.Fail<ListViewDefined>(CustomSchemaErrors.NameIsTaken(input.Name))
            : Result.Ok(new ListViewDefined(id.Value, input.Name));
    }
}

/// <summary>
/// Reads records of a custom object, with anything the caller may not see redacted.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the only projection of custom values, and that is what makes read-side field
/// security tractable.</strong> Migration <c>0007</c> declined to do the read half on the
/// grounds that a rule not applied to every projection is a rule that leaks; the answer is that
/// there is one, and it masks. <c>CustomFieldPolicy.Mask</c> is a function rather than three
/// lines here so that a second projection has something to call.
/// </para>
/// <para>
/// <strong><c>crm.read</c> to reach it at all, and a per-field grant on top.</strong> The two are
/// different questions: the first is whether you may query this tenant's records, the second is
/// whether this particular column is yours to see.
/// </para>
/// </remarks>
[Capability("crm.custom.query_records", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.read",
    Idempotent = true)]
public sealed class QueryCustomRecords : ICapability<ReadObjectRecords, RecordPage>
{
    private readonly CustomSchemaStore _schema;
    private readonly QueryStore _queries;

    /// <summary>Creates the capability.</summary>
    /// <param name="schema">Reads the declarations, for what may be masked.</param>
    /// <param name="queries">Reads the view and the rows.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public QueryCustomRecords(CustomSchemaStore schema, QueryStore queries)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(queries);

        _schema = schema;
        _queries = queries;
    }

    /// <inheritdoc />
    public async ValueTask<Result<RecordPage>> ExecuteAsync(
        ReadObjectRecords input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var query = input.Query;

        if (query.Target is null == query.View is null)
        {
            return Result.Fail<RecordPage>(QueryErrors.AskForOneOrTheOther());
        }

        var resolved = query.View is { Length: > 0 } name
            ? await _queries.ReadViewAsync(ctx.TenantId, name, ct).ConfigureAwait(false)
            : (query.Target!.Value, query.Filter, (RecordOrder?)null, query.Limit);

        if (resolved is not { } plan)
        {
            return Result.Fail<RecordPage>(QueryErrors.ViewNotFound(query.View!));
        }

        if (plan.Limit is < 1 or > QueryLimits.Max)
        {
            return Result.Fail<RecordPage>(QueryErrors.LimitIsOutOfRange(plan.Limit));
        }

        if (plan.Filter is { Criteria.Count: > QueryLimits.MaxCriteria } tooMany)
        {
            return Result.Fail<RecordPage>(QueryErrors.TooManyCriteria(tooMany.Criteria.Count));
        }

        (DateTimeOffset CreatedAt, Guid RecordId)? after = null;

        if (query.After is { Length: > 0 } cursor)
        {
            if (plan.Order is not null)
            {
                return Result.Fail<RecordPage>(QueryErrors.CursorNeedsInsertionOrder());
            }

            after = RecordCursor.Read(cursor);

            if (after is null)
            {
                return Result.Fail<RecordPage>(QueryErrors.CursorIsNotUsable(cursor));
            }
        }

        if (!await _schema.HasObjectAsync(ctx.TenantId, plan.Target, ct).ConfigureAwait(false))
        {
            return Result.Fail<RecordPage>(CustomSchemaErrors.ObjectNotFound(plan.Target));
        }

        var declared = await _schema.FieldsForAsync(ctx.TenantId, plan.Target, ct).ConfigureAwait(false);

        var rows = await _queries
            .RecordsAsync(ctx.TenantId, plan.Target, plan.Filter, plan.Order, plan.Limit, after, ct)
            .ConfigureAwait(false);

        var redacted = new SortedSet<string>(StringComparer.Ordinal);

        var records = rows
            .Select(row => new RecordView(
                row.Id,
                CustomFieldPolicy.Mask(
                    declared, CustomValues.FromJson(row.Values), input.Scopes, redacted)))
            .ToList();

        // A cursor only when a full page came back. Deciding it that way means a caller never
        // makes a request that returns nothing, at the cost of one extra request when the last
        // page happens to be exactly full — which is the cheaper of the two mistakes.
        var next = rows.Count == plan.Limit && plan.Order is null
            ? RecordCursor.For(rows[^1].CreatedAt, rows[^1].Id)
            : null;

        return Result.Ok(new RecordPage(records, [.. redacted], next));
    }
}

/// <summary>
/// Searches every entity this tenant has for a phrase.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One statement over five tables, and the tenant is in none of its predicates.</strong>
/// Row-level security scopes all five at once, which is the whole argument for the tenant being
/// a connection setting rather than a <c>WHERE</c> clause somebody has to remember to write in
/// a sixth place.
/// </para>
/// <para>
/// <strong>A hit is an identity, not a row.</strong> It carries the kind, the id and a title
/// taken from the entity's own columns — never a custom field, because those are read-secured per
/// field and a search returning them would be a second unmasked projection of them. A caller
/// follows a hit to the query surface, which decides what they may see.
/// </para>
/// </remarks>
[Capability("crm.search", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.read",
    Idempotent = true)]
public sealed class SearchCrm : ICapability<SearchEverything, SearchResults>
{
    private readonly QueryStore _queries;

    /// <summary>Creates the capability.</summary>
    /// <param name="queries">Runs the search.</param>
    /// <exception cref="ArgumentNullException"><paramref name="queries"/> is null.</exception>
    public SearchCrm(QueryStore queries)
    {
        ArgumentNullException.ThrowIfNull(queries);

        _queries = queries;
    }

    /// <inheritdoc />
    public async ValueTask<Result<SearchResults>> ExecuteAsync(
        SearchEverything input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (string.IsNullOrWhiteSpace(input.Phrase))
        {
            return Result.Fail<SearchResults>(QueryErrors.PhraseIsEmpty());
        }

        if (input.Limit is < 1 or > QueryLimits.Max)
        {
            return Result.Fail<SearchResults>(QueryErrors.LimitIsOutOfRange(input.Limit));
        }

        var hits = await _queries
            .SearchAsync(ctx.TenantId, input.Phrase, input.Limit, ct)
            .ConfigureAwait(false);

        return Result.Ok(new SearchResults(hits));
    }
}
