using Azure.Messaging.ServiceBus;
using FlowX.Generated;
using FlowX.Hosting;
using FlowX.Postgres;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Functions.Tests;

/// <summary>Whether a PostgreSQL server answered, and what to say when it did not.</summary>
/// <remarks>
/// <c>Scheduler.Tests</c>'s probe and its reasons: a skipped test that says nothing is a test
/// that has quietly stopped covering its subject.
/// </remarks>
internal static class WorkerDatabase
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

    /// <summary>Whether a server answered.</summary>
    public static bool IsAvailable => Probe.Value.Available;

    /// <summary>Why, in a sentence a reader can act on.</summary>
    public static string Reason => Probe.Value.Reason;

    private static (bool, string) Run()
    {
        if (ConnectionString is not { } connectionString)
        {
            return (false,
                "No PostgreSQL server is configured, so nothing about what a pushed item does " +
                $"to the journal has been verified. Set {ConnectionVariable}.");
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
    }
}

/// <summary>
/// One worker, wired the way <c>Program.cs</c> wires it, with the generated entry points on top.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The entry points under test are the generated ones.</strong>
/// <see cref="Entry"/> is <c>FlowX.Generated.FunctionsFunctions</c> — the class in
/// <c>obj/generated</c>, constructed exactly as the worker's container would construct it — so
/// a test here exercises the emitted text rather than a re-implementation of what it ought to
/// say. That is the whole reason this project references the sample instead of the runtime.
/// </para>
/// <para>
/// <strong>What is real and what is not.</strong> The journal, the lease store and the flows are
/// real; the platform is not, because it cannot be — see <c>PushedItemTests</c> for what is
/// owed. A schema per fixture, for <c>Scheduler.Tests</c>'s reason: no leftover instance row and
/// no ordering dependency between tests.
/// </para>
/// </remarks>
internal sealed class WorkerFixture : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _schema;

    private WorkerFixture(NpgsqlDataSource dataSource, string schema, IServiceProvider services)
    {
        _dataSource = dataSource;
        _schema = schema;
        Services = services;

        Entry = new FunctionsFunctions(services.GetRequiredService<FlowPushSeams>(), services);
    }

    /// <summary>The generated entry points, as the worker's container would build them.</summary>
    public FunctionsFunctions Entry { get; }

    /// <summary>The container the worker runs on.</summary>
    public IServiceProvider Services { get; }

    /// <summary>The order book the capabilities write to.</summary>
    public InMemoryOrderBook Book => Services.GetRequiredService<InMemoryOrderBook>();

    /// <summary>Builds a worker over its own schema.</summary>
    /// <param name="ct">Cancels the migration.</param>
    public static async Task<WorkerFixture> CreateAsync(CancellationToken ct)
    {
        var options = new PostgresJournalOptions
        {
            Schema = "flowx_f_" + Guid.NewGuid().ToString("n"),
        };

        // Fully qualified: the worker SDK ships two ServiceCollectionExtensions of its own and
        // an unqualified name here is ambiguous between three.
        var dataSource = FlowX.Postgres.ServiceCollectionExtensions.BuildDataSource(
            WorkerDatabase.ConnectionString!, options);

        await new PostgresMigrator(dataSource, options).MigrateAsync(ct).ConfigureAwait(false);

        var services = new ServiceCollection();

        services.AddFlowX(flowx =>
        {
            flowx.ApplicationName = "Functions";

            // The line the sample's Program.cs argues for, repeated here because a fixture that
            // left the sweeps on would have a background loop racing the entry point under test
            // — and would pass, because ADR-0031 makes the duplicate inert. It would just be
            // asserting the sweep rather than the push.
            flowx.Sweeps = HostSweeps.None;
        });

        services.AddFlowXPostgres(WorkerDatabase.ConnectionString!, options);
        services.AddSingleton<InMemoryOrderBook>();
        services.AddFlowXCapabilities();

        var provider = services.BuildServiceProvider();

        provider.AddFlowXSubscriptions();
        provider.AddFlowXSchedules();

        return new WorkerFixture(dataSource, options.Schema, provider);
    }

    /// <summary>
    /// How many instances of one flow the journal holds.
    /// </summary>
    /// <param name="flowId">The flow.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <remarks>
    /// Read with SQL rather than through <c>IFlowJournal</c>, which offers no query by flow —
    /// it reads one instance by the id a caller already derived, because that is the only read
    /// the runtime itself ever needs. Counting is a test's question, and asking it of the table
    /// directly is more honest than widening the contract for it.
    /// </remarks>
    public async Task<int> InstanceCountAsync(string flowId, CancellationToken ct)
    {
        await using var command = _dataSource.CreateCommand(
            $"SELECT count(*) FROM \"{_schema}\".flow_instance WHERE flow_id = $1");

        command.Parameters.AddWithValue(flowId);

        return (int)(long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    /// <summary>A Service Bus message as the platform would hand one over.</summary>
    /// <param name="order">The order the body carries.</param>
    /// <param name="eventId">The message id, which becomes the event id.</param>
    /// <param name="deliveryCount">How many times it has been handed out, including now.</param>
    /// <remarks>
    /// Built through <c>ServiceBusModelFactory</c>, which is the SDK's own supported way to
    /// construct a received message: the type is sealed with an internal constructor precisely
    /// so that nobody fakes one badly.
    /// </remarks>
    public static ServiceBusReceivedMessage Message(
        OrderPlaced order, Guid eventId, int deliveryCount = 1) =>
        ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString(System.Text.Json.JsonSerializer.Serialize(
                order, FunctionsJson.Default.OrderPlaced)),
            messageId: eventId.ToString("d"),
            subject: "order.placed",
            partitionKey: order.OrderId,
            deliveryCount: deliveryCount,
            lockTokenGuid: Guid.NewGuid());

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await using (var command = _dataSource.CreateCommand(
            $"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE"))
        {
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await _dataSource.DisposeAsync().ConfigureAwait(false);

        if (Services is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// A stand-in for the platform's settle verbs, which records rather than answers a broker.
/// </summary>
/// <remarks>
/// <strong>This is the seam the generated entry point settles through, and recording it is the
/// point.</strong> WP-140's constraint is that the runtime decides and the caller settles; what
/// a test can check on a push host is that the verb the entry point chose matches the
/// disposition the runtime returned. A real Service Bus would answer these and tell us nothing.
/// </remarks>
internal sealed class RecordingMessageActions : ServiceBusMessageActions
{
    /// <summary>Creates a recorder.</summary>
    /// <remarks>
    /// Declared because the base's own constructor is protected and Sonar reads an
    /// implicitly-generated one as making the class uninstantiable.
    /// </remarks>
    public RecordingMessageActions()
    {
    }

    /// <summary>The messages completed.</summary>
    public List<string> Completed { get; } = [];

    /// <summary>The messages abandoned.</summary>
    public List<string> Abandoned { get; } = [];

    /// <summary>The messages dead-lettered, with the description each carried.</summary>
    public List<(string MessageId, string? Description)> DeadLettered { get; } = [];

    /// <inheritdoc />
    public override Task CompleteMessageAsync(
        ServiceBusReceivedMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        Completed.Add(message.MessageId);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task AbandonMessageAsync(
        ServiceBusReceivedMessage message,
        IDictionary<string, object>? propertiesToModify = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        Abandoned.Add(message.MessageId);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task DeadLetterMessageAsync(
        ServiceBusReceivedMessage message,

        // A Dictionary and not an IDictionary, unlike every sibling on this type. Matched to
        // the SDK rather than tidied, because an override that does not match does not override.
        Dictionary<string, object>? propertiesToModify = null,
        string? deadLetterReason = null,
        string? deadLetterErrorDescription = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        DeadLettered.Add((message.MessageId, deadLetterErrorDescription));

        return Task.CompletedTask;
    }
}
