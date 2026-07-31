using FlowX.Runtime;

namespace FlowX.Hosting;

/// <summary>
/// The entry point a trigger uses to run a flow, and the thing that knows how many
/// flows are in flight.
/// </summary>
/// <remarks>
/// <para>
/// A thin wrapper over <see cref="FlowEngine"/> that adds exactly one concern the
/// engine deliberately does not have: <strong>lifecycle</strong>. The engine executes
/// a plan; this decides whether the process is still willing to start one.
/// </para>
/// <para>
/// Keeping the counter here rather than in the engine matters. The engine is on the
/// hot path and its allocation budget is a hard zero; a host that wants richer
/// lifecycle behaviour later — quotas, per-tenant admission — extends this class
/// without touching the step loop.
/// </para>
/// </remarks>
public sealed class FlowHost
{
    private readonly FlowEngine _engine;
    private readonly FlowXOptions _options;
    private readonly object _sync = new();

    /// <summary>
    /// Why work is refused during a drain. Accepting work during a drain is why drains
    /// never finish; <see cref="ErrorCategory.Unavailable"/> is what tells a caller — or
    /// a load balancer — that another node can take this.
    /// </summary>
    private static readonly Error Draining = new(
        "host.draining",
        "This node is shutting down and is not accepting new flows.",
        ErrorCategory.Unavailable);

    private TaskCompletionSource? _idle;
    private int _inFlight;
    private volatile bool _draining;
    private volatile bool _ready;

    /// <summary>Creates a host over an engine.</summary>
    public FlowHost(FlowEngine engine, FlowXOptions options)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(options);

        _engine = engine;
        _options = options;
    }

    /// <summary>How many flows are executing right now.</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>True once shutdown has begun. New work is refused from this point.</summary>
    public bool IsDraining => _draining;

    /// <summary>True once the host is ready to serve. Drives the readiness probe.</summary>
    public bool IsReady => _ready && !_draining;

    /// <summary>Marks the host ready. Called by the hosted service after registration completes.</summary>
    public void MarkReady() => _ready = true;

    /// <summary>Runs a flow, unless the host is shutting down.</summary>
    public async ValueTask<FlowExecutionResult> RunAsync(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        FlowInvocation invocation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(dispatcher);

        if (!TryEnter())
        {
            return FlowExecutionResult.Rejected(Draining);
        }

        try
        {
            return await _engine.ExecuteAsync(plan, dispatcher, invocation, ct).ConfigureAwait(false);
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>Runs a flow with an input, unless the host is shutting down.</summary>
    public async ValueTask<FlowExecutionResult> RunAsync<TIn>(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        FlowInvocation invocation,
        TIn input,
        CancellationToken ct = default)
        where TIn : notnull
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(dispatcher);

        if (!TryEnter())
        {
            return FlowExecutionResult.Rejected(Draining);
        }

        try
        {
            return await _engine.ExecuteAsync(plan, dispatcher, invocation, input, ct).ConfigureAwait(false);
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>Runs a flow and projects its declared output, unless the host is shutting down.</summary>
    /// <param name="plan">The compiled flow.</param>
    /// <param name="dispatcher">Invokes the capability behind each step index.</param>
    /// <param name="invocation">Correlation, tenant and the caller's remaining budget.</param>
    /// <param name="input">The flow's input.</param>
    /// <param name="projection">The generated <c>.Return(...)</c> clause.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    public async ValueTask<FlowExecutionResult<TOut>> RunAsync<TIn, TOut>(
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

        if (!TryEnter())
        {
            return FlowExecutionResult.Rejected<TOut>(Draining);
        }

        try
        {
            return await _engine
                .ExecuteAsync(plan, dispatcher, invocation, input, projection, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>
    /// Stops accepting work and waits for in-flight flows, up to
    /// <see cref="FlowXOptions.ShutdownDrainTimeout"/>.
    /// </summary>
    /// <returns>
    /// <c>true</c> when everything finished in time; <c>false</c> when the budget ran
    /// out with work still running.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Bounded on purpose. A drain that waits forever turns a rolling deploy into an
    /// outage: the orchestrator sends SIGKILL on its own schedule regardless, so the
    /// choice is between giving up loudly and being killed silently.
    /// </para>
    /// <para>
    /// <strong>Detached sub-flows are drained too, and they are not in this counter.</strong>
    /// A <c>.SubFlow&lt;T&gt;(Detached)</c> child is started from inside a step, so it never
    /// passed through <see cref="TryEnter"/> and the host does not know it exists. Waiting
    /// only on <see cref="InFlight"/> would report "everything finished" while a
    /// fire-and-forget saga was mid-way through its effects — and the kill that follows a
    /// successful drain would leave them uncompensated. The engine counts them; this waits
    /// for both.
    /// </para>
    /// </remarks>
    public async ValueTask<bool> DrainAsync(CancellationToken ct = default)
    {
        Task idle;

        lock (_sync)
        {
            _draining = true;

            if (_inFlight == 0)
            {
                idle = Task.CompletedTask;
            }
            else
            {
                _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                idle = _idle.Task;
            }
        }

        var drained = true;

        if (!idle.IsCompleted)
        {
            try
            {
                await idle.WaitAsync(_options.ShutdownDrainTimeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                drained = false;
            }
            catch (OperationCanceledException)
            {
                drained = false;
            }
        }

        // After the tracked flows, not before: a flow still running may yet start a
        // detached child, so waiting on the children first would leave a window where one
        // appears after the wait returned.
        var detached = await _engine
            .WaitForDetachedAsync(_options.ShutdownDrainTimeout, ct)
            .ConfigureAwait(false);

        return drained && detached;
    }

    private bool TryEnter()
    {
        lock (_sync)
        {
            if (_draining)
            {
                return false;
            }

            _inFlight++;
            return true;
        }
    }

    private void Exit()
    {
        lock (_sync)
        {
            _inFlight--;

            if (_inFlight == 0)
            {
                _idle?.TrySetResult();
            }
        }
    }
}
