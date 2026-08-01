using FlowX.Runtime;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shouldly;
using Xunit;

namespace FlowX.Hosting.Tests;

/// <summary>
/// Graceful shutdown. A pod that stops accepting work but abandons what it already
/// started leaves half-finished business behind on every deployment — and a
/// deployment happens far more often than a crash, so this path is exercised more
/// than the durability machinery is.
/// </summary>
public sealed class DrainTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static (FlowHost Host, GatedDispatcher Dispatcher) NewHost(TimeSpan? drainTimeout = null)
    {
        var host = new FlowHost(
            new FlowEngine(new FixedClock()),
            new FlowXOptions
            {
                ApplicationName = "Sample.App",
                ShutdownDrainTimeout = drainTimeout ?? TimeSpan.FromSeconds(5),
            });

        return (host, new GatedDispatcher());
    }

    [Fact]
    public async Task RunsAFlowAndReportsItsResult()
    {
        var (host, dispatcher) = NewHost();
        dispatcher.Release();

        var result = await host.RunAsync(
            Plans.TwoStep(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        host.InFlight.ShouldBe(0);
    }

    [Fact]
    public async Task RunsAFlowWithAnOutputAndProjectsIt()
    {
        var (host, dispatcher) = NewHost();
        dispatcher.Release();

        var result = await host.RunAsync(
            Plans.TwoStep(),
            dispatcher,
            Plans.Invocation,
            "input",
            static ctx => ctx.Get<string>().Length,
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(5);
        host.InFlight.ShouldBe(0);
    }

    [Fact]
    public async Task ADrainingHostRefusesAFlowWithAnOutput()
    {
        var (host, dispatcher) = NewHost();
        dispatcher.Release();

        await host.DrainAsync(TestContext.Current.CancellationToken);

        var result = await host.RunAsync(
            Plans.TwoStep(),
            dispatcher,
            Plans.Invocation,
            "input",
            static ctx => ctx.Get<string>().Length,
            TestContext.Current.CancellationToken);

        // The same refusal the non-generic overload gives. An overload that quietly
        // accepted work during a drain would be a hole in the shutdown contract.
        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("host.draining");
        result.Error.Category.ShouldBe(ErrorCategory.Unavailable);
        result.CompletedSteps.ShouldBe(0);
    }

    [Fact]
    public async Task CountsFlowsWhileTheyAreInFlight()
    {
        var (host, dispatcher) = NewHost();

        var running = host.RunAsync(Plans.TwoStep(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken).AsTask();
        await dispatcher.WaitUntilEntered(TestContext.Current.CancellationToken);

        host.InFlight.ShouldBe(1);

        dispatcher.Release();
        await running;

        host.InFlight.ShouldBe(0);
    }

    [Fact]
    public async Task DrainWaitsForInFlightFlowsToFinish()
    {
        var (host, dispatcher) = NewHost();

        var running = host.RunAsync(Plans.TwoStep(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken).AsTask();
        await dispatcher.WaitUntilEntered(TestContext.Current.CancellationToken);

        var drain = host.DrainAsync(TestContext.Current.CancellationToken).AsTask();

        drain.IsCompleted.ShouldBeFalse("Drain must not return while a flow is still running.");

        dispatcher.Release();
        await running;

        (await drain).ShouldBeTrue("Drain completed within its budget, so it reports success.");
    }

    [Fact]
    public async Task DrainRefusesNewWorkOnceItHasStarted()
    {
        var (host, dispatcher) = NewHost();

        var running = host.RunAsync(Plans.TwoStep(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken).AsTask();
        await dispatcher.WaitUntilEntered(TestContext.Current.CancellationToken);

        var drain = host.DrainAsync(TestContext.Current.CancellationToken).AsTask();

        var rejected = await host.RunAsync(
            Plans.TwoStep(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        rejected.IsSuccess.ShouldBeFalse();
        rejected.Error!.Code.ShouldBe("host.draining");
        rejected.Error.Category.ShouldBe(ErrorCategory.Unavailable,
            "Retryable: another node can take this, and the caller should be told so.");

        dispatcher.Release();
        await running;
        await drain;
        // Accepting work during a drain is why drains never finish. The rejection is
        // deliberate, and Unavailable is what tells a load balancer to try elsewhere.
    }

    [Fact]
    public async Task DrainGivesUpAfterItsBudgetAndSaysSo()
    {
        var (host, dispatcher) = NewHost(TimeSpan.FromMilliseconds(150));

        var running = host.RunAsync(Plans.TwoStep(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken).AsTask();
        await dispatcher.WaitUntilEntered(TestContext.Current.CancellationToken);

        var drained = await host.DrainAsync(TestContext.Current.CancellationToken);

        drained.ShouldBeFalse(
            "A drain that waits forever turns a rolling deploy into an outage. It gives " +
            "up, reports that it gave up, and lets the orchestrator kill the pod.");

        dispatcher.Release();
        await running;
    }

    [Fact]
    public async Task DrainReturnsImmediatelyWhenNothingIsRunning()
    {
        var (host, _) = NewHost();

        (await host.DrainAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task DrainIsIdempotent()
    {
        var (host, _) = NewHost();
        var ct = TestContext.Current.CancellationToken;

        (await host.DrainAsync(ct)).ShouldBeTrue();
        (await host.DrainAsync(ct)).ShouldBeTrue("A second SIGTERM must not throw.");
    }

    [Fact]
    public async Task HealthIsUnhealthyBeforeStartAndHealthyAfter()
    {
        var (host, _) = NewHost();
        var check = new FlowXHealthCheck(host);
        var ct = TestContext.Current.CancellationToken;

        (await check.CheckHealthAsync(new HealthCheckContext(), ct)).Status
            .ShouldBe(HealthStatus.Unhealthy, "A host that has not started is not ready for traffic.");

        host.MarkReady();

        (await check.CheckHealthAsync(new HealthCheckContext(), ct)).Status
            .ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public async Task HealthIsUnhealthyWhileDrainingSoTrafficStops()
    {
        var (host, dispatcher) = NewHost();
        host.MarkReady();
        var ct = TestContext.Current.CancellationToken;

        var running = host.RunAsync(Plans.TwoStep(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken).AsTask();
        await dispatcher.WaitUntilEntered(ct);

        var drain = host.DrainAsync(ct).AsTask();

        var report = await new FlowXHealthCheck(host).CheckHealthAsync(new HealthCheckContext(), ct);

        report.Status.ShouldBe(HealthStatus.Unhealthy,
            "Reporting healthy while draining keeps the load balancer sending work to a " +
            "pod that is about to disappear.");
        report.Description.ShouldNotBeNull().ShouldContain("draining");

        dispatcher.Release();
        await running;
        await drain;
    }

    /// <summary>
    /// A drain waits for detached sub-flows, which the host's own counter cannot see.
    /// </summary>
    /// <remarks>
    /// The hole this closes is specific and would have been silent. A
    /// <c>.SubFlow&lt;T&gt;(Detached)</c> child is started from <em>inside</em> a step, so
    /// it never passed through <c>TryEnter</c> and never appears in <see cref="FlowHost.InFlight"/>.
    /// Before this, <c>DrainAsync</c> would see its counter reach zero, report "everything
    /// finished", and the kill that follows a successful drain would land on a
    /// fire-and-forget saga halfway through reserving inventory — with nothing compensated.
    /// </remarks>
    [Fact]
    public async Task ADrainWaitsForDetachedSubFlowsTheHostNeverCounted()
    {
        var engine = new FlowEngine(new FixedClock());

        var host = new FlowHost(
            engine,
            new FlowXOptions
            {
                ApplicationName = "Sample.App",
                ShutdownDrainTimeout = TimeSpan.FromSeconds(5),
            });

        var child = new GatedDispatcher();
        var parent = new DetachingDispatcher(child);

        var result = await host.RunAsync(
            Plans.Composing(), parent, Plans.Invocation, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();

        await child.WaitUntilEntered(TestContext.Current.CancellationToken);

        host.InFlight.ShouldBe(0,
            "The parent finished, and the host never knew about the child.");
        engine.DetachedInFlight.ShouldBe(1,
            "But the engine did, which is the whole point.");

        var drain = host.DrainAsync(TestContext.Current.CancellationToken);

        drain.IsCompleted.ShouldBeFalse(
            "A drain that returned here would be reporting that a running saga had finished.");

        child.Release();

        (await drain).ShouldBeTrue();
        engine.DetachedInFlight.ShouldBe(0);
    }

    [Fact]
    public async Task ADrainWithNothingDetachedIsUnchanged()
    {
        // The path every flow that composes nothing takes: no detached children, so the
        // wait is a counter read and returns immediately.
        var (host, dispatcher) = NewHost();
        dispatcher.Release();

        await host.RunAsync(
            Plans.TwoStep(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        (await host.DrainAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    /// <summary>A dispatcher whose step 0 composes a child, detached.</summary>
    private sealed class DetachingDispatcher(IStepDispatcher child) : IStepDispatcher
    {
        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => ValueTask.FromResult(StepOutcome.Success);

        public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => ValueTask.FromResult(StepOutcome.Success);

        public bool Evaluate(int stepIndex, FlowContext ctx)
            => throw new NotSupportedException("This double runs plans with no branch step.");

        public int Select(int stepIndex, FlowContext ctx)
            => throw new NotSupportedException("This double runs plans with no switch step.");

        public IterationSource BeginIteration(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to begin.");

        public FlowContext EnterIteration(int stepIndex, in IterationSource source, int iteration, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to enter.");

        public SubFlowSource BeginSubFlow(int stepIndex, FlowContext ctx) =>
            new(Plans.TwoStep(), child, "input");

        public void EnterSubFlow(int stepIndex, in SubFlowSource source, FlowContext child) =>
            child.Set((string)source.Input!);
    }

    [Fact]
    public void RejectsNullDependencies()
    {
        Should.Throw<ArgumentNullException>(() => new FlowHost(null!, new FlowXOptions()));
        Should.Throw<ArgumentNullException>(() => new FlowHost(new FlowEngine(new FixedClock()), null!));
        Should.Throw<ArgumentNullException>(() => new FlowXHealthCheck(null!));
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => T0;
    }

    /// <summary>A dispatcher that blocks inside a step until released, so drain is observable.</summary>
    private sealed class GatedDispatcher : IStepDispatcher
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitUntilEntered(CancellationToken ct) => _entered.Task.WaitAsync(ct);

        public void Release() => _gate.TrySetResult();

        public async ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
        {
            _entered.TrySetResult();
            await _gate.Task.ConfigureAwait(false);
            return StepOutcome.Success;
        }

        public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => ValueTask.FromResult(StepOutcome.Success);

        public bool Evaluate(int stepIndex, FlowContext ctx)
            => throw new NotSupportedException("This double runs plans with no branch step.");

        /// <inheritdoc />
        /// <remarks>This double declares no iteration, so the engine never asks it for one.</remarks>
        public IterationSource BeginIteration(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to begin.");

        /// <inheritdoc />
        public FlowContext EnterIteration(int stepIndex, in IterationSource source, int iteration, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to enter.");

        public int Select(int stepIndex, FlowContext ctx)
            => throw new NotSupportedException("This double runs plans with no switch step.");
    }
}

/// <summary>Plans the hosting tests execute.</summary>
internal static class Plans
{
    private static readonly CapabilityDescriptor Validate =
        CapabilityDescriptor.Create("order.validate", "1.0.0", isIdempotent: true);

    public static ExecutionPlan TwoStep() => ExecutionPlan.Create(
        FlowDescriptor.Create("order.place", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromMinutes(5)),
        StepGraph.Create([
            StepNode.ForCapability(0, Validate),
            StepNode.ForCapability(1, Validate),
        ]));

    /// <summary>A flow whose first step composes another, detached: <c>0 subflow · 1 validate</c>.</summary>
    public static ExecutionPlan Composing() => ExecutionPlan.Create(
        FlowDescriptor.Create("order.notify", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromMinutes(5)),
        StepGraph.Create([
            StepNode.ForSubFlow(0, "order.place", SubFlowMode.Detached),
            StepNode.ForCapability(1, Validate),
        ]));

    /// <summary>A journaled two-step flow: <c>0 validate · 1 validate</c>.</summary>
    /// <remarks>
    /// Its own id rather than a Durable variant of <see cref="TwoStep"/>, so a test that runs
    /// both against one recorder can tell their spans and their series apart by name.
    /// </remarks>
    public static ExecutionPlan Durable() => ExecutionPlan.Create(
        FlowDescriptor.Create("order.durable", "1.0.0", ExecutionProfile.Durable, TimeSpan.FromMinutes(5)),
        StepGraph.Create([
            StepNode.ForCapability(0, Validate),
            StepNode.ForCapability(1, Validate),
        ]));

    public static FlowInvocation Invocation { get; } = new("corr-1", "idem-1", "acme");
}
