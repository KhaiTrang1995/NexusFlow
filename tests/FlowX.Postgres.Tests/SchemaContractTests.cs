using Shouldly;
using Xunit;

namespace FlowX.Postgres.Tests;

/// <summary>
/// The places where the C# contract and the SQL schema have to agree, checked against the
/// database rather than against a reading of it.
/// </summary>
public sealed class SchemaContractTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>
    /// Every declared instance state is a value the <c>CHECK</c> constraint accepts.
    /// </summary>
    /// <remarks>
    /// <c>FlowInstanceState</c>'s own remarks say "the set is closed and matches the CHECK
    /// constraint", which is a claim about two files that nothing checked. Adding a member
    /// without adding it to <c>0001_initial_schema.sql</c> would leave a flow that cannot
    /// record the state it is in — and it would fail at the first instance that reached it,
    /// in production, rather than here.
    /// </remarks>
    [Theory]
    [InlineData(FlowInstanceState.Pending)]
    [InlineData(FlowInstanceState.Running)]
    [InlineData(FlowInstanceState.Suspended)]
    [InlineData(FlowInstanceState.Compensating)]
    [InlineData(FlowInstanceState.Completed)]
    [InlineData(FlowInstanceState.Failed)]
    [InlineData(FlowInstanceState.TimedOut)]
    [InlineData(FlowInstanceState.CompensationFailed)]
    public async Task EveryInstanceStateIsAcceptedByTheCheckConstraint(FlowInstanceState state)
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var instance = Guid.NewGuid();

        await schema.Journal.StartAsync(
            new FlowInstanceStart
            {
                InstanceId = instance,
                FlowId = "order.place",
                FlowVersion = "1.0.0",
                Token = new FencingToken(1),
            },
            Cancellation);

        var completed = await schema.Journal.CompleteAsync(
            instance, new FencingToken(1), state, JournalPayload.Empty, Cancellation);

        completed.IsSuccess.ShouldBeTrue(
            $"'{state}' must be storable. The enum and the CHECK constraint on " +
            "flow_instance.state are one closed set, not two that happen to agree. " +
            $"{(completed.IsFailure ? completed.Error.ToString() : string.Empty)}");

        completed.Value.State.ShouldBe(state, "and it round-trips through the stored text.");
    }

    /// <summary>Every declared outcome is a value the step table accepts.</summary>
    [Theory]
    [InlineData(JournalOutcome.Success)]
    [InlineData(JournalOutcome.Failure)]
    [InlineData(JournalOutcome.Compensated)]
    public async Task EveryOutcomeIsAcceptedByTheCheckConstraint(JournalOutcome outcome)
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var instance = Guid.NewGuid();

        await schema.Journal.StartAsync(
            new FlowInstanceStart
            {
                InstanceId = instance,
                FlowId = "order.place",
                FlowVersion = "1.0.0",
                Token = new FencingToken(1),
            },
            Cancellation);

        var committed = await schema.Journal.CommitAsync(
            new StepCommit
            {
                Key = StepKey.First(instance, 0),
                Token = new FencingToken(1),
                CapabilityId = "inventory.reserve",
                CapabilityVersion = "2.1.0",
                Outcome = outcome,
            },
            Cancellation);

        committed.IsSuccess.ShouldBeTrue(
            $"'{outcome}' must be storable. " +
            $"{(committed.IsFailure ? committed.Error.ToString() : string.Empty)}");

        committed.Value.Outcome.ShouldBe(outcome);
    }

    /// <summary>
    /// A payload comes back exactly as the generated JSON context wrote it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the assertion that chose <c>json</c> over <c>jsonb</c>, and it is
    /// worth keeping as its own test rather than leaving it inside the conformance
    /// suite.</strong> Every payload column in the ERD drawn in
    /// <c>docs/11-Distributed-Runtime.md §2</c> is <c>jsonb</c>, and <c>jsonb</c> is a
    /// parsed representation rather than a document: it sorts object keys, re-renders
    /// separators with a space after the colon, and discards all but the last of a repeated
    /// key. Against a <c>jsonb</c> column the string below comes back as
    /// <c>{"OrderId": "order-7", "Quantity": 3, "PaymentToken": "tok"}</c> and ADR-0015
    /// commitment 5 stops being true.
    /// </para>
    /// <para>
    /// The journal is the replay source and the audit source. Neither survives a store that
    /// silently reorders and reformats what it was given, which is why the departure from
    /// the ERD is the schema's and not the contract's.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task APayloadIsStoredByteForByteAsItWasWritten()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var instance = Guid.NewGuid();
        var key = StepKey.First(instance, 0);

        await schema.Journal.StartAsync(
            new FlowInstanceStart
            {
                InstanceId = instance,
                FlowId = "order.place",
                FlowVersion = "1.0.0",
                Token = new FencingToken(1),
            },
            Cancellation);

        var payload = JournalPayload.Of(
            new Conformance.ConformanceOrder("order-7", "tok", 3),
            Conformance.ConformanceJson.Default.ConformanceOrder);

        var written = payload.ToJson();

        await schema.Journal.CommitAsync(
            new StepCommit
            {
                Key = key,
                Token = new FencingToken(1),
                CapabilityId = "inventory.reserve",
                CapabilityVersion = "2.1.0",
                Outcome = JournalOutcome.Success,
                Result = payload,
            },
            Cancellation);

        var frontier = await schema.Journal.ReadResumeFrontierAsync(instance, Cancellation);

        frontier.Value.Find(key)!.ResultJson.ShouldBe(
            written,
            "the stored document is the one the generated context produced, character for " +
            "character. A jsonb column would have reordered the keys and inserted a space " +
            "after every colon, and ADR-0015 commitment 5 would be false.");
    }

    /// <summary>
    /// A lease can be taken on an instance the journal has never heard of.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ERD makes <c>flow_lease.instance_id</c> a foreign key to <c>flow_instance</c>,
    /// and it cannot be one. <c>FlowInstanceStart.Token</c> is "the token of the lease held
    /// while starting", so the lease exists before the instance row does — the foreign key
    /// is inverted in time and would refuse the first acquisition of every flow.
    /// </para>
    /// <para>
    /// It would also make a lease store unusable for a journal kept elsewhere, which is the
    /// arrangement <c>ILeaseStore</c>'s own remarks describe and the one WP-54 exists to
    /// demonstrate.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ALeaseIsTakenBeforeTheInstanceExists()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var instance = Guid.NewGuid();

        var acquired = await schema.Leases.AcquireAsync(
            instance, "node-1", TimeSpan.FromMinutes(5), Cancellation);

        acquired.IsSuccess.ShouldBeTrue(
            "a node acquires the lease and only then opens the instance row, so the lease " +
            "table cannot carry a foreign key to a row that does not exist yet. " +
            $"{(acquired.IsFailure ? acquired.Error.ToString() : string.Empty)}");

        var started = await schema.Journal.StartAsync(
            new FlowInstanceStart
            {
                InstanceId = instance,
                FlowId = "order.place",
                FlowVersion = "1.0.0",
                Token = acquired.Value.Token,
            },
            Cancellation);

        started.IsSuccess.ShouldBeTrue("and the instance opens with the token it was issued.");
        started.Value.Fence.ShouldBe(acquired.Value.Token, "which becomes its opening fence.");
    }
}
