using System.Text.Json;
using System.Text.Json.Serialization;
using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Postgres.Tests;

/// <summary>
/// <c>.Emit&lt;T&gt;()</c>, from the author's chain to a broker, with nothing hand-built in
/// between.
/// </summary>
/// <remarks>
/// <para>
/// This is the test <c>docs/diagnostics/FLOWX1024.md</c> named as the condition for deleting
/// itself: the crash and double-publish tests in <c>OutboxPublisherTests</c> "become reachable
/// from a flow rather than from a hand-built commit". Every one of those stages its
/// <c>StepCommit.Outbox</c> by hand, which proved the store and the publisher and could not
/// prove that anything ever puts a row there.
/// </para>
/// <para>
/// <strong>Both generators run over this project.</strong> <c>FlowX.Compiler</c> emits
/// <see cref="OrderFlow"/>'s plan and dispatcher, and System.Text.Json's generator emits
/// <see cref="OrderJson"/>'s metadata — so the <c>JournalPayload.Of(value, context, members)</c>
/// call in the generated <c>DescribeStep</c> is bound by a real build against a real
/// generated context. The emitter tests assert the text; this asserts that the text is a
/// program.
/// </para>
/// </remarks>
public sealed class EmitReachesTheBrokerTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>An emitted event reaches a broker, body and all.</summary>
    [Fact]
    public async Task AnEmittedEventReachesTheBroker()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var instanceId = Guid.NewGuid();
        await RunAsync(schema, instanceId, new PlaceOrder("SKU-1", "tok_live_4242424242424242"));

        var broker = new RecordingEventPublisher();
        var pass = await schema.OutboxPublisher(broker).PublishPendingAsync(Cancellation);

        pass.Published.ShouldBe(1, pass.Failure?.ToString());

        var delivered = broker.Delivered.ShouldHaveSingleItem();

        delivered.Type.ShouldBe("order.placed");
        delivered.InstanceId.ShouldBe(instanceId);
        delivered.SchemaVersion.ShouldBe("1.0.0");

        delivered.PartitionKey.ShouldBe(instanceId.ToString(),
            "Per-key ordering is the only ordering ADR-0018 offers, and the key an instance's " +
            "own events share is the instance.");

        using var body = JsonDocument.Parse(delivered.PayloadJson.ShouldNotBeNull());

        body.RootElement.GetProperty("sku").GetString().ShouldBe("SKU-1",
            "The body is what the author's .Emit expression built, written by the generated " +
            "context — camelCase, because that is what this context declares.");
    }

    /// <summary>
    /// A <c>[Sensitive]</c> member is redacted before the event leaves the database.
    /// </summary>
    /// <remarks>
    /// The end of the chain the unit tests each hold one link of. An event body is the widest
    /// sink in the system — a broker fans it out to consumers nobody in this repository
    /// controls — and the control is inherited rather than restated: the generated
    /// <c>DescribeStep</c> builds a <c>JournalPayload</c> with the flow's own
    /// <c>SensitiveMembers</c>, and a <c>JournalPayload</c> has no exit but <c>ToJson</c>.
    /// </remarks>
    [Fact]
    public async Task ASensitiveMemberNeverReachesTheBroker()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        await RunAsync(schema, Guid.NewGuid(), new PlaceOrder("SKU-1", "tok_live_4242424242424242"));

        var broker = new RecordingEventPublisher();
        await schema.OutboxPublisher(broker).PublishPendingAsync(Cancellation);

        var json = broker.Delivered.ShouldHaveSingleItem().PayloadJson.ShouldNotBeNull();

        json.ShouldNotContain("tok_live_4242424242424242", Case.Sensitive,
            "A marked member left the process on a topic. That is worse than the log line " +
            "the attribute was written for and worse than the journal row it already guards.");

        json.ShouldContain(JournalPayload.Redacted, Case.Sensitive,
            "and the consumer can tell the field was withheld rather than absent.");
    }

    /// <summary>The event and the step row are one fact, read back off the database.</summary>
    /// <remarks>
    /// Asserted through the sequence rather than through a timestamp: the outbox row carries
    /// the sequence of the commit that staged it, so a row whose sequence names no committed
    /// step would be an event that outlived its transaction.
    /// </remarks>
    [Fact]
    public async Task TheEventIsStagedByTheStepsOwnCommit()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var instanceId = Guid.NewGuid();
        await RunAsync(schema, instanceId, new PlaceOrder("SKU-1", "tok"));

        var orphans = await schema.ScalarAsync(
            $"""
             SELECT count(*) FROM outbox_event o
             WHERE o.instance_id = '{instanceId}'
               AND NOT EXISTS (
                 SELECT 1 FROM flow_step s
                 WHERE s.instance_id = o.instance_id AND s.sequence = o.sequence)
             """,
            Cancellation);

        orphans.ShouldBe(0L,
            "Every staged event was written by the same transaction as a committed step row.");
    }

    /// <summary>Runs the generated flow against the schema's journal.</summary>
    private static async Task RunAsync(PostgresTestSchema schema, Guid instanceId, PlaceOrder input)
    {
        var run = await DurableExecution.BeginAsync(
            schema.Journal,
            OrderFlow.Plan,
            new FlowInvocation("corr-1", "idem-1"),
            instanceId,
            new FencingToken(1),
            JournalPayload.Of(input, OrderJson.Default.PlaceOrder, OrderFlow.SensitiveMembers),
            Cancellation);

        run.IsSuccess.ShouldBeTrue(run.IsFailure ? run.Error.ToString() : null);

        var engine = new FlowEngine(SystemClock.Instance);

        var result = await engine.ExecuteAsync(
            OrderFlow.Plan,
            new OrderFlow.Dispatcher(new ReserveStock()),
            new FlowInvocation("corr-1", "idem-1"),
            input,
            run.Value,
            Cancellation);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error!.ToString() : null);
    }
}

/// <summary>The flow's input, carrying one member its author marked.</summary>
public sealed record PlaceOrder(string Sku, [property: Sensitive] string PaymentToken);

/// <summary>What the step returns.</summary>
public sealed record Reservation(string Sku);

/// <summary>The event the flow publishes.</summary>
public sealed record OrderPlaced(string Sku, [property: Sensitive] string PaymentToken);

/// <summary>The flow's output.</summary>
public sealed record OrderPlacedResult(string Sku);

/// <summary>The one capability the flow invokes.</summary>
[Capability("inventory.reserve", Version = "1.0.0", Authorization = Authorization.Internal,
    Idempotent = true, SideEffects = ["inventory-ledger"])]
public sealed class ReserveStock : ICapability<PlaceOrder, Reservation>
{
    /// <inheritdoc />
    public ValueTask<Result<Reservation>> ExecuteAsync(
        PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        return ValueTask.FromResult(Result.Ok(new Reservation(input.Sku)));
    }
}

/// <summary>
/// A durable flow that emits, compiled by the real generator inside this project's build.
/// </summary>
/// <remarks>
/// It is here rather than in the reference sample because the sample is deliberately
/// <c>Ephemeral</c> — that decision is argued on <c>docs/diagnostics/FLOWX1012.md</c> — and an
/// ephemeral flow stages nothing. This is the smallest durable flow that emits.
/// </remarks>
[Flow("order.place", Version = "1.0.0", Profile = ExecutionProfile.Durable)]
public sealed partial class OrderFlow : Flow<PlaceOrder, OrderPlacedResult>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<ReserveStock>()
            .Emit<OrderPlaced>(ctx => new OrderPlaced(ctx.Input.Sku, ctx.Input.PaymentToken))
            .Return(ctx => new OrderPlacedResult(ctx.Get<Reservation>().Sku));
    }
}

/// <summary>The generated metadata every payload in this file is written through.</summary>
/// <remarks>
/// camelCase, so the assertion on the delivered body's property names is evidence that the
/// event was written by <em>this</em> context rather than by a reflecting serialiser.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PlaceOrder))]
[JsonSerializable(typeof(OrderPlaced))]
[JsonSerializable(typeof(OrderPlacedResult))]

// The step's result, which the flow being Durable makes a journal payload and FLOWX1006
// makes a build requirement. Omitting it does not produce a smaller journal; it produces a
// build that names the line.
[JsonSerializable(typeof(Reservation))]
public sealed partial class OrderJson : JsonSerializerContext;
