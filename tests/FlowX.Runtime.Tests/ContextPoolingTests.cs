using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Runtime.Tests;

/// <summary>
/// Context pooling. The reason it exists is budget B2; the reason it is dangerous is
/// that a pooled object which is not fully reset leaks one tenant's data into
/// another tenant's execution. Both properties are tested here, and the leak test
/// matters more.
///
/// These tests assert on <c>Snapshots</c> — values captured while a step was running —
/// rather than on the live context. Reading the context after the flow finished reads
/// a reset instance. The first version of this file did exactly that and failed, which
/// was the clearest possible confirmation that the reset works.
/// </summary>
public sealed class ContextPoolingTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    [Fact]
    public async Task ReusesTheSameContextInstanceAcrossSequentialExecutions()
    {
        var engine = new FlowEngine(new FakeClock(T0));
        var first = new RecordingDispatcher();
        var second = new RecordingDispatcher();
        var ct = TestContext.Current.CancellationToken;

        await engine.ExecuteAsync(Plans.TwoStepQuery(), first, Plans.Invocation, ct);
        await engine.ExecuteAsync(Plans.TwoStepQuery(), second, Plans.Invocation, ct);

        ReferenceEquals(first.ContextsSeen[0], second.ContextsSeen[0]).ShouldBeTrue(
            "A fresh context per execution would allocate on every flow, which is the " +
            "single largest source of Gen0 pressure a runtime can inflict.");
    }

    [Fact]
    public async Task UsesTheSameContextForEveryStepOfOneExecution()
    {
        var engine = new FlowEngine(new FakeClock(T0));
        var dispatcher = new RecordingDispatcher();

        await engine.ExecuteAsync(
            Plans.FourStepSaga(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        dispatcher.ContextsSeen.Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public async Task ResetsTenantAndCorrelationBetweenExecutions()
    {
        var engine = new FlowEngine(new FakeClock(T0));
        var ct = TestContext.Current.CancellationToken;

        var acme = new RecordingDispatcher();
        await engine.ExecuteAsync(
            Plans.TwoStepQuery(), acme, new FlowInvocation("corr-acme", "idem-1", "acme"), ct);

        var globex = new RecordingDispatcher();
        await engine.ExecuteAsync(
            Plans.TwoStepQuery(), globex, new FlowInvocation("corr-globex", "idem-2", "globex"), ct);

        globex.Snapshots[0].TenantId.ShouldBe("globex",
            "A pooled context that keeps the previous tenant is a cross-tenant data " +
            "leak, which is the worst defect this codebase could ship (OWASP A01).");
        globex.Snapshots[0].CorrelationId.ShouldBe("corr-globex");
        globex.Snapshots[0].IdempotencyKey.ShouldBe("idem-2");

        // And the instance really was recycled, so the assertions above are about a
        // reused object rather than a conveniently fresh one.
        ReferenceEquals(acme.ContextsSeen[0], globex.ContextsSeen[0]).ShouldBeTrue();
    }

    [Fact]
    public async Task ClearsTheStateBagBetweenExecutions()
    {
        var engine = new FlowEngine(new FakeClock(T0));
        var ct = TestContext.Current.CancellationToken;

        var first = new StateWritingDispatcher("secret-from-tenant-a");
        await engine.ExecuteAsync(Plans.TwoStepQuery(), first, Plans.Invocation, ct);

        var second = new StateReadingDispatcher();
        await engine.ExecuteAsync(Plans.TwoStepQuery(), second, Plans.Invocation, ct);

        second.Found.ShouldBeFalse(
            "State written by one execution must not be visible to the next.");
    }

    [Fact]
    public async Task ClearsTheErrorBetweenExecutions()
    {
        var engine = new FlowEngine(new FakeClock(T0));
        var ct = TestContext.Current.CancellationToken;

        var failing = new RecordingDispatcher()
            .FailAt(1, new Error("order.invalid", "bad", ErrorCategory.Validation));
        await engine.ExecuteAsync(Plans.TwoStepQuery(), failing, Plans.Invocation, ct);

        var succeeding = new RecordingDispatcher();
        await engine.ExecuteAsync(Plans.TwoStepQuery(), succeeding, Plans.Invocation, ct);

        succeeding.Snapshots[0].Error.ShouldBeNull(
            "A stale error would make EmitOnFailure fire on a successful flow.");
    }

    /// <summary>
    /// The generator's seed is a value the execution holds, not one the BCL keeps.
    /// </summary>
    /// <remarks>
    /// This is the assertion that makes <c>CapabilityContext.Random</c>'s documented replay
    /// guarantee possible rather than merely promised. Under <c>new Random()</c> the seed is
    /// chosen internally and readable by nothing at all, so "the seed is journaled" could
    /// never have been implemented — no amount of P2 work would have found a number to write
    /// down. Reproducing the draw from <c>RandomSeed</c> is exactly what a replay would do,
    /// so if this passes, the journal has something real to record.
    /// </remarks>
    [Fact]
    public async Task RecordsTheSeedItsGeneratorWasBuiltFrom()
    {
        var engine = new FlowEngine(new FakeClock(T0));
        var dispatcher = new RandomDrawingDispatcher();

        await engine.ExecuteAsync(
            Plans.TwoStepQuery(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        var seed = dispatcher.Seeds[0].ShouldNotBeNull(
            "A step that drew from ctx.Random left no seed behind, so nothing could journal it.");

        new Random(seed).Next().ShouldBe(dispatcher.Draws[0],
            "Rebuilding the generator from the recorded seed did not reproduce the run's " +
            "first draw. That is precisely the replay the remarks on Random describe, and it " +
            "is the only evidence that the seed recorded is the seed that was used.");
    }

    /// <summary>
    /// A flow that never asks for randomness has no generator and therefore no seed.
    /// </summary>
    /// <remarks>
    /// Budget B2 is why <c>Random</c> is lazy, and the seed must not have quietly bought that
    /// back by being drawn in <c>Initialise</c>. <c>null</c> is also the honest answer for a
    /// journal: there is no draw to reproduce, so there is nothing to write.
    /// </remarks>
    [Fact]
    public async Task RecordsNoSeedForAFlowThatNeverDrawsFromIt()
    {
        var engine = new FlowEngine(new FakeClock(T0));
        var dispatcher = new RandomDrawingDispatcher { Draw = false };

        await engine.ExecuteAsync(
            Plans.TwoStepQuery(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        dispatcher.Seeds[0].ShouldBeNull(
            "A seed was drawn for a flow that never touched ctx.Random, so the generator " +
            "stopped being lazy — an allocation on every execution of the linear path.");
    }

    /// <summary>
    /// The seed is cleared with everything else the pooled context carries.
    /// </summary>
    /// <remarks>
    /// The same rule as the tenant id and the state bag, and it earns the same test: a seed
    /// left behind would tell the next execution's journal that a flow which never drew a
    /// number drew one, and it would name the previous tenant's stream while doing so.
    /// </remarks>
    [Fact]
    public async Task ClearsTheSeedBetweenExecutions()
    {
        var engine = new FlowEngine(new FakeClock(T0));
        var ct = TestContext.Current.CancellationToken;

        var drawing = new RandomDrawingDispatcher();
        await engine.ExecuteAsync(Plans.TwoStepQuery(), drawing, Plans.Invocation, ct);

        var abstaining = new RandomDrawingDispatcher { Draw = false };
        await engine.ExecuteAsync(Plans.TwoStepQuery(), abstaining, Plans.Invocation, ct);

        drawing.Seeds[0].ShouldNotBeNull();
        abstaining.Seeds[0].ShouldBeNull(
            "The second execution inherited the first one's seed, which is the same class of " +
            "defect as inheriting its tenant id.");

        ReferenceEquals(drawing.ContextSeen, abstaining.ContextSeen).ShouldBeTrue(
            "The instance was not recycled, so the assertion above proves nothing.");
    }

    /// <summary>
    /// Draws from the context's generator and records the seed it was built from.
    /// </summary>
    /// <remarks>
    /// The seed is read <em>after</em> the draw, because reading it deliberately does not
    /// create the generator — a probe that looked first would report <c>null</c> for a flow
    /// that goes on to draw.
    /// </remarks>
    private sealed class RandomDrawingDispatcher : IStepDispatcher
    {
        /// <summary>Whether the step touches <c>ctx.Random</c> at all.</summary>
        public bool Draw { get; init; } = true;

        public List<int?> Seeds { get; } = [];

        public List<int> Draws { get; } = [];

        /// <summary>The context the last step ran on, so a test can prove it was pooled.</summary>
        public FlowContext? ContextSeen { get; private set; }

        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
        {
            if (Draw)
            {
                Draws.Add(ctx.Random.Next());
            }

            ContextSeen = ctx;
            Seeds.Add((ctx as FlowExecutionContext)?.RandomSeed);

            return ValueTask.FromResult(StepOutcome.Success);
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

    [Fact]
    public async Task ExposesTheFlowIdentityToEveryStep()
    {
        var engine = new FlowEngine(new FakeClock(T0));
        var dispatcher = new RecordingDispatcher();

        await engine.ExecuteAsync(
            Plans.FourStepSaga(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        var snapshot = dispatcher.Snapshots[0];
        snapshot.FlowId.ShouldBe("order.place");
        snapshot.FlowVersion.ShouldBe("1.0.0");
        snapshot.CapabilityId.ShouldBe("order.validate",
            "The context names the step currently running, so a log record written " +
            "inside a capability attributes itself correctly without being told.");
    }

    [Fact]
    public async Task DerivesTimeAndIdentifiersFromTheContextNotAmbientState()
    {
        var clock = new FakeClock(T0);
        var engine = new FlowEngine(clock);
        var dispatcher = new RecordingDispatcher();

        await engine.ExecuteAsync(
            Plans.FourStepSaga(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        var snapshot = dispatcher.Snapshots[0];

        snapshot.UtcNow.ShouldBe(T0,
            "Replay determinism requires the clock to come from the context. A " +
            "capability reading DateTimeOffset.UtcNow cannot be replayed.");
        snapshot.NewId.ShouldNotBe(Guid.Empty);
        snapshot.HasRandom.ShouldBeTrue();
        snapshot.TimeRemaining.ShouldBe(TimeSpan.FromSeconds(30));
    }

    private sealed class StateWritingDispatcher(string secret) : IStepDispatcher
    {
        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
        {
            ctx.Set(secret);
            return ValueTask.FromResult(StepOutcome.Success);
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

    private sealed class StateReadingDispatcher : IStepDispatcher
    {
        public bool Found { get; private set; }

        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
        {
            Found |= ctx.TryGet<string>(out _);
            return ValueTask.FromResult(StepOutcome.Success);
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
