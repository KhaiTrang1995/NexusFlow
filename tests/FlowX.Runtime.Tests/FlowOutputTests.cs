using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Runtime.Tests;

/// <summary>
/// The output path: a flow's <c>.Return(...)</c> projection, run while the pooled
/// context is still rented.
/// </summary>
/// <remarks>
/// The rental is the whole subtlety. The context is reset the instant the engine
/// returns, so a caller handed the context would read another flow's data — which is
/// why the projection is a parameter of the engine call rather than something the
/// caller applies afterwards.
/// </remarks>
public sealed class FlowOutputTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    [Fact]
    public async Task ASuccessfulFlowProjectsItsOutput()
    {
        var engine = new FlowEngine(new FakeClock(T0));

        var result = await engine.ExecuteAsync(
            Plans.TwoStepQuery(),
            new EchoDispatcher(),
            Plans.Invocation,
            "input",
            static ctx => ctx.Get<string>().ToUpperInvariant(),
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("INPUT");
        result.CompletedSteps.ShouldBe(2);
    }

    [Fact]
    public async Task AFailedFlowDoesNotRunTheProjection()
    {
        var ran = false;
        var engine = new FlowEngine(new FakeClock(T0));

        var result = await engine.ExecuteAsync(
            Plans.TwoStepQuery(),
            new FailingDispatcher(),
            Plans.Invocation,
            "input",
            ctx => { ran = true; return "unreachable"; },
            TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();

        // A projection over a context whose steps did not run would read defaults and
        // hand the caller a plausible-looking answer built from nothing.
        ran.ShouldBeFalse();
    }

    [Fact]
    public async Task ReadingTheValueOfAFailedFlowThrows()
    {
        var engine = new FlowEngine(new FakeClock(T0));

        var result = await engine.ExecuteAsync(
            Plans.TwoStepQuery(),
            new FailingDispatcher(),
            Plans.Invocation,
            "input",
            static _ => "unreachable",
            TestContext.Current.CancellationToken);

        Should.Throw<InvalidOperationException>(() => result.Value);
        result.TryGetValue(out var value).ShouldBeFalse();
        value.ShouldBeNull();
    }

    [Fact]
    public async Task TheProjectionSeesTheLastStepsOutput()
    {
        var engine = new FlowEngine(new FakeClock(T0));

        var result = await engine.ExecuteAsync(
            Plans.TwoStepQuery(),
            new ProducingDispatcher(),
            Plans.Invocation,
            "seed",
            static ctx => ctx.Get<int>(),
            TestContext.Current.CancellationToken);

        result.Value.ShouldBe(2, "Both steps ran, and the projection read what the second wrote.");
    }

    [Fact]
    public async Task ANullProjectionIsRejected()
    {
        var engine = new FlowEngine(new FakeClock(T0));

        await Should.ThrowAsync<ArgumentNullException>(async () => await engine.ExecuteAsync(
            Plans.TwoStepQuery(),
            new EchoDispatcher(),
            Plans.Invocation,
            "input",
            (Func<FlowContext, string>)null!,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ARejectedFlowCarriesTheErrorAndNoValue()
    {
        var error = new Error("host.draining", "no", ErrorCategory.Unavailable);
        var result = FlowExecutionResult.Rejected<string>(error);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(error);
        result.CompletedSteps.ShouldBe(0);
        result.Compensation.ShouldBe(CompensationOutcome.NotRequired);
        result.Outcome.Error.ShouldBe(error);
    }

    private sealed class EchoDispatcher : IStepDispatcher
    {
        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => ValueTask.FromResult(StepOutcome.Success);

        public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => ValueTask.FromResult(StepOutcome.Success);
    }

    private sealed class ProducingDispatcher : IStepDispatcher
    {
        private int _count;

        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
        {
            ctx.Set(++_count);
            return ValueTask.FromResult(StepOutcome.Success);
        }

        public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => ValueTask.FromResult(StepOutcome.Success);
    }

    private sealed class FailingDispatcher : IStepDispatcher
    {
        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => ValueTask.FromResult(StepOutcome.Failed(
                new Error("step.failed", "no", ErrorCategory.Conflict)));

        public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => ValueTask.FromResult(StepOutcome.Success);
    }
}
