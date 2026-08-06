using FlowX;

namespace Crm;

// -------------------------------------------------------------------------------- what is asked

/// <summary>Asks what this tenant's schema looks like.</summary>
/// <param name="Target">One object, or null for every object this tenant has.</param>
/// <remarks>
/// <strong>Why a client cannot work without this.</strong> Every other CRM API in this sample
/// describes something the client already knows the shape of. This one does not: an administrator
/// invented the objects, the fields, the picklist values and the views at run time, so a mobile
/// or web client has nothing to render a form or a list from until it asks. Hard-coding them in
/// the client would put the schema in two places and make every tenant's build different.
/// </remarks>
public sealed record DescribeSchema(Guid? Target);

/// <summary>What the describe capability is given, once the flow has read the caller.</summary>
/// <param name="Request">What was asked for.</param>
/// <param name="Scopes">The grants the caller holds, from their claims and never from the body.</param>
public sealed record DescribeFor(DescribeSchema Request, IReadOnlyList<string> Scopes);

// ------------------------------------------------------------------------------- what comes back

/// <summary>One field, as a client needs to render it.</summary>
/// <param name="Name">What to send and receive it as.</param>
/// <param name="Label">What to show a person.</param>
/// <param name="Type">Which of the six kinds it is.</param>
/// <param name="IsRequired">Whether a record without it is refused.</param>
/// <param name="IsComputed">
/// Whether this build writes it. A client shows it and does not offer to edit it.
/// </param>
/// <param name="CanRead">
/// Whether <em>this caller</em> may see it. False means every value comes back redacted, and a
/// client can say so instead of showing a column of placeholders.
/// </param>
/// <param name="CanWrite">
/// Whether <em>this caller</em> may write it. <strong>The reason this is here rather than left to
/// a 403:</strong> a form that greys out a field somebody may not fill in is a form they can
/// complete, and one that accepts it and fails on submit is a form they retype.
/// </param>
/// <param name="Options">A picklist's allowed values, in the order declared. Empty otherwise.</param>
/// <param name="References">The object a reference points at, or null.</param>
public sealed record DescribedField(
    string Name,
    string Label,
    string Type,
    bool IsRequired,
    bool IsComputed,
    bool CanRead,
    bool CanWrite,
    IReadOnlyList<string> Options,
    Guid? References);

/// <summary>One saved view, as a client needs to offer it <em>and draw it</em>.</summary>
/// <param name="Name">What to ask for it by.</param>
/// <param name="Label">What to show a person.</param>
/// <param name="Kind"><c>List</c>, <c>Kanban</c> or <c>Card</c>.</param>
/// <param name="GroupBy">The lane field, for a board.</param>
/// <param name="Lanes">The lane order, for a board. Empty for whatever order the values arrive in.</param>
/// <param name="WipLimit">
/// What counts as too many cards in a lane, or null. <strong>Reported, never enforced:</strong> a
/// limit that refused a write would turn a layout setting into a business rule, and whoever set it
/// was arranging a screen.
/// </param>
/// <param name="TitleField">A card's first line.</param>
/// <param name="SubtitleField">A card's second line, or null.</param>
/// <param name="Columns">A table's columns in order. Empty means every field the caller may read.</param>
public sealed record DescribedView(
    string Name,
    string Label,
    string Kind,
    string? GroupBy,
    IReadOnlyList<string> Lanes,
    int? WipLimit,
    string? TitleField,
    string? SubtitleField,
    IReadOnlyList<string> Columns);

/// <summary>One object an administrator invented.</summary>
/// <param name="Id">Its id, for a query.</param>
/// <param name="Name">Its identifier.</param>
/// <param name="Label">What to show a person.</param>
/// <param name="Fields">Its fields, in declaration order.</param>
/// <param name="Views">The saved views over it.</param>
public sealed record DescribedObject(
    Guid Id,
    string Name,
    string Label,
    IReadOnlyList<DescribedField> Fields,
    IReadOnlyList<DescribedView> Views);

/// <summary>One built-in entity kind: what this tenant calls it, and what is on it.</summary>
/// <param name="Kind">Which kind. The identifier, which never changes.</param>
/// <param name="Label">
/// What this tenant calls it. Defaults to the kind — a tenant who calls a Lead an "Enquiry" says
/// so here, and every screen follows without a deployment.
/// </param>
/// <param name="Columns">Its built-in columns, with whatever this tenant calls them.</param>
/// <param name="Fields">What an administrator added to it.</param>
public sealed record DescribedEntity(
    string Kind,
    string Label,
    IReadOnlyList<DescribedColumn> Columns,
    IReadOnlyList<DescribedField> Fields);

/// <summary>One built-in column of a built-in entity.</summary>
/// <param name="Name">The column. What a client sends and receives it as.</param>
/// <param name="Label">What this tenant calls it. Defaults to the column name.</param>
public sealed record DescribedColumn(string Name, string Label);

/// <summary>What this tenant's schema looks like to this caller.</summary>
/// <param name="Objects">The custom objects.</param>
/// <param name="Entities">The built-in kinds and what was added to them.</param>
/// <param name="Version">
/// The schema version this build reads and writes. A client caches a description and this is what
/// tells it the cache is stale — which is cheaper than asking for the whole description on every
/// screen and more correct than never asking again.
/// </param>
public sealed record SchemaDescription(
    IReadOnlyList<DescribedObject> Objects,
    IReadOnlyList<DescribedEntity> Entities,
    int Version);
