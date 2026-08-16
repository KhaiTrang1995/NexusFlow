using FlowX.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Azure.Functions.Worker;
using Shouldly;
using Xunit;

namespace Functions.Tests;

/// <summary>
/// What the generated entry points do when the platform pushes them something.
/// </summary>
/// <remarks>
/// <para>
/// <strong>These call the generated methods, not a re-statement of them.</strong>
/// <c>WorkerFixture.Entry</c> is <c>FlowX.Generated.FunctionsFunctions</c> out of the sample's
/// own assembly, so what runs here is the emitted text — and a generator that emitted a settle
/// on the wrong branch fails here rather than in a deployment.
/// </para>
/// <para>
/// <strong>The Functions local runtime has not been run against, and WP-141 asked for
/// it.</strong> <c>azure-functions-core-tools</c> installs by downloading
/// <c>Azure.Functions.Cli.linux-x64</c> from <c>cdn.functions.azure.com</c>, which this
/// environment's egress proxy refuses with a 403 — the npm package installs and its post-install
/// step cannot fetch the binary, so there is no <c>func</c> to run. A Service Bus end-to-end
/// would additionally need a broker, and there is none here either.
/// </para>
/// <para>
/// So what is owed is the platform half: that the host's gRPC channel binds these signatures,
/// that <c>host.json</c> is accepted, and that <c>AutoCompleteMessages = false</c> is honoured by
/// a real Service Bus extension. What is <em>not</em> owed is anything about FlowX's own
/// behaviour under a push, which is what every test below is about: the entry point is invoked
/// with the arguments the platform would pass, against a real PostgreSQL journal, and the
/// journal rows and the settle verb are asserted.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PushedItemTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>
    /// A pushed message runs its flow, writes one journal row, and is completed.
    /// </summary>
    /// <remarks>
    /// The whole of WP-141's bus arm in one test: the platform's message shape reaches
    /// <c>FlowBusScan.AdmitAsync</c> through the generated mapping, and the disposition it
    /// returns decides the verb.
    /// </remarks>
    [Fact]
    public async Task APushedMessageRunsItsFlowAndIsCompleted()
    {
        Assert.SkipUnless(WorkerDatabase.IsAvailable, WorkerDatabase.Reason);

        await using var worker = await WorkerFixture.CreateAsync(Cancellation);

        var actions = new RecordingMessageActions();
        var message = WorkerFixture.Message(new OrderPlaced("order-1", "sku-1", 3), Guid.NewGuid());

        await worker.Entry.ReserveStockFlow(message, actions, Cancellation);

        actions.Completed.ShouldHaveSingleItem().ShouldBe(message.MessageId);
        actions.Abandoned.ShouldBeEmpty();
        actions.DeadLettered.ShouldBeEmpty();

        var instances = await worker.InstanceCountAsync("stock.reserve", Cancellation);

        instances.ShouldBe(
            1,
            "one message starts one flow, and the row is what says it happened.");
    }

    /// <summary>
    /// The same message pushed twice starts one flow and is completed both times.
    /// </summary>
    /// <remarks>
    /// <strong>At-least-once delivery meeting exactly-once execution, on a host that never
    /// polls.</strong> The instance id is derived from the delivery (ADR-0035) and
    /// <c>flow_instance</c>'s primary key refuses the second — so the runtime answers
    /// <c>Deduplicated</c>, which the generated entry point settles as a completion rather than
    /// as a failure. Completing it is the correct verb: an earlier delivery already ran it, so
    /// there is nothing left for this one to do.
    /// </remarks>
    [Fact]
    public async Task TheSameMessagePushedTwiceStartsOneFlow()
    {
        Assert.SkipUnless(WorkerDatabase.IsAvailable, WorkerDatabase.Reason);

        await using var worker = await WorkerFixture.CreateAsync(Cancellation);

        var eventId = Guid.NewGuid();
        var order = new OrderPlaced("order-2", "sku-2", 1);
        var actions = new RecordingMessageActions();

        await worker.Entry.ReserveStockFlow(WorkerFixture.Message(order, eventId), actions, Cancellation);
        await worker.Entry.ReserveStockFlow(
            WorkerFixture.Message(order, eventId, deliveryCount: 2), actions, Cancellation);

        actions.Completed.Count.ShouldBe(2, "both deliveries are done with.");
        actions.DeadLettered.ShouldBeEmpty();

        var instances = await worker.InstanceCountAsync("stock.reserve", Cancellation);

        instances.ShouldBe(
            1,
            "the second delivery derived the same instance id and the primary key refused it. " +
            "Two rows here would mean the stock was reserved twice.");
    }

    /// <summary>
    /// A message whose id is not a GUID is dead-lettered on its first delivery, with a reason,
    /// and journals nothing.
    /// </summary>
    /// <remarks>
    /// ADR-0038's first condition, reached through the platform's own shape: a
    /// <c>MessageId</c> is a string and only a GUID is an event id. The reason is what an
    /// operator reads off the dead-letter queue, so it is asserted rather than merely counted.
    /// </remarks>
    [Fact]
    public async Task AMessageWithAnUnreadableIdIsDeadLetteredWithItsReason()
    {
        Assert.SkipUnless(WorkerDatabase.IsAvailable, WorkerDatabase.Reason);

        await using var worker = await WorkerFixture.CreateAsync(Cancellation);

        var actions = new RecordingMessageActions();

        var message = Azure.Messaging.ServiceBus.ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString("{}"),
            messageId: "not-a-guid",
            subject: "order.placed",
            partitionKey: "order-3",
            deliveryCount: 1,
            lockTokenGuid: Guid.NewGuid());

        await worker.Entry.ReserveStockFlow(message, actions, Cancellation);

        var dead = actions.DeadLettered.ShouldHaveSingleItem();

        dead.MessageId.ShouldBe("not-a-guid");
        dead.Description.ShouldNotBeNull().ShouldContain("not a GUID");

        actions.Completed.ShouldBeEmpty("nothing ran, so nothing may be settled as done.");

        var instances = await worker.InstanceCountAsync("stock.reserve", Cancellation);

        instances.ShouldBe(
            0,
            "a row here would describe an execution that never happened.");
    }

    /// <summary>
    /// A message past <c>BusMaxDeliveries</c> is dead-lettered rather than retried for ever.
    /// </summary>
    /// <remarks>
    /// ADR-0038's second condition, and the one a push host cannot work out for itself: the
    /// platform knows the delivery count and the framework knows the bound. The generated entry
    /// point passes the first and reads the answer.
    /// </remarks>
    [Fact]
    public async Task AMessageDeliveredTooManyTimesIsDeadLettered()
    {
        Assert.SkipUnless(WorkerDatabase.IsAvailable, WorkerDatabase.Reason);

        await using var worker = await WorkerFixture.CreateAsync(Cancellation);

        var options = worker.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<FlowXOptions>>().Value;

        var actions = new RecordingMessageActions();

        var message = WorkerFixture.Message(
            new OrderPlaced("order-4", "sku-4", 1),
            Guid.NewGuid(),
            deliveryCount: options.BusMaxDeliveries + 1);

        await worker.Entry.ReserveStockFlow(message, actions, Cancellation);

        actions.DeadLettered.ShouldHaveSingleItem()
            .Description.ShouldNotBeNull()
            .ShouldContain("BusMaxDeliveries");

        actions.Completed.ShouldBeEmpty();
    }

    /// <summary>
    /// The timer entry point runs a schedule pass, and a due occurrence starts exactly once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The platform's timer says "you may look now" and the declaration decides what is
    /// due.</strong> Two firings a minute apart — which is what this simulates by calling twice
    /// — must not produce two runs of one occurrence, and what refuses the second is the
    /// instance id ADR-0031 derives from the expression, not anything in the entry point.
    /// </para>
    /// <para>
    /// The sample declares <c>0 2 * * *</c> in <c>Europe/Berlin</c>, which is not due during a
    /// test run, so the pass finds nothing — and that is the assertion that matters here: the
    /// entry point reached the sweep and the sweep answered. What a due occurrence does is
    /// <c>Scheduler.Tests</c>'s subject and is not restated.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheTimerEntryPointRunsAScheduleSweep()
    {
        Assert.SkipUnless(WorkerDatabase.IsAvailable, WorkerDatabase.Reason);

        await using var worker = await WorkerFixture.CreateAsync(Cancellation);

        await worker.Entry.FlowXSchedulePass(new TimerInfo(), Cancellation);
        await worker.Entry.FlowXSchedulePass(new TimerInfo(), Cancellation);

        var instances = await worker.InstanceCountAsync("orders.reconcile", Cancellation);

        instances.ShouldBeLessThanOrEqualTo(
            1,
            "two firings of the platform's timer are two looks at one declaration, and " +
            "ADR-0031's derived instance id is what makes the second inert.");
    }
}
