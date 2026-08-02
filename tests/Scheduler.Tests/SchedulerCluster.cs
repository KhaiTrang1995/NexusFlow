using FlowX;
using FlowX.Hosting;
using FlowX.Postgres;
using FlowX.Runtime;
using FlowX.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Scheduler.Tests;

/// <summary>
/// Decides, once, whether there is a PostgreSQL server to test against — and records why when
/// there is not.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A silently skipped test is indistinguishable from a passing one</strong>, so the
/// decision here is noisy in two directions. No server configured: every test skips and each
/// skip carries this reason. A server configured but unreachable: every test <em>fails</em>,
/// because that is the case that looks like success in CI — somebody wired a service container,
/// the variable is set, the container did not come up, and a skip would report a green suite
/// for a sample whose central claim is about a database.
/// </para>
/// <para>
/// The same arrangement <c>FlowX.Postgres.Tests.PostgresTestDatabase</c> makes, restated rather
/// than shared because that type is <c>internal</c> to a test assembly and making it public
/// would be widening a test surface so that another test can reach it.
/// </para>
/// </remarks>
internal static class SchedulerDatabase
{
    /// <summary>The variable that supplies a connection string.</summary>
    public const string ConnectionVariable = "FLOWX_POSTGRES_CONNECTION";

    private static readonly Lazy<(bool Available, string Reason)> Probe =
        new(Run, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The configured connection string, or null when none was supplied.</summary>
    public static string? ConnectionString =>
        Environment.GetEnvironmentVariable(ConnectionVariable) is { Length: > 0 } value
            ? value
            : null;

    /// <summary>Whether the environment promised a database, whether or not one answered.</summary>
    public static bool IsPromised => ConnectionString is not null;

    /// <summary>Whether a server answered.</summary>
    public static bool IsAvailable => Probe.Value.Available;

    /// <summary>Why, in a sentence a reader can act on. Never empty.</summary>
    public static string Reason => Probe.Value.Reason;

    private static (bool, string) Run()
    {
        if (ConnectionString is not { } connectionString)
        {
            return (false,
                $"No PostgreSQL server is configured, so every test that needs one is skipped " +
                $"and nothing about this sample's fleet behaviour has been verified. Set " +
                $"{ConnectionVariable} to a connection string to run them. The account needs " +
                "CREATE on the database, because each test is given its own schema.");
        }

        try
        {
            using var dataSource = NpgsqlDataSource.Create(connectionString);
            using var connection = dataSource.OpenConnection();
            using var command = connection.CreateCommand();

            command.CommandText = "SELECT version()";

            return (true, $"Connected to {command.ExecuteScalar() as string}.");
        }
        catch (NpgsqlException failure)
        {
            return (false, $"{ConnectionVariable} is set but the server did not answer: {failure.Message}");
        }
        catch (ArgumentException failure)
        {
            return (false, $"{ConnectionVariable} is not a usable connection string: {failure.Message}");
        }
    }
}

/// <summary>
/// A fleet of scheduler nodes over one PostgreSQL schema, and the sample's own flow registered
/// on all of them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The stores are the real ones and the sweep is the real one.</strong> What is
/// simulated is time — a <see cref="FlowTestClock"/> every node reads, so a suite can wind a day
/// forward without waiting for one — and the fleet, which is <em>n</em>
/// <see cref="FlowScheduleScan"/> instances with different node names over one journal. That is
/// what <em>n</em> replicas are: the only thing a second pod adds is a second sweep against the
/// same database.
/// </para>
/// <para>
/// <strong>There is no leader here and no <c>KillLeader</c>, and the README used to promise
/// both.</strong> A node is not elected, does not renew anything, and holds no schedule-level
/// lease; killing one is therefore not an event the schedule can observe. What replaces it is
/// stronger and is what these tests assert: every node computes the same occurrence, derives the
/// same instance id from it, and <c>flow_instance</c>'s primary key refuses all but one — for
/// ever, not for a TTL
/// (<c>docs/adr/ADR-0031-an-occurrence-names-the-instance-it-starts.md</c>).
/// </para>
/// <para>
/// A schema per fixture rather than a truncate per test: no leftover instance row and no
/// ordering dependency that would let one test pass because of what another left behind.
/// </para>
/// </remarks>
internal sealed class SchedulerCluster : IAsyncDisposable
{
    /// <summary>1970-01-01T00:00:00Z — a Thursday, on the hour, and the epoch the fixture books at.</summary>
    public static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    /// <summary>Hourly in UTC: the demonstration expression, not the declared one.</summary>
    /// <remarks>
    /// The declared expression is <c>0 2 * * *</c> in <c>Europe/Berlin</c> and is asserted
    /// against the generated registration in <see cref="DeclarationTests"/>. A test that wound a
    /// clock forward a day per firing would be testing the clock; this registers a second
    /// schedule for the same flow, exactly as <c>Program.cs</c> does for a demonstration run.
    /// </remarks>
    public const string Hourly = "0 * * * *";

    /// <summary>
    /// Every minute in UTC — the expression the overlap tests use, and they have to.
    /// </summary>
    /// <remarks>
    /// <strong>Because the flow declares <c>[FlowDeadline("PT10M")]</c> and the clock is the
    /// test's.</strong> Holding a firing open across an <em>hour</em> of wound-forward time
    /// expires its deadline, so the run under observation ends <c>TimedOut</c> and the assertion
    /// is about a flow that gave up rather than one that is still going. A minute between
    /// occurrences leaves the whole demonstration inside the deadline the sample declares.
    /// </remarks>
    public const string Minutely = "* * * * *";

    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresJournalOptions _options;

    private SchedulerCluster(
        NpgsqlDataSource dataSource,
        PostgresJournalOptions options,
        IReadOnlyList<string> tenants,
        IBank? bank)
    {
        _dataSource = dataSource;
        _options = options;

        Journal = new PostgresFlowJournal(dataSource);
        Leases = new PostgresLeaseStore(dataSource);
        Durability = new FlowDurability(Journal, Leases);
        Tenants = new DeclaredTenantDirectory([.. tenants]);
        Dispatcher = new DailyReconciliationFlow.Dispatcher(
            new LoadBankStatement(bank ?? Bank),
            new LoadLedgerSnapshot(Ledger),
            new MatchByAmountAndDate(),
            new MatchByReference(),
            new ProduceReport(Desk));

        Clock.Advance(T0 - Clock.UtcNow);
    }

    /// <summary>The ledger the reconciliation reads.</summary>
    public InMemoryLedger Ledger { get; } = new();

    /// <summary>The bank, whose latency is how a night is made to overrun.</summary>
    public InMemoryBank Bank { get; } = new();

    /// <summary>Where a finished report lands.</summary>
    public InMemoryReportDesk Desk { get; } = new();

    /// <summary>The clock every node and the engine read.</summary>
    public FlowTestClock Clock { get; } = new();

    /// <summary>The journal, in PostgreSQL, whose primary key settles every race here.</summary>
    public PostgresFlowJournal Journal { get; }

    /// <summary>The lease store, in PostgreSQL.</summary>
    public PostgresLeaseStore Leases { get; }

    /// <summary>The stores as a host and a sweep see them.</summary>
    public FlowDurability Durability { get; }

    /// <summary>Who a per-tenant fan-out visits.</summary>
    public DeclaredTenantDirectory Tenants { get; }

    /// <summary>The generated dispatcher, over the sample's own capabilities.</summary>
    public DailyReconciliationFlow.Dispatcher Dispatcher { get; }

    /// <summary>Which schedules every node in this fleet fires.</summary>
    public FlowScheduleCatalog Schedules { get; } = new();

    /// <summary>Opens a schema, migrates it, and books a day for each tenant.</summary>
    /// <param name="cancellationToken">Cancels the setup.</param>
    /// <param name="tenants">Who the fan-out visits. Empty for an untenanted fleet.</param>
    /// <returns>The cluster.</returns>
    /// <exception cref="InvalidOperationException">
    /// A database was promised by the environment and is not reachable.
    /// </exception>
    public static ValueTask<SchedulerCluster> CreateAsync(
        CancellationToken cancellationToken, params string[] tenants) =>
        CreateAsync(bank: null, cancellationToken, tenants);

    /// <summary>Opens a cluster whose bank is the caller's, so a night can be held open.</summary>
    /// <param name="bank">What <c>reconciliation.statement.read</c> calls, or null for the fixture's.</param>
    /// <param name="cancellationToken">Cancels the setup.</param>
    /// <param name="tenants">Who the fan-out visits.</param>
    /// <returns>The cluster.</returns>
    /// <remarks>
    /// <see cref="OverlapTests"/> is the reason this exists. Overlap is a statement about a run
    /// that has <em>not finished</em>, so proving it needs a firing that is genuinely still
    /// executing while the next sweep decides — which needs the step in the middle to be under
    /// the test's control rather than under a timer's.
    /// </remarks>
    public static async ValueTask<SchedulerCluster> CreateAsync(
        IBank? bank, CancellationToken cancellationToken, params string[] tenants)
    {
        if (!SchedulerDatabase.IsAvailable)
        {
            if (SchedulerDatabase.IsPromised)
            {
                throw new InvalidOperationException(
                    $"{SchedulerDatabase.ConnectionVariable} is set, so this run promised a " +
                    $"PostgreSQL server, and none answered. This is a failure rather than a " +
                    $"skip on purpose: a skip here would report every claim this sample makes " +
                    $"about a fleet as green against a database that was never reached. " +
                    SchedulerDatabase.Reason);
            }

            Assert.Skip(SchedulerDatabase.Reason);
        }

        var options = new PostgresJournalOptions
        {
            Schema = "flowx_s_" + Guid.NewGuid().ToString("n"),
        };

        var dataSource = ServiceCollectionExtensions.BuildDataSource(
            SchedulerDatabase.ConnectionString!, options);

        var cluster = new SchedulerCluster(dataSource, options, tenants, bank);

        await new PostgresMigrator(dataSource, options)
            .MigrateAsync(cancellationToken)
            .ConfigureAwait(false);

        cluster.Register(Hourly);
        cluster.Seed(tenants);

        // Eight replicas, all up since T0. More than any test uses; a scan that nothing sweeps
        // costs nothing, and constructing them here is what makes every node's floor the epoch.
        for (var node = 0; node < 8; node++)
        {
            cluster._fleet.Add(cluster.NodeNamed(
                "node-" + node.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        return cluster;
    }

    /// <summary>Registers the sample's flow under one expression and one set of options.</summary>
    /// <param name="cron">The expression this fleet fires.</param>
    /// <param name="overlap">What happens when the previous firing is still running.</param>
    /// <param name="missedFire">What happens to firings nobody was there to take.</param>
    /// <param name="jitter">The spread, or null for a schedule that fires on its occurrence.</param>
    /// <param name="perTenant">Whether one occurrence is one firing per tenant.</param>
    /// <remarks>
    /// The plan and the dispatcher are the sample's own — generated from its <c>Define</c> — so
    /// a change to the flow's shape reaches every assertion here. Only the schedule's own
    /// options are varied, because those are what each test is about.
    /// </remarks>
    public void Register(
        string cron,
        OverlapPolicy overlap = OverlapPolicy.Skip,
        MissedFirePolicy missedFire = MissedFirePolicy.RunOnce,
        string? jitter = null,
        bool perTenant = false) =>
        Schedules.Add(
            FlowSchedule.Create(
                DailyReconciliationFlow.Plan.Flow.Id,
                DailyReconciliationFlow.Plan.Flow.Version,
                cron,
                "UTC",
                missedFire,
                perTenant,
                overlap,
                jitter),
            DailyReconciliationFlow.Plan,
            Dispatcher);

    /// <summary>The schedule this fleet fires.</summary>
    public FlowSchedule Schedule => Schedules.Registrations[0].Schedule;

    /// <summary>
    /// A fleet of nodes that have been up since <see cref="T0"/>.
    /// </summary>
    /// <param name="count">How many replicas.</param>
    /// <returns>One sweep per node.</returns>
    /// <remarks>
    /// <strong>Built when the cluster is, not when this is called, and the difference is a rule
    /// rather than a convenience.</strong> A node's floor for a schedule the journal has never
    /// held is <em>when that node started</em> — a first deployment fires from its next
    /// occurrence rather than replaying a day of work nobody missed. A node constructed after the
    /// clock has been wound forward has therefore already missed the occurrence the test is
    /// about, correctly, and the test would be asserting against the wrong deployment. Use
    /// <see cref="Replacement"/> for a node that genuinely started later.
    /// </remarks>
    public IReadOnlyList<FlowScheduleScan> Nodes(int count) => [.. _fleet.Take(count)];

    /// <summary>
    /// Sweeps a fleet once while nothing is due, so every node holds its own floor before the
    /// occurrence a test is about falls due.
    /// </summary>
    /// <param name="fleet">The nodes.</param>
    /// <param name="cancellationToken">Cancels the sweeps.</param>
    /// <remarks>
    /// <para>
    /// <strong>This is what a replica that has been up for an hour is, and it is what makes a
    /// fleet race a race.</strong> A node takes its floor from the newest occurrence it has
    /// itself accounted for and asks the journal only when it has never accounted for one. A node
    /// whose first sweep of its life is the racing one therefore probes the journal — and if the
    /// winner's row is already there it correctly finds nothing due and never reaches the claim
    /// at all. On a loaded box that is how many of <em>n</em> nodes get as far as being refused,
    /// which is not a property of the mechanism and not something a count may be asserted
    /// against.
    /// </para>
    /// <para>
    /// A primed node reads no store to decide, so every node holds the occurrence before any of
    /// them writes a row and all of them race. The cold fleet is
    /// <see cref="FleetTests.AColdFleetFiresOnceHoweverManyNodesReachTheClaim"/>.
    /// </para>
    /// </remarks>
    public static async Task PrimeAsync(
        IReadOnlyList<FlowScheduleScan> fleet, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fleet);

        foreach (var node in fleet)
        {
            (await node.RunOnceAsync(cancellationToken)).Due.ShouldBe(
                0, "priming must run before the occurrence falls due, or it is the firing");
        }
    }

    /// <summary>
    /// A node that started just now, with no memory of any schedule.
    /// </summary>
    /// <returns>The sweep.</returns>
    /// <remarks>
    /// What a replacement pod is. It has no in-process accounting, so its floor comes from the
    /// journal — which is the path worth exercising, because it is the one that decides whether a
    /// restarted fleet re-fires what it already ran.
    /// </remarks>
    public FlowScheduleScan Replacement() => NodeNamed("replacement");

    private readonly List<FlowScheduleScan> _fleet = [];

    private FlowScheduleScan NodeNamed(string name)
    {
        var options = new FlowXOptions
        {
            ApplicationName = "Scheduler.Tests",
            NodeName = name,
        };

        return new FlowScheduleScan(
            new FlowHost(new FlowEngine(Clock), options, Durability),
            Schedules,
            Durability,
            options,
            Clock,
            Tenants);
    }

    /// <summary>How many instances of the sample's flow the journal holds.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The count.</returns>
    /// <remarks>
    /// Read out of the database rather than out of a sweep's report, because "how many times did
    /// this occurrence run" is a question about the store and a report is what one node thought
    /// it did.
    /// </remarks>
    public async ValueTask<int> InstanceCountAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT count(*) FROM flow_instance WHERE flow_id = @flow";
        command.Parameters.AddWithValue("flow", DailyReconciliationFlow.Plan.Flow.Id);

        return (int)(long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>The state of one occurrence's instance, or null when there is none.</summary>
    /// <param name="occurrence">The instant the expression named.</param>
    /// <param name="tenantId">Whose firing, or null.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The state, or null.</returns>
    public async ValueTask<FlowInstanceState?> StateOfAsync(
        DateTimeOffset occurrence, string? tenantId, CancellationToken cancellationToken)
    {
        var read = await Journal
            .ReadInstanceAsync(Schedule.InstanceIdFor(occurrence, tenantId), cancellationToken)
            .ConfigureAwait(false);

        return read.IsSuccess ? read.Value.State : null;
    }

    /// <summary>Books a day for each tenant, and for the untenanted fleet.</summary>
    /// <remarks>
    /// Three entries and two statement lines, so a report has a non-zero unmatched count: one
    /// reference the bank echoes, one it settles without a reference, and one it has not settled
    /// at all.
    /// </remarks>
    private void Seed(string[] tenants)
    {
        foreach (var tenant in tenants.Length == 0 ? [null] : tenants.Cast<string?>())
        {
            Ledger.Book(tenant, new LedgerEntry("ref-1", 12_50, T0));
            Ledger.Book(tenant, new LedgerEntry("ref-2", 99_00, T0));
            Ledger.Book(tenant, new LedgerEntry("ref-3", 5_00, T0));

            Bank.Post(tenant, new StatementLine("ref-1", 12_50, T0));
            Bank.Post(tenant, new StatementLine(string.Empty, 99_00, T0));
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await using (var connection = await _dataSource.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            // The parameterised-identifier route the migrator uses to create it.
            command.CommandText =
                """
                SELECT set_config('flowx.drop_schema', @schema, false);
                DO $$
                BEGIN
                    EXECUTE format('DROP SCHEMA IF EXISTS %I CASCADE', current_setting('flowx.drop_schema'));
                END
                $$;
                """;

            command.Parameters.AddWithValue("schema", _options.Schema);

            await command.ExecuteNonQueryAsync();
        }

        await _dataSource.DisposeAsync();
    }
}

/// <summary>Shared conveniences for this suite.</summary>
internal static class Cancellation
{
    /// <summary>The runner's own token, so a hung test is cancelled rather than timing out the run.</summary>
    public static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Asserts a sweep did what it was asked, naming what it actually did.</summary>
    /// <param name="report">The sweep's report.</param>
    /// <param name="fired">How many firings were expected.</param>
    /// <param name="because">Why.</param>
    public static void ShouldHaveFired(this ScheduleScanReport report, int fired, string because)
    {
        ArgumentNullException.ThrowIfNull(report);

        report.Fired.ShouldBe(
            fired,
            $"{because} — the sweep reported due={report.Due}, fired={report.Fired}, " +
            $"contended={report.Contended}, skipped={report.Skipped}, held={report.Held}, " +
            $"failed={report.Failed}.");
    }
}
