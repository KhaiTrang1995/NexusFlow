namespace FlowX.Conformance.InMemory;

/// <summary>
/// <see cref="PublisherConformance"/>'s harness over <see cref="RecordingEventPublisher"/>.
/// </summary>
/// <remarks>
/// The whole broker is a list, so reading one key back is a filter over it and "unreachable" is
/// a publisher with a <see cref="RecordingEventPublisher.Refusal"/> set. That is as close to the
/// real thing as a double gets, and the distance between it and
/// <c>RedisStreamBrokerUnderTest</c> is precisely what
/// <see href="../../../docs/adr/ADR-0018-outbox-publication-and-ordering.md">ADR-0018</see> means
/// by "no broker integration is proved".
/// </remarks>
public sealed class RecordingBrokerUnderTest : BrokerUnderTest
{
    private readonly RecordingEventPublisher _recorder = new();

    /// <inheritdoc />
    public override IEventPublisher Publisher => _recorder;

    /// <inheritdoc />
    public override ValueTask<IReadOnlyList<DeliveredEvent>> ReadAsync(
        string? partitionKey,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<DeliveredEvent> delivered =
        [
            .. _recorder.Delivered
                .Where(staged => string.Equals(staged.PartitionKey, partitionKey, StringComparison.Ordinal))
                .Select(static staged => new DeliveredEvent(
                    staged.EventId,
                    staged.Type,
                    staged.SchemaVersion,
                    staged.PartitionKey,
                    staged.PayloadJson)),
        ];

        return new ValueTask<IReadOnlyList<DeliveredEvent>>(delivered);
    }

    /// <inheritdoc />
    public override ValueTask<IEventPublisher> UnreachableAsync(CancellationToken cancellationToken) =>
        new((IEventPublisher)new RecordingEventPublisher
        {
            Refusal = new Error(
                "broker.unreachable",
                "No broker answered.",
                ErrorCategory.Unavailable),
        });
}
