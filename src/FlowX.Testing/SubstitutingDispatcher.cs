using FlowX.Runtime;

namespace FlowX.Testing;

/// <summary>
/// A capability's stand-in: the same job the generated dispatcher's <c>case</c> does —
/// read the context, produce an outcome — with the real capability never constructed.
/// </summary>
/// <param name="ctx">
/// The context the step would have run under. Inside a <c>ForEach</c> body this is the
/// iteration's scope, so a substitute sees the element exactly as the real step would.
/// </param>
/// <param name="ct">Cancellation linked to the caller's token.</param>
internal delegate ValueTask<StepOutcome> CapabilitySubstitute(FlowContext ctx, CancellationToken ct);

/// <summary>
/// The state one run shares with every dispatcher it reaches, parent and children alike.
/// </summary>
/// <remarks>
/// One per <c>RunAsync</c> call, never per host. Two runs on the same host must not see
/// each other's trace, and a substitution applied by the first must not count as applied
/// by the second — which is the same reason the engine resets a pooled context rather
/// than reusing it as it stands.
/// </remarks>
internal sealed class FlowTestRunScope
{
    private readonly Dictionary<IStepDispatcher, SubstitutingDispatcher> _wrappers =
        new(ReferenceEqualityComparer.Instance);

    private readonly Dictionary<string, CapabilitySubstitute> _substitutions;
    private readonly HashSet<string> _applied = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();

    internal FlowTestRunScope(Dictionary<string, CapabilitySubstitute> substitutions)
    {
        _substitutions = substitutions;
        Trace = new FlowTestTrace();
    }

    internal FlowTestTrace Trace { get; }

    /// <summary>Capabilities the caller substituted that this run never reached.</summary>
    internal IReadOnlyList<string> UnusedSubstitutions
    {
        get
        {
            lock (_sync)
            {
                var unused = new List<string>();

                foreach (var id in _substitutions.Keys)
                {
                    if (!_applied.Contains(id))
                    {
                        unused.Add(id);
                    }
                }

                unused.Sort(StringComparer.Ordinal);

                return unused;
            }
        }
    }

    /// <summary>
    /// Returns the recording, substituting view of a dispatcher, creating it once.
    /// </summary>
    /// <remarks>
    /// Memoised on the inner instance rather than built per call. A <c>SubFlow</c> inside
    /// a <c>ForEach</c> asks for the child's dispatcher once per element, and a wrapper
    /// allocated per element would put an allocation inside the loop for no reason.
    /// </remarks>
    internal IStepDispatcher Wrap(ExecutionPlan plan, IStepDispatcher inner)
    {
        lock (_sync)
        {
            if (_wrappers.TryGetValue(inner, out var existing))
            {
                return existing;
            }

            var wrapper = new SubstitutingDispatcher(plan, inner, this);
            _wrappers[inner] = wrapper;

            return wrapper;
        }
    }

    /// <summary>Finds the stand-in for a capability, and records that it was reached.</summary>
    internal bool TryTake(string? capabilityId, out CapabilitySubstitute substitute)
    {
        if (capabilityId is not null)
        {
            lock (_sync)
            {
                if (_substitutions.TryGetValue(capabilityId, out var found))
                {
                    _applied.Add(capabilityId);
                    substitute = found;

                    return true;
                }
            }
        }

        substitute = null!;

        return false;
    }
}

/// <summary>
/// The one piece of machinery <see cref="FlowTestHost"/> adds to the real runtime: an
/// <see cref="IStepDispatcher"/> that stands in front of the generated one, records what
/// the engine asked, and answers for the capabilities the test replaced.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Substitution happens here and not in a container, because the generated
/// dispatcher takes its capabilities as constructor parameters of their concrete sealed
/// types.</strong> There is nothing to override and nothing to resolve — a stand-in has
/// to sit at the only seam the runtime actually has, which is the dispatcher interface
/// the engine calls. That seam is also the honest one: it is exactly what the generated
/// <c>case</c> does, so a substituted step reads and writes the context in the same
/// order the real one would.
/// </para>
/// <para>
/// <strong>Everything not substituted is forwarded untouched.</strong> The engine, the
/// plan, the pooled context, the compensation stack and the real capabilities are the
/// production ones. That is the whole claim of this host: what a test observes is what
/// the runtime does, minus the capabilities the test said to replace.
/// </para>
/// <para>
/// A child flow's dispatcher is wrapped the same way when the engine asks for it, so a
/// substitution applies wherever its capability appears and a composed flow's steps land
/// in the same trace as its parent's.
/// </para>
/// </remarks>
internal sealed class SubstitutingDispatcher : IStepDispatcher
{
    private readonly ExecutionPlan _plan;
    private readonly IStepDispatcher _inner;
    private readonly FlowTestRunScope _scope;

    internal SubstitutingDispatcher(ExecutionPlan plan, IStepDispatcher inner, FlowTestRunScope scope)
    {
        _plan = plan;
        _inner = inner;
        _scope = scope;
    }

    /// <inheritdoc />
    public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
    {
        var step = _plan.Graph[stepIndex];

        Record(FlowTestEntryKind.Step, stepIndex, StepName(step));

        return _scope.TryTake(step.Capability?.Id, out var substitute)
            ? substitute(ctx, ct)
            : _inner.ExecuteAsync(stepIndex, ctx, ct);
    }

    /// <inheritdoc />
    public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
    {
        var compensation = _plan.Graph[stepIndex].Compensation;

        // The engine compensates only steps that declared one, so the descriptor is
        // present. Named defensively rather than dereferenced, because a plan and a
        // dispatcher from different builds would otherwise fail with a null reference
        // instead of with something a reader can act on.
        Record(FlowTestEntryKind.Compensation, stepIndex, compensation?.Id ?? "compensation");

        return _scope.TryTake(compensation?.Id, out var substitute)
            ? substitute(ctx, ct)
            : _inner.CompensateAsync(stepIndex, ctx, ct);
    }

    /// <inheritdoc />
    public bool Evaluate(int stepIndex, FlowContext ctx)
    {
        var taken = _inner.Evaluate(stepIndex, ctx);

        Record(FlowTestEntryKind.Branch, stepIndex, taken ? "branch:then" : "branch:otherwise");

        return taken;
    }

    /// <inheritdoc />
    public int Select(int stepIndex, FlowContext ctx)
    {
        var arm = _inner.Select(stepIndex, ctx);

        Record(
            FlowTestEntryKind.Switch,
            stepIndex,
            arm < 0 ? "switch:default" : "switch:" + arm.ToString(System.Globalization.CultureInfo.InvariantCulture));

        return arm;
    }

    /// <inheritdoc />
    public IterationSource BeginIteration(int stepIndex, FlowContext ctx)
    {
        var source = _inner.BeginIteration(stepIndex, ctx);

        Record(
            FlowTestEntryKind.Iteration,
            stepIndex,
            "foreach:" + source.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));

        return source;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Forwarded and not recorded. The engine calls this once per element on the thread
    /// about to run that element, so recording here would add <em>n</em> entries that say
    /// nothing <see cref="BeginIteration"/>'s count does not already say, in an order the
    /// concurrency bound makes arbitrary.
    /// </remarks>
    public FlowContext EnterIteration(int stepIndex, in IterationSource source, int iteration, FlowContext ctx) =>
        _inner.EnterIteration(stepIndex, in source, iteration, ctx);

    /// <inheritdoc />
    public SubFlowSource BeginSubFlow(int stepIndex, FlowContext ctx)
    {
        var step = _plan.Graph[stepIndex];
        var source = _inner.BeginSubFlow(stepIndex, ctx);

        Record(
            FlowTestEntryKind.SubFlow,
            stepIndex,
            "subflow:" + source.Plan.Flow.Id + ":" + step.Mode);

        // The child's dispatcher is wrapped too, so its steps and its compensations land
        // in this run's trace and a substitution reaches the capabilities it composes.
        return new SubFlowSource(source.Plan, _scope.Wrap(source.Plan, source.Dispatcher), source.Input);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The wrapper is stripped before forwarding. A generated <c>EnterSubFlow</c> reads
    /// only <see cref="SubFlowSource.Input"/>, but handing back a source whose dispatcher
    /// is not the one the flow produced would be a lie this type has no reason to tell.
    /// </remarks>
    public void EnterSubFlow(int stepIndex, in SubFlowSource source, FlowContext child)
    {
        var original = source.Dispatcher is SubstitutingDispatcher wrapper ? wrapper._inner : source.Dispatcher;

        _inner.EnterSubFlow(stepIndex, new SubFlowSource(source.Plan, original, source.Input), child);
    }

    // Forwarded rather than traced. None of these is a step boundary, so none belongs in the
    // trace — but each has an interface default, so leaving them out is the silent kind of
    // wrong: a substituted run would describe nothing and every one of them would read as an
    // empty payload. That is the defect StepTelemetry shipped with.

    /// <inheritdoc />
    public StepJournalEntry DescribeStep(int stepIndex, FlowContext ctx) =>
        _inner.DescribeStep(stepIndex, ctx);

    /// <inheritdoc />
    public JournalPayload DescribeInput(object? input) => _inner.DescribeInput(input);

    /// <inheritdoc />
    public JournalPayload DescribeCacheKey(int stepIndex, FlowContext ctx) =>
        _inner.DescribeCacheKey(stepIndex, ctx);

    /// <inheritdoc />
    public JournalPayload DescribeCacheEntry(int stepIndex, FlowContext ctx) =>
        _inner.DescribeCacheEntry(stepIndex, ctx);

    /// <inheritdoc />
    public JournalPayload DescribeAudit(int stepIndex, FlowContext ctx, IReadOnlyList<string> redact) =>
        _inner.DescribeAudit(stepIndex, ctx, redact);

    /// <inheritdoc />
    public void RestoreState(FlowContext ctx, string stateBagJson) =>
        _inner.RestoreState(ctx, stateBagJson);

    private static string StepName(StepNode step) => step.Kind switch
    {
        StepKind.Capability => step.Capability!.Id,
        StepKind.Emit => "emit:" + step.EventType,
        StepKind.AwaitSignal => "signal:" + step.SignalType,

        // Named rather than left to the fallback, so a trace distinguishes "the timer at
        // index 3 came due and was dispatched" from a kind nobody expected to reach here.
        // A delay's identity is a constant, but it is the constant a journal row carries,
        // and a trace that disagreed with the row would be the wrong kind of test double.
        StepKind.Delay => "delay:" + StepNode.DelayIdentity,
        StepKind.Fail => "fail",

        // Control transfers never reach a dispatcher — the engine resolves them itself —
        // so this arm exists to keep the switch total rather than to be reached.
        _ => step.Kind.ToString().ToLowerInvariant(),
    };

    private void Record(FlowTestEntryKind kind, int stepIndex, string name) =>
        _scope.Trace.Record(kind, _plan.Flow.Id, stepIndex, name);
}
