using System.Text;
using FlowX.Conformance;
using RabbitMQ.Client;

namespace FlowX.RabbitMq.Tests;

/// <summary>
/// <see cref="PublisherConformance"/>'s harness over a real RabbitMQ, in its own topology.
/// </summary>
/// <remarks>
/// <para>
/// The suite needs three things and this supplies exactly those: the publisher, a way to read one
/// key's events back in broker order, and a publisher pointed at a broker that is not there.
/// <strong>Nothing in <c>tests/FlowX.Conformance.Tests</c> was changed to accommodate RabbitMQ</strong>
/// — the same finding <c>RedisStreamBrokerUnderTest</c> was commissioned to produce, one transport
/// over, and a stronger one: Redis and RabbitMQ reach ADR-0018's per-key order by mechanisms with
/// nothing in common, and the suite could not tell.
/// </para>
/// <para>
/// <strong>The read is a queue bound to <c>#</c>, declared before the first publish.</strong> An
/// exchange holds nothing, so "what reached the broker" only exists if something was bound when
/// the message arrived — which is also the honest model of this transport: an event published
/// with no binding is dropped, and the harness must not pretend otherwise by asking the exchange
/// afterwards. Messages are drained as they are asked for and accumulated, because the suite asks
/// about two keys in one test and a queue answers a question once.
/// </para>
/// <para>
/// <strong>The unreachable publisher is a closed port, not a stub.</strong> Port 1 is reserved and
/// nothing listens on it, so the first publish fails the way a broker that went away between two
/// passes of the drain fails. A hand-written <see cref="IEventPublisher"/> that returned an
/// <see cref="Error"/> would assert that this test project can construct one.
/// </para>
/// </remarks>
internal sealed class RabbitMqBrokerUnderTest : BrokerUnderTest
{
    private const string CatchAllQueue = ".conformance";

    private readonly RabbitMqConnection _connection;
    private readonly RabbitMqOptions _options;
    private readonly RabbitMqEventPublisher _publisher;
    private readonly List<RabbitMqConnection> _unreachable = [];
    private readonly List<DeliveredEvent> _drained = [];
    private readonly string _queue;
    private IChannel? _channel;

    private RabbitMqBrokerUnderTest(RabbitMqConnection connection, RabbitMqOptions options)
    {
        _connection = connection;
        _options = options;
        _queue = options.QueuePrefix + CatchAllQueue;
        _publisher = new RabbitMqEventPublisher(connection, options);
    }

    /// <inheritdoc />
    public override IEventPublisher Publisher => _publisher;

    /// <summary>Creates a harness over a private topology, or refuses to pretend it did.</summary>
    /// <param name="cancellationToken">Cancels the setup.</param>
    /// <returns>The harness.</returns>
    /// <exception cref="InvalidOperationException">
    /// A RabbitMQ broker was promised by the environment and is not reachable.
    /// </exception>
    public static async ValueTask<RabbitMqBrokerUnderTest> CreateAsync(CancellationToken cancellationToken)
    {
        var connection = await RabbitMqTestBroker.ConnectAsync(cancellationToken);
        var harness = new RabbitMqBrokerUnderTest(connection, RabbitMqTestBroker.IsolatedTopology());

        await harness.BindAsync(cancellationToken);

        return harness;
    }

    /// <inheritdoc />
    public override async ValueTask<IReadOnlyList<DeliveredEvent>> ReadAsync(
        string? partitionKey,
        CancellationToken cancellationToken)
    {
        await DrainAsync(cancellationToken);

        return
        [
            .. _drained.Where(delivered =>
                string.Equals(delivered.PartitionKey, partitionKey, StringComparison.Ordinal)),
        ];
    }

    /// <inheritdoc />
    public override ValueTask<IEventPublisher> UnreachableAsync(CancellationToken cancellationToken)
    {
        // Port 1 is reserved and nothing listens on it. The connection is opened lazily, so this
        // constructor succeeds and the first publish is what meets the closed port — which is the
        // shape a broker that went away mid-deployment has.
        var dead = new RabbitMqConnection("amqp://guest:guest@127.0.0.1:1/", "flowx-tests-dead");

        _unreachable.Add(dead);

        return ValueTask.FromResult<IEventPublisher>(new RabbitMqEventPublisher(dead, _options));
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        foreach (var dead in _unreachable)
        {
            await dead.DisposeAsync();
        }

        _unreachable.Clear();

        if (_channel is not null)
        {
            await _channel.CloseAsync(CancellationToken.None);
            _channel.Dispose();
            _channel = null;
        }

        await _publisher.DisposeAsync();
        await _connection.DisposeAsync();
        await RabbitMqTestBroker.DropAsync(_options, [_queue], CancellationToken.None);

        await base.DisposeAsync();
    }

    /// <summary>Declares the exchange and a queue bound to everything on it.</summary>
    private async ValueTask BindAsync(CancellationToken cancellationToken)
    {
        var open = await _connection.OpenAsync(cancellationToken);

        _channel = await open.CreateChannelAsync(cancellationToken: cancellationToken);

        await _channel.ExchangeDeclareAsync(
            _options.Exchange, ExchangeType.Topic, durable: true, autoDelete: false,
            cancellationToken: cancellationToken);

        await _channel.QueueDeclareAsync(
            _queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" },
            cancellationToken: cancellationToken);

        await _channel.QueueBindAsync(_queue, _options.Exchange, "#", cancellationToken: cancellationToken);
    }

    /// <summary>Takes everything the queue is holding and appends it to what was taken before.</summary>
    /// <remarks>
    /// Acknowledged as it is read, because the harness is the only consumer and a suite that left
    /// messages unacknowledged would answer the same question twice on the second call. The order
    /// is the queue's, which is the order the exchange filled it in, which is the order the
    /// publisher sent — the chain the suite is actually asserting on.
    /// </remarks>
    private async ValueTask DrainAsync(CancellationToken cancellationToken)
    {
        while (await _channel!.BasicGetAsync(_queue, autoAck: true, cancellationToken) is { } got)
        {
            _drained.Add(new DeliveredEvent(
                Guid.TryParse(got.BasicProperties.MessageId, out var id) ? id : Guid.Empty,
                got.BasicProperties.Type ?? string.Empty,
                RabbitMqHeaders.Text(got.BasicProperties, RabbitMqHeaders.SchemaVersion) ?? string.Empty,
                RabbitMqHeaders.Text(got.BasicProperties, RabbitMqHeaders.PartitionKey),
                got.BasicProperties.ContentType is null
                    ? null
                    : Encoding.UTF8.GetString(got.Body.Span)));
        }
    }
}
