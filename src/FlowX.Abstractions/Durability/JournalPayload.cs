using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace FlowX;

/// <summary>
/// A value on its way into the journal, together with the generated
/// <see cref="JsonTypeInfo"/> that serialises it and the contract members that must never
/// be written down.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This type exists to keep redaction implementable.</strong> The journal is a new
/// sink for <c>[Sensitive]</c> values, and it arrives three phases before the fitness
/// function that guards sinks. A payload API taking an already-serialised
/// <c>string</c> or <c>byte[]</c> would settle that question in the wrong direction
/// permanently: by the time a store received the bytes, the member names would be gone and
/// no later work package could put them back. So a store cannot be handed a blob — it is
/// handed this, and the only way out of it is <see cref="ToJson"/>, which redacts.
/// </para>
/// <para>
/// <strong>What a store can and cannot see.</strong> There is no accessor for the value.
/// A store calls <see cref="ToJson"/> and stores what it gets; it has no route to the object
/// graph and therefore no route to serialise the graph itself and skip the redaction. That
/// is deliberate — an opt-in redaction helper beside a public <c>Value</c> property is a
/// control that the first store under deadline pressure walks around.
/// </para>
/// <para>
/// <strong>Serialisation is the generated context, per ADR-0008 and commitment 5 of
/// ADR-0015.</strong> Requiring a <see cref="JsonTypeInfo{T}"/> is what makes that a compile
/// error rather than a convention: there is no overload that reflects over a type, so a
/// contract outside the generated context cannot reach the journal. It is also what keeps
/// the write path NativeAOT- and trim-safe (constraint C2).
/// </para>
/// <para>
/// The member names come from <c>Flow.SensitiveMembers</c>, which the generator already
/// emits onto every flow's partial class. Nothing new has to be discovered to redact a
/// journal row, only remembered.
/// </para>
/// <para>
/// <strong>WP-59 opened a second way to <em>build</em> a payload and deliberately no second
/// way <em>out</em> of one.</strong> The generated payload writer needs a state-bag snapshot,
/// and no contract type describes a <c>Dictionary&lt;Type, object&gt;</c> — so
/// <see cref="OfState"/> composes one from <see cref="JournalMember"/>s. Composition happens
/// <em>inside</em> this type, before <see cref="ToJson"/>, so the composed document goes
/// through the same redaction pass every other payload goes through. There is still exactly
/// one place that decides what a marked member is replaced with, still no accessor for a
/// value, and — since <see cref="Serialize"/> — still nothing that hands an object graph to a
/// plugin.
/// </para>
/// </remarks>
public sealed class JournalPayload
{
    /// <summary>
    /// What a sensitive value is replaced with.
    /// </summary>
    /// <remarks>
    /// A placeholder rather than a removed property, and the same spelling the RFC 7807 sink
    /// uses. An operator reading a journal row during an incident needs to know the field was
    /// recorded and withheld; a key that silently vanishes reads as a field the flow never
    /// received, which is the wrong thing to conclude at three in the morning.
    /// </remarks>
    public const string Redacted = "[redacted]";

    /// <summary>
    /// The version of the payload envelope every persisted document is stamped with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>What this versions, and what it deliberately does not.</strong> It versions the
    /// <em>envelope</em> — the rules this type applies on the way to a store: the redaction
    /// placeholder and the name-matching that produces it, the composition of a state bag from
    /// its members, and the presence of this stamp itself. It does <em>not</em> version the
    /// contract. A row already records the contract's version beside the payload —
    /// <c>flow_instance.flow_version</c> for an input or a state bag,
    /// <c>flow_step.capability_version</c> for a step result, <c>outbox_event.schema_version</c>
    /// for an event body — and stamping a second, invented contract version here is exactly the
    /// fabrication <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0017-manifest-v1-freeze-criteria.md">ADR-0017</a>'s
    /// criterion F2 exists to refuse. A constant is the honest representation of an envelope
    /// version for the same reason a file-format version is a constant: it is a property of the
    /// writer, and the writer is this file.
    /// </para>
    /// <para>
    /// This is the clause of ADR-0008's Decision — "every persisted payload carries
    /// <c>schemaVersion</c>" — that had no producer until WP-59.
    /// </para>
    /// </remarks>
    public const string SchemaVersion = "1.0.0";

    /// <summary>
    /// The reserved member name the stamp is written under.
    /// </summary>
    /// <remarks>
    /// Reserved: a contract declaring a member of this name has it replaced by the stamp
    /// rather than written twice, because a document with the key twice is one an operator
    /// and a store disagree about. Spelled the same whatever naming policy the contract's
    /// context uses: this is a member of the envelope, not of the contract, and a reader
    /// looking for the stamp does not know which policy wrote the row it is holding.
    /// </remarks>
    public const string SchemaVersionMember = "schemaVersion";

    private readonly object? _value;
    private readonly JsonTypeInfo? _typeInfo;
    private readonly IReadOnlyList<JournalMember> _members;
    private readonly IReadOnlyList<string> _sensitiveMembers;

    private JournalPayload(
        object? value,
        JsonTypeInfo? typeInfo,
        IReadOnlyList<JournalMember> members,
        IReadOnlyList<string> sensitiveMembers)
    {
        _value = value;
        _typeInfo = typeInfo;
        _members = members;
        _sensitiveMembers = sensitiveMembers;
    }

    /// <summary>No payload — the column is null.</summary>
    public static JournalPayload Empty { get; } = new(null, null, [], []);

    /// <summary>Whether there is nothing to write.</summary>
    public bool IsEmpty => _typeInfo is null && _members.Count == 0;

    /// <summary>
    /// Wraps a value for the journal.
    /// </summary>
    /// <typeparam name="T">The contract type. Must be in the generated JSON context.</typeparam>
    /// <param name="value">The value to record.</param>
    /// <param name="typeInfo">
    /// The generated metadata for <typeparamref name="T"/>, e.g.
    /// <c>MyJsonContext.Default.PlaceOrder</c>.
    /// </param>
    /// <param name="sensitiveMembers">
    /// The contract members declared <c>[Sensitive]</c> — pass <c>Flow.SensitiveMembers</c>.
    /// Their values never reach the store.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="typeInfo"/> is null.</exception>
    public static JournalPayload Of<T>(
        T value,
        JsonTypeInfo<T> typeInfo,
        IReadOnlyList<string>? sensitiveMembers = null)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);

        return new JournalPayload(value, typeInfo, [], sensitiveMembers ?? []);
    }

    /// <summary>
    /// Wraps a value for the journal, taking its metadata out of a source-generated context.
    /// </summary>
    /// <typeparam name="T">The contract type. Must be declared by <paramref name="context"/>.</typeparam>
    /// <param name="value">The value to record.</param>
    /// <param name="context">
    /// The generated context declaring <typeparamref name="T"/>, e.g. <c>MyJsonContext.Default</c>.
    /// </param>
    /// <param name="sensitiveMembers">
    /// The contract members declared <c>[Sensitive]</c> — pass <c>Flow.SensitiveMembers</c>.
    /// Their values never reach the store.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The context does not declare <typeparamref name="T"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <strong>Still no reflection.</strong> <c>GetTypeInfo</c> on a source-generated context
    /// is a switch over the types its <c>[JsonSerializable]</c> attributes named, so this is
    /// a lookup rather than a discovery and the write path stays trim- and NativeAOT-safe
    /// (constraint C2). <c>FlowX.Http</c>'s <c>MapFlow</c> resolves a request body the same
    /// way and for the same reason.
    /// </para>
    /// <para>
    /// <strong>Why the overload exists.</strong> Generated code can name a context <em>type</em>
    /// — the compiler reads it off the author's <c>[JsonSerializable]</c> attributes — but it
    /// cannot name the property System.Text.Json's own generator produces for each contract,
    /// because one source generator does not see another's output. Naming the property by
    /// convention would break on the first nested or renamed type.
    /// </para>
    /// <para>
    /// The throw is not a fallback to reflection. A context that does not declare the
    /// contract cannot serialise it at all, and quietly writing nothing would put an empty
    /// event on a broker.
    /// </para>
    /// </remarks>
    public static JournalPayload Of<T>(
        T value,
        JsonSerializerContext context,
        IReadOnlyList<string>? sensitiveMembers = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.GetTypeInfo(typeof(T)) is not JsonTypeInfo<T> typeInfo)
        {
            throw new InvalidOperationException(
                $"'{context.GetType().Name}' does not declare [JsonSerializable(typeof({typeof(T).Name}))], " +
                "so it cannot serialise this payload. Add the attribute to the context, or " +
                "pass one that has it.");
        }

        return new JournalPayload(value, typeInfo, [], sensitiveMembers ?? []);
    }

    /// <summary>
    /// Composes a payload out of several contracts, each with its own generated metadata.
    /// </summary>
    /// <param name="members">
    /// The values to record, each named by the key the document holds it under. An empty list
    /// is <see cref="Empty"/>.
    /// </param>
    /// <param name="sensitiveMembers">
    /// The contract members declared <c>[Sensitive]</c> — pass <c>Flow.SensitiveMembers</c>.
    /// Matched against the composed document at every depth, exactly as for a single contract.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="members"/> is null.</exception>
    /// <exception cref="ArgumentException">A member carries no metadata.</exception>
    /// <remarks>
    /// <para>
    /// <strong>What this exists for: the state-bag snapshot, and nothing else so far.</strong>
    /// The engine's bag is a <c>Dictionary&lt;Type, object&gt;</c> whose membership the flow's
    /// shape decides, so there is no contract type for it to be an <c>Of&lt;T&gt;</c> of, and
    /// no generator can produce one — FlowX's generator could emit a snapshot record, but
    /// <c>System.Text.Json</c>'s generator would never see it, because one source generator
    /// does not see another's output.
    /// </para>
    /// <para>
    /// <strong>The composition is here rather than in the generated writer on purpose.</strong>
    /// A generated writer that assembled the document itself would be a second exit from this
    /// type and would need its own copy of the redaction pass — the failure mode ADR-0015's
    /// commitment 5 and this type's own remarks are written against. Handing in named values
    /// and letting <see cref="ToJson"/> compose them keeps one exit, one redaction and one
    /// stamp.
    /// </para>
    /// </remarks>
    public static JournalPayload OfState(
        IReadOnlyList<JournalMember> members,
        IReadOnlyList<string>? sensitiveMembers = null)
    {
        ArgumentNullException.ThrowIfNull(members);

        for (var i = 0; i < members.Count; i++)
        {
            if (members[i].IsEmpty)
            {
                throw new ArgumentException(
                    "A state-bag member carries no generated metadata, so it cannot be " +
                    "written without reflection. Build every member with JournalMember.Of.",
                    nameof(members));
            }
        }

        return members.Count == 0
            ? Empty
            : new JournalPayload(null, null, members, sensitiveMembers ?? []);
    }

    /// <summary>
    /// Serialises the value as the store should persist it, with every declared sensitive
    /// member replaced by <see cref="Redacted"/> and the <see cref="SchemaVersion"/> stamp
    /// written on.
    /// </summary>
    /// <returns>The JSON to store, or <c>null</c> when there is no payload.</returns>
    /// <remarks>
    /// <para>
    /// Matching is by property name, case-insensitively, at every depth. Case-insensitively
    /// because the wire form is camelCase and the declared member is PascalCase, so an exact
    /// match would let through precisely the realistic spelling. At every depth because a
    /// contract that nests a type carrying a marked member is not less sensitive for being
    /// one level down.
    /// </para>
    /// <para>
    /// Redaction is applied after serialisation rather than by omitting the property, so the
    /// stored document keeps the shape the schema describes and stays deserialisable by the
    /// same generated context for the whole retention window.
    /// </para>
    /// <para>
    /// <strong>The stamp rides the same pass, and does not need a second one.</strong>
    /// <see cref="SchemaVersionMember"/> is written as the document's first member, so
    /// ADR-0008's "every persisted payload carries <c>schemaVersion</c>" costs one property
    /// rather than one more walk of the tree. A document whose root is not an object carries
    /// no stamp: a stamp is a member, and a scalar has none. ADR-0008 makes contracts
    /// immutable records, so that case is a payload that is not a contract, and it is left
    /// exactly as the context wrote it rather than wrapped in an envelope no reader expects.
    /// </para>
    /// <para>
    /// Deserialising the document back through the same generated context still works:
    /// <c>System.Text.Json</c> ignores an unmapped member by default, which is the same
    /// tolerance ADR-0008's additive-only evolution rules already rely on.
    /// </para>
    /// </remarks>
    public string? ToJson()
    {
        if (IsEmpty)
        {
            return null;
        }

        using var document = JsonDocument.Parse(Compose());
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteStamped(document.RootElement, writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Serialises the payload as <see cref="ToJson"/> does, but only when the redaction pass had
    /// nothing to replace.
    /// </summary>
    /// <param name="json">
    /// The document — byte for byte what <see cref="ToJson"/> returns — or <c>null</c> when this
    /// payload cannot be reproduced faithfully.
    /// </param>
    /// <returns>
    /// <c>true</c> when the document is the whole of what the value held. <c>false</c> when a
    /// declared sensitive member was replaced, or when there is no payload at all.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>This is narrower than <see cref="ToJson"/> and is not a second way out of this
    /// type.</strong> Every byte it emits, <see cref="ToJson"/> also emits; there is still no
    /// accessor for the value, still one redaction pass, and still one place that decides what a
    /// marked member is replaced with. What it adds is the answer to the one question only this
    /// type can answer: <em>did writing this lose anything?</em>
    /// </para>
    /// <para>
    /// <strong>It exists because a replay is not a journal row.</strong> A journal accepts the
    /// loss — <see cref="JournalState"/> says so, and the alternative is stranding an instance
    /// whose effects already happened. A stage-3 idempotency replay has a strictly better option
    /// available at the moment of the choice, which is to dispatch the capability again, so
    /// returning <see cref="Redacted"/> to a caller as if it were the value would be choosing a
    /// fabricated answer over a second call. See
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0038-a-recorded-result-is-replayed-only-when-recording-lost-nothing.md">ADR-0038</a>.
    /// </para>
    /// <para>
    /// An empty payload answers <c>false</c> rather than yielding an empty document, for the
    /// same reason: a caller answered from a record that describes nothing has been answered
    /// with nothing, and every step after the frontier would bind values no step produced.
    /// </para>
    /// </remarks>
    public bool TryToReplayableJson([NotNullWhen(true)] out string? json)
    {
        json = null;

        if (IsEmpty)
        {
            return false;
        }

        // The declared set is what the pass matches against, so an empty one cannot have
        // replaced anything and the walk below is skipped entirely. That is the common case:
        // most flows mark nothing.
        var document = ToJson();

        if (document is null)
        {
            return false;
        }

        if (_sensitiveMembers.Count > 0 && Redacts(document))
        {
            return false;
        }

        json = document;

        return true;
    }

    /// <summary>Whether the written document carries the placeholder under a declared member.</summary>
    /// <remarks>
    /// Read off the written document rather than tracked during the pass, so it answers the
    /// question that actually matters — "is the stored text different from the value" — rather
    /// than "did the writer take the replacing branch". The two differ in one case and it is
    /// the honest one: a contract whose marked member genuinely held the string
    /// <see cref="Redacted"/> is reproduced exactly, and reporting it as lossy would refuse a
    /// replay that would have been correct. That is the conservative direction anyway.
    /// </remarks>
    private bool Redacts(string document)
    {
        using var parsed = JsonDocument.Parse(document);

        return CarriesPlaceholder(parsed.RootElement);
    }

    private bool CarriesPlaceholder(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (IsSensitive(property.Name) &&
                        property.Value.ValueKind == JsonValueKind.String &&
                        string.Equals(property.Value.GetString(), Redacted, StringComparison.Ordinal))
                    {
                        return true;
                    }

                    if (CarriesPlaceholder(property.Value))
                    {
                        return true;
                    }
                }

                return false;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (CarriesPlaceholder(item))
                    {
                        return true;
                    }
                }

                return false;

            default:
                return false;
        }
    }

    /// <summary>
    /// Serialises the payload through a plugged serialiser
    /// (<see cref="IPayloadSerializer"/>).
    /// </summary>
    /// <param name="serializer">The serialiser the store persists through.</param>
    /// <returns>What the store writes, or <c>null</c> when there is no payload.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="serializer"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// The plugin seam ADR-0008 named and nothing implemented until WP-59. What the serialiser
    /// receives is the <em>redacted, stamped</em> document — never the value, and never its
    /// metadata — so a binary plugin cannot become a second exit for a <c>[Sensitive]</c>
    /// member. That is the same structural argument that keeps a store out of the object
    /// graph, applied one layer further out.
    /// </para>
    /// <para>
    /// <see cref="ToJson"/> is the JSON case of this method and skips the extra parse, because
    /// the document is already JSON text by the time a serialiser could be asked for it.
    /// </para>
    /// </remarks>
    public string? Serialize(IPayloadSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(serializer);

        if (ToJson() is not { } json)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);

        return serializer.Serialize(document.RootElement);
    }

    /// <summary>The document as the generated metadata writes it, before the envelope pass.</summary>
    private string Compose()
    {
        if (_typeInfo is not null)
        {
            return JsonSerializer.Serialize(_value, _typeInfo);
        }

        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            for (var i = 0; i < _members.Count; i++)
            {
                _members[i].WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Writes the stamp, then the redacted document, in one pass.</summary>
    private void WriteStamped(JsonElement root, Utf8JsonWriter writer)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            WriteRedacted(root, writer);
            return;
        }

        writer.WriteStartObject();
        writer.WriteString(SchemaVersionMember, SchemaVersion);

        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, SchemaVersionMember, StringComparison.Ordinal))
            {
                // Reserved. The stamp has already been written under this name, and a
                // document carrying the key twice is one a store and an operator read
                // differently.
                continue;
            }

            if (IsSensitive(property.Name))
            {
                writer.WriteString(property.Name, Redacted);
            }
            else
            {
                writer.WritePropertyName(property.Name);
                WriteRedacted(property.Value, writer);
            }
        }

        writer.WriteEndObject();
    }

    private void WriteRedacted(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();

                foreach (var property in element.EnumerateObject())
                {
                    if (IsSensitive(property.Name))
                    {
                        writer.WriteString(property.Name, Redacted);
                    }
                    else
                    {
                        writer.WritePropertyName(property.Name);
                        WriteRedacted(property.Value, writer);
                    }
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();

                foreach (var item in element.EnumerateArray())
                {
                    WriteRedacted(item, writer);
                }

                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private bool IsSensitive(string name)
    {
        for (var i = 0; i < _sensitiveMembers.Count; i++)
        {
            if (string.Equals(_sensitiveMembers[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
