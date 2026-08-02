using FlowX;

namespace RealtimeStream;

/// <summary>The policy sets this application declares, named once and applied by name.</summary>
public static class Policies
{
    /// <summary>
    /// The store's resilience stance: a timeout per attempt, three attempts, and a breaker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The retry is legal only because <see cref="PersistAggregate"/> declares
    /// <c>Idempotent = true</c></strong> — <c>FLOWX1014</c> is a build error otherwise — and that
    /// declaration is true for a reason that matters more here than on a request-driven flow: the
    /// write is keyed on the window's interval, so a second attempt replaces the first rather
    /// than adding a row.
    /// </para>
    /// <para>
    /// <strong>The timeout is five seconds and the flow's deadline is sixty.</strong> A window
    /// whose store is wedged fails after three attempts rather than holding a lease for the
    /// deadline, and a subscription whose windows fail is visible; a subscription whose windows
    /// hang looks exactly like a quiet stream. The engine clamps each attempt's timeout to what
    /// is left of the flow deadline, so the two numbers cannot disagree.
    /// </para>
    /// </remarks>
    public static PolicySet BulkWrite { get; } = PolicySet
        .Named("aggregate-bulk-write")
        .Timeout(TimeSpan.FromSeconds(5))
        .Retry(attempts: 3)
        .CircuitBreaker(failureRatio: 0.5, breakDuration: TimeSpan.FromSeconds(10));
}
