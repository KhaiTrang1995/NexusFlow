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

/// <summary>One saved view, as a client needs to offer it.</summary>
/// <param name="Name">What to ask for it by.</param>
/// <param name="Label">What to show a person.</param>
public sealed record DescribedView(string Name, string Label);

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

/// <summary>The custom fields of one built-in entity kind.</summary>
/// <param name="Kind">Which kind.</param>
/// <param name="Fields">What an administrator added to it.</param>
public sealed record DescribedEntity(string Kind, IReadOnlyList<DescribedField> Fields);

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
