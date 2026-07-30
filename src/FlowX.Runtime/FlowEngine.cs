namespace FlowX.Runtime;

/// <summary>
/// Executes a compiled <see cref="ExecutionPlan"/>: walks its steps, honours the
/// deadline, and unwinds compensation when something fails.
/// </summary>
/// <remarks>
/// <para>
/// The engine owns <em>control flow</em> and nothing else. It does not know what a
/// capability is, what types a step consumes, or how a step was triggered — those
/// belong to the dispatcher, to the generator and to the transport plugin
/// respectively. That separation is what lets the same flow run behind HTTP, Kafka
/// or cron without changing (quality goal Q4), and what keeps this class small
/// enough to reason about.
/// </para>
/// <para>
/// <strong>Deadlines are enforced at step boundaries.</strong> The engine will not
/// start a step whose flow has run out of budget, but it does not interrupt a step
/// already running. Interrupting in-flight work is the Timeout policy's job (P4),
/// and conflating the two would put a timer allocation on every execution to solve a
/// problem most flows do not have.
/// </para>
/// </remarks>
public sealed class FlowEngine
{
    private const int DefaultMaxPooledContexts = 128;

    private readonly IClock _clock;
    private readonly ContextPool _contexts;

    /// <summary>Creates an engine.</summary>
    /// <param name="clock">
    /// The time source. Injected so deadline behaviour is testable without sleeping,
    /// and so a durable replay can drive journaled time through the same path.
    /// </param>
    /// <param name="maxPooledContexts">How many contexts to retain between executions.</param>
    public FlowEngine(IClock clock, int maxPooledContexts = DefaultMaxPooledContexts)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPooledContexts);

        _clock = clock;
        _contexts = new ContextPool(maxPooledContexts);
    }

    /// <summary>Executes a plan to completion, to first failure, or to its deadline.</summary>
    /// <param name="plan">The compiled flow.</param>
    /// <param name="dispatcher">Invokes the capability behind each step index.</param>
    /// <param name="invocation">Correlation, tenant and the caller's remaining budget.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    public async ValueTask<FlowExecutionResult> ExecuteAsync(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        FlowInvocation invocation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(dispatcher);

        var context = _contexts.Rent();

        try
        {
            context.Initialise(plan, invocation, _clock);
            return await RunAsync(plan, dispatcher, context, ct).ConfigureAwait(false);
        }
        finally
        {
            _contexts.Return(context);
        }
    }

    /// <summary>
    /// Executes a plan, seeding the flow's input so steps can bind to it by type.
    /// </summary>
    /// <remarks>
    /// Generic at the entry point and nowhere else. The step loop still knows nothing
    /// about types — it is this one call that puts the input into the context under its
    /// own type, which is what lets the generated dispatcher write
    /// <c>ctx.Get&lt;PlaceOrder&gt;()</c> for the first step and
    /// <c>ctx.Get&lt;ValidatedOrder&gt;()</c> for the second.
    /// </remarks>
    public async ValueTask<FlowExecutionResult> ExecuteAsync<TIn>(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        FlowInvocation invocation,
        TIn input,
        CancellationToken ct = default)
        where TIn : notnull
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(input);

        var context = _contexts.Rent();

        try
        {
            context.Initialise(plan, invocation, _clock);
            context.Set(input);

            return await RunAsync(plan, dispatcher, context, ct).ConfigureAwait(false);
        }
        finally
        {
            _contexts.Return(context);
        }
    }

    /// <summary>
    /// Executes a plan and projects its declared output from the finished context.
    /// </summary>
    /// <param name="plan">The compiled flow.</param>
    /// <param name="dispatcher">Invokes the capability behind each step index.</param>
    /// <param name="invocation">Correlation, tenant and the caller's remaining budget.</param>
    /// <param name="input">The flow's input, seeded into the context under its own type.</param>
    /// <param name="projection">
    /// The generated <c>.Return(...)</c> clause. A static delegate on the generated
    /// partial class, so passing it allocates nothing.
    /// </param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <remarks>
    /// The projection runs <em>here</em>, inside the rental, and not in the caller. The
    /// context is pooled and reset the moment this method returns, so a caller handed the
    /// context would read another flow's data — this is the only place the output can be
    /// taken safely.
    /// </remarks>
    public async ValueTask<FlowExecutionResult<TOut>> ExecuteAsync<TIn, TOut>(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        FlowInvocation invocation,
        TIn input,
        Func<FlowContext, TOut> projection,
        CancellationToken ct = default)
        where TIn : notnull
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(projection);

        var context = _contexts.Rent();

        try
        {
            context.Initialise(plan, invocation, _clock);
            context.Set(input);

            var outcome = await RunAsync(plan, dispatcher, context, ct).ConfigureAwait(false);

            return outcome.IsSuccess
                ? new FlowExecutionResult<TOut>(outcome, projection(context))
                : new FlowExecutionResult<TOut>(outcome, default);
        }
        finally
        {
            _contexts.Return(context);
        }
    }

    private static async ValueTask<FlowExecutionResult> RunAsync(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        FlowExecutionContext context,
        CancellationToken ct)
    {
        // Only steps that both completed and declared a compensation go on the stack,
        // so the unwind never has to filter. The stack belongs to the pooled context,
        // so a saga costs no more per execution than a query does.
        var compensations = plan.HasCompensation ? context.Compensations : null;
        var steps = plan.Graph.Steps;
        var completed = 0;
        Error? failure = null;

        for (var i = 0; i < steps.Length && failure is null; i++)
        {
            var step = steps[i];
            context.EnterStep(step);

            if (context.UtcNow >= context.Deadline)
            {
                failure = FlowErrors.DeadlineExceeded(plan.Flow.Id, context.Deadline);
                break;
            }

            StepOutcome outcome;

            try
            {
                outcome = await dispatcher.ExecuteAsync(i, context, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Caller-initiated, and not the flow's fault — but the work already
                // done still has to be undone, so this joins the failure path rather
                // than propagating.
                failure = FlowErrors.Cancelled(plan.Flow.Id);
                break;
            }
#pragma warning disable CA1031 // A capability that throws is a defect; the engine converts
            catch (Exception exception)  //   it into an error rather than letting it kill the
            {                            //   trigger's consumer loop. This is the one place a
                failure = FlowErrors     //   general catch is correct, and it re-reports rather
                    .Unhandled(context.CapabilityId, exception); // than swallowing.
                break;
            }
#pragma warning restore CA1031

            if (outcome.IsSuccess)
            {
                completed++;
                compensations?.RecordCompleted(step);
                continue;
            }

            failure = outcome.Error;
        }

        if (failure is null)
        {
            return new FlowExecutionResult(null, completed, CompensationOutcome.NotRequired);
        }

        context.SetError(failure);

        var compensation = compensations is null
            ? CompensationOutcome.NotRequired
            : await CompensateAsync(compensations, dispatcher, context).ConfigureAwait(false);

        return new FlowExecutionResult(failure, completed, compensation);
    }

    /// <summary>
    /// Unwinds the completed compensable steps, newest first, and keeps going when one
    /// of them fails.
    /// </summary>
    /// <remarks>
    /// Best-effort by design. Abandoning the remaining undo work because one
    /// compensation failed leaves strictly more inconsistency than continuing does —
    /// a failed refund is no reason to also leak the inventory reservation. The
    /// partial failure is reported, not hidden.
    /// <para>
    /// Cancellation is deliberately not honoured here: a cancelled flow still has to
    /// clean up after itself, and a token cancelled mid-unwind would leave the
    /// dangling state the unwind exists to prevent.
    /// </para>
    /// </remarks>
    private static async ValueTask<CompensationOutcome> CompensateAsync(
        CompensationStack compensations,
        IStepDispatcher dispatcher,
        FlowExecutionContext context)
    {
        if (compensations.IsEmpty)
        {
            return CompensationOutcome.NotRequired;
        }

        var allSucceeded = true;

        foreach (var step in compensations.Unwind())
        {
            context.EnterStep(step);

            try
            {
                var outcome = await dispatcher
                    .CompensateAsync(step.Index, context, CancellationToken.None)
                    .ConfigureAwait(false);

                allSucceeded &= outcome.IsSuccess;
            }
#pragma warning disable CA1031 // Same reasoning as the step loop: a throwing compensation is a
            catch (Exception)  //   defect, and one broken undo must not abandon the others.
            {
                allSucceeded = false;
            }
#pragma warning restore CA1031
        }

        return allSucceeded ? CompensationOutcome.Succeeded : CompensationOutcome.PartiallyFailed;
    }
}
