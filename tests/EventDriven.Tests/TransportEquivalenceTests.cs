using System.Globalization;
using System.Text.Json;
using EventDriven;
using FlowX;
using FlowX.Hosting;
using FlowX.Postgres;
using FlowX.Redis;
using FlowX.Runtime;
using FlowX.Testing;
using Npgsql;
using Shouldly;
using StackExchange.Redis;
using Xunit;

namespace EventDriven.Tests;

/// <summary>
/// One billing reference, four transports, one invoice — against a real HTTP server, a real
/// broker, a real outbox feed, a real cron sweep and a real journal.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is quality goal Q4's behavioural half, and the assertions are on what the
/// journal recorded rather than on what any of the four calls returned.</strong> Two of the four
/// transports have nobody waiting for a result — a delivery and an occurrence return to a sweep —
/// so a test that compared return values could only compare the two that have one. The journal
/// holds the same evidence for all four: which capabilities ran, in which order, with what
/// outcome, and what the persisting step committed.
/// </para>
/// <para>
/// <strong>The expected chain and the expected invoice are written down, not derived.</strong>
/// Four transports compared only with each other agree perfectly when all four are broken, which
/// is the failure mode of every equality-only portability check.
/// </para>
/// <para>
/// <strong>Gating.</strong> Both servers are opt-in. With neither variable set every test here
/// skips with a reason; with a variable set and no server the harness fails rather than skips — a
/// skip there reports an integration that never ran as a green job.
/// </para>
/// </remarks>
public sealed class TransportEquivalenceTests : IAsyncLifetime
{
    private const string PostgresVariable = "FLOWX_POSTGRES_CONNECTION";

    private const string RedisVariable = "FLOWX_REDIS_CONNECTION";

    /// <summary>What every arm bills, so only the reference differs between them.</summary>
    private const decimal Net = 100m;

    private readonly List<Fixture> _fixtures = [];

    /// <summary>
    /// The same request issued over HTTP, over a broker, over the outbox and on a schedule
    /// produces the same invoice, computed by the same steps.
    /// </summary>
    /// <remarks>
    /// <strong>The test the sample exists for.</strong> Each arm carries its own reference so
    /// that its journal rows are identifiable; the invoice ids are then normalised away and the
    /// four invoices must be one value. A transport that stopped reaching a step, a stance a
    /// delivery cannot satisfy, a contract the journal cannot write, an adapter that decoded the
    /// wrong field — each fails here and none of them fails in
    /// <see cref="TransportPortabilityTests"/>.
    /// </remarks>
    [Fact]
    public async Task OneRequestIssuesOneInvoiceOverEveryTransport()
    {
        var fixture = await CreateAsync();

        var arms = new List<Arm>
        {
            await fixture.OverHttpAsync(),
            await fixture.OverBusAsync(),
            await fixture.OverBusPushAsync(),
            await fixture.OverChangeAsync(),
            await fixture.OverScheduleAsync(),
        };

        foreach (var arm in arms)
        {
            var run = await fixture.RunOfAsync(arm);

            run.State.ShouldBe(
                "Completed",
                $"the {arm.FlowId} instance for '{arm.Reference}' did not complete, so the " +
                "chain does not survive this transport.");

            run.Chain.ShouldBe(
                [
                    "invoice.validate Success",
                    "invoice.tax Success",
                    "invoice.persist Success",
                    "invoice.issued Success",
                ],
                $"the steps '{arm.FlowId}' actually ran below its transport adapter are not the " +
                "steps the other three ran. Quality goal Q4 is a claim about what executes, not " +
                "about what compiles.");

            run.Invoice.ShouldBe(
                new Invoice("INV-" + arm.Reference, "acme", Net, 20m, 120m, "GBP"),
                $"'{arm.FlowId}' committed a different invoice from the one the declared chain " +
                "computes.");
        }

        // And the four are one value once the reference each arm carried is taken out of it.
        var invoices = new List<Invoice>();

        foreach (var arm in arms)
        {
            invoices.Add((await fixture.RunOfAsync(arm)).Invoice! with { InvoiceId = "INV" });
        }

        invoices.Distinct().Count().ShouldBe(
            1,
            "Four transports and the push route produced more than one invoice from the same " +
            "request:" + Environment.NewLine + string.Join(Environment.NewLine, invoices));
    }

    /// <summary>
    /// The four transports run one set of capabilities, not four copies of one.
    /// </summary>
    /// <remarks>
    /// The container registers <c>invoice.validate</c>, <c>invoice.tax</c>,
    /// <c>invoice.persist</c> and <c>invoice.void</c> once each and hands the same instances to
    /// four generated dispatchers. Asserted against the ledger every arm wrote to, because a
    /// per-transport copy of the business logic is exactly what "portable" is meant to exclude
    /// and would be invisible in every other assertion here.
    /// </remarks>
    [Fact]
    public async Task EveryTransportWritesThroughOneLedger()
    {
        var fixture = await CreateAsync();

        var arms = new List<Arm>
        {
            await fixture.OverHttpAsync(),
            await fixture.OverBusAsync(),
            await fixture.OverBusPushAsync(),
            await fixture.OverChangeAsync(),
            await fixture.OverScheduleAsync(),
        };

        foreach (var arm in arms)
        {
            var written = await fixture.Ledger.ReadAsync(
                "INV-" + arm.Reference, TestContext.Current.CancellationToken);

            written.ShouldNotBeNull(
                $"'{arm.FlowId}' completed and the one ledger every transport shares holds " +
                "nothing for it.");

            written.Gross.ShouldBe(120m);
        }
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

    /// <summary>Builds the whole arrangement, or explains why it did not.</summary>
    private async ValueTask<Fixture> CreateAsync()
    {
        var postgres = Environment.GetEnvironmentVariable(PostgresVariable);
        var redis = Environment.GetEnvironmentVariable(RedisVariable);

        if (string.IsNullOrEmpty(postgres) || string.IsNullOrEmpty(redis))
        {
            Assert.Skip(
                $"This test runs one request through four real transports, so it needs a " +
                $"journal and a broker. Set {PostgresVariable} and {RedisVariable} to run it — " +
                $"for example 'Host=localhost;Port=5432;Database=postgres;Username=postgres' " +
                "and 'localhost:6379'. Nothing about transport portability is verified without " +
                "them.");
        }

        var fixture = await Fixture.CreateAsync(
            postgres!, redis!, TestContext.Current.CancellationToken);

        _fixtures.Add(fixture);

        return fixture;
    }

    /// <summary>One transport, exercised, and the reference that identifies what it did.</summary>
    /// <param name="FlowId">Which flow the transport started.</param>
    /// <param name="Reference">The billing reference only this arm used.</param>
    internal sealed record Arm(string FlowId, string Reference);

    /// <summary>What the journal holds about one arm's instance.</summary>
    /// <param name="State">Where it ended.</param>
    /// <param name="Chain">Its capability steps, in commit order, with their outcomes.</param>
    /// <param name="Invoice">What the persisting step committed.</param>
    internal sealed record Run(string State, IReadOnlyList<string> Chain, Invoice? Invoice);

    /// <summary>One schema, one key space, and the sample's five flows wired across them.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private const string Cron = "0 3 * * *";

        private static readonly DateTimeOffset Occurrence =
            new(2026, 1, 1, 3, 0, 0, TimeSpan.Zero);

        private readonly NpgsqlDataSource _dataSource;
        private readonly IConnectionMultiplexer _redis;
        private readonly RedisStreamOptions _streams;
        private readonly string _schema;
        private readonly FlowXOptions _options;
        private readonly FlowDurability _durability;
        private readonly FlowHost _host;
        private readonly PostgresOutboxPublisher _outbox;
        private readonly PostgresChangeFeed _feed;
        private readonly FlowBusScan _bus;
        private readonly RedisStreamBusConsumer _broker;
        private readonly BusRegistration _busRegistration;
        private readonly FlowChangeScan _change;
        private readonly FlowScheduleScan _schedule;
        private readonly FlowTestClock _clock;
        private readonly InvoiceWebHost _web;

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

            _options = new FlowXOptions { ApplicationName = "EventDriven", NodeName = "test-node" };
            _durability = new FlowDurability(journal, leases);
            _host = new FlowHost(new FlowEngine(SystemClock.Instance), _options, _durability);

            // One instance of each business capability, shared by every dispatcher — which is
            // the sample's claim expressed as an object graph rather than as a comment.
            var validate = new ValidateInvoice();
            var tax = new CalculateTax();
            var persist = new PersistInvoice(Ledger);
            var @void = new VoidInvoice(Ledger);
            var read = new ReadInvoiceRequest();
            var due = new DueInvoice(Calendar);

            _web = new InvoiceWebHost(journal, leases, validate, tax, persist, @void);

            _outbox = new PostgresOutboxPublisher(
                dataSource, new RedisStreamEventPublisher(redis, streams));

            _feed = new PostgresChangeFeed(dataSource);

            var subscriptions = new FlowBusCatalog().Add(
                new BusSubscription(
                    "invoice.issue.bus", "1.0.0", "invoice.requested", "billing"),
                IssueInvoiceOverBusFlow.Plan,
                new IssueInvoiceOverBusFlow.Dispatcher(tax, persist, read, validate, @void));

            _broker = new RedisStreamBusConsumer(redis, streams, "test-node");
            _busRegistration = subscriptions.Registrations[0];

            _bus = new FlowBusScan(_host, subscriptions, _broker, _durability, _options);

            _change = new FlowChangeScan(
                _host,
                new FlowChangeCatalog().Add(
                    new ChangeSubscription(
                        "invoice.issue.change", "1.0.0", "invoice.requested", "billing-projection"),
                    IssueInvoiceOverChangeFlow.Plan,
                    new IssueInvoiceOverChangeFlow.Dispatcher(tax, persist, read, validate, @void)),
                _feed,
                _durability,
                _options);

            // The scheduler's own clock, started a minute before the declared occurrence so that
            // advancing past it makes 03:00 genuinely fall due through the ordinary sweep rather
            // than through a firing this test names.
            _clock = new FlowTestClock(Occurrence.AddMinutes(-1));

            _schedule = new FlowScheduleScan(
                new FlowHost(new FlowEngine(_clock), _options, _durability),
                new FlowScheduleCatalog().Add(
                    FlowSchedule.Create(
                        "invoice.issue.schedule", "1.0.0", Cron, "UTC", MissedFirePolicy.RunOnce),
                    IssueInvoiceOverScheduleFlow.Plan,
                    new IssueInvoiceOverScheduleFlow.Dispatcher(tax, due, persist, validate, @void)),
                _durability,
                _options,
                _clock);
        }

        /// <summary>The one ledger every transport writes through.</summary>
        public InMemoryInvoiceLedger Ledger { get; } = new();

        /// <summary>What the cron transport bills at its occurrence.</summary>
        public InMemoryBillingCalendar Calendar { get; } = new();

        public static async ValueTask<Fixture> CreateAsync(
            string postgres, string redis, CancellationToken ct)
        {
            var schema = "flowx_portability_" + Guid.NewGuid().ToString("n")[..12];

            var builder = new NpgsqlConnectionStringBuilder(postgres) { SearchPath = schema };
            var dataSource = NpgsqlDataSource.Create(builder.ConnectionString);

            await using (var connection = await dataSource.OpenConnectionAsync(ct))
            {
                await using var create = connection.CreateCommand();

                create.CommandText = $"create schema if not exists {schema};";

                await create.ExecuteNonQueryAsync(ct);
            }

            await new PostgresMigrator(dataSource, new PostgresJournalOptions { Schema = schema })
                .MigrateAsync(ct);

            var fixture = new Fixture(
                dataSource,
                await ConnectionMultiplexer.ConnectAsync(redis),
                new RedisStreamOptions { KeyPrefix = "flowx_portability_" + Guid.NewGuid().ToString("n") },
                schema,
                new PostgresFlowJournal(dataSource),
                new PostgresLeaseStore(dataSource));

            await fixture._web.StartAsync(ct);

            return fixture;
        }

        /// <summary>Issues over HTTP, through the generated endpoint and a real server.</summary>
        public async ValueTask<Arm> OverHttpAsync()
        {
            var arm = new Arm("invoice.issue.http", "http");

            var issued = await _web.PostAsync<Invoice>(
                "/api/v1/invoices", RequestFor(arm.Reference), TestContext.Current.CancellationToken);

            issued.InvoiceId.ShouldBe("INV-" + arm.Reference);

            return arm;
        }

        /// <summary>Issues over the broker: request, stage, drain to Redis, consume.</summary>
        public async ValueTask<Arm> OverBusAsync()
        {
            var arm = new Arm("invoice.issue.bus", "bus");
            var ct = TestContext.Current.CancellationToken;

            await RequestAsync(arm.Reference);

            var drained = await _outbox.PublishPendingAsync(ct);

            drained.Published.ShouldBeGreaterThanOrEqualTo(
                1, "the request flow staged an event and the broker took it");

            var pass = await _bus.RunOnceAsync(ct);

            pass.Started.ShouldBeGreaterThanOrEqualTo(
                1, "the broker offered the request and the subscription started the flow");

            return arm;
        }

        /// <summary>
        /// Issues over the broker again, pushed: one delivery handed straight to the admission
        /// seam, with nothing sweeping and nothing acknowledged.
        /// </summary>
        /// <remarks>
        /// <strong>The arm a serverless deployment is.</strong> A Functions host is handed one
        /// message by its platform and settles it there; this arm takes the delivery from the
        /// broker exactly as such a platform would, calls the seam, and never answers. What it
        /// adds to this file is that the fifth route through the same chain produces the same
        /// invoice — the sweep and the push entry agreeing about the four steps below them is
        /// the whole of WP-140's claim.
        /// </remarks>
        public async ValueTask<Arm> OverBusPushAsync()
        {
            var arm = new Arm("invoice.issue.bus", "push");
            var ct = TestContext.Current.CancellationToken;

            await RequestAsync(arm.Reference);

            var drained = await _outbox.PublishPendingAsync(ct);

            drained.Published.ShouldBeGreaterThanOrEqualTo(1);

            var subscription = _busRegistration.Subscription;

            (await _broker.SubscribeAsync(subscription, ct)).IsSuccess.ShouldBeTrue();

            var received = await _broker.ReceiveAsync(subscription, 1, 1, ct);

            received.IsSuccess.ShouldBeTrue();

            var delivery = received.Value
                .SelectMany(static batch => batch.Deliveries)
                .ShouldHaveSingleItem();

            var admission = await _bus.AdmitAsync(_busRegistration, delivery, ct);

            admission.Disposition.ShouldBe(
                BusDisposition.Started,
                "the push entry started the flow, and the message is still the platform's to " +
                "settle — nothing here acknowledged it.");

            return arm;
        }

        /// <summary>Issues over the outbox itself, with no broker in the path.</summary>
        public async ValueTask<Arm> OverChangeAsync()
        {
            var arm = new Arm("invoice.issue.change", "change");
            var ct = TestContext.Current.CancellationToken;

            await RequestAsync(arm.Reference);
            await AwaitVisibleAsync(arm.Reference);

            var pass = await _change.RunOnceAsync(ct);

            pass.Error.ShouldBeNull();
            pass.Started.ShouldBeGreaterThanOrEqualTo(
                1, "the feed offered the staged request and the subscription started the flow");

            return arm;
        }

        /// <summary>Issues on the declared cron occurrence, through an ordinary sweep.</summary>
        public async ValueTask<Arm> OverScheduleAsync()
        {
            var arm = new Arm("invoice.issue.schedule", "schedule");

            Calendar.Add(Occurrence, RequestFor(arm.Reference));

            _clock.Advance(TimeSpan.FromMinutes(2));

            var pass = await _schedule.RunOnceAsync(TestContext.Current.CancellationToken);

            pass.Fired.ShouldBe(
                1, "03:00 fell due and this node won it, which is what a schedule with no leader " +
                   "looks like on a fleet of one.");

            return arm;
        }

        /// <summary>What the journal holds about the instance one arm produced.</summary>
        public async ValueTask<Run> RunOfAsync(Arm arm)
        {
            var ct = TestContext.Current.CancellationToken;
            var states = new Dictionary<Guid, string>();
            var steps = new Dictionary<Guid, List<(long Sequence, string Capability, string Outcome, string? Result)>>();

            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var command = connection.CreateCommand();

            command.CommandText =
                $"select i.instance_id, i.state, s.sequence, s.capability_id, s.outcome, s.result " +
                $"from {_schema}.flow_instance i " +
                $"join {_schema}.flow_step s on s.instance_id = i.instance_id " +
                $"where i.flow_id = @flow order by i.created_at, s.sequence;";

            command.Parameters.AddWithValue("flow", arm.FlowId);

            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    var instance = reader.GetGuid(0);

                    states[instance] = reader.GetString(1);

                    if (!steps.TryGetValue(instance, out var rows))
                    {
                        steps[instance] = rows = [];
                    }

                    rows.Add((
                        reader.GetInt64(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        await reader.IsDBNullAsync(5, ct) ? null : reader.GetString(5)));
                }
            }

            foreach (var (instance, rows) in steps)
            {
                var persisted = rows
                    .Where(static row => row.Capability == "invoice.persist" && row.Result is not null)
                    .Select(static row => JsonSerializer.Deserialize(
                        row.Result!, EventDrivenJsonContext.Default.Invoice))
                    .FirstOrDefault(invoice => invoice?.InvoiceId == "INV-" + arm.Reference);

                if (persisted is null)
                {
                    continue;
                }

                return new Run(
                    states[instance],
                    // From the first shared step onwards. Dropping the transport's own adapter by
                    // position rather than by name, so a fifth transport needs no edit here — and
                    // so an adapter that quietly moved below the line is a failure rather than an
                    // exclusion.
                    [.. rows
                        .OrderBy(static row => row.Sequence)
                        .SkipWhile(static row => row.Capability != "invoice.validate")
                        .Select(static row => row.Capability + " " + row.Outcome)],
                    persisted);
            }

            throw new InvalidOperationException(
                $"No '{arm.FlowId}' instance in {_schema} persisted 'INV-{arm.Reference}'. The " +
                "transport did not start the flow, or the flow did not reach its persisting step.");
        }

        public async ValueTask DisposeAsync()
        {
            await _web.DisposeAsync();

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

        private static IssueInvoice RequestFor(string reference) =>
            new(reference, "acme", Net, "GBP");

        /// <summary>Asks for an invoice over HTTP, which stages <c>invoice.requested</c>.</summary>
        private async ValueTask RequestAsync(string reference)
        {
            var accepted = await _web.PostAsync<InvoiceRequestAccepted>(
                "/api/v1/invoice-requests",
                RequestFor(reference),
                TestContext.Current.CancellationToken);

            accepted.Reference.ShouldBe(reference);
        }

        /// <summary>
        /// Waits until this schema's staged request is below the change feed's visibility barrier.
        /// </summary>
        /// <remarks>
        /// <c>pg_snapshot_xmin</c> is cluster-wide: a transaction open anywhere in the database —
        /// including one belonging to a test running in parallel against the same server — holds
        /// the barrier down and makes a freshly committed change invisible for as long as it
        /// lasts. That is the latency floor ADR-0048 records as the price of the guarantee.
        /// </remarks>
        private async ValueTask AwaitVisibleAsync(string reference)
        {
            var ct = TestContext.Current.CancellationToken;
            var subscription = new ChangeSubscription(
                "invoice.issue.change", "1.0.0", "invoice.requested", "visibility-probe");
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);

            while (DateTimeOffset.UtcNow < deadline)
            {
                var read = await _feed.ReadAsync(subscription, 32, ct);

                if (read.IsSuccess && read.Value.Any(change =>
                        change.Message.Payload?.Contains(reference, StringComparison.Ordinal) == true))
                {
                    return;
                }

                await Task.Delay(25, ct);
            }

            throw new InvalidOperationException(
                $"The request '{reference}' was staged and committed and is still below no " +
                "barrier after 30 seconds. Either a transaction is open somewhere in this " +
                "database and has not ended, or the feed is not reading what the journal staged.");
        }
    }
}
