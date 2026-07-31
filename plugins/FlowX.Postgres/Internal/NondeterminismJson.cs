using System.Buffers;
using System.Text;
using System.Text.Json;

namespace FlowX.Postgres;

/// <summary>
/// Reads and writes <see cref="NondeterminismCapture"/> as the <c>nondeterministic</c>
/// column.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Hand-written rather than serialised.</strong> A capture is not a user payload —
/// it never passes through <see cref="JournalPayload"/> and never carries a
/// <c>[Sensitive]</c> member — so ADR-0015 commitment 5 does not reach it, and there is no
/// generated context for it to go through. That leaves reflection-based serialisation,
/// which constraint C2 forbids, or an explicit reader and writer. This is the explicit
/// one: no reflection, nothing to trim, and the stored shape is a decision rather than a
/// by-product of how the record happens to be declared today.
/// </para>
/// <para>
/// <strong><c>seed</c> is written only when there is one.</strong> "This run never asked
/// for randomness" and "the seed happened to be zero" are different facts, and a column
/// that flattened the first into the second would replay a run that drew no number as one
/// that did. The property is an <see cref="int"/>? for that reason and the document
/// preserves it, as <c>AStepThatDrewNoRandomnessRecordsNoSeed</c> checks.
/// </para>
/// </remarks>
internal static class NondeterminismJson
{
    private const string UtcNowProperty = "utcNow";
    private const string NewIdsProperty = "newIds";
    private const string SeedProperty = "randomSeed";

    /// <summary>The JSON for a capture, or null when nothing was captured.</summary>
    /// <param name="capture">What the step read from outside itself.</param>
    public static string? ToJson(NondeterminismCapture capture)
    {
        if (capture.IsEmpty)
        {
            return null;
        }

        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            if (capture.UtcNow is { } utcNow)
            {
                writer.WriteString(UtcNowProperty, utcNow);
            }

            if (capture.RandomSeed is { } seed)
            {
                writer.WriteNumber(SeedProperty, seed);
            }

            if (capture.NewIds.Count > 0)
            {
                writer.WriteStartArray(NewIdsProperty);

                for (var i = 0; i < capture.NewIds.Count; i++)
                {
                    writer.WriteStringValue(capture.NewIds[i]);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>The capture a stored document describes.</summary>
    /// <param name="json">The column's value, or null.</param>
    public static NondeterminismCapture FromJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return NondeterminismCapture.None;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        return new NondeterminismCapture
        {
            UtcNow = root.TryGetProperty(UtcNowProperty, out var utcNow)
                ? utcNow.GetDateTimeOffset()
                : null,
            RandomSeed = root.TryGetProperty(SeedProperty, out var seed)
                ? seed.GetInt32()
                : null,
            NewIds = ReadIds(root),
        };
    }

    private static List<Guid> ReadIds(JsonElement root)
    {
        if (!root.TryGetProperty(NewIdsProperty, out var ids))
        {
            return [];
        }

        var read = new List<Guid>(ids.GetArrayLength());

        foreach (var id in ids.EnumerateArray())
        {
            read.Add(id.GetGuid());
        }

        return read;
    }
}
