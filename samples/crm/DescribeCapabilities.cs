using FlowX;

namespace Crm;

/// <summary>
/// Tells a client what this tenant's schema looks like, and what this caller may do with it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The one endpoint a client cannot start without.</strong> Every other route in this
/// sample describes something whose shape is compiled in. This one does not: an administrator
/// invented the objects, the fields, the picklist values and the views at run time, so a mobile
/// or web client has nothing to render until it asks. Hard-coding them in the client would put
/// the schema in two places and make every tenant's build different.
/// </para>
/// <para>
/// <strong>The permissions are resolved for the caller, not reported as rules.</strong> A client
/// that received <c>readPermission: "crm.admin"</c> would have to know which grants its user
/// holds and reimplement the comparison — a second copy of an authorisation rule, in JavaScript,
/// which is where they go wrong. It receives <c>canRead</c> and <c>canWrite</c> instead, computed
/// here from the same scopes the write path checks.
/// </para>
/// <para>
/// <strong><c>crm.read</c>, and it does not leak what it hides.</strong> A field the caller may
/// not read is still described — a client has to know the column exists to say why it is empty —
/// but no value of it is returned by anything, which is the query surface's job.
/// </para>
/// </remarks>
[Capability("crm.custom.describe", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.read",
    Idempotent = true)]
public sealed class DescribeCrmSchema : ICapability<DescribeFor, SchemaDescription>
{
    private readonly CustomSchemaStore _schema;
    private readonly QueryStore _queries;
    private readonly LabelStore _labels;

    /// <summary>Creates the capability.</summary>
    /// <param name="schema">Reads the objects and their fields.</param>
    /// <param name="queries">Reads the saved views.</param>
    /// <param name="labels">Reads what this tenant calls the built-in entities.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public DescribeCrmSchema(CustomSchemaStore schema, QueryStore queries, LabelStore labels)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(labels);

        _schema = schema;
        _queries = queries;
        _labels = labels;
    }

    /// <inheritdoc />
    public async ValueTask<Result<SchemaDescription>> ExecuteAsync(
        DescribeFor input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var held = new HashSet<string>(input.Scopes, StringComparer.Ordinal);

        var objects = await _schema
            .ObjectsAsync(ctx.TenantId, input.Request.Target, ct)
            .ConfigureAwait(false);

        if (input.Request.Target is { } target && objects.Count == 0)
        {
            return Result.Fail<SchemaDescription>(CustomSchemaErrors.ObjectNotFound(target));
        }

        var described = new List<DescribedObject>();

        foreach (var (id, name, label) in objects)
        {
            var fields = await _schema.FieldsForAsync(ctx.TenantId, id, ct).ConfigureAwait(false);
            var views = await _queries.ViewsForAsync(ctx.TenantId, id, ct).ConfigureAwait(false);

            described.Add(new DescribedObject(id, name, label, Describe(fields, held), views));
        }

        // The built-in kinds are described whether or not anything was added to them, because a
        // client rendering a lead form needs to know there are no custom fields as much as it
        // needs to know there are three. An absent key and an empty list are the same fact only
        // if somebody remembers they are.
        var entities = new List<DescribedEntity>();

        // One read for every label this tenant has set. A read per entity would be four round
        // trips to build one screen, and the whole table is a handful of rows.
        var labels = await _labels.LabelsAsync(ctx.TenantId, ct).ConfigureAwait(false);

        foreach (var kind in new[]
                 {
                     EntityKind.Lead, EntityKind.Account, EntityKind.Contact, EntityKind.Opportunity,
                 })
        {
            var fields = await _schema.FieldsForAsync(ctx.TenantId, kind, ct).ConfigureAwait(false);
            var name = kind.ToString();

            entities.Add(new DescribedEntity(
                name,
                Called(labels, name, LabelLimits.TheEntityItself, name),
                [
                    .. EntityColumns.Of(kind).Select(column => new DescribedColumn(
                        column, Called(labels, name, column, column))),
                ],
                Describe(fields, held)));
        }

        return Result.Ok(new SchemaDescription(described, entities, CrmMigrator.TargetVersion));
    }

    private static List<DescribedField> Describe(
        IReadOnlyDictionary<string, CustomFieldRow> fields,
        HashSet<string> held) =>
    [
        .. fields.Values
            .OrderBy(static field => field.Name, StringComparer.Ordinal)
            .Select(field => new DescribedField(
                field.Name,

                // The label an administrator typed, which has been stored since 0005 and read back
                // by nothing: describe returned the identifier twice, so every client drew
                // `floor_area` where somebody had written "Floor area".
                field.Label,
                field.Type.ToString(),
                field.IsRequired,
                field.IsComputed,
                Allows(field.ReadPermission, held),

                // A computed field is writable by nobody, whatever grants the caller holds. Said
                // here as well as refused at the write, because a form that offers to edit a
                // roll-up is a form whose next screen contradicts it.
                !field.IsComputed && Allows(field.RequiredPermission, held),
                field.Options ?? [],
                field.References)),
    ];

    private static string Called(
        IReadOnlyDictionary<(string Kind, string Field), string> labels,
        string kind,
        string field,
        string otherwise) =>
        labels.TryGetValue((kind, field), out var label) ? label : otherwise;

    private static bool Allows(string? permission, HashSet<string> held) =>
        permission is not { Length: > 0 } || held.Contains(permission);
}
