using FlowX.Runtime;

namespace FlowX.Testing;

/// <summary>
/// Runs a compiled flow in-process, with capabilities substituted and nothing else
/// replaced, so a test can assert on control flow, compensation and output.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the flow level of the test pyramid</strong>
/// (<c>docs/23-Testing-Strategy.md §3</c>): one flow, fake capabilities, no
/// infrastructure. Below it a capability is a class with a method and needs only
/// <see cref="TestCapabilityContext"/>; above it an endpoint's status codes and media
/// types need a real server, because those are not properties of the flow.
/// </para>
/// <para>
/// <strong>It is the real runtime.</strong> The engine, the compiled plan, the pooled
/// context, the compensation stack, the deadline check, the merge strategies and the
/// sub-flow recursion are the production ones. The host contributes exactly two things:
/// an <see cref="IStepDispatcher"/> in front of the generated one, and a trace. A test
/// that passes here is a statement about the runtime, not about a simulation of it.
/// </para>
/// <para>
/// <strong>Why the plan and the dispatcher are passed in rather than discovered from a
/// flow type.</strong> The generator emits <c>Plan</c> and a nested <c>Dispatcher</c>
/// whose constructor takes each capability as its own concrete sealed type. Finding them
/// from a <c>TFlow</c> type parameter would need reflection over generated members, and
/// constructing the dispatcher would need a container to resolve those capabilities —
/// the first breaks constraint C2 (NativeAOT and trim), and the second is the mock
/// framework this deliberately is not. Naming them costs one line and keeps the host
/// reflection-free:
/// </para>
/// <code>
/// var host = FlowTestHost
///     .For(PlaceOrderFlow.Plan, new PlaceOrderFlow.Dispatcher(capture, release, reserve, validate))
///     .Substitute("payment.capture", OrderErrors.PaymentDeclined("insufficient funds"))
///     .Build();
///
/// var run = await host.RunAsync(new PlaceOrder("SKU-1", 4, "tok"));
///
/// run.Error!.Code.ShouldBe("payment.declined");
/// run.Trace.Compensated.ShouldBe(["inventory.release"]);
/// </code>
/// <para>
/// <strong>One host owns one engine, and therefore one context pool.</strong> Contexts
/// are pooled and reset on return, so a host built per test cannot hand a second test the
/// first one's data — and repeated runs on a single host exercise the reset rather than
/// avoiding it. A trace, by contrast, is created per run: two runs on one host must not
/// see each other's.
/// </para>
/// <para>
/// <strong>Thread safety.</strong> A host may be run concurrently; the engine and the
/// pool are built for it and each run has its own trace. The recorded order of steps
/// inside a <c>Parallel</c> or a concurrent <c>ForEach</c> is arbitrary, which
/// <see cref="FlowTestTrace"/> documents.
/// </para>
/// </remarks>
public sealed class FlowTestHost
{
    private readonly ExecutionPlan _plan;
    private readonly IStepDispatcher _dispatcher;
    private readonly Dictionary<string, CapabilitySubstitute> _substitutions;
    private readonly FlowEngine _engine;
    private readonly FlowInvocation _invocation;
    private readonly TimeSpan _detachedTimeout;

    internal FlowTestHost(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        Dictionary<string, CapabilitySubstitute> substitutions,
        IClock clock,
        FlowInvocation invocation,
        TimeSpan detachedTimeout)
    {
        _plan = plan;
        _dispatcher = dispatcher;
        _substitutions = substitutions;
        _invocation = invocation;
        _detachedTimeout = detachedTimeout;

        Clock = clock;

        // The host's own engine, so its context pool is the host's too. Sharing one
        // across tests is the contamination this type exists to prevent.
        _engine = new FlowEngine(clock);
    }

    /// <summary>Starts configuring a host over a compiled plan and its dispatcher.</summary>
    /// <param name="plan">
    /// The generated <c>Plan</c> of the flow under test. The real one: a hand-built plan
    /// would test the host rather than the flow.
    /// </param>
    /// <param name="dispatcher">
    /// The generated <c>Dispatcher</c>, constructed with whatever capabilities the test
    /// wants real. Anything it should not construct is substituted instead.
    /// </param>
    /// <returns>A builder.</returns>
    public static FlowTestHostBuilder For(ExecutionPlan plan, IStepDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(dispatcher);

        return new FlowTestHostBuilder(plan, dispatcher);
    }

    /// <summary>The plan under test.</summary>
    public ExecutionPlan Plan => _plan;

    /// <summary>The time source the engine reads. Advance it to test deadline behaviour.</summary>
    public IClock Clock { get; }

    /// <summary>Runs the flow with no input.</summary>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>What happened, and what it did.</returns>
    public async ValueTask<FlowTestRun> RunAsync(CancellationToken ct = default)
    {
        var scope = NewScope();

        var result = await _engine
            .ExecuteAsync(_plan, scope.Wrap(_plan, _dispatcher), _invocation, ct)
            .ConfigureAwait(false);

        await SettleAsync(scope).ConfigureAwait(false);

        return new FlowTestRun(result, scope.Trace, scope.UnusedSubstitutions);
    }

    /// <summary>Runs the flow, seeding its input so the first step can bind to it.</summary>
    /// <typeparam name="TInput">The flow's declared input contract.</typeparam>
    /// <param name="input">The value a trigger would have deserialised.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>What happened, and what it did.</returns>
    public async ValueTask<FlowTestRun> RunAsync<TInput>(TInput input, CancellationToken ct = default)
        where TInput : notnull
    {
        ArgumentNullException.ThrowIfNull(input);

        var scope = NewScope();

        var result = await _engine
            .ExecuteAsync(_plan, scope.Wrap(_plan, _dispatcher), _invocation, input, ct)
            .ConfigureAwait(false);

        await SettleAsync(scope).ConfigureAwait(false);

        return new FlowTestRun(result, scope.Trace, scope.UnusedSubstitutions);
    }

    /// <summary>Runs the flow and projects its declared output.</summary>
    /// <typeparam name="TInput">The flow's declared input contract.</typeparam>
    /// <typeparam name="TOutput">The flow's declared output contract.</typeparam>
    /// <param name="input">The value a trigger would have deserialised.</param>
    /// <param name="projection">
    /// The generated <c>Projection</c> — the flow's own <c>.Return(...)</c> clause. Passing
    /// it rather than reading the context afterwards is not a convenience: the context is
    /// pooled and reset the moment the run ends, so inside the rental is the only place
    /// the output can be taken.
    /// </param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>What happened, what it did, and what it returned.</returns>
    public async ValueTask<FlowTestRun<TOutput>> RunAsync<TInput, TOutput>(
        TInput input,
        Func<FlowContext, TOutput> projection,
        CancellationToken ct = default)
        where TInput : notnull
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(projection);

        var scope = NewScope();

        var result = await _engine
            .ExecuteAsync(_plan, scope.Wrap(_plan, _dispatcher), _invocation, input, projection, ct)
            .ConfigureAwait(false);

        await SettleAsync(scope).ConfigureAwait(false);

        return new FlowTestRun<TOutput>(result, scope.Trace, scope.UnusedSubstitutions);
    }

    private FlowTestRunScope NewScope() => new(_substitutions);

    /// <summary>
    /// Waits for anything the flow detached, then closes the trace.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The wait uses <see cref="CancellationToken.None"/> rather than the caller's token,
    /// and that is the point: a detached child is started with <c>None</c> by the engine
    /// precisely so it can outlive its parent, so cancelling the wait would abandon a
    /// child that is still running while its dispatcher still holds this trace.
    /// </para>
    /// <para>
    /// Sealing after the wait is what makes "the trace is complete" true rather than
    /// hopeful. A late entry then throws instead of arriving in the middle of an
    /// assertion.
    /// </para>
    /// </remarks>
    private async ValueTask SettleAsync(FlowTestRunScope scope)
    {
        if (_plan.HasSubFlow && !await _engine.WaitForDetachedAsync(_detachedTimeout).ConfigureAwait(false))
        {
            throw new TimeoutException(
                $"Flow '{_plan.Flow.Id}' still had {_engine.DetachedInFlight} detached sub-flow(s) " +
                $"running after {_detachedTimeout}. Its trace would be incomplete, so the run is " +
                "not reported. Give the child a substitute that completes, or raise the wait with " +
                "WithDetachedTimeout.");
        }

        scope.Trace.Seal();
    }
}
