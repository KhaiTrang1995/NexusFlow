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
/// <strong>Branching is flat.</strong> A <c>When</c>/<c>Otherwise</c> is compiled into
/// the same step array as everything else, as a <see cref="StepKind.Branch"/> carrying a
/// false target and a <see cref="StepKind.Jump"/> closing the <c>then</c> block. A
/// <c>Switch</c> is the same shape with more destinations: one
/// <see cref="StepKind.Switch"/> carrying a target per case, and a jump closing each case
/// block. So the engine does not recurse, holds no branch stack, and allocates nothing to
/// take a branch or a case — the only difference from a linear flow is that the loop
/// index sometimes moves by more than one. A tree of nested plan objects would have read
/// more naturally and would have cost an enumerator per level on the hot path.
/// </para>
/// <para>
/// <strong>Parallel is where that stops being the whole story, and it is worth being exact
/// about how much stops.</strong> A fork uses the identical flat layout — one node
/// carrying a target per branch, each branch a contiguous span closed by a jump to the
/// join — so the plan, the graph validation, the termination proof and the manifest's
/// <c>branches</c> array are all unchanged. What changes is that the engine runs
/// <em>every</em> span instead of choosing one, concurrently, which means that between a
/// fork and its join there is no single loop index describing the flow. The engine
/// therefore recurses exactly once per fork, into <c>RunRangeAsync</c> over a sub-range;
/// it still holds no branch stack, still builds no plan objects, and a flow that does not
/// fork never reaches that code at all. A fork allocates — a linked token source, a task
/// per branch, their awaiters — and that is the one documented exception to budget B2,
/// measured rather than waved at.
/// </para>
/// <para>
/// <strong>Branches share one pooled context.</strong> That is what the disjoint-slot rule
/// (FLOWX1013) is for. The runtime's half of the bargain is that the state bag and the
/// compensation stack are serialised while a fork is in flight, so a race is a wrong value
/// and never a corrupted dictionary; the compiler's half is that two branches must not
/// write the same slot in the first place.
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

        var outcome = await RunRangeAsync(
            plan, dispatcher, context, context, compensations, 0, plan.Graph.Count, ct).ConfigureAwait(false);

        if (outcome.Failure is null)
        {
            return new FlowExecutionResult(null, outcome.Completed, CompensationOutcome.NotRequired);
        }

        context.SetError(outcome.Failure);

        var compensation = compensations is null
            ? CompensationOutcome.NotRequired
            : await CompensateAsync(compensations, dispatcher, context).ConfigureAwait(false);

        return new FlowExecutionResult(outcome.Failure, outcome.Completed, compensation);
    }

    /// <summary>How a range of steps ended: the first failure in it, and how many ran.</summary>
    /// <remarks>
    /// A struct so a branch's result costs nothing to return. The whole flow is one range,
    /// <c>[0, Count)</c>, so the sequential path and a parallel branch are literally the
    /// same code — which is the point of the shape, and the reason a branch inherits the
    /// deadline check, the compensation recording and the exception handling for free
    /// rather than by being kept in step with them.
    /// </remarks>
    private readonly struct RangeOutcome(Error? failure, int completed)
    {
        public Error? Failure { get; } = failure;

        public int Completed { get; } = completed;
    }

    /// <summary>
    /// Runs the steps in <c>[from, end)</c> — the whole flow, or one branch of a fork.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The range is what makes <c>Parallel</c> fit the flat array.</strong> A
    /// branch is a contiguous half-open span of the same one array, and
    /// <see cref="StepNode.BranchTargets"/> is validated to be strictly ascending inside
    /// the fork, so the spans are disjoint and cover the fork exactly. Termination still
    /// follows from the same fact it always did: every target points strictly forward, and
    /// a target past <paramref name="end"/> simply ends the range.
    /// </para>
    /// <para>
    /// <strong>What the flat model gives up.</strong> Between a fork and its join, "the
    /// loop index" stops describing the flow — there are several, one per branch, on
    /// several threads. Everything else survives: no branch stack, no nested plan objects,
    /// no per-step recursion, and a linear or conditional flow never leaves this method.
    /// </para>
    /// <para>
    /// <strong><paramref name="scope"/> is what the dispatcher sees;
    /// <paramref name="context"/> is what the engine keeps its books in.</strong> They are
    /// the same object everywhere except inside a <c>ForEach</c> body, where the scope is
    /// the iteration's view — the element, shadowing the shared state bag on its own type.
    /// The split is deliberate: the deadline, the compensation stack and the running-step
    /// identity belong to the flow and must not fork per element, while what a step
    /// <em>reads</em> must.
    /// </para>
    /// </remarks>
    private static async ValueTask<RangeOutcome> RunRangeAsync(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        FlowExecutionContext context,
        FlowContext scope,
        CompensationStack? compensations,
        int from,
        int end,
        CancellationToken ct)
    {
        var steps = plan.Graph.Steps;
        var completed = 0;
        Error? failure = null;

        // Not `for (i = from; i < end; i++)`. A conditional is compiled into this same flat
        // array as a Branch and a Jump, so the index advances either by one or to a
        // target. The loop is still guaranteed to terminate: StepGraph rejects any target
        // that is out of range or points backwards, which is the whole reason that check
        // exists.
        var i = from;

        while (i < end)
        {
            var step = steps[i];

            // A control transfer does no work. It invokes nothing, so it cannot fail and
            // cannot be compensated; it consumes no measurable time, so charging it a
            // deadline check would buy nothing but a clock read. The step it lands on
            // does both.
            if (step.Kind == StepKind.Jump)
            {
                // `.Value`, not `.GetValueOrDefault()`. The factories make a control
                // transfer without a target unrepresentable, so this cannot be null — but
                // if it ever were, defaulting to zero would silently restart the flow and
                // loop forever, and a throw is the diagnosable failure.
                i = step.Target!.Value;
                continue;
            }

            if (step.Kind == StepKind.Branch)
            {
                bool taken;

                try
                {
                    taken = dispatcher.Evaluate(i, scope);
                }
#pragma warning disable CA1031 // Same reasoning as the capability call below, plus one
                catch (Exception exception) //   more: a predicate escaping here would skip
                {                           //   the compensation the already-completed
                    failure = FlowErrors    //   steps need, leaving exactly the dangling
                        .PredicateFailed(plan.Flow.Id, i, exception); // state a saga prevents.
                    break;
                }
#pragma warning restore CA1031

                // The `then` block is laid out immediately after the branch, so the true
                // path is the ordinary next index and only the false path needs a target.
                i = taken ? i + 1 : step.Target!.Value;
                continue;
            }

            if (step.Kind == StepKind.Switch)
            {
                int arm;

                try
                {
                    arm = dispatcher.Select(i, scope);
                }
#pragma warning disable CA1031 // Same reasoning as the predicate above: a selector that
                catch (Exception exception) //   escapes here would skip the compensation
                {                           //   the already-completed steps need.
                    failure = FlowErrors.SelectorFailed(plan.Flow.Id, i, exception);
                    break;
                }
#pragma warning restore CA1031

                // Unsigned, so "no case matched" (-1) and a dispatcher that answered out
                // of range take the same path — the default target — rather than throwing
                // an IndexOutOfRangeException from the middle of a flow. One comparison,
                // one array read, nothing allocated.
                var cases = step.CaseTargets;

                i = (uint)arm < (uint)cases.Length ? cases[arm] : step.Target!.Value;
                continue;
            }

            if (step.Kind == StepKind.Parallel)
            {
                var forked = await RunParallelAsync(
                    plan, dispatcher, context, scope, compensations, step, ct).ConfigureAwait(false);

                // Counted whether or not the merge held: those steps really ran, and a
                // caller reading CompletedSteps to decide what was touched needs the
                // work a cancelled branch had already done to be in the number.
                completed += forked.Completed;

                if (forked.Failure is not null)
                {
                    failure = forked.Failure;
                    break;
                }

                i = step.Target!.Value;
                continue;
            }

            if (step.Kind == StepKind.ForEach)
            {
                var iterated = await RunForEachAsync(
                    plan, dispatcher, context, scope, compensations, step, ct).ConfigureAwait(false);

                // Counted for the same reason a fork's are: those steps really ran, and a
                // loop that stopped at the third of ten elements has still done the work
                // of the first two.
                completed += iterated.Completed;

                if (iterated.Failure is not null)
                {
                    failure = iterated.Failure;
                    break;
                }

                i = step.Target!.Value;
                continue;
            }

            // The identity is taken from the return value rather than read back off the
            // context. Inside a fork a sibling overwrites the field between the throw and
            // the catch, and an error naming the wrong capability is worse than none.
            var capabilityId = context.EnterStep(step);

            if (context.UtcNow >= context.Deadline)
            {
                failure = FlowErrors.DeadlineExceeded(plan.Flow.Id, context.Deadline);
                break;
            }

            StepOutcome outcome;

            try
            {
                outcome = await dispatcher.ExecuteAsync(i, scope, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Caller-initiated, or a sibling branch's failure cancelling this one, and
                // not this step's fault — but the work already done still has to be
                // undone, so this joins the failure path rather than propagating.
                failure = FlowErrors.Cancelled(plan.Flow.Id);
                break;
            }
#pragma warning disable CA1031 // A capability that throws is a defect; the engine converts
            catch (Exception exception)  //   it into an error rather than letting it kill the
            {                            //   trigger's consumer loop. This is the one place a
                failure = FlowErrors     //   general catch is correct, and it re-reports rather
                    .Unhandled(capabilityId, exception); // than swallowing.
                break;
            }
#pragma warning restore CA1031

            if (!outcome.IsSuccess)
            {
                failure = outcome.Error;
                break;
            }

            completed++;

            if (compensations is not null)
            {
                // Through the context, not the stack directly: two branches can complete a
                // compensable step at the same instant, and the context is what serialises
                // the push. A lost push is an undo that never runs.
                //
                // The scope goes on the stack with the step, and is null everywhere except
                // inside an iteration — that is what lets the undo of "the line this step
                // reserved" find the right line once the loop is over.
                context.RecordCompleted(step, ReferenceEquals(scope, context) ? null : scope);
            }

            i++;
        }

        return new RangeOutcome(failure, completed);
    }

    /// <summary>
    /// Runs every branch of a fork and applies its merge strategy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This method allocates, and that is the deliberate exception to budget B2.</strong>
    /// A linked <see cref="CancellationTokenSource"/>, a <see cref="Task"/> per branch, and
    /// the awaiters behind them are what concurrency costs; there is no version of running
    /// three things at once that costs nothing. The budget stays a hard zero for the
    /// linear, conditional and switch paths, which never reach this method, and
    /// <c>EngineAllocationTests</c> records the parallel figure as a ceiling rather than
    /// pretending it is zero.
    /// </para>
    /// <para>
    /// <strong>How concurrent the branches actually are.</strong> Each branch is started
    /// eagerly on the calling thread and runs until its first incomplete await, then yields;
    /// the rest interleave on the thread pool. So branches whose steps all complete
    /// synchronously run one after another — they were never going to overlap, and forcing
    /// them onto <see cref="Task.Run(Func{Task})"/> would buy a thread-pool dispatch per
    /// branch to make CPU-bound work contend. Branches that do I/O, which is what a fork is
    /// for, genuinely overlap.
    /// </para>
    /// <para>
    /// <strong>Every branch is drained before this returns, cancelled or not.</strong> That
    /// is not tidiness: the context is pooled, so a branch still writing to it after the
    /// engine has moved on would eventually write into the <em>next</em> flow's context,
    /// which may belong to another tenant. It is also what makes compensation sound —
    /// nothing is still running when the unwind starts.
    /// </para>
    /// </remarks>
    private static async ValueTask<RangeOutcome> RunParallelAsync(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        FlowExecutionContext context,
        FlowContext scope,
        CompensationStack? compensations,
        StepNode step,
        CancellationToken ct)
    {
        var targets = step.BranchTargets;
        var join = step.Target!.Value;
        var merge = step.Merge;

        // AllSettled cancels nothing, so it needs no source at all. The other three do,
        // and the source is linked so the caller's own cancellation still reaches the
        // branches.
        using var cancellation = merge.Kind == MergeKind.AllSettled
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(ct);

        var branchToken = cancellation?.Token ?? ct;

        // Two collections on purpose. `started` keeps declaration order, which is what
        // AllSettled publishes and what a reader matches against the manifest's `branches`
        // array; `pending` is drained as branches finish, and completion order is not
        // declaration order. Collapsing them would mean either losing the branch's identity
        // or reordering the array a reader is meant to index into.
        var started = new Task<RangeOutcome>[targets.Length];
        var pending = new List<Task<RangeOutcome>>(targets.Length);

        for (var b = 0; b < targets.Length; b++)
        {
            // Branch b owns [targets[b], targets[b + 1]) — or up to the join, for the
            // last. StepNode.ForParallel has already proved the spans are ascending and
            // non-empty, so this arithmetic cannot produce an overlap.
            var branchEnd = b + 1 < targets.Length ? targets[b + 1] : join;

            started[b] = RunRangeAsync(
                plan, dispatcher, context, scope, compensations, targets[b], branchEnd, branchToken).AsTask();

            pending.Add(started[b]);
        }

        var errors = new Error?[targets.Length];
        var completed = 0;
        var succeeded = 0;
        Error? firstFailure = null;

        while (pending.Count > 0)
        {
            var finished = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(finished);

            var outcome = Observe(finished, plan.Flow.Id);

            completed += outcome.Completed;
            errors[Array.IndexOf(started, finished)] = outcome.Failure;

            if (outcome.Failure is null)
            {
                succeeded++;
            }
            else
            {
                firstFailure ??= outcome.Failure;
            }

            // Cancelling does not end the loop: the remaining branches still have to be
            // drained, for the reason given in the remarks. It only stops them doing more
            // work than the merge needs.
            if (ShouldCancelSiblings(merge, outcome.Failure is null, succeeded))
            {
                cancellation?.Cancel();
            }
        }

        return new RangeOutcome(Verdict(plan, step, merge, errors, succeeded, firstFailure, context), completed);
    }

    /// <summary>
    /// Runs the body of a <see cref="StepKind.ForEach"/> once per element.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The body is one range, run <em>n</em> times.</strong> Everything else in the
    /// DSL executes each index at most once; this does not, and that is the single fact
    /// worth being clear about. It costs a pass over the same span per element — so a
    /// ten-line order runs the body's step loop ten times, with ten sets of dispatcher
    /// calls — and it buys the flat array staying flat: no unrolled copy of the body per
    /// element, no separate sub-plan, nothing in the graph that depends on the size of the
    /// data. The manifest of a flow that reserves one line and one that reserves a thousand
    /// is the same document.
    /// </para>
    /// <para>
    /// <strong>The element count is read once, before the first pass.</strong> That is what
    /// bounds the loop, and it is why the DSL types the selector as
    /// <c>IReadOnlyList&lt;TItem&gt;</c>: the forward-target rule proves each pass
    /// terminates, and a count fixed in advance proves the loop over passes does.
    /// </para>
    /// <para>
    /// <strong>Two paths, and the second is <c>Parallel</c>'s.</strong> A bound of one runs
    /// the elements in order on the calling thread, allocating nothing beyond one scope per
    /// element and never touching a task. Above one, this is a fork with a sliding window:
    /// the same linked <see cref="CancellationTokenSource"/>, the same
    /// <see cref="Task.WhenAny(IEnumerable{Task})"/> drain, the same rule that every started
    /// iteration is awaited before the method returns — because the context is pooled, and
    /// an iteration still writing to it after the engine has moved on would eventually write
    /// into the next flow's context.
    /// </para>
    /// </remarks>
    private static async ValueTask<RangeOutcome> RunForEachAsync(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        FlowExecutionContext context,
        FlowContext scope,
        CompensationStack? compensations,
        StepNode step,
        CancellationToken ct)
    {
        IterationSource source;

        try
        {
            source = dispatcher.BeginIteration(step.Index, scope);
        }
#pragma warning disable CA1031 // Same reasoning as the predicate and the switch selector:
        catch (Exception exception) //   a selector escaping here would skip the compensation
        {                           //   the already-completed steps need.
            return new RangeOutcome(FlowErrors.IterationFailed(plan.Flow.Id, step.Index, exception), 0);
        }
#pragma warning restore CA1031

        // An empty collection is not a failure and not a special case worth a diagnostic:
        // a loop over nothing does nothing, exactly as a `When` nobody took does.
        if (source.Count == 0)
        {
            return default;
        }

        var body = step.Index + 1;
        var join = step.Target!.Value;

        return step.MaxDegreeOfParallelism == 1
            ? await RunIterationsInOrderAsync(
                plan, dispatcher, context, scope, compensations, step, source, body, join, ct)
                .ConfigureAwait(false)
            : await RunIterationsConcurrentlyAsync(
                plan, dispatcher, context, scope, compensations, step, source, body, join, ct)
                .ConfigureAwait(false);
    }

    /// <summary>Runs the elements one at a time, in order.</summary>
    /// <remarks>
    /// No task, no token source, no drain list — a sequential loop is a sequential loop,
    /// and wrapping it in the concurrent path's machinery to save a method would charge
    /// every ordinary <c>ForEach</c> for concurrency it explicitly did not ask for.
    /// </remarks>
    private static async ValueTask<RangeOutcome> RunIterationsInOrderAsync(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        FlowExecutionContext context,
        FlowContext scope,
        CompensationStack? compensations,
        StepNode step,
        IterationSource source,
        int body,
        int join,
        CancellationToken ct)
    {
        var errors = step.ContinueOnError ? new Error?[source.Count] : null;
        var completed = 0;
        Error? firstFailure = null;
        Error? fatal = null;

        for (var element = 0; element < source.Count; element++)
        {
            FlowContext iteration;

            try
            {
                iteration = dispatcher.EnterIteration(step.Index, in source, element, scope);
            }
#pragma warning disable CA1031 // Reading one element out of a list the selector already
            catch (Exception exception) //   produced should not throw; if it does, the
            {                           //   already-completed elements still need unwinding.
                fatal = FlowErrors.IterationFailed(plan.Flow.Id, step.Index, exception);
                break;
            }
#pragma warning restore CA1031

            var outcome = await RunRangeAsync(
                plan, dispatcher, context, iteration, compensations, body, join, ct).ConfigureAwait(false);

            completed += outcome.Completed;

            if (outcome.Failure is null)
            {
                continue;
            }

            firstFailure ??= outcome.Failure;

            if (errors is null)
            {
                // ContinueOnError = false, which is the documented default: the first
                // failing element stops the iteration. The elements that already ran are
                // not undone here — they are on the compensation stack, and the flow's
                // own unwind takes them in strict reverse along with everything before
                // the loop. Compensating them here would undo half a saga twice.
                break;
            }

            errors[element] = outcome.Failure;
        }

        return new RangeOutcome(IterationVerdict(step, errors, firstFailure, fatal, context), completed);
    }

    /// <summary>Runs the elements with a sliding window of at most <c>MaxDegreeOfParallelism</c>.</summary>
    /// <remarks>
    /// A window rather than a task per element: a thousand-line order must not start a
    /// thousand reservations, which is the whole reason the bound is required rather than
    /// optional. Every started iteration is drained before this returns, cancelled or not,
    /// for the reason given on <see cref="RunParallelAsync"/> — the context is pooled.
    /// </remarks>
    private static async ValueTask<RangeOutcome> RunIterationsConcurrentlyAsync(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        FlowExecutionContext context,
        FlowContext scope,
        CompensationStack? compensations,
        StepNode step,
        IterationSource source,
        int body,
        int join,
        CancellationToken ct)
    {
        // ContinueOnError cancels nothing, so it needs no source at all — the same shape
        // AllSettled uses. The other case links to the caller's token so cancellation
        // still reaches the elements in flight.
        using var cancellation = step.ContinueOnError
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(ct);

        var iterationToken = cancellation?.Token ?? ct;
        var window = Math.Min(step.MaxDegreeOfParallelism, source.Count);

        var pending = new List<Task<RangeOutcome>>(window);
        var elements = new List<int>(window);

        var errors = step.ContinueOnError ? new Error?[source.Count] : null;
        var next = 0;
        var completed = 0;
        var stopped = false;
        Error? firstFailure = null;
        Error? fatal = null;

        while (next < source.Count || pending.Count > 0)
        {
            while (!stopped && next < source.Count && pending.Count < window)
            {
                FlowContext iteration;

                try
                {
                    iteration = dispatcher.EnterIteration(step.Index, in source, next, scope);
                }
#pragma warning disable CA1031 // As in the sequential path, and with the same consequence:
                catch (Exception exception) //   whatever is already in flight still has to
                {                           //   be drained before this returns.
                    fatal = FlowErrors.IterationFailed(plan.Flow.Id, step.Index, exception);
                    stopped = true;
                    cancellation?.Cancel();
                    break;
                }
#pragma warning restore CA1031

                pending.Add(RunRangeAsync(
                    plan, dispatcher, context, iteration, compensations, body, join, iterationToken).AsTask());

                elements.Add(next);
                next++;
            }

            if (pending.Count == 0)
            {
                break;
            }

            var finished = await Task.WhenAny(pending).ConfigureAwait(false);
            var slot = pending.IndexOf(finished);
            var element = elements[slot];

            pending.RemoveAt(slot);
            elements.RemoveAt(slot);

            var outcome = Observe(finished, plan.Flow.Id);

            completed += outcome.Completed;

            if (outcome.Failure is null)
            {
                continue;
            }

            firstFailure ??= outcome.Failure;

            if (errors is null)
            {
                // Stopping does not end the loop: the iterations already in flight still
                // have to be drained. It only stops the window from starting more.
                stopped = true;
                cancellation?.Cancel();
                continue;
            }

            errors[element] = outcome.Failure;
        }

        return new RangeOutcome(IterationVerdict(step, errors, firstFailure, fatal, context), completed);
    }

    /// <summary>Turns the element outcomes into the loop's own outcome.</summary>
    /// <remarks>
    /// <para>
    /// With <c>ContinueOnError = false</c> the loop reports the failing element's own
    /// error, because there is one failure and it is the reason — the same choice
    /// <c>AllMustSucceed</c> makes. With <c>ContinueOnError = true</c> it reports success
    /// and publishes a <see cref="ForEachOutcome"/>, because the author has said that a
    /// failed element is a result rather than an end, and the step after the loop is the
    /// only thing entitled to decide what a partial success means.
    /// </para>
    /// <para>
    /// <paramref name="fatal"/> outranks both. <c>ContinueOnError</c> is a statement about
    /// elements failing, not about the loop itself being unable to produce one; a flow that
    /// swallowed a selector that threw would carry on over a collection it never read.
    /// </para>
    /// </remarks>
    private static Error? IterationVerdict(
        StepNode step,
        Error?[]? errors,
        Error? firstFailure,
        Error? fatal,
        FlowExecutionContext context)
    {
        if (errors is null)
        {
            return fatal ?? firstFailure;
        }

        context.Set(new ForEachOutcome(step.Index, errors));

        return fatal;
    }

    /// <summary>Reads a finished branch, converting a fault into an error rather than rethrowing.</summary>
    /// <remarks>
    /// <see cref="RunRangeAsync"/> is written not to throw — it converts every failure into
    /// an <see cref="Error"/>. This exists for the case where that is one day untrue: a
    /// branch that faulted must not take the whole fork down through an unobserved
    /// exception, because the siblings still need draining and the flow still needs
    /// compensating.
    /// </remarks>
    private static RangeOutcome Observe(Task<RangeOutcome> finished, string flowId)
    {
        if (finished.IsCompletedSuccessfully)
        {
            return finished.Result;
        }

        if (finished.IsCanceled)
        {
            return new RangeOutcome(FlowErrors.Cancelled(flowId), 0);
        }

        // Unwrapped when there is exactly one, which is every case a branch can produce:
        // an AggregateException wrapper in the message would name the plumbing rather than
        // the defect.
        Exception fault = finished.Exception switch
        {
            { InnerExceptions.Count: 1 } aggregate => aggregate.InnerExceptions[0],
            { } aggregate => aggregate,
            _ => new InvalidOperationException(
                "A parallel branch reported neither success, cancellation nor an exception."),
        };

        return new RangeOutcome(FlowErrors.Unhandled(flowId, fault), 0);
    }

    /// <summary>Whether the branch that just finished means the rest can stop.</summary>
    private static bool ShouldCancelSiblings(MergeStrategy merge, bool branchSucceeded, int succeeded) =>
        merge.Kind switch
        {
            // The documented rule: "first failure cancels siblings via linked token".
            MergeKind.AllMustSucceed => !branchSucceeded,

            // "the flow continues; branch errors available in context" — nothing is cancelled.
            MergeKind.AllSettled => false,

            // FirstSuccess is Quorum(1); both stop as soon as they have enough.
            _ => branchSucceeded && succeeded >= merge.RequiredSuccesses,
        };

    /// <summary>Turns the branch outcomes into the fork's own outcome.</summary>
    /// <remarks>
    /// <c>AllMustSucceed</c> reports the branch's own error, because there is one failure
    /// and it is the reason. <c>FirstSuccess</c> and <c>Quorum</c> report a count, because
    /// the reason is that not enough branches worked and picking one of several errors to
    /// stand for that would be arbitrary — the errors are still on the branch outcomes, and
    /// the first is attached to the flow's own error data.
    /// </remarks>
    private static Error? Verdict(
        ExecutionPlan plan,
        StepNode step,
        MergeStrategy merge,
        Error?[] errors,
        int succeeded,
        Error? firstFailure,
        FlowExecutionContext context)
    {
        switch (merge.Kind)
        {
            case MergeKind.AllSettled:
                // The one strategy that writes something for the next step to read. It is
                // also the only one that can reach the next step having failed at all.
                context.Set(new ParallelOutcome(step.Index, errors));
                return null;

            case MergeKind.AllMustSucceed:
                return firstFailure;

            default:
                return succeeded >= merge.RequiredSuccesses
                    ? null
                    : FlowErrors.MergeNotSatisfied(
                        plan.Flow.Id, step.Index, merge.RequiredSuccesses, succeeded, errors.Length);
        }
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

        foreach (var entry in compensations.Unwind())
        {
            _ = context.EnterStep(entry.Step);

            try
            {
                // Under the scope the step completed in, which is the flow's own context
                // everywhere except inside a `ForEach` — there it is the iteration's, so
                // `ReleaseLine` undoes the line `ReserveLine` reserved rather than
                // whichever line the loop happened to end on.
                var outcome = await dispatcher
                    .CompensateAsync(entry.Index, entry.Scope ?? context, CancellationToken.None)
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
