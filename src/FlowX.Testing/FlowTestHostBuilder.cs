using System.Security.Claims;
using FlowX.Runtime;

namespace FlowX.Testing;

/// <summary>Configures a <see cref="FlowTestHost"/>. Created by <see cref="FlowTestHost.For"/>.</summary>
/// <remarks>
/// <para>
/// Every knob here has a usable default, so the shortest useful test is
/// <c>FlowTestHost.For(plan, dispatcher).Build()</c>.
/// </para>
/// <para>
/// <strong>What is deliberately absent.</strong> There is no mock configuration, no
/// verification API, no auto-wiring of capabilities from a container, and no journal.
/// Substitution is the whole feature: a capability is replaced by a delegate, and
/// everything else — the engine, the plan, the pooled context, the compensation stack —
/// is the production article. See <c>docs/23-Testing-Strategy.md §6</c> for what a
/// question this host cannot answer should be asked of instead.
/// </para>
/// </remarks>
public sealed class FlowTestHostBuilder
{
    private readonly ExecutionPlan _plan;
    private readonly IStepDispatcher _dispatcher;
    private readonly Dictionary<string, CapabilitySubstitute> _substitutions = new(StringComparer.Ordinal);

    private IClock _clock = new FlowTestClock();
    private FlowInvocation _invocation = new("test-correlation-id", "test-idempotency-key");
    private TimeSpan _detachedTimeout = TimeSpan.FromSeconds(5);

    internal FlowTestHostBuilder(ExecutionPlan plan, IStepDispatcher dispatcher)
    {
        _plan = plan;
        _dispatcher = dispatcher;
    }

    /// <summary>Makes a capability fail with <paramref name="error"/> wherever it appears.</summary>
    /// <param name="capabilityId">
    /// The capability id as the plan carries it, e.g. <c>payment.capture</c>. Not the
    /// class name: the plan is keyed by contract identity, which is what survives a
    /// capability being moved, renamed or replaced by a different implementation.
    /// </param>
    /// <param name="error">The business error the stand-in returns.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// The common case by a wide margin, because the interesting half of a saga is the
    /// half no happy-path run reaches. It takes no output type, since a failed step
    /// produces none.
    /// </remarks>
    public FlowTestHostBuilder Substitute(string capabilityId, Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return Substitute(capabilityId, (_, _) => ValueTask.FromResult(StepOutcome.Failed(error)));
    }

    /// <summary>Replaces a capability with a synchronous function of the context.</summary>
    /// <typeparam name="TOutput">
    /// The contract the capability produces. Inferred from the lambda, so
    /// <c>Substitute("inventory.reserve", ctx =&gt; Result.Ok(new Reservation(…)))</c>
    /// needs no type argument.
    /// </typeparam>
    /// <param name="capabilityId">The capability id as the plan carries it.</param>
    /// <param name="replacement">
    /// Reads the context exactly as the real capability's step would, and returns what it
    /// would have returned. A success value is written into the context under its own
    /// type, which is how the next step binds to it.
    /// </param>
    /// <returns>This builder.</returns>
    public FlowTestHostBuilder Substitute<TOutput>(
        string capabilityId,
        Func<FlowContext, Result<TOutput>> replacement)
        where TOutput : notnull
    {
        ArgumentNullException.ThrowIfNull(replacement);

        return Substitute(capabilityId, (ctx, _) => ValueTask.FromResult(Apply(ctx, replacement(ctx))));
    }

    /// <summary>Replaces a capability with an asynchronous function of the context.</summary>
    /// <typeparam name="TOutput">The contract the capability produces, inferred from the lambda.</typeparam>
    /// <param name="capabilityId">The capability id as the plan carries it.</param>
    /// <param name="replacement">
    /// The stand-in. Awaited by the engine on the step's own thread, so a substitute that
    /// yields makes the step genuinely asynchronous — which is what a test of a
    /// <c>Parallel</c> or a bounded <c>ForEach</c> needs in order to overlap at all.
    /// </param>
    /// <returns>This builder.</returns>
    public FlowTestHostBuilder Substitute<TOutput>(
        string capabilityId,
        Func<FlowContext, CancellationToken, ValueTask<Result<TOutput>>> replacement)
        where TOutput : notnull
    {
        ArgumentNullException.ThrowIfNull(replacement);

        return Substitute(
            capabilityId,
            async (ctx, ct) => Apply(ctx, await replacement(ctx, ct).ConfigureAwait(false)));
    }

    /// <summary>Sets the time source. Defaults to a <see cref="FlowTestClock"/> at the Unix epoch.</summary>
    /// <param name="clock">The clock the engine reads to check the flow's deadline.</param>
    /// <returns>This builder.</returns>
    public FlowTestHostBuilder WithClock(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;

        return this;
    }

    /// <summary>Sets what a trigger would have supplied. Defaults to fixed test values.</summary>
    /// <param name="invocation">Correlation, idempotency key, tenant and caller budget.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// The idempotency key is worth setting deliberately: a capability passes it
    /// downstream, and that is what turns at-least-once delivery into effectively-once
    /// effects. A test that asserts on it is testing the property that makes a retry safe.
    /// </remarks>
    public FlowTestHostBuilder WithInvocation(FlowInvocation invocation)
    {
        _invocation = invocation;

        return this;
    }

    /// <summary>Runs the flow as <paramref name="principal"/>.</summary>
    /// <param name="principal">
    /// The caller a trigger would have resolved from validated claims, or <c>null</c> to run
    /// the flow anonymously.
    /// </param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// <para>
    /// Keeps the rest of the invocation as it stands, because the two are set for different
    /// reasons: correlation and the idempotency key are about what a capability passes
    /// downstream, and this is about which steps the flow is allowed to reach at all.
    /// </para>
    /// <para>
    /// <strong>A flow whose capabilities declare a stance needs this or it is refused</strong>,
    /// which is the point: the default is anonymous, so a test that forgets to say who is
    /// calling gets the same answer a real anonymous caller would. See
    /// <see cref="TestPrincipal"/> for the two shapes worth passing.
    /// </para>
    /// </remarks>
    public FlowTestHostBuilder As(ClaimsPrincipal? principal)
    {
        _invocation = _invocation with { Principal = principal };

        return this;
    }

    /// <summary>How long a run waits for detached sub-flows before giving up. Default five seconds.</summary>
    /// <param name="timeout">The wait. Must be positive.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// A <see cref="SubFlowMode.Detached"/> child outlives its parent by design, so a run
    /// that returned the instant the parent finished would hand back a trace missing
    /// whatever the child had not reached yet — a test that passes or fails on timing.
    /// The host waits instead, and says so by throwing when the wait runs out.
    /// </remarks>
    public FlowTestHostBuilder WithDetachedTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        _detachedTimeout = timeout;

        return this;
    }

    /// <summary>Builds the host.</summary>
    /// <returns>A host that can run the flow, repeatedly, in isolation.</returns>
    /// <exception cref="InvalidOperationException">
    /// A substituted capability id appears nowhere in the plan. Thrown rather than
    /// ignored: a mistyped id is a substitution that never happens, and a test whose
    /// stand-in never ran asserts on the real capability while claiming otherwise.
    /// </exception>
    public FlowTestHost Build()
    {
        Validate();

        return new FlowTestHost(
            _plan,
            _dispatcher,
            new Dictionary<string, CapabilitySubstitute>(_substitutions, StringComparer.Ordinal),
            _clock,
            _invocation,
            _detachedTimeout);
    }

    private FlowTestHostBuilder Substitute(string capabilityId, CapabilitySubstitute substitute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityId);

        _substitutions[capabilityId] = substitute;

        return this;
    }

    private static StepOutcome Apply<TOutput>(FlowContext ctx, Result<TOutput> result)
        where TOutput : notnull
    {
        if (!result.IsSuccess)
        {
            return StepOutcome.Failed(result.Error);
        }

        // Written into the context under its own type, which is precisely what the
        // generated `ctx.Set(result.Value)` does. A stand-in that skipped this would
        // leave the next step reading a contract nothing produced.
        ctx.Set(result.Value);

        return StepOutcome.Success;
    }

    private void Validate()
    {
        if (_substitutions.Count == 0)
        {
            return;
        }

        // A composed child's capabilities are in the child's plan, which this plan does
        // not carry — a sub-flow node names an id, not a graph. So the check is exact for
        // a flow that composes nothing and is skipped for one that does, rather than
        // being approximate for both. FlowTestRun.UnusedSubstitutions covers the rest.
        if (_plan.HasSubFlow)
        {
            return;
        }

        var known = new HashSet<string>(StringComparer.Ordinal);

        foreach (var step in _plan.Graph.Steps)
        {
            if (step.Capability is { } capability)
            {
                known.Add(capability.Id);
            }

            if (step.Compensation is { } compensation)
            {
                known.Add(compensation.Id);
            }
        }

        foreach (var id in _substitutions.Keys)
        {
            if (!known.Contains(id))
            {
                var available = new List<string>(known);
                available.Sort(StringComparer.Ordinal);

                throw new InvalidOperationException(
                    $"Flow '{_plan.Flow.Id}' has no capability '{id}' to substitute. " +
                    $"It uses: {string.Join(", ", available)}. " +
                    "Substitution is keyed by capability id, not by class name.");
            }
        }
    }
}
