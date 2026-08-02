using System.Text;
using RabbitMQ.Client;

namespace FlowX.RabbitMq;

/// <summary>
/// The wire layout of a FlowX event on AMQP: which of its fields are AMQP properties, which are
/// headers, and how a header is read back.
/// </summary>
/// <remarks>
/// <para>
/// <strong>An AMQP property is preferred to a header wherever AMQP already has one.</strong>
/// <c>message-id</c> and <c>type</c> are basic properties in the 0-9-1 specification, so an event's
/// id and its type travel there and a consumer written against no FlowX library at all can read
/// both. Only the fields AMQP has no property for become headers, and they carry a prefix so they
/// cannot collide with a broker's own (<c>x-death</c>, <c>x-delivery-count</c>) or with a header
/// the client adds for its own bookkeeping (<c>x-dotnet-pub-seq-no</c>).
/// </para>
/// <para>
/// <strong>A null payload is an absent content type, not an empty body.</strong> Both nullable
/// members of <c>OutboxRecord</c> are legitimately absent — an unkeyed event has no partition key,
/// and an event whose contract has no body has no payload — and the empty string is a different
/// fact from absence in both cases. A header is simply not written when the value is null, and
/// the body's presence is signalled by <see cref="PayloadContentType"/> rather than by its length,
/// because a zero-length body cannot be told from a body that is the empty string.
/// </para>
/// <para>
/// <strong>Header values come back as bytes.</strong> RabbitMQ encodes a string header as an AMQP
/// long string and the client hands it back as <c>byte[]</c> rather than as the <c>string</c> that
/// was written. <see cref="Text"/> is the one place that is decoded, so a caller never has to know.
/// </para>
/// </remarks>
public static class RabbitMqHeaders
{
    /// <summary>The header carrying the emitting flow instance's id.</summary>
    public const string InstanceId = "flowx-instance-id";

    /// <summary>The header carrying the event contract's semantic version.</summary>
    public const string SchemaVersion = "flowx-schema-version";

    /// <summary>The header carrying the key whose order this event belongs to.</summary>
    public const string PartitionKey = "flowx-partition-key";

    /// <summary>The header carrying the tenant the event was emitted in.</summary>
    public const string TenantId = "flowx-tenant-id";

    /// <summary>The header a dead-lettered message gains, saying why it was diverted.</summary>
    public const string DeadLetterReason = "flowx-dead-letter-reason";

    /// <summary>The header a dead-lettered message gains, saying when.</summary>
    public const string DeadLetterAt = "flowx-dead-letter-at";

    /// <summary>
    /// The broker's own count of how many times it has redelivered a message, on a quorum queue.
    /// </summary>
    /// <remarks>
    /// Absent on a first delivery and <c>n</c> on the delivery after the <c>n</c>th, so a
    /// delivery count in <c>IBusConsumer</c>'s sense — "including now", one on a first delivery —
    /// is this value plus one. Written by RabbitMQ, never by this package.
    /// </remarks>
    public const string DeliveryCount = "x-delivery-count";

    /// <summary>The content type a message with a body carries.</summary>
    public const string PayloadContentType = "application/json";

    /// <summary>Reads a header written by <see cref="Write"/>, or null when it is absent.</summary>
    /// <param name="properties">The message's properties.</param>
    /// <param name="name">The header name.</param>
    /// <returns>The value, or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="properties"/> is null.</exception>
    public static string? Text(IReadOnlyBasicProperties properties, string name)
    {
        ArgumentNullException.ThrowIfNull(properties);

        if (properties.Headers is not { } headers ||
            !headers.TryGetValue(name, out var value) ||
            value is null)
        {
            return null;
        }

        return value switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            string text => text,
            _ => value.ToString(),
        };
    }

    /// <summary>Reads a numeric header, or null when it is absent or not a number.</summary>
    /// <param name="properties">The message's properties.</param>
    /// <param name="name">The header name.</param>
    /// <returns>The value, or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="properties"/> is null.</exception>
    /// <remarks>
    /// <c>x-delivery-count</c> arrives as a boxed integer of whichever width the broker chose, so
    /// the conversion is by <see cref="IConvertible"/> rather than by a cast to one type. A value
    /// that is not a number at all returns null and the caller falls back to "delivered once",
    /// which errs towards processing a message rather than dead-lettering it.
    /// </remarks>
    public static long? Number(IReadOnlyBasicProperties properties, string name)
    {
        ArgumentNullException.ThrowIfNull(properties);

        if (properties.Headers is not { } headers ||
            !headers.TryGetValue(name, out var value) ||
            value is null)
        {
            return null;
        }

        return value switch
        {
            byte or sbyte or short or ushort or int or uint or long or ulong =>
                Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    /// <summary>Adds a header, or leaves it out when the value is null.</summary>
    /// <param name="headers">The header table being built.</param>
    /// <param name="name">The header name.</param>
    /// <param name="value">The value, or null to write nothing.</param>
    /// <exception cref="ArgumentNullException"><paramref name="headers"/> is null.</exception>
    /// <remarks>
    /// <strong>Absent and empty are two facts, and this preserves the difference.</strong> A null
    /// value writes no header — an unkeyed event has no partition key, and reading one back would
    /// invent it — while the empty string writes an empty header, because an event staged with the
    /// empty key was staged with a key. <c>RedisStreamEventPublisher</c> draws the same line at
    /// the same place, and the tenant is the one field where the caller draws it differently: an
    /// empty tenant is a tenant no row can carry, so the publisher omits that one itself.
    /// </remarks>
    public static void Write(IDictionary<string, object?> headers, string name, string? value)
    {
        ArgumentNullException.ThrowIfNull(headers);

        if (value is not null)
        {
            headers[name] = value;
        }
    }
}
