using FlowX.Runtime;

namespace FlowX.Runtime.Tests;

/// <summary>
/// A hand-written <see cref="IStepDispatcher"/> that records what the engine asked
/// it to do.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately shaped like the code <c>FlowPlanGenerator</c> will emit at
/// WP-5: a switch on the step index, with the step's typed state held in the
/// dispatcher's own fields rather than in a state bag. Writing it by hand first
/// proves the shape works before a generator has to produce it — and if this is
/// awkward to write, the generated version would have been awkward to debug.
/// </para>
/// <para>
/// The type erasure matters. The engine's loop cannot know each step's input and
/// output types; if it boxed them into <c>object</c> it would allocate per step and
/// budget B2 would be lost on the first commit. So the dispatcher keeps the types
/// and the engine sees only success or failure.
/// </para>
/// </remarks>
internal sealed class RecordingDispatcher : IStepDispatcher
{
    private readonly Dictionary<int, Error> _failures = [];
    private readonly Dictionary<int, Error> _compensationFailures = [];

    /// <summary>Step indices executed, in the order the engine invoked them.</summary>
    public List<int> Executed { get; } = [];

    /// <summary>Step indices compensated, in the order the engine unwound them.</summary>
    public List<int> Compensated { get; } = [];

    /// <summary>The context instances seen, for reference-identity assertions only.</summary>
    /// <remarks>
    /// Do not read values off these after the engine returns: the context is reset the
    /// moment it goes back to the pool, so every field reads as empty. That the naive
    /// version of this test failed is the clearest evidence the reset works — see
    /// <see cref="Snapshots"/> for values captured while the step was running.
    /// </remarks>
    public List<FlowContext> ContextsSeen { get; } = [];

    /// <summary>Context values captured <em>during</em> each step, while they are still live.</summary>
    public List<ContextSnapshot> Snapshots { get; } = [];

    /// <summary>Set to have a step observe cancellation instead of completing.</summary>
    public int? CancelAtStep { get; set; }

    /// <summary>Invoked before each step runs, so a test can advance a fake clock.</summary>
    public Action<int>? BeforeStep { get; set; }

    /// <summary>Makes step <paramref name="index"/> fail with <paramref name="error"/>.</summary>
    public RecordingDispatcher FailAt(int index, Error error)
    {
        _failures[index] = error;
        return this;
    }

    /// <summary>Makes the compensation for step <paramref name="index"/> fail.</summary>
    public RecordingDispatcher FailCompensationAt(int index, Error error)
    {
        _compensationFailures[index] = error;
        return this;
    }

    /// <inheritdoc />
    public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
    {
        BeforeStep?.Invoke(stepIndex);

        if (CancelAtStep == stepIndex)
        {
            ct.ThrowIfCancellationRequested();
        }

        Executed.Add(stepIndex);
        ContextsSeen.Add(ctx);
        Snapshots.Add(ContextSnapshot.Of(ctx));

        return _failures.TryGetValue(stepIndex, out var error)
            ? ValueTask.FromResult(StepOutcome.Failed(error))
            : ValueTask.FromResult(StepOutcome.Success);
    }

    /// <inheritdoc />
    public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
    {
        Compensated.Add(stepIndex);

        return _compensationFailures.TryGetValue(stepIndex, out var error)
            ? ValueTask.FromResult(StepOutcome.Failed(error))
            : ValueTask.FromResult(StepOutcome.Success);
    }
}

/// <summary>
/// The values a context carried at the instant a step ran.
/// </summary>
/// <remarks>
/// Needed because the context is pooled and reset. Asserting on the live object after
/// the flow finished would assert on a cleared instance — which is correct behaviour
/// and a useless test.
/// </remarks>
internal readonly record struct ContextSnapshot(
    string FlowId,
    string FlowVersion,
    string CorrelationId,
    string IdempotencyKey,
    string? TenantId,
    string CapabilityId,
    DateTimeOffset UtcNow,
    TimeSpan TimeRemaining,
    Error? Error,
    Guid NewId,
    bool HasRandom)
{
    public static ContextSnapshot Of(FlowContext ctx) => new(
        ctx.FlowId,
        ctx.FlowVersion,
        ctx.CorrelationId,
        ctx.IdempotencyKey,
        ctx.TenantId,
        ctx.CapabilityId,
        ctx.UtcNow,
        ctx.TimeRemaining,
        ctx.Error,
        ctx.NewId(),
        ctx.Random is not null);
}

/// <summary>A clock a test can move, so deadline behaviour is deterministic.</summary>
internal sealed class FakeClock(DateTimeOffset start) : IClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow { get; private set; } = start;

    /// <summary>Moves the clock forward.</summary>
    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>Plans the engine tests execute.</summary>
internal static class Plans
{
    public static CapabilityDescriptor Validate { get; } =
        CapabilityDescriptor.Create("order.validate", "1.0.0", isIdempotent: true);

    public static CapabilityDescriptor Reserve { get; } =
        CapabilityDescriptor.Create("inventory.reserve", "1.0.0", isIdempotent: true, "inventory-ledger");

    public static CapabilityDescriptor Release { get; } =
        CapabilityDescriptor.Create("inventory.release", "1.0.0", isIdempotent: true, "inventory-ledger");

    public static CapabilityDescriptor Capture { get; } =
        CapabilityDescriptor.Create("payment.capture", "2.1.0", isIdempotent: false, "payment-gateway");

    public static CapabilityDescriptor Refund { get; } =
        CapabilityDescriptor.Create("payment.refund", "2.1.0", isIdempotent: true, "payment-gateway");

    /// <summary>Four steps; steps 1 and 2 are compensable; step 3 emits.</summary>
    public static ExecutionPlan FourStepSaga(TimeSpan? deadline = null) => ExecutionPlan.Create(
        FlowDescriptor.Create(
            "order.place", "1.0.0", ExecutionProfile.Ephemeral, deadline ?? TimeSpan.FromSeconds(30)),
        StepGraph.Create([
            StepNode.ForCapability(0, Validate),
            StepNode.ForCapability(1, Reserve, Release),
            StepNode.ForCapability(2, Capture, Refund),
            StepNode.ForEmit(3, "order.placed"),
        ]));

    /// <summary>Two steps, neither compensable.</summary>
    public static ExecutionPlan TwoStepQuery() => ExecutionPlan.Create(
        FlowDescriptor.Create("order.get", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(5)),
        StepGraph.Create([
            StepNode.ForCapability(0, Validate),
            StepNode.ForCapability(1, Validate),
        ]));

    public static FlowInvocation Invocation { get; } = new("corr-1", "idem-1", TenantId: "acme");
}
