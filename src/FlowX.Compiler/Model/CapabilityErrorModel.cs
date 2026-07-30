using System;
using System.Collections.Generic;
using System.Linq;

namespace FlowX.Compiler.Model;

/// <summary>
/// One failure a capability can return, reduced to the two facts that are structure
/// rather than data.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Code and category only.</strong> An <c>Error</c> also carries a message and a
/// structured <c>Data</c> bag, and neither belongs in a published artifact: the messages
/// in this codebase interpolate runtime values (<c>"'{sku}' has {available} in stock."</c>)
/// and the data bag exists precisely to carry them. The code is a stable identifier a
/// caller branches on, and the category is the closed set the transport maps to a status
/// code — both are contract, and both are the same in every execution.
/// </para>
/// <para>
/// This is the same line WP-15 drew when it declined to publish a condition's predicate:
/// <c>order.Total &gt; 80</c> is a business threshold, and <c>payment.declined</c> is not.
/// </para>
/// </remarks>
public sealed class CapabilityErrorModel : IEquatable<CapabilityErrorModel>
{
    /// <summary>Creates a model of one declared failure.</summary>
    /// <param name="code">Stable identifier, e.g. <c>inventory.out_of_stock</c>.</param>
    /// <param name="category">One of the six names in <c>ErrorCategory</c>.</param>
    public CapabilityErrorModel(string code, string category)
    {
        Code = code;
        Category = category;
    }

    /// <summary>Stable, greppable error code.</summary>
    public string Code { get; }

    /// <summary>Validation, NotFound, Conflict, Forbidden, Unavailable or Internal.</summary>
    public string Category { get; }

    /// <inheritdoc />
    public bool Equals(CapabilityErrorModel? other) =>
        other is not null
        && string.Equals(Code, other.Code, StringComparison.Ordinal)
        && string.Equals(Category, other.Category, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as CapabilityErrorModel);

    /// <inheritdoc />
    public override int GetHashCode() => Code.GetHashCode();
}

/// <summary>
/// The failures one capability can return, and whether that list is known to be complete.
/// </summary>
/// <remarks>
/// <para>
/// <strong><see cref="IsComplete"/> is the load-bearing field.</strong> The catalogue is
/// read out of the capability's own source, by following every expression of type
/// <c>Error</c> back to the literal code and category that produced it. That works for
/// the documented pattern — a static factory class per domain
/// (<c>docs/07-Capability-Model.md §7</c>) — and it does not work when the trail leaves
/// the compilation or ends at a value only known at run time.
/// </para>
/// <para>
/// A partial catalogue published as if it were whole is worse than none: a consumer
/// generating OpenAPI responses, or an agent deciding which failures it must handle,
/// would act on a list that is missing exactly the cases nobody thought about. So an
/// incomplete catalogue is not published at all, and the manifest's <c>errors</c> array
/// is absent rather than short. An empty <em>present</em> array is then a real statement:
/// this capability was analysed, and it returns no declared error.
/// </para>
/// </remarks>
public sealed class CapabilityErrorCatalogue : IEquatable<CapabilityErrorCatalogue>
{
    /// <summary>Creates a catalogue for one capability.</summary>
    /// <param name="capabilityId">Business identity from <c>[Capability]</c>.</param>
    /// <param name="capabilityVersion">Contract version from <c>[Capability]</c>.</param>
    /// <param name="errors">The failures found, in any order.</param>
    /// <param name="isComplete">
    /// False when at least one failure path could not be reduced to a literal code and
    /// category. The catalogue is then not published.
    /// </param>
    public CapabilityErrorCatalogue(
        string capabilityId,
        string capabilityVersion,
        IReadOnlyList<CapabilityErrorModel> errors,
        bool isComplete)
    {
        CapabilityId = capabilityId;
        CapabilityVersion = capabilityVersion;
        IsComplete = isComplete;
        Errors = errors
            .GroupBy(e => e.Code, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(e => e.Code, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Business identity of the capability.</summary>
    public string CapabilityId { get; }

    /// <summary>Contract version of the capability.</summary>
    public string CapabilityVersion { get; }

    /// <summary>Whether every failure path was resolved. See the type's remarks.</summary>
    public bool IsComplete { get; }

    /// <summary>The failures, deduplicated by code and ordinally sorted.</summary>
    public IReadOnlyList<CapabilityErrorModel> Errors { get; }

    /// <summary>The key the manifest joins on: <c>id@version</c>.</summary>
    public string Key => CapabilityId + "@" + CapabilityVersion;

    /// <inheritdoc />
    public bool Equals(CapabilityErrorCatalogue? other) =>
        other is not null
        && string.Equals(Key, other.Key, StringComparison.Ordinal)
        && IsComplete == other.IsComplete
        && Errors.Count == other.Errors.Count
        && Errors.SequenceEqual(other.Errors);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as CapabilityErrorCatalogue);

    /// <inheritdoc />
    public override int GetHashCode() => Key.GetHashCode();
}
