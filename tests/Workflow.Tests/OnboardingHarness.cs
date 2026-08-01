using System.Security.Claims;
using System.Globalization;
using System.Text;
using FlowX;
using FlowX.Conformance.InMemory;
using FlowX.Hosting;
using FlowX.Runtime;
using FlowX.Testing;

namespace Workflow.Tests;

/// <summary>
/// A <see cref="FlowTestHost"/> for a flow that declares <see cref="ExecutionProfile.Durable"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This exists because the shipped test host cannot run this sample's flow, and that
/// is asserted rather than asserted-about.</strong>
/// <see cref="WhyTheseTestsDoNotUseFlowTestHostTests"/> runs <c>employee.onboard</c> through
/// <see cref="FlowTestHost"/> and gets <c>flow.durability_not_configured</c> before its first
/// step. The host has no journal seam: every <c>RunAsync</c> on it reaches the
/// <c>FlowEngine.ExecuteAsync</c> overload that passes <c>durable: null</c>, and the engine
/// refuses a durable plan with no instance rather than running it ephemerally — which is the
/// correct refusal and leaves a durable sample with nothing to test on.
/// </para>
/// <para>
/// <strong>What is real here is everything except the two things a test has to own.</strong>
/// The engine, the compiled plan, the pooled context, the compensation stack, the deadline
/// check, the merge strategy, the sub-flow recursion, the lease, the fencing token and the
/// journal writes are the production ones — the journal and the lease store are the
/// conformance suite's reference implementations, held to the same contract PostgreSQL is.
/// This type contributes an <see cref="IStepDispatcher"/> in front of the generated one and
/// a trace, which is exactly what <c>FlowTestHost</c> contributes.
/// </para>
/// <para>
/// <strong>It is deliberately a copy of a shape that already ships, not a new idea.</strong>
/// <c>SubstitutingDispatcher</c> in <c>FlowX.Testing</c> is <c>internal</c>, so it cannot be
/// reused; the recording rules below — a capability by its id, a branch by the arm it took, a
/// switch by its arm number, a loop by its count, a sub-flow by the child's id — are its
/// rules, so a reader who knows one knows the other. When <c>FlowTestHost</c> grows a durable
/// seam, this file is what should be deleted.
/// </para>
/// </remarks>
internal sealed class OnboardingHarness
{
    private readonly Dictionary<string, CapabilityStandIn> _substitutions = new(StringComparer.Ordinal);

    /// <summary>
    /// The caller every onboarding in this file runs as.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>OnboardEmployeeFlow</c> and the flows it composes declare six permissions between
    /// them, and the engine decides each stance against the invocation's principal before
    /// the step is dispatched. A harness supplying none would refuse every run at
    /// <c>OpenPayrollRecord</c>, and every test below would be one assertion about
    /// authorisation wearing the name of an assertion about control flow.
    /// </para>
    /// <para>
    /// Listed rather than granted wholesale so a capability added with a seventh permission
    /// fails here, naming the grant it needs. The sample's many
    /// <c>Authorization.Internal</c> capabilities need nothing: a trigger addresses a flow
    /// and never a capability, so that stance admits every caller and is why an
    /// HTTP-triggered flow may call <c>hardware.order</c> at all.
    /// </para>
    /// </remarks>
    internal static ClaimsPrincipal Coordinator { get; } = TestPrincipal.Holding(
        "access.write",
        "equipment.approve",
        "identity.write",
        "payroll.write",
        "screening.write",
        "supplier.write");

    private FlowInvocation _invocation =
        new("corr-test", "key-test", TenantId: null, Deadline: null, Principal: Coordinator);

    private OnboardingHarness(OnboardingWorld world) => World = world;

    /// <summary>The in-memory systems the capabilities write to. Assert on these for effects.</summary>
    public OnboardingWorld World { get; }

    /// <summary>The clock the engine reads. Advance it to make the flow's deadline expire.</summary>
    public FlowTestClock Clock { get; } = new();

    /// <summary>The journal every step boundary commits to.</summary>
    public InMemoryFlowJournal Journal { get; } = new();

    /// <summary>Where exclusive ownership and fencing tokens come from.</summary>
    public InMemoryLeaseStore Leases { get; } = new();

    /// <summary>Starts a harness over a fresh set of in-memory systems.</summary>
    public static OnboardingHarness Create() => new(new OnboardingWorld());

    /// <summary>Makes a capability fail wherever it appears, in the parent or in the child.</summary>
    /// <param name="capabilityId">The id as the plan carries it, e.g. <c>workspace.issue_pass</c>.</param>
    /// <param name="error">The business error the stand-in returns.</param>
    public OnboardingHarness Substitute(string capabilityId, Error error)
        => Substitute(capabilityId, (_, _) => ValueTask.FromResult(StepOutcome.Failed(error)));

    /// <summary>Replaces a capability with a function of the context.</summary>
    public OnboardingHarness Substitute(string capabilityId, CapabilityStandIn standIn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityId);
        ArgumentNullException.ThrowIfNull(standIn);

        _substitutions[capabilityId] = standIn;

        return this;
    }

    /// <summary>Sets what a trigger would have supplied.</summary>
    public OnboardingHarness WithInvocation(FlowInvocation invocation)
    {
        _invocation = invocation;

        return this;
    }

    /// <summary>Runs <c>employee.onboard</c> and projects its declared output.</summary>
    /// <param name="input">The value a trigger would have deserialised.</param>
    /// <param name="ct">The test's cancellation token.</param>
    public async ValueTask<DurableRun> RunAsync(OnboardEmployee input, CancellationToken ct)
    {
        var trace = new DurableTrace();
        var host = NewHost();

        var result = await host
            .RunAsync(
                OnboardEmployeeFlow.Plan,
                Wrap(OnboardEmployeeFlow.Plan, World.Parent, trace),
                _invocation,
                input,
                OnboardEmployeeFlow.Projection,
                ct)
            .ConfigureAwait(false);

        return new DurableRun(result, trace);
    }

    /// <summary>
    /// The host these runs go through: the real one, with the reference stores behind it.
    /// </summary>
    /// <remarks>
    /// Built per run rather than held, so a test that runs twice does not share a drain
    /// counter — and so that <see cref="Clock"/> advanced between two runs is read by the
    /// engine of the second.
    /// </remarks>
    public FlowHost NewHost() => new(
        new FlowEngine(Clock),
        new FlowXOptions
        {
            ApplicationName = "Workflow.Tests",
            NodeName = "test-node",
            ShutdownDrainTimeout = TimeSpan.FromSeconds(5),
        },
        new FlowDurability(Journal, Leases));

    /// <summary>Puts the recording, substituting view in front of a generated dispatcher.</summary>
    public IStepDispatcher Wrap(ExecutionPlan plan, IStepDispatcher inner, DurableTrace trace) =>
        new RecordingDispatcher(plan, inner, trace, _substitutions);
}

/// <summary>A capability's stand-in, with the same job the generated dispatcher's case does.</summary>
/// <param name="ctx">The context the step would have run under — the iteration's scope inside a loop.</param>
/// <param name="ct">Cancellation linked to the caller's token.</param>
internal delegate ValueTask<StepOutcome> CapabilityStandIn(FlowContext ctx, CancellationToken ct);

/// <summary>The in-memory systems one run writes to, and the dispatchers over them.</summary>
/// <remarks>
/// The adapters are the sample's own <c>internal</c> ones, reached through
/// <c>InternalsVisibleTo</c>, so what a test asserts on is the behaviour the application
/// ships rather than a double written to agree with it.
/// </remarks>
internal sealed class OnboardingWorld
{
    /// <summary>Payroll, agreements and directory accounts.</summary>
    public InMemoryPeopleDirectory People { get; } = new();

    /// <summary>Orders, approvals and assignments.</summary>
    public InMemoryAssetRegistry Assets { get; } = new();

    /// <summary>Grants, bookings, screening and post.</summary>
    public InMemoryAccessControl Access { get; } = new();

    /// <summary>Desks and building passes.</summary>
    public InMemoryFacilities Facilities { get; } = new();

    /// <summary>The composed child's generated dispatcher, over the real capabilities.</summary>
    public ProvisionWorkspaceFlow.Dispatcher Child => new(
        new AllocateDesk(Facilities),
        new CancelBuildingPass(Facilities),
        new IssueBuildingPass(Facilities),
        new ReleaseDesk(Facilities));

    /// <summary>The parent's generated dispatcher, over the real capabilities and the real child.</summary>
    public OnboardEmployeeFlow.Dispatcher Parent => new(
        new AssignEquipment(Assets),
        new AutoClearEquipment(),
        new CancelLaptopOrder(Assets),
        new ClosePayrollRecord(People),
        new CreateIdentity(People),
        new DisableIdentity(People),
        new GrantSystemAccess(Access),
        new OpenPayrollRecord(People),
        new OrderLaptop(Assets),
        new RecordEquipmentApproval(Assets),
        new ReturnEquipment(Assets),
        new RevokeSystemAccess(Access),
        new ScheduleInduction(Access),
        new SendWelcomePack(Access),
        new SignSupplierAgreement(People),
        new StartBackgroundCheck(Access),
        new ValidateOffer(),
        new VoidSupplierAgreement(People),
        new WaiveBackgroundCheck(Access),
        Child);
}

/// <summary>What one run did, in the order it did it.</summary>
/// <remarks>
/// <para>
/// Entries hold strings and integers only. The engine's contexts are pooled and reset the
/// instant a flow returns, so a trace that captured one would read as empty at best and as
/// the next run's data at worst.
/// </para>
/// <para>
/// <strong>Order is exact for sequential control flow and arbitrary inside a fork.</strong>
/// This flow's <c>Parallel</c> genuinely interleaves, so assert on <see cref="Count"/> or
/// <see cref="Ran"/> there. Compensation is always sequential and strictly reverse, so
/// <see cref="Compensated"/> is exact even for a run that forked.
/// </para>
/// </remarks>
internal sealed class DurableTrace
{
    private readonly List<string> _entries = [];
    private readonly List<string> _steps = [];
    private readonly List<string> _compensations = [];
    private readonly Lock _sync = new();

    /// <summary>Everything the engine asked, in the order it asked.</summary>
    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_sync)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>The steps that ran, in order. Includes the one that failed.</summary>
    public IReadOnlyList<string> Executed
    {
        get
        {
            lock (_sync)
            {
                return [.. _steps];
            }
        }
    }

    /// <summary>The compensations that ran, in unwind order, across the sub-flow boundary.</summary>
    public IReadOnlyList<string> Compensated
    {
        get
        {
            lock (_sync)
            {
                return [.. _compensations];
            }
        }
    }

    /// <summary>Whether a capability ran at least once.</summary>
    public bool Ran(string capabilityId) => Count(capabilityId) > 0;

    /// <summary>How many times a capability ran. The question a fork or a loop actually has.</summary>
    public int Count(string capabilityId)
    {
        lock (_sync)
        {
            var count = 0;

            foreach (var name in _steps)
            {
                if (string.Equals(name, capabilityId, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>The whole trace, one entry per line. Written for an assertion message.</summary>
    public override string ToString()
    {
        lock (_sync)
        {
            if (_entries.Count == 0)
            {
                return "(nothing ran)";
            }

            var text = new StringBuilder();

            foreach (var entry in _entries)
            {
                text.Append(entry).Append('\n');
            }

            return text.ToString();
        }
    }

    internal void RecordStep(string flowId, int index, string name)
    {
        lock (_sync)
        {
            _steps.Add(name);
            _entries.Add(Line(flowId, index, "step", name));
        }
    }

    internal void RecordCompensation(string flowId, int index, string name)
    {
        lock (_sync)
        {
            _compensations.Add(name);
            _entries.Add(Line(flowId, index, "undo", name));
        }
    }

    internal void RecordControl(string flowId, int index, string kind, string name)
    {
        lock (_sync)
        {
            _entries.Add(Line(flowId, index, kind, name));
        }
    }

    private static string Line(string flowId, int index, string kind, string name) =>
        string.Create(CultureInfo.InvariantCulture, $"{flowId}[{index}] {kind}: {name}");
}

/// <summary>What one run produced: the engine's result, and the trace.</summary>
internal sealed class DurableRun
{
    internal DurableRun(FlowExecutionResult<OnboardingResult> result, DurableTrace trace)
    {
        Result = result;
        Trace = trace;
    }

    /// <summary>The engine's own result.</summary>
    public FlowExecutionResult<OnboardingResult> Result { get; }

    /// <summary>What the flow did.</summary>
    public DurableTrace Trace { get; }

    /// <summary>The projected output. Reading it on a failed flow throws.</summary>
    public OnboardingResult Output => Result.Value;

    /// <summary>The business error, or <c>null</c> when the flow completed.</summary>
    public Error? Error => Result.Error;

    /// <summary>True when every step completed.</summary>
    public bool IsSuccess => Result.IsSuccess;

    /// <summary>What happened to the compensations.</summary>
    public CompensationOutcome Compensation => Result.Compensation;

    /// <inheritdoc />
    public override string ToString() =>
        (Result.IsSuccess ? "completed" : "failed: " + Result.Error!.Code) + "\n" + Trace;
}

/// <summary>
/// The one piece of machinery the harness adds to the real runtime: a dispatcher that stands
/// in front of the generated one, records what the engine asked, and answers for the
/// capabilities a test replaced.
/// </summary>
/// <remarks>
/// A child flow's dispatcher is wrapped the same way when the engine asks for it, so a
/// substitution applies wherever its capability appears and the composed flow's steps land in
/// the same trace as its parent's. That is what makes an assertion about strict-reverse
/// unwind <em>across</em> the sub-flow boundary expressible at all.
/// </remarks>
internal sealed class RecordingDispatcher : IStepDispatcher
{
    private readonly ExecutionPlan _plan;
    private readonly IStepDispatcher _inner;
    private readonly DurableTrace _trace;
    private readonly Dictionary<string, CapabilityStandIn> _substitutions;

    internal RecordingDispatcher(
        ExecutionPlan plan,
        IStepDispatcher inner,
        DurableTrace trace,
        Dictionary<string, CapabilityStandIn> substitutions)
    {
        _plan = plan;
        _inner = inner;
        _trace = trace;
        _substitutions = substitutions;
    }

    /// <inheritdoc />
    public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
    {
        var step = _plan.Graph[stepIndex];

        _trace.RecordStep(_plan.Flow.Id, stepIndex, StepName(step));

        return step.Capability is { } capability && _substitutions.TryGetValue(capability.Id, out var standIn)
            ? standIn(ctx, ct)
            : _inner.ExecuteAsync(stepIndex, ctx, ct);
    }

    /// <inheritdoc />
    public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
    {
        var compensation = _plan.Graph[stepIndex].Compensation;

        _trace.RecordCompensation(_plan.Flow.Id, stepIndex, compensation?.Id ?? "compensation");

        return compensation is not null && _substitutions.TryGetValue(compensation.Id, out var standIn)
            ? standIn(ctx, ct)
            : _inner.CompensateAsync(stepIndex, ctx, ct);
    }

    /// <inheritdoc />
    public bool Evaluate(int stepIndex, FlowContext ctx)
    {
        var taken = _inner.Evaluate(stepIndex, ctx);

        _trace.RecordControl(_plan.Flow.Id, stepIndex, "branch", taken ? "then" : "otherwise");

        return taken;
    }

    /// <inheritdoc />
    public int Select(int stepIndex, FlowContext ctx)
    {
        var arm = _inner.Select(stepIndex, ctx);

        _trace.RecordControl(
            _plan.Flow.Id,
            stepIndex,
            "switch",
            arm < 0 ? "default" : arm.ToString(CultureInfo.InvariantCulture));

        return arm;
    }

    /// <inheritdoc />
    public IterationSource BeginIteration(int stepIndex, FlowContext ctx)
    {
        var source = _inner.BeginIteration(stepIndex, ctx);

        _trace.RecordControl(
            _plan.Flow.Id,
            stepIndex,
            "foreach",
            source.Count.ToString(CultureInfo.InvariantCulture));

        return source;
    }

    /// <inheritdoc />
    public FlowContext EnterIteration(int stepIndex, in IterationSource source, int iteration, FlowContext ctx) =>
        _inner.EnterIteration(stepIndex, in source, iteration, ctx);

    /// <inheritdoc />
    public SubFlowSource BeginSubFlow(int stepIndex, FlowContext ctx)
    {
        var source = _inner.BeginSubFlow(stepIndex, ctx);

        _trace.RecordControl(
            _plan.Flow.Id,
            stepIndex,
            "subflow",
            source.Plan.Flow.Id + ":" + _plan.Graph[stepIndex].Mode);

        return new SubFlowSource(
            source.Plan,
            new RecordingDispatcher(source.Plan, source.Dispatcher, _trace, _substitutions),
            source.Input);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The wrapper is stripped before forwarding. A generated <c>EnterSubFlow</c> reads only
    /// the input, but handing back a source whose dispatcher is not the one the flow produced
    /// would be a lie this type has no reason to tell.
    /// </remarks>
    public void EnterSubFlow(int stepIndex, in SubFlowSource source, FlowContext child)
    {
        var original = source.Dispatcher is RecordingDispatcher wrapper ? wrapper._inner : source.Dispatcher;

        _inner.EnterSubFlow(stepIndex, new SubFlowSource(source.Plan, original, source.Input), child);
    }

    /// <inheritdoc />
    public StepJournalEntry DescribeStep(int stepIndex, FlowContext ctx) => _inner.DescribeStep(stepIndex, ctx);

    /// <inheritdoc />
    public void RestoreState(FlowContext ctx, string stateBagJson) => _inner.RestoreState(ctx, stateBagJson);

    /// <inheritdoc />
    public JournalPayload DescribeInput(object? input) => _inner.DescribeInput(input);

    private static string StepName(StepNode step) => step.Kind switch
    {
        StepKind.Capability => step.Capability!.Id,
        StepKind.Emit => "emit:" + step.EventType,

        // A suspension point is only ever dispatched when the signal it waits for has been
        // delivered — an unsatisfied one stops the loop before the dispatcher is reached — so
        // its presence in a trace is exactly the fact "this invocation carried the signal".
        StepKind.AwaitSignal => "await:" + step.SignalType,

        // And a timer is only ever dispatched when it has come due, for the same reason: an
        // unelapsed one stops the loop before the dispatcher is reached. Its presence in a
        // trace is the fact "the wait was over by the time this invocation looked".
        StepKind.Delay => "delay:" + StepNode.DelayIdentity,
        StepKind.Fail => "fail",
        _ => step.Kind.ToString().ToLowerInvariant(),
    };
}
