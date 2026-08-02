namespace FlowX;

/// <summary>
/// What a broker hands the flow it starts: the message, and the identity a redelivery is
/// recognised by.
/// </summary>
/// <param name="EventId">
/// The event's identity, assigned by the store that staged it (<c>OutboxRecord.EventId</c>) and
/// carried on the wire. <strong>This is the term a redelivery is folded on</strong>
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0035-a-delivery-names-the-instance-it-starts.md">ADR-0035</a>):
/// the same message arriving twice carries the same value here, derives the same instance id,
/// and is refused by the journal's primary key rather than executed a second time.
/// </param>
/// <param name="Topic">The subscription's declared topic, as the manifest published it.</param>
/// <param name="Type">The event type, e.g. <c>order.placed</c>.</param>
/// <param name="SchemaVersion">The event contract's semantic version, as staged.</param>
/// <param name="PartitionKey">
/// The key the event was ordered by, or null for one staged without one. Per-key order is what
/// consumption offers and the only order it offers
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0037-the-consumer-offers-per-key-order.md">ADR-0037</a>).
/// </param>
/// <param name="Payload">
/// The event body exactly as the store held it, or null for a contract with no body. Deliberately
/// a string and not a deserialised object — see the remarks.
/// </param>
/// <param name="TenantId">
/// Whose event this is, as the publishing side wrote it onto the delivery, or null when the
/// producing deployment did not isolate.
/// </param>
/// <remarks>
/// <para>
/// <strong>A bus-triggered flow must take this as its input</strong>, and <c>FLOWX1039</c>
/// reports one that does not. It is <see cref="ScheduledFire"/>'s rule one transport over, and it
/// is there for the same reason: letting a subscriber declare any input and starting it with a
/// default would journal an instance whose recorded request is a value nobody sent.
/// </para>
/// <para>
/// <strong><see cref="Payload"/> is the raw JSON, and that is a deliberate refusal to
/// deserialise.</strong> The host would need a <c>JsonTypeInfo</c> for the event contract to turn
/// it into an object, and only generated code can name one — so a host that deserialised would
/// either reflect, which constraint C2 forbids in the repository's only NativeAOT-published
/// assembly, or would need a registry mapping every topic to a contract, which is a second place
/// for the wire format to be declared. Handing the body over verbatim keeps the whole trigger
/// path trim-safe: nothing between the broker and <c>FlowEngine.ExecuteAsync</c> names a type
/// that was not statically referenced. A flow that wants the typed event deserialises it in a
/// capability, where a serialiser context is in scope and the failure is a <c>Result</c>.
/// </para>
/// <para>
/// <strong>It is journalled like any other trigger's body.</strong> <c>flow_instance.input</c>
/// carries it, so a replay reconstructs what was delivered and an operator reading the row can
/// see which message started the instance — including the <see cref="EventId"/> the row's own
/// primary key was derived from.
/// </para>
/// <para>
/// <strong><see cref="TenantId"/> is a broker field this platform's own publisher wrote, and
/// that is the whole of why it may be believed.</strong> <c>docs/16 §3</c> allows a bus tenant
/// from "a message header set by a FlowX producer" and forbids one from "untrusted payload
/// fields" — so it travels beside the body rather than in it, is written from
/// <c>OutboxRecord.TenantId</c> at publication, and is read back by the consumer plugin. A
/// producer outside this platform that sets the field is a producer the deployment chose to
/// subscribe to, which is the same trust it already extends to every other field on the entry.
/// </para>
/// </remarks>
public sealed record BusMessage(
    Guid EventId,
    string Topic,
    string Type,
    string SchemaVersion,
    string? PartitionKey,
    string? Payload,
    string? TenantId = null);
