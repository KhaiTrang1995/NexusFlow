using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace FlowX;

/// <summary>
/// One entry of a composed payload: a name, a value, and the generated
/// <see cref="JsonTypeInfo"/> that writes it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the state bag needs this and a single <c>JsonTypeInfo</c> will not do.</strong>
/// The engine's bag is a <c>Dictionary&lt;Type, object&gt;</c> whose membership is decided by
/// the flow's shape, so no contract type describes it and no generator can emit one: FlowX's
/// generator could write a snapshot record, but <c>System.Text.Json</c>'s generator would
/// never see it — one source generator does not see another's output. So the snapshot is
/// composed from the members' own generated metadata, which the flow's own code can name.
/// </para>
/// <para>
/// <strong>It carries no accessor for the value, for the same reason
/// <see cref="JournalPayload"/> carries none.</strong> A member is something you hand to
/// <see cref="JournalPayload.OfState"/> and cannot read back. Composing the document is
/// <c>JournalPayload</c>'s work and redaction happens on the composed document, so there is
/// no arrangement in which a caller assembles the bag itself and skips the pass.
/// </para>
/// </remarks>
public readonly struct JournalMember : IEquatable<JournalMember>
{
    private readonly string? _name;
    private readonly object? _value;
    private readonly JsonTypeInfo? _typeInfo;

    private JournalMember(string name, object? value, JsonTypeInfo typeInfo)
    {
        _name = name;
        _value = value;
        _typeInfo = typeInfo;
    }

    /// <summary>The member's name in the composed document.</summary>
    /// <remarks>
    /// The contract's simple type name, as the generator writes it — the same key a resumed
    /// flow reads to put the value back. Empty for a <c>default</c> member, which
    /// <see cref="JournalPayload.OfState"/> refuses.
    /// </remarks>
    public string Name => _name ?? string.Empty;

    /// <summary>Whether this member carries nothing.</summary>
    public bool IsEmpty => _typeInfo is null;

    /// <summary>Names a value for a composed payload.</summary>
    /// <typeparam name="T">The contract type. Must be in the generated JSON context.</typeparam>
    /// <param name="name">The key the composed document holds it under.</param>
    /// <param name="value">The value to record.</param>
    /// <param name="typeInfo">
    /// The generated metadata for <typeparamref name="T"/>, e.g.
    /// <c>MyJsonContext.Default.ValidatedTransfer</c>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="name"/> or <paramref name="typeInfo"/> is null.
    /// </exception>
    /// <remarks>
    /// Requiring the metadata is what makes membership of the generated context a compile-time
    /// fact on this path too, exactly as <c>JournalPayload.Of</c> does for a single contract.
    /// <c>FLOWX1006</c> is the diagnostic that reports the same requirement before the build
    /// reaches a call site that cannot satisfy it.
    /// </remarks>
    public static JournalMember Of<T>(string name, T value, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(typeInfo);

        return new JournalMember(name, value, typeInfo);
    }

    /// <summary>
    /// Names a value for a composed payload, taking its metadata out of a generated context.
    /// </summary>
    /// <typeparam name="T">The contract type. Must be declared by <paramref name="context"/>.</typeparam>
    /// <param name="name">The key the composed document holds it under.</param>
    /// <param name="value">The value to record.</param>
    /// <param name="context">
    /// The generated context declaring <typeparamref name="T"/>, e.g. <c>MyJsonContext.Default</c>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="name"/> or <paramref name="context"/> is null.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The context does not declare <typeparamref name="T"/>.
    /// </exception>
    /// <remarks>
    /// <strong>The overload the generator actually calls, and it exists for a reason worth
    /// keeping.</strong> Generated code can name a context <em>type</em> — the compiler reads
    /// it off the author's <c>[JsonSerializable]</c> attributes — but it cannot name the
    /// property <c>System.Text.Json</c>'s own generator produces for each contract, because
    /// one source generator does not see another's output. <c>JournalPayload.Of</c> carries
    /// the same pair of overloads for the same reason.
    /// <para>
    /// <c>GetTypeInfo</c> on a source-generated context is a switch over the types its
    /// attributes named, so this is a lookup and not a discovery: the write path stays trim-
    /// and NativeAOT-safe (constraint C2).
    /// </para>
    /// </remarks>
    public static JournalMember Of<T>(string name, T value, JsonSerializerContext context)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(context);

        if (context.GetTypeInfo(typeof(T)) is not JsonTypeInfo<T> typeInfo)
        {
            throw new InvalidOperationException(
                $"'{context.GetType().Name}' does not declare [JsonSerializable(typeof({typeof(T).Name}))], " +
                "so it cannot write this state-bag member. Add the attribute to the context.");
        }

        return new JournalMember(name, value, typeInfo);
    }

    /// <summary>Writes this member's value into the document being composed.</summary>
    internal void WriteTo(Utf8JsonWriter writer)
    {
        writer.WritePropertyName(Name);
        JsonSerializer.Serialize(writer, _value, _typeInfo!);
    }

    /// <inheritdoc />
    public bool Equals(JournalMember other) =>
        string.Equals(_name, other._name, StringComparison.Ordinal) &&
        ReferenceEquals(_value, other._value) &&
        ReferenceEquals(_typeInfo, other._typeInfo);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is JournalMember other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_name, _value, _typeInfo);

    /// <summary>Compares two members by name, value identity and metadata identity.</summary>
    public static bool operator ==(JournalMember left, JournalMember right) => left.Equals(right);

    /// <summary>Compares two members by name, value identity and metadata identity.</summary>
    public static bool operator !=(JournalMember left, JournalMember right) => !left.Equals(right);
}
