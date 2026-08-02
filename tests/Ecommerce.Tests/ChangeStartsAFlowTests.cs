using System.Security.Claims;
using Ecommerce;
using FlowX;
using FlowX.Hosting;
using FlowX.Postgres;
using FlowX.Runtime;
using Npgsql;
using Shouldly;
using Xunit;

namespace Ecommerce.Tests;

/// <summary>
/// One flow's <c>.Emit</c> starts another flow through the outbox alone — no broker, no
/// publisher, one server.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every link in the chain is the real one, and there are fewer of them than in
/// <see cref="EmitStartsAFlowTests"/>.</strong> <see cref="ConfirmOrderFlow"/> stages
/// <c>order.placed</c> in its step's own transaction, <see cref="PostgresChangeFeed"/> reads that
/// row back out of <c>outbox_event</c>, and <c>FlowChangeScan</c> starts
/// <see cref="ProjectOrderFlow"/> — which the sample declares with a <c>[ChangeTrigger]</c> and
/// nothing else. Nothing between the two flows names either of them, and nothing between them is
/// a broker.
/// </para>
/// <para>
/// <strong>Both silent failures are asserted, for that file's reason.</strong> A chain that never
/// fires and a chain that starts two flows for one change look identical from outside. The second
/// is what re-reading an uncommitted cursor produces, so the redelivery test <em>does not commit
/// the cursor</em> and passes again — which is exactly what a crash between the flow committing
/// and the cursor committing leaves behind.
/// </para>
/// <para>
/// <strong>Gating.</strong> One variable, one server. Unset, every test here skips with a reason;
/// set with no server, the harness fails rather than skips — a skip there reports an integration
/// that never ran as a green job.
/// </para>
/// </remarks>
public sealed class ChangeStartsAFlowTests : IAsyncLifetime
{
    private const string PostgresVariable = "FLOWX_POSTGRES_CONNECTION";

    private readonly List<Fixture> _fixtures = [];

    /// <summary>A staged event starts the observing flow, and the journal says so.</summary>
    [Fact]
    public async Task AStagedEventStartsTheObservingFlow()
    {
        var fixture = await CreateAsync();

        (await fixture.ConfirmAsync("SKU-1", 2)).IsSuccess.ShouldBeTrue();
        await fixture.AwaitVisibleAsync(1);

        var pass = await fixture.ChangePassAsync();

        pass.Error.ShouldBeNull();
        pass.Observed.ShouldBe(1, "the step staged one event and the feed offered it");
        pass.Started.ShouldBe(1);
        pass.Held.ShouldBe(0);

        var rows = await fixture.InstancesAsync();

        rows.Count(static row => row.FlowId == "order.confirm").ShouldBe(1);
        rows.Count(static row => row.FlowId == "order.project").ShouldBe(1);

        rows.Single(static row => row.FlowId == "order.project").State.ShouldBe("Completed",
            "the observing flow ran to completion, so the projection is a fact and not a queue " +
            "entry.");
    }

    /// <summary>
    /// The instance the change started is the one every node would derive for it.
    /// </summary>
    /// <remarks>
    /// The whole of the deduplication mechanism, asserted against the row rather than against the
    /// scan's own report: an id computed independently from the four terms and the event id must
    /// be the primary key the journal holds
    /// (<a href="../../docs/adr/ADR-0049-a-change-names-the-instance-it-starts.md">ADR-0049</a>).
    /// If it were minted instead, every test below would still pass and nothing would deduplicate
    /// in production.
    /// </remarks>
    [Fact]
    public async Task TheInstanceIsNamedByTheChangeThatStartedIt()
    {
        var fixture = await CreateAsync();

        (await fixture.ConfirmAsync("SKU-1", 1)).IsSuccess.ShouldBeTrue();
        await fixture.AwaitVisibleAsync(1);

        await fixture.ChangePassAsync();

        var staged = await fixture.StagedEventAsync();

        var derived = ChangeIdentity.InstanceIdFor(
            "order.project", "1.0.0", "order.placed", "projection", staged.EventId);

        (await fixture.InstancesAsync())
            .Single(static row => row.FlowId == "order.project")
            .InstanceId
            .ShouldBe(derived);
    }

    /// <summary>
    /// A change offered twice starts one flow, and the second offer is a deduplication.
    /// </summary>
    /// <remarks>
    /// <strong>The test the whole design exists for.</strong> The cursor is committed after the
    /// flows have run, which is the only order that cannot lose work — so the window between the
    /// two writes is real, and a crash inside it re-offers every change in the batch. This
    /// arranges exactly that by running a pass whose cursor commit is suppressed, then running an
    /// ordinary pass: the same change is offered again, and the journal's primary key answers it.
    /// </remarks>
    [Fact]
    public async Task AReofferedChangeStartsNoSecondFlow()
    {
        var fixture = await CreateAsync();

        (await fixture.ConfirmAsync("SKU-1", 3)).IsSuccess.ShouldBeTrue();
        await fixture.AwaitVisibleAsync(1);

        var first = await fixture.ChangePassAsync(commitCursor: false);

        first.Started.ShouldBe(1);

        var second = await fixture.ChangePassAsync();

        second.Observed.ShouldBe(
            1, "the cursor never moved, so the feed offers the change again — which is what a " +
               "crash between the flow committing and the cursor committing leaves behind");

        second.Started.ShouldBe(0);
        second.Deduplicated.ShouldBe(1);

        (await fixture.InstancesAsync())
            .Count(static row => row.FlowId == "order.project")
            .ShouldBe(1, "one change names one instance, however many times it is offered");

        var third = await fixture.ChangePassAsync();

        third.Observed.ShouldBe(
            0, "and the second pass did commit the cursor, so the subscription has moved on.");
    }

    /// <summary>
    /// The outbox publisher and a change subscription read the same rows and take nothing from
    /// each other.
    /// </summary>
    /// <remarks>
    /// The property that makes <c>Change</c> a second consumer rather than a replacement
    /// (<a href="../../docs/adr/ADR-0050-a-change-trigger-observes-the-outbox.md">ADR-0047</a>
    /// decision 2). The publisher claims and marks <c>published_at</c>; the feed never reads or
    /// writes it. A feed that filtered on the marker would observe nothing here, and one that set
    /// it would leave the broker with nothing.
    /// </remarks>
    [Fact]
    public async Task APublishedEventIsStillObservedByAChangeSubscription()
    {
        var fixture = await CreateAsync();

        (await fixture.ConfirmAsync("SKU-1", 1)).IsSuccess.ShouldBeTrue();
        await fixture.AwaitVisibleAsync(1);

        var drained = await fixture.DrainOutboxAsync();

        drained.Published.ShouldBe(1, drained.Failure?.ToString());

        var pass = await fixture.ChangePassAsync();

        pass.Started.ShouldBe(
            1, "published_at is the publisher's progress and nobody else's, so marking it takes " +
               "nothing away from a change subscription.");
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

    /// <summary>Builds the whole chain, or explains why it did not.</summary>
    /// <exception cref="InvalidOperationException">
    /// A server was promised by the environment and is not reachable. A failure rather than a
    /// skip on purpose.
    /// </exception>
    private async ValueTask<Fixture> CreateAsync()
    {
        var postgres = Environment.GetEnvironmentVariable(PostgresVariable);

        if (string.IsNullOrEmpty(postgres))
        {
            Assert.Skip(
                $"This test drives the sample's emitted event through a real outbox read as a " +
                $"change feed, so it needs a server. Set {PostgresVariable} to run it — for " +
                $"example 'Host=localhost;Port=5432;Database=postgres;Username=postgres'. " +
                "Nothing about the change trigger is verified without it.");
        }

        var fixture = await Fixture.CreateAsync(postgres!, TestContext.Current.CancellationToken);

        _fixtures.Add(fixture);

        return fixture;
    }

    /// <summary>One journal row, as an operator reading the table would see it.</summary>
    /// <param name="FlowId">Which flow.</param>
    /// <param name="InstanceId">Its primary key — minted for the confirming flow, derived from the change for the observing one (ADR-0049).</param>
    /// <param name="State">Where it ended.</param>
    private sealed record InstanceRow(string FlowId, Guid InstanceId, string State);

    /// <summary>One schema, and the two sample flows wired across it.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly NpgsqlDataSource _dataSource;
        private readonly string _schema;
        private readonly FlowHost _host;
        private readonly FlowDurability _durability;
        private readonly FlowXOptions _options;
        private readonly FlowChangeCatalog _subscriptions;
        private readonly PostgresChangeFeed _feed;
        private readonly ConfirmOrderFlow.Dispatcher _confirm;

        private Fixture(
            NpgsqlDataSource dataSource,
            string schema,
            PostgresFlowJournal journal,
            PostgresLeaseStore leases)
        {
            _dataSource = dataSource;
            _schema = schema;

            _options = new FlowXOptions { ApplicationName = "Ecommerce", NodeName = "test-node" };
            _durability = new FlowDurability(journal, leases);

            _host = new FlowHost(new FlowEngine(SystemClock.Instance), _options, _durability);
            _confirm = new ConfirmOrderFlow.Dispatcher(new ValidateOrder());
            _feed = new PostgresChangeFeed(dataSource);

            Journal = journal;

            // The registration the generated AddFlowXChangeSubscriptions makes, by hand, because
            // this project builds no container — the same shape EmitStartsAFlowTests uses for the
            // bus subscription, and it goes through FlowChangeCatalog.Add, so the profile rule
            // and the self-emit refusal are both exercised by every test in this file.
            _subscriptions = new FlowChangeCatalog().Add(
                new ChangeSubscription("order.project", "1.0.0", "order.placed", "projection"),
                ProjectOrderFlow.Plan,
                new ProjectOrderFlow.Dispatcher(new ProjectOrder()));
        }

        /// <summary>The journal, so a test can read what the flows wrote.</summary>
        public PostgresFlowJournal Journal { get; }

        public static async ValueTask<Fixture> CreateAsync(string postgres, CancellationToken ct)
        {
            var schema = "flowx_change_" + Guid.NewGuid().ToString("n")[..12];

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

        /// <summary>
        /// Waits until this schema's staged events are below the feed's visibility barrier.
        /// </summary>
        /// <remarks>
        /// <strong><c>pg_snapshot_xmin</c> is cluster-wide, not schema-wide.</strong> A
        /// transaction open anywhere in the database — including one belonging to another test
        /// running in parallel against the same server — holds the barrier down and makes a
        /// freshly committed change invisible for as long as it lasts. That is the latency floor
        /// <a href="../../docs/adr/ADR-0048-a-change-feed-advances-a-cursor.md">ADR-0048</a>
        /// records as the price of the guarantee, and it is why this waits rather than asserting
        /// that a committed change is immediately observable.
        /// </remarks>
        public async ValueTask AwaitVisibleAsync(int changes)
        {
            var ct = TestContext.Current.CancellationToken;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);

            while (DateTimeOffset.UtcNow < deadline)
            {
                var read = await _feed.ReadAsync(
                    new ChangeSubscription("order.project", "1.0.0", "order.placed", "projection"),
                    changes + 1,
                    ct);

                if (read.IsSuccess && read.Value.Count >= changes)
                {
                    return;
                }

                await Task.Delay(25, ct);
            }

            throw new InvalidOperationException(
                $"{changes} change(s) were staged and committed and are still below no barrier " +
                "after 30 seconds. Either a transaction is open somewhere in this database and " +
                "has not ended, or the feed is not reading what the journal staged.");
        }

        /// <summary>One pass of the change scan.</summary>
        /// <param name="commitCursor">
        /// False to run the pass with the cursor commit suppressed, which is what a crash between
        /// the flow committing and the cursor committing leaves behind.
        /// </param>
        public ValueTask<ChangeScanReport> ChangePassAsync(bool commitCursor = true)
        {
            var feed = commitCursor ? (IChangeFeed)_feed : new UncommittableFeed(_feed);

            var scan = new FlowChangeScan(_host, _subscriptions, feed, _durability, _options);

            scan.IsEnabled.ShouldBeTrue(
                "a host with a journal and a registered change subscription observes.");

            return scan.RunOnceAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>Drains the outbox to a broker that takes everything.</summary>
        public ValueTask<OutboxPass> DrainOutboxAsync() =>
            new PostgresOutboxPublisher(_dataSource, new AcceptingPublisher())
                .PublishPendingAsync(TestContext.Current.CancellationToken);

        /// <summary>Every instance row this schema holds, which is the assertion's subject.</summary>
        public async ValueTask<IReadOnlyList<InstanceRow>> InstancesAsync()
        {
            var ct = TestContext.Current.CancellationToken;
            var rows = new List<InstanceRow>();

            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var command = connection.CreateCommand();

            command.CommandText =
                $"select flow_id, instance_id, state from {_schema}.flow_instance order by created_at;";

            await using var reader = await command.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                rows.Add(new InstanceRow(reader.GetString(0), reader.GetGuid(1), reader.GetString(2)));
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

        public async ValueTask DisposeAsync()
        {
            await using (var connection = await _dataSource.OpenConnectionAsync())
            {
                await using var drop = connection.CreateCommand();

                drop.CommandText = $"drop schema if exists {_schema} cascade;";

                await drop.ExecuteNonQueryAsync();
            }

            await _dataSource.DisposeAsync();
        }
    }

    /// <summary>The real feed with its cursor commit suppressed.</summary>
    /// <remarks>
    /// A crash, expressed as a decorator. Everything the scan reads is the real store's; the one
    /// write that would record progress does not happen, which is the state a node that died
    /// between committing a flow and committing its cursor leaves the subscription in. Simulating
    /// it by rolling the cursor back afterwards would assert that this test can write a cursor.
    /// </remarks>
    private sealed class UncommittableFeed(IChangeFeed inner) : IChangeFeed
    {
        public string Feed => inner.Feed;

        public ValueTask<Result<IReadOnlyList<ObservedChange>>> ReadAsync(
            ChangeSubscription subscription, int max, CancellationToken cancellationToken) =>
            inner.ReadAsync(subscription, max, cancellationToken);

        public ValueTask<Result<bool>> CommitAsync(
            ChangeSubscription subscription,
            ChangePosition position,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Ok(false));
    }

    /// <summary>A broker that acknowledges everything, so the outbox can be marked published.</summary>
    private sealed class AcceptingPublisher : IEventPublisher
    {
        public ValueTask<Result<int>> PublishAsync(
            IReadOnlyList<OutboxRecord> batch, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(batch);

            return ValueTask.FromResult(Result.Ok(batch.Count));
        }
    }
}
