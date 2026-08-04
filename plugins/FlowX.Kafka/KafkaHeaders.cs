using System.Text;
using Confluent.Kafka;

namespace FlowX.Kafka;

/// <summary>
/// The wire layout of a FlowX event on Kafka: what the record key is, and which fields are
/// headers.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Kafka has almost no envelope, so almost everything is a header.</strong> A record is a
/// key, a value, a timestamp and a header list — there is no message id, no type and no content
/// type. Only the key has a meaning the broker acts on, and this package gives it the one field
/// whose semantics match: the partition key, because a partition is where Kafka's ordering
/// guarantee lives.
/// </para>
/// <para>
/// <strong>A null payload is a null value, which Kafka has and most brokers do not.</strong> A
/// record with a null value is a tombstone on a compacted topic; on a normal topic it is simply
/// an event with no body, which is what an event whose contract has no members is.
/// </para>
/// </remarks>
public static class KafkaHeaders
{
    /// <summary>The header carrying the event's id — the consumer's idempotency key.</summary>
    public const string EventId = "flowx-event-id";

    /// <summary>The header carrying the event type.</summary>
    public const string Type = "flowx-type";

    /// <summary>The header carrying the emitting flow instance's id.</summary>
    public const string InstanceId = "flowx-instance-id";

    /// <summary>The header carrying the event contract's semantic version.</summary>
    public const string SchemaVersion = "flowx-schema-version";

    /// <summary>The header carrying the tenant the event was emitted in.</summary>
    public const string TenantId = "flowx-tenant-id";

    /// <summary>The header a dead-lettered record gains, saying why it was diverted.</summary>
    public const string DeadLetterReason = "flowx-dead-letter-reason";

    /// <summary>Writes a header, or nothing at all when the value is absent.</summary>
    /// <param name="headers">The header list being built.</param>
    /// <param name="name">The header name.</param>
    /// <param name="value">The value, or null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="headers"/> is null.</exception>
    public static void Write(Headers headers, string name, string? value)
    {
        ArgumentNullException.ThrowIfNull(headers);

        if (value is not null)
        {
            headers.Add(name, Encoding.UTF8.GetBytes(value));
        }
    }

    /// <summary>Reads a header as text, or null when it is absent.</summary>
    /// <param name="headers">The delivered header list, which may be null.</param>
    /// <param name="name">The header name.</param>
    /// <returns>The value, or null.</returns>
    /// <remarks>
    /// The last occurrence wins. Kafka permits a header name more than once and nothing in this
    /// package writes one twice, so a repeat came from somewhere else — and taking the last is
    /// what a consumer reading a record another producer touched would expect.
    /// </remarks>
    public static string? Text(Headers? headers, string name)
    {
        if (headers is null)
        {
            return null;
        }

        string? found = null;

        foreach (var header in headers)
        {
            if (string.Equals(header.Key, name, StringComparison.Ordinal))
            {
                found = Encoding.UTF8.GetString(header.GetValueBytes());
            }
        }

        return found;
    }
}
