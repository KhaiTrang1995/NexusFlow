using System.Collections.Concurrent;
using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Runtime.Tests;

/// <summary>
/// The engine pools contexts with a hand-rolled lock-free pool, so the question that
/// matters is not "is it fast" but "can two concurrent flows ever be handed the same
/// context". If they can, one tenant reads another's data — the worst defect this
/// codebase could ship.
/// </summary>
public sealed class ConcurrencyTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    [Fact]
    public async Task NeverHandsTheSameContextToTwoConcurrentFlows()
    {
        var engine = new FlowEngine(new FakeClock(T0));
        var plan = Plans.TwoStepQuery();
        var overlaps = new ConcurrentBag<string>();

        var tasks = Enumerable.Range(0, 64).Select(i => Task.Run(async () =>
        {
            var dispatcher = new OverlapDetectingDispatcher(overlaps);
            await engine.ExecuteAsync(
                plan, dispatcher, new FlowInvocation($"corr-{i}", $"idem-{i}", $"tenant-{i}"));
        }));

        await Task.WhenAll(tasks);

        overlaps.ShouldBeEmpty(
            "A context was in use by two flows at once. The pool's CompareExchange " +
            "claim is the only thing preventing a cross-tenant read, so this failing " +
            "means the claim is wrong, not that the test is flaky.");
    }

    [Fact]
    public async Task KeepsEachConcurrentFlowsTenantSeparate()
    {
        var engine = new FlowEngine(new FakeClock(T0));
        var plan = Plans.TwoStepQuery();
        var mismatches = new ConcurrentBag<string>();

        var tasks = Enumerable.Range(0, 64).Select(i => Task.Run(async () =>
        {
            var expected = $"tenant-{i}";
            var dispatcher = new TenantCheckingDispatcher(expected, mismatches);
            await engine.ExecuteAsync(
                plan, dispatcher, new FlowInvocation($"corr-{i}", $"idem-{i}", expected));
        }));

        await Task.WhenAll(tasks);

        mismatches.ShouldBeEmpty("A flow observed a tenant that was not its own.");
    }

    [Fact]
    public async Task SurvivesMoreConcurrentFlowsThanThePoolRetains()
    {
        // The pool holds four; forty run at once. The excess must simply allocate
        // fresh contexts and be dropped on return, never corrupt the pool.
        var engine = new FlowEngine(new FakeClock(T0), maxPooledContexts: 4);
        var plan = Plans.TwoStepQuery();
        var overlaps = new ConcurrentBag<string>();

        var tasks = Enumerable.Range(0, 40).Select(i => Task.Run(async () =>
        {
            var dispatcher = new OverlapDetectingDispatcher(overlaps);
            var result = await engine.ExecuteAsync(
                plan, dispatcher, new FlowInvocation($"c-{i}", $"k-{i}"));
            result.IsSuccess.ShouldBeTrue();
        }));

        await Task.WhenAll(tasks);

        overlaps.ShouldBeEmpty();
    }

    [Fact]
    public void RejectsANonPositivePoolSize()
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => new FlowEngine(new FakeClock(T0), maxPooledContexts: 0));
        Should.Throw<ArgumentOutOfRangeException>(
            () => new FlowEngine(new FakeClock(T0), maxPooledContexts: -1));
    }

    /// <summary>Flags a context that is already claimed by another in-flight flow.</summary>
    private sealed class OverlapDetectingDispatcher(ConcurrentBag<string> overlaps) : IStepDispatcher
    {
        private static readonly ConcurrentDictionary<FlowContext, int> InUse = new();

        public async ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
        {
            if (!InUse.TryAdd(ctx, stepIndex))
            {
                overlaps.Add($"context claimed twice at step {stepIndex}");
            }

            // Yield so the scheduler can interleave. Without this the flows would run
            // one after another and the test would prove nothing.
            await Task.Yield();

            InUse.TryRemove(ctx, out _);
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

    /// <summary>Flags a flow that observes a tenant other than its own.</summary>
    private sealed class TenantCheckingDispatcher(string expected, ConcurrentBag<string> mismatches)
        : IStepDispatcher
    {
        public async ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
        {
            await Task.Yield();

            if (ctx.TenantId != expected)
            {
                mismatches.Add($"expected {expected}, observed {ctx.TenantId ?? "<null>"}");
            }

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
