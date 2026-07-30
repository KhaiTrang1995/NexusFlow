using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace FlowX.Http;

/// <summary>
/// Writes an RFC 7807 document with <see cref="Utf8JsonWriter"/>, without reflection.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written rather than serialised, for a reason that only became visible when the
/// trim analyzer rejected the obvious version. <c>ProblemDetails.Extensions</c> is an
/// <c>IDictionary&lt;string, object?&gt;</c>, and serialising <c>object</c> values needs
/// runtime type resolution — which is precisely what constraint C2 forbids. Source
/// generation cannot help: the types are not known until an error carries them.
/// </para>
/// <para>
/// So the extension values are written by inspecting a small, closed set of primitive
/// shapes. Anything outside it is written as its invariant string form rather than
/// silently dropped, because an extension that disappears in production and not in a
/// test is worse than one that reads oddly.
/// </para>
/// </remarks>
public static class ProblemDetailsJson
{
    /// <summary>The media type an RFC 7807 response carries.</summary>
    public const string ContentType = "application/problem+json";

    /// <summary>Writes the document.</summary>
    public static void Write(Utf8JsonWriter writer, ProblemDetails problem)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(problem);

        writer.WriteStartObject();

        WriteIfPresent(writer, "type", problem.Type);
        WriteIfPresent(writer, "title", problem.Title);

        if (problem.Status is { } status)
        {
            writer.WriteNumber("status", status);
        }

        WriteIfPresent(writer, "detail", problem.Detail);
        WriteIfPresent(writer, "instance", problem.Instance);

        foreach (var extension in problem.Extensions)
        {
            writer.WritePropertyName(extension.Key);
            WriteValue(writer, extension.Value);
        }

        writer.WriteEndObject();
    }

    /// <summary>Serialises the document to a UTF-8 byte array.</summary>
    public static byte[] ToUtf8(ProblemDetails problem)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, problem);
        }

        return buffer.ToArray();
    }

    private static void WriteIfPresent(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case string s: writer.WriteStringValue(s); break;
            case bool b: writer.WriteBooleanValue(b); break;
            case int i: writer.WriteNumberValue(i); break;
            case long l: writer.WriteNumberValue(l); break;
            case double d: writer.WriteNumberValue(d); break;
            case decimal m: writer.WriteNumberValue(m); break;
            case DateTimeOffset dto: writer.WriteStringValue(dto); break;
            case DateTime dt: writer.WriteStringValue(dt); break;
            case Guid g: writer.WriteStringValue(g); break;

            default:
                // Written, not dropped. An extension that vanishes in production but
                // not in a test is a worse outcome than one that reads oddly.
                writer.WriteStringValue(
                    Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                break;
        }
    }
}
