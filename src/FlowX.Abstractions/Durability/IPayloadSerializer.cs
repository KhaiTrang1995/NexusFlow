using System.Text.Json;

namespace FlowX;

/// <summary>
/// Turns a payload's redacted document into what a store persists.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The seam <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0008-serialization-and-schema.md">ADR-0008</a>
/// needs before a binary serialiser is even expressible.</strong> That record chose
/// source-generated <c>System.Text.Json</c> as the default "with pluggable binary
/// serialisers via <c>IPayloadSerializer</c>", and until WP-59 there was no such interface —
/// so the plugin the decision's Negative consequences point at ("the binary plugin exists
/// for workloads that measure the difference") could not have been written against anything.
/// </para>
/// <para>
/// <strong>It receives the redacted document, never the object graph, and that is the whole
/// design.</strong> A serialiser handed the value and its <c>JsonTypeInfo</c> would be a
/// second exit from <see cref="JournalPayload"/> — the exact control
/// <c>JournalPayload</c>'s own remarks say a store must not be given, and the one an
/// out-of-tree plugin under deadline pressure would walk around. What arrives here has
/// already been through the single redaction pass and already carries the
/// <c>schemaVersion</c> stamp, so a plugin author cannot get redaction wrong: there is
/// nothing left for them to redact.
/// </para>
/// <para>
/// <strong>What that costs, stated rather than glossed.</strong> A binary serialiser
/// transcodes an already-written JSON document instead of writing its own from the object
/// graph, so the ~20 % commit-time saving ADR-0008 quotes is not fully available through this
/// seam — the size saving, which is the larger half of the claim at 2–3×, is. The alternative
/// buys back the rest of the CPU by handing a plugin the values, and this project has decided
/// once already that a redaction control which depends on every implementer remembering it is
/// not a control.
/// </para>
/// </remarks>
public interface IPayloadSerializer
{
    /// <summary>The media type of what <see cref="Serialize"/> returns.</summary>
    /// <remarks>
    /// Recorded rather than assumed, so a store that holds payloads written by two
    /// serialisers over a retention window can tell which is which. The default is
    /// <c>application/json</c>.
    /// </remarks>
    string ContentType { get; }

    /// <summary>Writes the redacted, stamped document as the store should hold it.</summary>
    /// <param name="document">
    /// The payload as it will be persisted — sensitive members already replaced, the
    /// <c>schemaVersion</c> member already present.
    /// </param>
    /// <returns>The text the store writes.</returns>
    /// <remarks>
    /// Text rather than bytes because every journal column that exists today is <c>json</c>
    /// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0016-postgres-journal-adapter.md">ADR-0016</a>,
    /// decision 1). A binary serialiser encodes to a text form the column can hold; a binary
    /// column is a store's decision and no store has taken it, so pretending otherwise here
    /// would be a shape nothing can use.
    /// </remarks>
    string Serialize(in JsonElement document);
}

/// <summary>
/// The default serialiser: the document, as JSON, unchanged.
/// </summary>
/// <remarks>
/// The identity implementation, and deliberately so. <see cref="JournalPayload"/> has already
/// written the document through the generated <c>System.Text.Json</c> context by the time a
/// serialiser sees it, so "serialise as JSON" is "hand back what is already there" — and
/// <c>JournalPayload.ToJson()</c> skips this call rather than paying a parse to reach it.
/// This type exists so the seam has a named default that a store can compare against and a
/// plugin author can read.
/// </remarks>
public sealed class JsonPayloadSerializer : IPayloadSerializer
{
    /// <summary>The shared instance. Stateless, so one is enough.</summary>
    public static JsonPayloadSerializer Default { get; } = new();

    /// <inheritdoc />
    public string ContentType => "application/json";

    /// <inheritdoc />
    public string Serialize(in JsonElement document) => document.GetRawText();
}
