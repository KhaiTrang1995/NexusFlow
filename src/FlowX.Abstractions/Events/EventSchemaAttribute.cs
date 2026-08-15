namespace FlowX;

/// <summary>
/// Declares the semantic version of an event contract. Absent, the version is
/// <c>1.0.0</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>On the contract type, because the schema is the type.</strong> An event's
/// version describes the shape a subscriber deserialises, and that shape changes when the
/// record changes — not when a particular <c>.Emit&lt;T&gt;()</c> call site is edited. Two
/// flows emitting one contract publish one version, which is what makes the manifest's
/// <c>events</c> array a catalogue rather than a list of call sites.
/// </para>
/// <para>
/// <strong>One reading reaches both places the version is written.</strong> The compiler
/// reads this attribute once and it becomes the manifest's <c>event.schemaVersion</c> —
/// what a consumer team reads — and the <c>schema_version</c> column of the outbox row —
/// what arrives beside the body. A manifest promising <c>2.0.0</c> over rows stamped
/// <c>1.0.0</c> is the two-copies defect
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0017-manifest-v1-freeze-criteria.md">ADR-0017</a>
/// F2 refuses.
/// </para>
/// <para>
/// <strong>Absence means <c>1.0.0</c>, and that is a decision rather than a default.</strong>
/// Every event this repository publishes carried that constant before the attribute existed,
/// so an undeclared contract keeps publishing exactly what it published — the manifests of
/// applications that declare nothing are byte-identical across the change. Requiring the
/// attribute instead would have been a breaking change to every build in exchange for a
/// value they were already emitting.
/// </para>
/// <para>
/// The value is SemVer 2.0 and is checked at build time: <c>FLOWX1055</c> refuses anything
/// else, because a version that reaches the manifest unread is a number a subscriber pins
/// against and nothing can compare.
/// </para>
/// </remarks>
/// <param name="version">SemVer 2.0 of the contract, for example <c>2.0.0</c>.</param>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface,
    AllowMultiple = false,
    Inherited = false)]
public sealed class EventSchemaAttribute(string version) : Attribute
{
    /// <summary>SemVer 2.0 of the event contract.</summary>
    public string Version { get; } = version;
}
