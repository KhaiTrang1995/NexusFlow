using System.Text.Json;
using System.Text.Json.Serialization;
using FlowX.Conformance.InMemory;
using Shouldly;
using Xunit;

namespace FlowX.Runtime.Tests;

/// <summary>
/// The first link of the outbox chain: an <c>.Emit</c> step stages a row, in the same
/// transaction as the step that emitted it.
/// </summary>
/// <remarks>
/// <para>
/// WP-56 built the last three links — the table, the atomic commit and the publisher — and
/// left this one open, which is why <c>FLOWX1024</c> could not be retired: the engine never
/// set <c>StepCommit.Outbox</c> and <c>IStepDispatcher.DescribeStep</c> returned no event, so
/// the publisher drained an empty table. Everything below is about the row existing and about
/// it existing <em>only</em> where it should.
/// </para>
/// <para>
/// <strong>The store is the reference journal from the conformance suite</strong>, for the
/// reason <c>DurableSeamTests</c> gives: a store written for these tests would be a store
/// nothing holds to <c>JournalConformance</c>, and the first thing it would get wrong is
/// deciding every refusal before it writes — which is the property the rollback assertion
/// below rests on.
/// </para>
/// </remarks>
public sealed class OutboxStagingTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;
    private static readonly FencingToken First = new(1);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>The same plan, re-declared <c>Durable</c>.</summary>
    private static ExecutionPlan Durable(ExecutionPlan plan) => ExecutionPlan.Create(
        FlowDescriptor.Create(
            plan.Flow.Id, plan.Flow.Version, ExecutionProfile.Durable, plan.Flow.Deadline),
        plan.Graph);

    private static async Task<DurableExecution> BeginAsync(
        InMemoryFlowJournal journal, ExecutionPlan plan, Guid instanceId)
    {
        var begun = await DurableExecution.BeginAsync(
            journal, plan, Plans.Invocation, instanceId, First, cancellationToken: Cancellation);

        begun.IsSuccess.ShouldBeTrue(begun.IsFailure ? begun.Error.ToString() : null);

        return begun.Value;
    }

    private static async Task<IReadOnlyList<OutboxRecord>> OutboxAsync(
        InMemoryFlowJournal journal, Guid instanceId)
    {
        var staged = await journal.ReadOutboxAsync(instanceId, Cancellation);

        staged.IsSuccess.ShouldBeTrue(staged.IsFailure ? staged.Error.ToString() : null);

        return staged.Value;
    }

    /// <summary>
    /// The event the emit step of <see cref="Plans.FourStepSaga"/> stands for, described the
    /// way a generated dispatcher describes one.
    /// </summary>
    private static OutboxWrite Placed(FlowContext ctx) => new()
    {
        Type = "order.placed",
        SchemaVersion = "1.0.0",
        PartitionKey = ctx.FlowInstanceId,
        Payload = JournalPayload.Of(
            new OrderPlaced("order-77", "tok_live_4242424242424242"),
            EventContracts.Default.OrderPlaced,
            EmittingFlow.SensitiveMembers),
    };

    /// <summary>Describes the emit step and nothing else, as a generated dispatcher would.</summary>
    private static StepJournalEntry DescribeEmitOnly(int stepIndex, FlowContext ctx) =>
        stepIndex == 3 ? StepJournalEntry.OfEvent(Placed(ctx)) : StepJournalEntry.Nothing;

    // ---------------------------------------------------------------------------------
    // The row exists
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// A durable flow's <c>.Emit</c> step stages exactly one row, with the step that emitted it.
    /// </summary>
    /// <remarks>
    /// One row rather than at-least-one: a step that staged twice would be a duplicate the
    /// consumer's idempotency key cannot collapse, because each copy carries its own event id.
    /// </remarks>
    [Fact]
    public async Task AnEmitStepOnADurableFlowStagesExactlyOneOutboxRow()
    {
        var journal = new InMemoryFlowJournal();
        var plan = Durable(Plans.FourStepSaga());
        var instanceId = Guid.NewGuid();
        var run = await BeginAsync(journal, plan, instanceId);

        var dispatcher = new RecordingDispatcher { Describe = DescribeEmitOnly };
        var engine = new FlowEngine(new FakeClock(T0));

        var result = await engine.ExecuteAsync(plan, dispatcher, Plans.Invocation, run, Cancellation);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error!.ToString() : null);

        var staged = await OutboxAsync(journal, instanceId);

        staged.Count.ShouldBe(1,
            "Four steps ran and one of them emits. A row per step would mean the engine " +
            "staged whatever the dispatcher last described rather than what this step produced.");

        staged[0].Type.ShouldBe("order.placed");
        staged[0].SchemaVersion.ShouldBe("1.0.0");
        staged[0].InstanceId.ShouldBe(instanceId);

        staged[0].PartitionKey.ShouldBe(instanceId.ToString(),
            "Two events from one instance must reach a consumer in the order the instance " +
            "staged them, and ADR-0018 offers that per partition key and nowhere else.");
    }

    /// <summary>
    /// The payload is the JSON the generated context wrote, not a re-serialisation of it.
    /// </summary>
    [Fact]
    public async Task TheStagedPayloadIsWhatTheGeneratedContextWrote()
    {
        var journal = new InMemoryFlowJournal();
        var plan = Durable(Plans.FourStepSaga());
        var instanceId = Guid.NewGuid();
        var run = await BeginAsync(journal, plan, instanceId);

        var dispatcher = new RecordingDispatcher { Describe = DescribeEmitOnly };
        var engine = new FlowEngine(new FakeClock(T0));

        await engine.ExecuteAsync(plan, dispatcher, Plans.Invocation, run, Cancellation);

        var staged = await OutboxAsync(journal, instanceId);
        var json = staged.ShouldHaveSingleItem().PayloadJson.ShouldNotBeNull();

        using var document = JsonDocument.Parse(json);

        document.RootElement.GetProperty("orderId").GetString().ShouldBe("order-77",
            "The property name is the generated context's camelCase policy, which is the " +
            "proof that the row holds what that context wrote rather than what a reflecting " +
            "serialiser would have produced from the same object.");
    }

    // ---------------------------------------------------------------------------------
    // [Sensitive]
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// A marked member is redacted in the staged row, structurally.
    /// </summary>
    /// <remarks>
    /// An event payload is a <em>second</em> sink for <c>[Sensitive]</c> values, and it
    /// inherits the first one's control rather than re-implementing it:
    /// <c>OutboxWrite.Payload</c> is a <see cref="JournalPayload"/>, there is no accessor for
    /// the value on one, and its only exit — <see cref="JournalPayload.ToJson"/> — redacts. A
    /// store is handed the payload and never a blob, so the outbox writer has no route to the
    /// object graph even if it wanted one.
    /// </remarks>
    [Fact]
    public async Task ASensitiveMemberIsRedactedInTheStagedOutboxRow()
    {
        var journal = new InMemoryFlowJournal();
        var plan = Durable(Plans.FourStepSaga());
        var instanceId = Guid.NewGuid();
        var run = await BeginAsync(journal, plan, instanceId);

        var dispatcher = new RecordingDispatcher { Describe = DescribeEmitOnly };
        var engine = new FlowEngine(new FakeClock(T0));

        await engine.ExecuteAsync(plan, dispatcher, Plans.Invocation, run, Cancellation);

        var staged = await OutboxAsync(journal, instanceId);
        var json = staged.ShouldHaveSingleItem().PayloadJson.ShouldNotBeNull();

        json.ShouldNotContain("tok_live_4242424242424242", Case.Sensitive,
            "A marked member reached a queue a broker will fan out to every consumer, which " +
            "is strictly worse than the journal row the same attribute already guards.");

        json.ShouldContain(JournalPayload.Redacted, Case.Sensitive,
            "The key survives redacted rather than vanishing, for the reason it does on a " +
            "journal row: a consumer needs to know the field was withheld, not absent.");

        json.ShouldContain("order-77", Case.Sensitive,
            "Only the marked member is withheld.");
    }

    // ---------------------------------------------------------------------------------
    // The row does not exist
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// A refused step commit takes its outbox row with it.
    /// </summary>
    /// <remarks>
    /// This is the whole point of staging inside the step's transaction rather than beside
    /// it. A fenced-out node has lost the instance; an event it staged anyway would be
    /// published by whoever drains the table next, announcing a state change that never
    /// committed.
    /// </remarks>
    [Fact]
    public async Task ARefusedStepCommitStagesNoOutboxRow()
    {
        var journal = new InMemoryFlowJournal();
        var plan = Durable(Plans.OneStepEmit());
        var instanceId = Guid.NewGuid();
        var run = await BeginAsync(journal, plan, instanceId);

        // Another node takes the lease and raises the fence. This node's token is now stale,
        // so the commit that would have carried the event is refused.
        (await journal.FenceAsync(instanceId, new FencingToken(9), Cancellation))
            .IsSuccess.ShouldBeTrue();

        var dispatcher = new RecordingDispatcher
        {
            Describe = static (_, ctx) => StepJournalEntry.OfEvent(Placed(ctx)),
        };

        var engine = new FlowEngine(new FakeClock(T0));

        var result = await engine.ExecuteAsync(plan, dispatcher, Plans.Invocation, run, Cancellation);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(DurabilityErrors.FencedOutCode);

        (await OutboxAsync(journal, instanceId)).ShouldBeEmpty(
            "The step row was refused, so the event that step emitted must be refused with " +
            "it. An outbox row outliving the commit it belonged to is the dual write the " +
            "outbox exists to remove.");
    }

    /// <summary>
    /// An ephemeral flow stages nothing, whatever its dispatcher would have described.
    /// </summary>
    /// <remarks>
    /// The plan is the same one the durable assertions use, re-declared <c>Ephemeral</c>: an
    /// emit step is legal there and compiles into the plan, and there is simply no
    /// transaction for it to be part of. <see cref="ExecutionPlan.HasEmit"/> is the flag the
    /// seam is gated on, and <c>EngineAllocationTests</c> holds the same path to zero bytes.
    /// </remarks>
    [Fact]
    public async Task AnEphemeralFlowStagesNothing()
    {
        var journal = new InMemoryFlowJournal();
        var plan = Plans.FourStepSaga();
        var instanceId = Guid.NewGuid();

        // Opened so the read below has an instance to read, not because the flow uses it.
        (await journal.StartAsync(
            new FlowInstanceStart
            {
                InstanceId = instanceId,
                FlowId = plan.Flow.Id,
                FlowVersion = plan.Flow.Version,
                Token = First,
            },
            Cancellation)).IsSuccess.ShouldBeTrue();

        var dispatcher = new RecordingDispatcher { Describe = DescribeEmitOnly };
        var engine = new FlowEngine(new FakeClock(T0));

        var result = await engine.ExecuteAsync(plan, dispatcher, Plans.Invocation, Cancellation);

        result.IsSuccess.ShouldBeTrue();

        (await OutboxAsync(journal, instanceId)).ShouldBeEmpty();
    }

    /// <summary>
    /// A durable flow with no emit step stages nothing.
    /// </summary>
    /// <remarks>
    /// The complement of the gate: <see cref="ExecutionPlan.HasEmit"/> is false, so the
    /// engine never reads the event a dispatcher offered. A dispatcher describing one anyway
    /// is exactly the disagreement between a plan and a dispatcher from a different build
    /// that the rest of <c>IStepDispatcher</c> treats as a defect.
    /// </remarks>
    [Fact]
    public async Task ADurableFlowWithNoEmitStepStagesNothing()
    {
        var journal = new InMemoryFlowJournal();
        var plan = Durable(Plans.TwoStepQuery());
        var instanceId = Guid.NewGuid();
        var run = await BeginAsync(journal, plan, instanceId);

        plan.HasEmit.ShouldBeFalse();

        var dispatcher = new RecordingDispatcher
        {
            Describe = static (_, ctx) => StepJournalEntry.OfEvent(Placed(ctx)),
        };

        var engine = new FlowEngine(new FakeClock(T0));

        (await engine.ExecuteAsync(plan, dispatcher, Plans.Invocation, run, Cancellation))
            .IsSuccess.ShouldBeTrue();

        (await OutboxAsync(journal, instanceId)).ShouldBeEmpty();
    }
}

/// <summary>The event contract an emit step publishes, with one member the flow marked.</summary>
public sealed record OrderPlaced(string OrderId, string PaymentToken);

/// <summary>
/// The generated JSON context an emitting flow's dispatcher names.
/// </summary>
/// <remarks>
/// camelCase, like the reference sample's, so the assertion on the stored property name is
/// evidence that the row holds what <em>this</em> context wrote.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OrderPlaced))]
internal sealed partial class EventContracts : JsonSerializerContext;

/// <summary>Stands in for the generated partial class the array is emitted onto.</summary>
internal static class EmittingFlow
{
    public static readonly string[] SensitiveMembers = ["PaymentToken"];
}
