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

    public static FlowInvocation Invocation { get; } = new("corr-1", "idem-1", "acme");
}
