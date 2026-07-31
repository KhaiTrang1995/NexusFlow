using System.Buffers;
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

    private readonly object? _value;
    private readonly JsonTypeInfo? _typeInfo;
    private readonly IReadOnlyList<string> _sensitiveMembers;

    private JournalPayload(object? value, JsonTypeInfo? typeInfo, IReadOnlyList<string> sensitiveMembers)
    {
        _value = value;
        _typeInfo = typeInfo;
        _sensitiveMembers = sensitiveMembers;
    }

    /// <summary>No payload — the column is null.</summary>
    public static JournalPayload Empty { get; } = new(null, null, []);

    /// <summary>Whether there is nothing to write.</summary>
    public bool IsEmpty => _typeInfo is null;

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

        return new JournalPayload(value, typeInfo, sensitiveMembers ?? []);
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

        return new JournalPayload(value, typeInfo, sensitiveMembers ?? []);
    }

    /// <summary>
    /// Serialises the value as the store should persist it, with every declared sensitive
    /// member replaced by <see cref="Redacted"/>.
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
    /// </remarks>
    public string? ToJson()
    {
        if (_typeInfo is null)
        {
            return null;
        }

        var json = JsonSerializer.Serialize(_value, _typeInfo);

        if (_sensitiveMembers.Count == 0)
        {
            return json;
        }

        using var document = JsonDocument.Parse(json);
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteRedacted(document.RootElement, writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
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
