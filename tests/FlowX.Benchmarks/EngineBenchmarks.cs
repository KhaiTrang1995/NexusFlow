using BenchmarkDotNet.Attributes;
using FlowX.Runtime;

namespace FlowX.Benchmarks;

/// <summary>
/// Budget <strong>B1 — 4-step ephemeral flow overhead, p50 ≤ 1.5 µs / p99 ≤ 5 µs</strong>,
/// measured through the real <see cref="FlowEngine"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>StepLoopBenchmarks</c> measured the skeleton before the engine existed. These
/// measure what the engine actually costs: pooled context rent and return, deadline
/// checks at every step boundary, compensation registration, and the failure path
/// with a full unwind.
/// </para>
/// <para>
/// The generator still does not exist, so the dispatcher here is hand-written. What
/// this does <em>not</em> measure is the cost of the capability itself — that is the
/// application's, not the platform's, and B1 is a budget on platform overhead.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[HideColumns("Job", "Error", "StdDev", "Median", "RatioSD")]
public class EngineBenchmarks
{
    private FlowEngine _engine = null!;
    private ExecutionPlan _query = null!;
    private ExecutionPlan _saga = null!;
    private IStepDispatcher _dispatcher = null!;
    private IStepDispatcher _failing = null!;
    private FlowInvocation _invocation;

    [GlobalSetup]
    public void Setup()
    {
        var validate = CapabilityDescriptor.Create("order.validate", "1.0.0", true);
        var reserve = CapabilityDescriptor.Create("inventory.reserve", "1.0.0", true, "inventory-ledger");
        var release = CapabilityDescriptor.Create("inventory.release", "1.0.0", true, "inventory-ledger");
        var capture = CapabilityDescriptor.Create("payment.capture", "2.1.0", false, "payment-gateway");
        var refund = CapabilityDescriptor.Create("payment.refund", "2.1.0", true, "payment-gateway");

        // Four steps, no compensation — the shape of a read or a validation API.
        _query = ExecutionPlan.Create(
            FlowDescriptor.Create("order.get", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
            StepGraph.Create([
                StepNode.ForCapability(0, validate),
                StepNode.ForCapability(1, validate),
                StepNode.ForCapability(2, validate),
                StepNode.ForEmit(3, "order.read"),
            ]));

        // Four steps, two compensable — the shape of a saga.
        _saga = ExecutionPlan.Create(
            FlowDescriptor.Create("order.place", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
            StepGraph.Create([
                StepNode.ForCapability(0, validate),
                StepNode.ForCapability(1, reserve, release),
                StepNode.ForCapability(2, capture, refund),
                StepNode.ForEmit(3, "order.placed"),
            ]));

        _engine = new FlowEngine(new FixedClock());
        _dispatcher = new NullDispatcher();
        _failing = new NullDispatcher
        {
            FailAtStep = 2,
            Failure = new Error("payment.declined", "declined", ErrorCategory.Conflict),
        };
        _invocation = new FlowInvocation("bench-corr", "bench-idem", "bench-tenant");
    }

    /// <summary>B1 proper: a four-step flow with no saga machinery.</summary>
    [Benchmark(Baseline = true, Description = "4-step flow, no compensation")]
    public async ValueTask<bool> Query()
    {
        var result = await _engine.ExecuteAsync(_query, _dispatcher, _invocation).ConfigureAwait(false);
        return result.IsSuccess;
    }

    /// <summary>What declaring compensations costs when nothing fails.</summary>
    [Benchmark(Description = "4-step saga, success path")]
    public async ValueTask<bool> SagaSuccess()
    {
        var result = await _engine.ExecuteAsync(_saga, _dispatcher, _invocation).ConfigureAwait(false);
        return result.IsSuccess;
    }

    /// <summary>The failure path: two completed steps unwound in reverse.</summary>
    [Benchmark(Description = "4-step saga, failure + unwind")]
    public async ValueTask<CompensationOutcome> SagaFailure()
    {
        var result = await _engine.ExecuteAsync(_saga, _failing, _invocation).ConfigureAwait(false);
        return result.Compensation;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
    }

    private sealed class NullDispatcher : IStepDispatcher
    {
        public int? FailAtStep { get; init; }

        public Error? Failure { get; init; }

        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => stepIndex == FailAtStep && Failure is not null
                ? ValueTask.FromResult(StepOutcome.Failed(Failure))
                : ValueTask.FromResult(StepOutcome.Success);

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
