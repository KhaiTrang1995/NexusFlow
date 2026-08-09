using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace FlowX.Http;

/// <summary>
/// The schemas an OpenAPI document names, derived from the JSON source generator's metadata.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The shapes come from metadata, not from reflecting over the contract.</strong>
/// <see cref="JsonTypeInfo.Properties"/> is what the source generator emitted, so once a
/// <see cref="Type"/> is in hand the description costs no reflection over its members and survives
/// trimming.
/// </para>
/// <para>
/// <strong>Getting that <see cref="Type"/> from the manifest is the part that does not.</strong>
/// The manifest spells a contract as a namespace-qualified name, and turning a name back into a
/// type is an assembly search the trimmer cannot follow — so the convenient path is annotated
/// <see cref="RequiresUnreferencedCodeAttribute"/> and warns at the caller, and an application
/// that trims passes its contract types in explicitly instead. Saying which half is safe beats
/// claiming both are.
/// </para>
/// <para>
/// <strong>A type the context does not carry is described as opaque rather than invented.</strong>
/// Every contract of a flow is required to be in the context — <c>FLOWX1006</c> is the diagnostic
/// — so this is the case of a contract somebody excluded deliberately. Guessing a shape for it
/// would produce a document that is confidently wrong, which is worse for a client generator than
/// one that is honestly incomplete.
/// </para>
/// </remarks>
internal sealed class SchemaSet
{
    private readonly IJsonTypeInfoResolver? _resolver;
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web);
    private readonly SortedDictionary<string, Type?> _named = new(StringComparer.Ordinal);

    private readonly IReadOnlyDictionary<string, Type> _contracts;

    /// <summary>Builds the set over an application's contract metadata.</summary>
    /// <param name="resolver">The generated context, or null when there is none.</param>
    /// <param name="contracts">
    /// The contract types by the name the manifest spells them, for an application that trims.
    /// Empty falls back to searching the loaded assemblies.
    /// </param>
    public SchemaSet(IJsonTypeInfoResolver? resolver, IReadOnlyDictionary<string, Type>? contracts)
    {
        _resolver = resolver;
        _contracts = contracts ?? new Dictionary<string, Type>(StringComparer.Ordinal);

        if (resolver is not null)
        {
            _options.TypeInfoResolver = resolver;
        }
    }

    /// <summary>Names a contract, and remembers to describe it.</summary>
    /// <param name="clrType">The contract's type, as the manifest spells it.</param>
    /// <returns>The JSON pointer an operation refers to it by.</returns>
    public string Reference(string clrType)
    {
        var name = Short(clrType);

        if (!_named.ContainsKey(name))
        {
            _named[name] = Resolve(clrType);
        }

        return "#/components/schemas/" + name;
    }

    /// <summary>Writes every schema this document names.</summary>
    /// <param name="writer">Where to write.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is null.</exception>
    public void WriteAll(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        var written = new HashSet<string>(StringComparer.Ordinal);

        // BY NAME, NOT BY COUNT. Describing one type can name another — a record with a nested
        // record — and this walks the collection while that happens. `_named` is sorted, so a
        // discovery inserts at its sorted position rather than at the end, and `Skip(count)` then
        // skips whatever now occupies the first `count` places instead of what was written. The
        // schemas the insertion pushed past the boundary were emitted a second time and the
        // discovery itself was never emitted at all: on this repository's own document, 36
        // duplicate keys and 36 `$ref`s pointing at nothing. A name is what identifies a schema,
        // so a name is what is remembered.
        while (_named.Where(entry => !written.Contains(entry.Key)).ToList() is { Count: > 0 } batch)
        {
            foreach (var (name, type) in batch)
            {
                written.Add(name);
                writer.WriteStartObject(name);
                Describe(writer, type, name);
                writer.WriteEndObject();
            }
        }
    }

    private void Describe(Utf8JsonWriter writer, Type? type, string name)
    {
        if (type is null || _resolver is null)
        {
            writer.WriteString("type", "object");
            writer.WriteString("x-flowx-contract", name);
            writer.WriteString(
                "description",
                "This contract is not in the application's JsonSerializerContext, so its shape " +
                "is not described rather than guessed.");

            return;
        }

        var info = _resolver.GetTypeInfo(type, _options);

        if (info is null || info.Properties.Count == 0)
        {
            writer.WriteString("type", "object");
            writer.WriteString("x-flowx-contract", type.FullName ?? name);

            return;
        }

        writer.WriteString("type", "object");
        writer.WriteStartObject("properties");

        foreach (var property in info.Properties)
        {
            writer.WriteStartObject(property.Name);
            WriteType(writer, property.PropertyType);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();

        var required = info.Properties
            .Where(static property => property.IsRequired)
            .Select(static property => property.Name)
            .ToList();

        if (required.Count > 0)
        {
            writer.WriteStartArray("required");

            foreach (var property in required)
            {
                writer.WriteStringValue(property);
            }

            writer.WriteEndArray();
        }
    }

    private void WriteType(Utf8JsonWriter writer, Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying == typeof(string) || underlying == typeof(Guid)
            || underlying == typeof(DateTimeOffset) || underlying == typeof(DateTime)
            || underlying == typeof(DateOnly) || underlying == typeof(TimeSpan))
        {
            writer.WriteString("type", "string");

            if (underlying == typeof(Guid))
            {
                writer.WriteString("format", "uuid");
            }
            else if (underlying == typeof(DateTimeOffset) || underlying == typeof(DateTime))
            {
                writer.WriteString("format", "date-time");
            }
            else if (underlying == typeof(DateOnly))
            {
                writer.WriteString("format", "date");
            }

            return;
        }

        if (underlying == typeof(bool))
        {
            writer.WriteString("type", "boolean");

            return;
        }

        if (underlying == typeof(int) || underlying == typeof(long) || underlying == typeof(short))
        {
            writer.WriteString("type", "integer");

            return;
        }

        if (underlying == typeof(decimal) || underlying == typeof(double)
            || underlying == typeof(float))
        {
            writer.WriteString("type", "number");

            return;
        }

        // An enum is a string here because that is how the application serialises one on the wire
        // when its context says so, and a number when it does not — but a document that guessed
        // wrong would make every generated client send the other. The names are the useful half
        // and they are the same either way, so they are listed and the type is left open.
        if (underlying.IsEnum)
        {
            writer.WriteStartArray("enum");

            foreach (var value in Enum.GetNames(underlying))
            {
                writer.WriteStringValue(value);
            }

            writer.WriteEndArray();

            return;
        }

        if (Element(underlying) is { } element)
        {
            writer.WriteString("type", "array");
            writer.WriteStartObject("items");

            // The same recursion as any other position, rather than "scalar or `$ref`". That
            // dichotomy had no arm for a dictionary, so `IReadOnlyList<IReadOnlyDictionary<…>>`
            // — a bulk import's rows — took the `$ref` arm and referred to a schema named after
            // the assembly-qualified spelling of the constructed generic. Recursing gets the
            // `additionalProperties` object the dictionary arm below already writes, and the
            // `$ref` for a contract is what the fall-through at the end of this method does.
            WriteType(writer, element);

            writer.WriteEndObject();

            return;
        }

        if (IsDictionary(underlying))
        {
            writer.WriteString("type", "object");
            writer.WriteStartObject("additionalProperties");
            writer.WriteString("type", "string");
            writer.WriteEndObject();

            return;
        }

        writer.WriteString("$ref", Reference(underlying.FullName ?? underlying.Name));
        _named[Short(underlying.FullName ?? underlying.Name)] = underlying;
    }

    private static bool IsDictionary(Type type) =>
        type.IsGenericType
        && (type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)
            || type.GetGenericTypeDefinition() == typeof(IDictionary<,>)
            || type.GetGenericTypeDefinition() == typeof(Dictionary<,>));

    private static Type? Element(Type type)
    {
        if (type == typeof(string) || IsDictionary(type))
        {
            return null;
        }

        if (type.IsArray)
        {
            return type.GetElementType();
        }

        return type.IsGenericType && typeof(IEnumerable).IsAssignableFrom(type)
            ? type.GetGenericArguments().FirstOrDefault()
            : null;
    }

    /// <summary>
    /// Resolves a manifest type name to a loaded type, or null when nothing carries it.
    /// </summary>
    /// <remarks>
    /// The manifest spells a contract as the compiler saw it — a namespace-qualified name with no
    /// assembly. Searching the loaded assemblies is what turns that back into a type, and finding
    /// nothing is a real answer rather than a failure: the document then describes the contract as
    /// opaque and says why.
    /// </remarks>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification =
            "The search is the fallback for an application that did not pass its contracts in. " +
            "Every public entry point that can reach it is annotated RequiresUnreferencedCode, " +
            "so a trimming application is warned at its own call site; and a type the trimmer " +
            "removed is simply not found, which produces an opaque schema and a description " +
            "saying so rather than a wrong one.")]
    private Type? Resolve(string clrType)
    {
        if (_contracts.TryGetValue(clrType, out var declared))
        {
            return declared;
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetType(clrType, throwOnError: false) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>The last segment of a type name, with nothing a JSON pointer cannot carry.</summary>
    /// <remarks>
    /// The generic-argument list goes first, then the arity tick, and only then the namespace.
    /// Taking the last <c>.</c> straight off an assembly-qualified name found the one inside a
    /// version number: <c>IReadOnlyDictionary`2[[System.String, …, Version=8.0.0.0, …]]</c> became
    /// a schema called <c>0, Culture=neutral, PublicKeyToken=…]]</c>. Nothing reaches this with a
    /// constructed generic any more, but a name that cannot be read is not the failure this should
    /// produce if something does.
    /// </remarks>
    private static string Short(string clrType)
    {
        var name = clrType;

        if (name.IndexOf('[', StringComparison.Ordinal) is >= 0 and var bracket)
        {
            name = name[..bracket];
        }

        if (name.IndexOf('`', StringComparison.Ordinal) is >= 0 and var arity)
        {
            name = name[..arity];
        }

        return name.LastIndexOf('.') is >= 0 and var dot ? name[(dot + 1)..] : name;
    }
}
