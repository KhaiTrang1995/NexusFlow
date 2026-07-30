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
            // Accepting work during a drain is why drains never finish. Unavailable is
            // what tells a caller — or a load balancer — that another node can take this.
            return FlowExecutionResult.Rejected(new Error(
                "host.draining",
                "This node is shutting down and is not accepting new flows.",
                ErrorCategory.Unavailable));
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

    /// <summary>
    /// Stops accepting work and waits for in-flight flows, up to
    /// <see cref="FlowXOptions.ShutdownDrainTimeout"/>.
    /// </summary>
    /// <returns>
    /// <c>true</c> when everything finished in time; <c>false</c> when the budget ran
    /// out with work still running.
    /// </returns>
    /// <remarks>
    /// Bounded on purpose. A drain that waits forever turns a rolling deploy into an
    /// outage: the orchestrator sends SIGKILL on its own schedule regardless, so the
    /// choice is between giving up loudly and being killed silently.
    /// </remarks>
    public async ValueTask<bool> DrainAsync(CancellationToken ct = default)
    {
        Task idle;

        lock (_sync)
        {
            _draining = true;

            if (_inFlight == 0)
            {
                return true;
            }

            _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            idle = _idle.Task;
        }

        try
        {
            await idle.WaitAsync(_options.ShutdownDrainTimeout, ct).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
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
