using System.Security.Claims;
using Ecommerce;
using FlowX;
using FlowX.Hosting;
using FlowX.Postgres;
using FlowX.Redis;
using FlowX.Runtime;
using Npgsql;
using Shouldly;
using StackExchange.Redis;
using Xunit;

namespace Ecommerce.Tests;

/// <summary>
/// One flow's <c>.Emit</c> starts another flow, through a real PostgreSQL outbox and a real
/// Redis broker.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every link in the chain is the real one.</strong> <see cref="ConfirmOrderFlow"/> stages
/// <c>order.placed</c> in its step's own transaction, <c>PostgresOutboxPublisher</c> claims it,
/// <see cref="RedisStreamEventPublisher"/> appends it to its partition's stream,
/// <see cref="RedisStreamBusConsumer"/> reads it back under a consumer group, and
/// <c>FlowBusScan</c> starts <see cref="RepriceOrderFlow"/> — which the sample declares with a
/// <c>[BusTrigger]</c> and nothing else. Nothing between the two flows names either of them.
/// </para>
/// <para>
/// <strong>Both silent failures are asserted, and the second is the reason this file exists.</strong>
/// A chain that never fires and a chain that starts two flows for one event look identical from
/// outside: no exception, no status code, no metric. The second is what an at-least-once broker
/// produces by default. So the assertions are on <em>counts of journal rows</em>, and the
/// redelivery test republishes the same <c>event_id</c> rather than trusting that one will never
/// happen.
/// </para>
/// <para>
/// <strong>Gating.</strong> Both servers are opt-in. With neither variable set every test here
/// skips with a reason; with a variable set and no server, the harness fails rather than skips —
/// a skip there reports an integration that never ran as a green job. That is
/// <c>PostgresTestDatabase</c>'s and <c>RedisTestServer</c>'s discipline, restated here because
/// this project has neither.
/// </para>
/// <para>
/// <strong>Why the infrastructure is here and not in the sample.</strong> <c>samples/ecommerce</c>
/// is the repository's only NativeAOT-published assembly and CI publishes it (constraint C2), so
/// it takes no broker or database package. It declares the two flows, the contracts and the
/// serialiser context; this project supplies Npgsql and StackExchange.Redis and wires them.
/// </para>
/// </remarks>
public sealed class EmitStartsAFlowTests : IAsyncLifetime
{
    private const string PostgresVariable = "FLOWX_POSTGRES_CONNECTION";

    private const string RedisVariable = "FLOWX_REDIS_CONNECTION";

    private readonly List<Fixture> _fixtures = [];

    /// <summary>An emitted event starts the subscribing flow, and the journal says so.</summary>
    [Fact]
    public async Task AnEmittedEventStartsTheSubscribingFlow()
    {
        var fixture = await CreateAsync();
        var ct = TestContext.Current.CancellationToken;

        var confirmed = await fixture.ConfirmAsync("SKU-1", 2);

        confirmed.IsSuccess.ShouldBeTrue();

        var drained = await fixture.Outbox.PublishPendingAsync(ct);

        drained.Published.ShouldBe(1, "the step staged one event and the broker took it");

        var pass = await fixture.BusPassAsync();

        pass.Received.ShouldBe(1);
        pass.Started.ShouldBe(1);

        var rows = await fixture.InstancesAsync();

        rows.Count(static row => row.FlowId == "order.confirm").ShouldBe(1);
        rows.Count(static row => row.FlowId == "order.reprice").ShouldBe(1);
    }

    /// <summary>
    /// The outbox row carries the version the contract declares, not a compiler constant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>ADR-0017 F2's other half, in the one place it can actually be read.</strong>
    /// <c>OrderPlaced</c> declares <c>[EventSchema("2.0.0")]</c>; the manifest's
    /// <c>event.schemaVersion</c> says so and <c>flowx diff</c> keys the event on it. If the
    /// <c>schema_version</c> column said <c>1.0.0</c>, the published contract and the wire
    /// would disagree — a subscriber would be pinning against a promise nothing keeps, and
    /// every gate in the repository would be green. That state existed by construction while
    /// <c>ManifestWriter</c> and <c>FlowEmitter</c> shared a constant instead of a reading.
    /// </para>
    /// <para>
    /// The value is read out of the table rather than off the <c>OutboxWrite</c>, and it
    /// survives one more hop: the same string reaches the broker and comes back on the
    /// delivery, which is what a consumer in another repository sees.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheStagedRowCarriesTheVersionTheContractDeclares()
    {
        var fixture = await CreateAsync();

        (await fixture.ConfirmAsync("SKU-3", 1)).IsSuccess.ShouldBeTrue();

        var staged = await fixture.StagedEventAsync();

        staged.Type.ShouldBe("order.placed");
        staged.SchemaVersion.ShouldBe(
            "2.0.0",
            "OrderPlaced declares [EventSchema(\"2.0.0\")], and the manifest publishes that " +
            "number — a row stamped 1.0.0 would be the two-copies defect ADR-0017 F2 names.");
    }

    /// <summary>
    /// The same event delivered twice starts one flow, and the second delivery is finished with.
    /// </summary>
    /// <remarks>
    /// <strong>The test the whole design exists for, end to end.</strong> The second entry is
    /// published with the same <c>event_id</c> the outbox staged, which is exactly what a crash
    /// between the broker acknowledging and the outbox committing leaves behind — ADR-0018
    /// decision 4 says that window is real and rolls back rather than closing it. One instance is
    /// journalled; both entries are acknowledged, so neither comes back.
    /// </remarks>
    [Fact]
    public async Task ARedeliveredEventStartsNoSecondFlow()
    {
        var fixture = await CreateAsync();
        var ct = TestContext.Current.CancellationToken;

        (await fixture.ConfirmAsync("SKU-2", 1)).IsSuccess.ShouldBeTrue();

        await fixture.Outbox.PublishPendingAsync(ct);

        var first = await fixture.BusPassAsync();

        first.Started.ShouldBe(1);

        var staged = await fixture.StagedEventAsync();

        await fixture.RepublishAsync(staged);

        var second = await fixture.BusPassAsync();

        second.Received.ShouldBe(1, "the broker offered it again, which is what at-least-once means");
        second.Started.ShouldBe(0);
        second.Deduplicated.ShouldBe(1);

        (await fixture.InstancesAsync())
            .Count(static row => row.FlowId == "order.reprice")
            .ShouldBe(1, "one event names one instance, however many times it arrives");
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var fixture in _fixtures)
        {
            await fixture.DisposeAsync();
        }

        _fixtures.Clear();
    }

    /// <summary>
    /// Builds the whole chain, or explains why it did not.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A server was promised by the environment and is not reachable. A failure rather than a
    /// skip on purpose.
    /// </exception>
    private async ValueTask<Fixture> CreateAsync()
    {
        var postgres = Environment.GetEnvironmentVariable(PostgresVariable);
        var redis = Environment.GetEnvironmentVariable(RedisVariable);

        if (string.IsNullOrEmpty(postgres) || string.IsNullOrEmpty(redis))
        {
            Assert.Skip(
                $"This test drives the sample's emitted event through a real outbox and a real " +
                $"broker, so it needs both. Set {PostgresVariable} and {RedisVariable} to run it " +
                $"— for example 'Host=localhost;Port=5432;Database=postgres;Username=postgres' " +
                $"and 'localhost:6379'. Nothing about the bus trigger is verified without them.");
        }

        var fixture = await Fixture.CreateAsync(
            postgres!, redis!, TestContext.Current.CancellationToken);

        _fixtures.Add(fixture);

        return fixture;
    }

    /// <summary>One journal row, as an operator reading the table would see it.</summary>
    /// <param name="FlowId">Which flow.</param>
    /// <param name="InstanceId">Its primary key — minted for the confirming flow, derived from the delivery for the repricing one (ADR-0035).</param>
    /// <param name="State">Where it ended.</param>
    /// <param name="Input">What it was started with, as journalled.</param>
    private sealed record InstanceRow(string FlowId, Guid InstanceId, string State, string? Input);

    /// <summary>One schema, one key space, and the two sample flows wired across them.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly NpgsqlDataSource _dataSource;
        private readonly IConnectionMultiplexer _redis;
        private readonly RedisStreamOptions _streams;
        private readonly string _schema;
        private readonly FlowHost _host;
        private readonly FlowBusScan _bus;
        private readonly ConfirmOrderFlow.Dispatcher _confirm;

        private Fixture(
            NpgsqlDataSource dataSource,
            IConnectionMultiplexer redis,
            RedisStreamOptions streams,
            string schema,
            PostgresFlowJournal journal,
            PostgresLeaseStore leases)
        {
            _dataSource = dataSource;
            _redis = redis;
            _streams = streams;
            _schema = schema;

            var options = new FlowXOptions { ApplicationName = "Ecommerce", NodeName = "test-node" };
            var durability = new FlowDurability(journal, leases);

            _host = new FlowHost(new FlowEngine(SystemClock.Instance), options, durability);
            _confirm = new ConfirmOrderFlow.Dispatcher(new ValidateOrder());

            Outbox = new PostgresOutboxPublisher(
                dataSource, new RedisStreamEventPublisher(redis, streams));

            var subscriptions = new FlowBusCatalog().Add(
                new BusSubscription("order.reprice", "1.0.0", "order.placed", "pricing"),
                RepriceOrderFlow.Plan,
                new RepriceOrderFlow.Dispatcher(new RepriceBasket()));

            _bus = new FlowBusScan(
                _host,
                subscriptions,
                new RedisStreamBusConsumer(redis, streams, "test-node"),
                durability,
                options);
        }

        public PostgresOutboxPublisher Outbox { get; }

        public static async ValueTask<Fixture> CreateAsync(
            string postgres, string redis, CancellationToken ct)
        {
            var schema = "flowx_bus_" + Guid.NewGuid().ToString("n")[..12];

            var builder = new NpgsqlConnectionStringBuilder(postgres)
            {
                SearchPath = schema,
            };

            var dataSource = NpgsqlDataSource.Create(builder.ConnectionString);

            await using (var connection = await dataSource.OpenConnectionAsync(ct))
            {
                await using var create = connection.CreateCommand();

                create.CommandText = $"create schema if not exists {schema};";

                await create.ExecuteNonQueryAsync(ct);
            }

            var journalOptions = new PostgresJournalOptions { Schema = schema };

            await new PostgresMigrator(dataSource, journalOptions).MigrateAsync(ct);

            return new Fixture(
                dataSource,
                await ConnectionMultiplexer.ConnectAsync(redis),
                new RedisStreamOptions { KeyPrefix = "flowx_e2e_" + Guid.NewGuid().ToString("n") },
                schema,
                new PostgresFlowJournal(dataSource),
                new PostgresLeaseStore(dataSource));
        }

        /// <summary>Runs the emitting flow, as a caller with the claims its steps require.</summary>
        public async ValueTask<FlowExecutionResult> ConfirmAsync(string sku, int quantity)
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "tester")], "test"));

            return await _host.RunAsync(
                ConfirmOrderFlow.Plan,
                _confirm,
                new FlowInvocation(
                    Guid.NewGuid().ToString("d"),
                    Guid.NewGuid().ToString("d"),
                    null,
                    DateTimeOffset.UtcNow.AddMinutes(1),
                    principal),
                new PlaceOrder(sku, quantity, "tok_test"),
                TestContext.Current.CancellationToken);
        }

        public ValueTask<BusScanReport> BusPassAsync() =>
            _bus.RunOnceAsync(TestContext.Current.CancellationToken);

        /// <summary>Every instance row this schema holds, which is the assertion's subject.</summary>
        public async ValueTask<IReadOnlyList<InstanceRow>> InstancesAsync()
        {
            var ct = TestContext.Current.CancellationToken;
            var rows = new List<InstanceRow>();

            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var command = connection.CreateCommand();

            command.CommandText =
                $"select flow_id, instance_id, state, input " +
                $"from {_schema}.flow_instance order by created_at;";

            await using var reader = await command.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                rows.Add(new InstanceRow(
                    reader.GetString(0),
                    reader.GetGuid(1),
                    reader.GetString(2),
                    await reader.IsDBNullAsync(3, ct) ? null : reader.GetString(3)));
            }

            return rows;
        }

        /// <summary>The one event the emitting flow staged, as the outbox recorded it.</summary>
        public async ValueTask<OutboxRecord> StagedEventAsync()
        {
            var ct = TestContext.Current.CancellationToken;

            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var command = connection.CreateCommand();

            command.CommandText =
                $"select event_id, instance_id, type, schema_version, partition_key, payload " +
                $"from {_schema}.outbox_event order by staged_seq limit 1;";

            await using var reader = await command.ExecuteReaderAsync(ct);

            (await reader.ReadAsync(ct)).ShouldBeTrue("the step staged an event");

            return new OutboxRecord
            {
                EventId = reader.GetGuid(0),
                InstanceId = reader.GetGuid(1),
                Type = reader.GetString(2),
                SchemaVersion = reader.GetString(3),
                PartitionKey = await reader.IsDBNullAsync(4, ct) ? null : reader.GetString(4),
                PayloadJson = await reader.IsDBNullAsync(5, ct) ? null : reader.GetString(5),
            };
        }

        /// <summary>Publishes one already-published event again, by its own identity.</summary>
        /// <remarks>
        /// Through the real publisher, so the second entry is byte-identical to the first in every
        /// field a consumer reads. Anything less would be asserting that this test can construct a
        /// duplicate rather than that the outbox can produce one.
        /// </remarks>
        public async ValueTask RepublishAsync(OutboxRecord staged)
        {
            var published = await new RedisStreamEventPublisher(_redis, _streams)
                .PublishAsync([staged], TestContext.Current.CancellationToken);

            published.IsSuccess.ShouldBeTrue();
        }

        public async ValueTask DisposeAsync()
        {
            var database = _redis.GetDatabase(_streams.Database);

            foreach (var endpoint in _redis.GetEndPoints())
            {
                var server = _redis.GetServer(endpoint);

                if (server.IsReplica)
                {
                    continue;
                }

                await foreach (var key in server.KeysAsync(database.Database, _streams.KeyPrefix + "*"))
                {
                    await database.KeyDeleteAsync(key);
                }
            }

            _redis.Dispose();

            await using (var connection = await _dataSource.OpenConnectionAsync())
            {
                await using var drop = connection.CreateCommand();

                drop.CommandText = $"drop schema if exists {_schema} cascade;";

                await drop.ExecuteNonQueryAsync();
            }

            await _dataSource.DisposeAsync();
        }
    }
}
