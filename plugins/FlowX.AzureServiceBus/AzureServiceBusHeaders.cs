using Azure.Messaging.ServiceBus;

namespace FlowX.AzureServiceBus;

/// <summary>
/// The wire layout of a FlowX event on Service Bus: which of its fields are broker properties,
/// which are application properties, and how one is read back.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A broker property is preferred to an application property wherever Service Bus already
/// has one.</strong> <c>MessageId</c>, <c>Subject</c> and <c>ContentType</c> are first-class, so
/// an event's id, its type and the presence of a body travel there — a consumer written against
/// no FlowX library at all can read all three, and the correlation filter a subscription carries
/// can only match on the first-class ones. Only the fields Service Bus has no property for become
/// application properties, and they carry a prefix so they cannot collide with the broker's own.
/// </para>
/// <para>
/// <strong>A null payload is an absent content type, not an empty body.</strong> Both nullable
/// members of <c>OutboxRecord</c> are legitimately absent — an unkeyed event has no partition key,
/// and an event whose contract has no body has no payload — and the empty string is a different
/// fact from absence in both cases.
/// </para>
/// </remarks>
public static class AzureServiceBusHeaders
{
    /// <summary>The property carrying the emitting flow instance's id.</summary>
    public const string InstanceId = "flowx-instance-id";

    /// <summary>The property carrying the event contract's semantic version.</summary>
    public const string SchemaVersion = "flowx-schema-version";

    /// <summary>The property carrying the key whose order this event belongs to.</summary>
    /// <remarks>
    /// <strong>Carried in addition to <see cref="ServiceBusMessage.PartitionKey"/>, not instead of
    /// it.</strong> A namespace's entities may or may not be partitioned, and on an unpartitioned
    /// one the broker discards the value; the consumer needs the key on every namespace in order
    /// to build a <c>BusPartitionBatch</c>, so the plugin keeps its own copy.
    /// </remarks>
    public const string PartitionKey = "flowx-partition-key";

    /// <summary>The property carrying the tenant the event was emitted in.</summary>
    public const string TenantId = "flowx-tenant-id";

    /// <summary>The content type a message with a body carries.</summary>
    public const string PayloadContentType = "application/json";

    /// <summary>Writes a property, or nothing at all when the value is absent.</summary>
    /// <param name="message">The message being built.</param>
    /// <param name="name">The property name.</param>
    /// <param name="value">The value, or null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    public static void Write(ServiceBusMessage message, string name, string? value)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (value is not null)
        {
            message.ApplicationProperties[name] = value;
        }
    }

    /// <summary>Reads a property as text, or null when it is absent.</summary>
    /// <param name="message">The received message.</param>
    /// <param name="name">The property name.</param>
    /// <returns>The value, or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    public static string? Text(ServiceBusReceivedMessage message, string name)
    {
        ArgumentNullException.ThrowIfNull(message);

        return message.ApplicationProperties.TryGetValue(name, out var value)
            ? value as string
            : null;
    }
}
