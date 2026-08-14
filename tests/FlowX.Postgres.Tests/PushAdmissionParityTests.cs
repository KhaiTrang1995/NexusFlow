using FlowX.Conformance.InMemory;
using FlowX.Hosting;
using FlowX.Runtime;
using Npgsql;
using Shouldly;
using Xunit;

namespace FlowX.Postgres.Tests;

/// <summary>
/// The push entry and the sweep write the same journal, against a real database.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the acceptance test for WP-140, and it is here rather than in
/// <c>FlowX.Hosting.Tests</c> because the claim is about rows.</strong> The in-memory journal
/// holds objects the same code path built, so a seam that diverged in what it wrote — a state,
/// a sequence, a step that never committed — would produce two identical object graphs and one
/// pair of different tables. PostgreSQL is where "identical journal rows" is a statement about
/// a journal.
/// </para>
/// <para>
/// <strong>What differs between the two arms is asserted, not excluded.</strong> An instance id
/// and a correlation id are derived from the delivery (ADR-0035), so two deliveries of two
/// messages cannot share them; each is checked against what its own message derives, which is a
/// stronger statement than dropping the column. The three genuinely per-execution columns —
/// <c>created_at</c>, <c>updated_at</c>, <c>deadline_at</c>, and the durations on the steps —
/// are clocks and are left out.
/// </para>
/// </remarks>
public sealed class PushAdmissionParityTests
{
    private const string Topic = "order.placed";

    private const string Group = "pricing";

    private static readonly CapabilityDescriptor Reprice =
        CapabilityDescriptor.Create("pricing.reprice", "1.0.0", isIdempotent: true);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>
    /// One message through <c>AdmitAsync</c> and one through <c>RunOnceAsync</c> leave the same
    /// rows behind.
    /// </summary>
    [Fact]
    public async Task ThePushEntryWritesTheRowsTheSweepWrites()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var fixture = Fixture.Over(schema);

        var swept = Guid.NewGuid();
        var pushed = Guid.NewGuid();

        fixture.Broker.Stage(Topic, partitionKey: "instance-1", eventId: swept);

        var sweep = await fixture.Scan.RunOnceAsync(Cancellation);

        sweep.Started.ShouldBe(1);

        var admission = await fixture.Scan.AdmitAsync(
            fixture.Registration, Fixture.Delivery(pushed), Cancellation);

        admission.Disposition.ShouldBe(BusDisposition.Started);

        // The derived identity, checked per arm rather than normalised away: this is the fact
        // ADR-0035 rests on, and it is the only reason two arms may legitimately differ.
        var sweptInstance = fixture.Registration.InstanceIdFor(swept);
        var pushedInstance = fixture.Registration.InstanceIdFor(pushed);

        admission.InstanceId.ShouldBe(pushedInstance);

        var one = await fixture.InstanceAsync(sweptInstance);
        var other = await fixture.InstanceAsync(pushedInstance);

        one.CorrelationId.ShouldBe(swept.ToString("d"));
        other.CorrelationId.ShouldBe(pushed.ToString("d"));

        other.Row.ShouldBe(
            one.Row,
            "the push entry and the sweep are one decision with two settlements. A difference " +
            "here is the two routes having drifted — a state, a sequence or a payload written " +
            "one way by a serverless host and another way by a worker.");

        other.Steps.ShouldBe(
            one.Steps,
            "and the histories match row for row. A seam that started the flow differently " +
            "would show up here first, because flow_step is what a resume reads.");

        one.Steps.ShouldNotBeEmpty("otherwise this test compares two empty lists.");
    }

    /// <summary>
    /// A poison message decided by the push entry writes nothing at all.
    /// </summary>
    /// <remarks>
    /// The other half of the parity claim, and the one worth checking against a database: "no
    /// journal row" is a statement about a table, and an instance row written for a message that
    /// was then dead-lettered would describe an execution that never happened — visible to
    /// recovery, to retention and to every operator query.
    /// </remarks>
    [Fact]
    public async Task APoisonMessageDecidedByThePushEntryWritesNoRow()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var fixture = Fixture.Over(schema);

        var admission = await fixture.Scan.AdmitAsync(
            fixture.Registration,
            BusDelivery.Unreadable("lock-token", 1, "instance-1", "its event-id field is absent"),
            Cancellation);

        admission.Disposition.ShouldBe(BusDisposition.DeadLetter);
        admission.Reason.ShouldNotBeNull().ShouldContain("not a message");

        (await fixture.InstanceCountAsync()).ShouldBe(
            0,
            "ADR-0038 diverts the entry; nothing ran, so flow_instance must hold nothing. A row " +
            "here would be an execution the journal claims happened.");
    }

    /// <summary>One node over one schema, with a broker the push arm never touches.</summary>
    private sealed class Fixture
    {
        private readonly NpgsqlDataSource _dataSource;

        private Fixture(PostgresTestSchema schema)
        {
            _dataSource = schema.DataSource;

            var options = new FlowXOptions
            {
                ApplicationName = "Tests",
                NodeName = "node",
                ShutdownDrainTimeout = TimeSpan.FromSeconds(5),
            };

            var durability = new FlowDurability(schema.Journal, schema.Leases);

            // A stopped clock, because a step's captured nondeterminism is a clock reading and
            // the two arms are compared column for column. With SystemClock the only difference
            // between them would be the microsecond each one ran at, which is a fact about the
            // test rather than about the seam.
            var host = new FlowHost(new FlowEngine(new FixedClock()), options, durability);

            var subscriptions = new FlowBusCatalog().Add(
                new BusSubscription("pricing.reprice", "1.0.0", Topic, Group),
                ExecutionPlan.Create(
                    FlowDescriptor.Create(
                        "pricing.reprice", "1.0.0", ExecutionProfile.Durable, TimeSpan.FromMinutes(5)),
                    StepGraph.Create([StepNode.ForCapability(0, Reprice)])),
                new SucceedingDispatcher());

            Registration = subscriptions.Registrations[0];
            Scan = new FlowBusScan(host, subscriptions, Broker, durability, options);
        }

        public RecordingBusConsumer Broker { get; } = new();

        public BusRegistration Registration { get; }

        public FlowBusScan Scan { get; }

        public static Fixture Over(PostgresTestSchema schema) => new(schema);

        /// <summary>A delivery as a push platform would hand one over.</summary>
        public static BusDelivery Delivery(Guid eventId) => BusDelivery.Of(
            new BusMessage(eventId, Topic, Topic, "1.0.0", "instance-1", null, null),
            "lock-token",
            1);

        /// <summary>How many instances the schema holds, whatever they are.</summary>
        public async Task<long> InstanceCountAsync()
        {
            await using var connection = await _dataSource.OpenConnectionAsync(Cancellation);
            await using var command = connection.CreateCommand();

            command.CommandText = "SELECT count(*) FROM flow_instance";

            return (long)(await command.ExecuteScalarAsync(Cancellation))!;
        }

        /// <summary>One instance's row and history, as strings an assertion can compare.</summary>
        public async Task<Journalled> InstanceAsync(Guid instanceId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync(Cancellation);

            string row;
            string correlationId;

            await using (var command = connection.CreateCommand())
            {
                // Every column except the four clocks and the two derived identities, which are
                // asserted on their own above.
                command.CommandText =
                    "SELECT flow_id, flow_version, tenant_id, state, fence, next_sequence, " +
                    "       resume_from_step, input::text, state_bag::text, trace_id, " +
                    "       parent_instance_id, parent_scope, parent_step_id, version, " +
                    "       correlation_id " +
                    "FROM flow_instance WHERE instance_id = @instance";

                command.Parameters.AddWithValue("instance", instanceId);

                await using var reader = await command.ExecuteReaderAsync(Cancellation);

                (await reader.ReadAsync(Cancellation)).ShouldBeTrue(
                    $"no flow_instance row for {instanceId}, so this arm did not start its flow.");

                row = string.Join(
                    "|",
                    Enumerable
                        .Range(0, reader.FieldCount - 1)
                        .Select(column => reader.IsDBNull(column)
                            ? "null"
                            : reader.GetValue(column).ToString()));

                correlationId = reader.GetString(reader.FieldCount - 1);
            }

            var steps = new List<string>();

            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT scope, step_id, attempt, sequence, capability_id, capability_version, " +
                    "       outcome, result::text, nondeterministic::text " +
                    "FROM flow_step WHERE instance_id = @instance ORDER BY sequence";

                command.Parameters.AddWithValue("instance", instanceId);

                await using var reader = await command.ExecuteReaderAsync(Cancellation);

                while (await reader.ReadAsync(Cancellation))
                {
                    steps.Add(string.Join(
                        "|",
                        Enumerable
                            .Range(0, reader.FieldCount)
                            .Select(column => reader.IsDBNull(column)
                                ? "null"
                                : reader.GetValue(column).ToString())));
                }
            }

            return new Journalled(row, correlationId, steps);
        }
    }

    /// <summary>What one arm left in the journal.</summary>
    /// <param name="Row">Its instance row, minus the clocks and the derived ids.</param>
    /// <param name="CorrelationId">The correlation id, checked against the message it came from.</param>
    /// <param name="Steps">Its committed steps, in commit order.</param>
    private sealed record Journalled(
        string Row, string CorrelationId, IReadOnlyList<string> Steps);

    /// <summary>A clock that does not move, so two executions capture the same instant.</summary>
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
    }

    /// <summary>A dispatcher whose one step succeeds, so an admitted message reaches a terminal row.</summary>
    private sealed class SucceedingDispatcher : IStepDispatcher
    {
        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct) =>
            ValueTask.FromResult(StepOutcome.Success);

        public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct) =>
            ValueTask.FromResult(StepOutcome.Success);

        public bool Evaluate(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException("This double runs plans with no branch step.");

        public int Select(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException("This double runs plans with no switch step.");

        public IterationSource BeginIteration(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to begin.");

        public FlowContext EnterIteration(
            int stepIndex, in IterationSource source, int iteration, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to enter.");
    }
}
