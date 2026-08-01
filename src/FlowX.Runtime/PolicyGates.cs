namespace FlowX.Runtime;

/// <summary>
/// One capability's circuit breaker: what it has seen lately, and whether it is open.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Per capability, per process, and not shared.</strong>
/// <c>docs/10-Policy-Framework.md §6</c> describes a composite <c>BreakerKey</c> —
/// <c>Capability | Downstream | Tenant | Partition</c> — and <c>Capability</c> is the one
/// component it marks "always included". The other three are not expressible:
/// <c>CircuitBreakerAttribute</c> does not exist and <see cref="PolicySet.CircuitBreaker"/>
/// has no key parameter, so there is nothing for a wider key to read. Narrowing later is
/// additive; guessing a tenant component now would put a value nobody declared into a safety
/// decision.
/// </para>
/// <para>
/// <strong>Process-local, deliberately.</strong> A breaker shared across a deployment would be
/// a store, which is a plugin contract and a different decision
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0009-plugin-contracts.md">ADR-0009</a>).
/// Process-local is also the conservative half: every node discovers the outage for itself,
/// so the worst case is <em>n</em> nodes each spending
/// <see cref="StepPolicy.DefaultMinimumThroughput"/> calls learning what one node already
/// knows — slower to protect, never wrong.
/// </para>
/// <para>
/// <strong>The clock is the caller's.</strong> Every instant arrives as a parameter rather
/// than being read here, so the whole of a breaker's behaviour — a window rolling, a break
/// expiring, a probe succeeding — is provable against <c>IClock</c> without a suite that
/// sleeps. It is the reason <see cref="StepPolicy.DelayBefore"/> takes its jitter draw as an
/// argument.
/// </para>
/// </remarks>
internal sealed class CircuitBreakerState
{
    private readonly Lock _sync = new();

    private int _successes;
    private int _failures;
    private DateTimeOffset _windowStart;
    private DateTimeOffset _openUntil;
    private bool _probing;

    /// <summary>
    /// Whether a call may be made, and when the breaker will next allow one if not.
    /// </summary>
    /// <param name="policy">The declared breaker, for its break duration.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="until">When the breaker next opens the gate. Meaningful only on refusal.</param>
    /// <remarks>
    /// A breaker whose break duration has elapsed does not simply close: it admits exactly one
    /// call and watches it. Closing outright would send the whole backlog at a dependency that
    /// has had no chance to say whether it recovered, which is the stampede a breaker exists
    /// to prevent, arriving one break duration late.
    /// </remarks>
    public bool TryEnter(StepPolicy policy, DateTimeOffset now, out DateTimeOffset until)
    {
        lock (_sync)
        {
            if (_openUntil == default)
            {
                until = default;
                return true;
            }

            if (now < _openUntil)
            {
                until = _openUntil;
                return false;
            }

            // Half open. The counters are cleared with the gate so that the probe is judged on
            // its own evidence rather than against the outage that opened it.
            _openUntil = default;
            _probing = true;
            _successes = 0;
            _failures = 0;
            _windowStart = now;

            until = default;
            return true;
        }
    }

    /// <summary>Records what one call did, and opens the breaker when the ratio says to.</summary>
    /// <param name="policy">The declared breaker: its ratio, window and break duration.</param>
    /// <param name="now">When the call finished.</param>
    /// <param name="success">Whether it succeeded.</param>
    /// <remarks>
    /// <para>
    /// <strong>Only calls that were actually made reach here.</strong> A step refused by this
    /// breaker, or by a bulkhead, or by a timeout that had already expired before the dispatch,
    /// tells us nothing about the dependency — counting a refusal as a failure would make an
    /// open breaker self-sustaining, which is a breaker that never closes.
    /// </para>
    /// <para>
    /// <strong>A failed probe reopens immediately</strong>, without waiting for the minimum
    /// throughput. The half-open state exists precisely to test one call, and requiring ten
    /// more before believing the answer would defeat it.
    /// </para>
    /// </remarks>
    public void Record(StepPolicy policy, DateTimeOffset now, bool success)
    {
        lock (_sync)
        {
            if (_probing)
            {
                _probing = false;

                if (!success)
                {
                    Open(policy, now);
                    return;
                }
            }

            // A sampling window that never rolls is a lifetime average, which cannot recover:
            // a dependency that failed a thousand times last week would keep a breaker open
            // through any amount of present health.
            if (now - _windowStart >= policy.SamplingWindow)
            {
                _successes = 0;
                _failures = 0;
                _windowStart = now;
            }

            if (success)
            {
                _successes++;
            }
            else
            {
                _failures++;
            }

            var total = _successes + _failures;

            if (total >= StepPolicy.DefaultMinimumThroughput &&
                (double)_failures / total >= policy.FailureRatio)
            {
                Open(policy, now);
            }
        }
    }

    private void Open(StepPolicy policy, DateTimeOffset now)
    {
        _openUntil = now + policy.BreakDuration;
        _probing = false;
        _successes = 0;
        _failures = 0;
        _windowStart = now;
    }
}

/// <summary>
/// One capability's bulkhead: how many callers are inside it, and how many may wait.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Per capability, for <see cref="CircuitBreakerState"/>'s reason.</strong> The thing
/// being isolated is the dependency, so two steps that call the same capability share one
/// pool — which is the point: a flow that reserves inventory twice must not be able to consume
/// twice the concurrency the author granted the inventory service. Two steps declaring
/// <em>different</em> bounds for one capability therefore share whichever reached the engine
/// first; the DSL has no way to say "this step's share of the pool", and inventing one would
/// be a bulkhead per declaration rather than per dependency.
/// </para>
/// <para>
/// <strong>The queue depth is a refusal, not a wait.</strong> A bulkhead that queues without
/// bound converts a concurrency problem into a latency one and hides it — every caller
/// succeeds, eventually, past the deadline. Refusing at the boundary is what makes the
/// isolation visible to the flow that hit it.
/// </para>
/// </remarks>
internal sealed class BulkheadGate : IDisposable
{
    private readonly SemaphoreSlim _permits;
    private readonly int _queueDepth;

    private int _waiting;

    public BulkheadGate(int maxConcurrency, int queueDepth)
    {
        _permits = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _queueDepth = queueDepth;
    }

    /// <summary>Releases the semaphore behind the pool.</summary>
    /// <remarks>
    /// Here because the type owns a disposable field, not because anything calls it: a gate
    /// lives as long as the engine that keyed it, and an engine outlives every flow it runs.
    /// A gate that were disposed while a caller held a permit would be a bulkhead that
    /// admitted everybody.
    /// </remarks>
    public void Dispose() => _permits.Dispose();

    /// <summary>Takes a permit, waits for one, or refuses.</summary>
    /// <param name="ct">Cancels a wait for a permit.</param>
    /// <returns><c>true</c> when a permit was taken and must be given back.</returns>
    /// <remarks>
    /// The zero-timeout <c>Wait</c> first, so the uncontended case — which is nearly all of
    /// them — costs one interlocked operation and never goes asynchronous. A bulkhead that
    /// awaited on every call would put a state machine on a step that was never going to
    /// queue.
    /// </remarks>
    public async ValueTask<bool> EnterAsync(CancellationToken ct)
    {
        if (await _permits.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
        {
            return true;
        }

        if (Interlocked.Increment(ref _waiting) > _queueDepth)
        {
            Interlocked.Decrement(ref _waiting);
            return false;
        }

        try
        {
            await _permits.WaitAsync(ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }
    }

    /// <summary>Gives a permit back.</summary>
    public void Exit() => _permits.Release();
}
