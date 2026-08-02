using System.Collections.Immutable;

namespace FlowX;

/// <summary>
/// A compensation's retry, resolved out of its declared <see cref="PolicyChain"/> into the
/// three numbers the unwind actually needs.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This was the only policy FlowX executed, and it shipped alone on purpose.</strong>
/// The forward path now applies <see cref="PolicyStage.Resilience"/> — see
/// <see cref="StepPolicy"/> — and four declarable kinds are still applied by nothing. What
/// shipped here first was the slice compensation cannot do without: a saga whose undo cannot
/// survive a broker being briefly unreachable is a saga that reports an unrecoverable
/// business inconsistency for a blip, which is the ordinary case rather than the rare one.
/// </para>
/// <para>
/// <strong>Resolved once, when the plan is built.</strong> It hangs off
/// <see cref="StepNode.CompensationRetry"/>, so the unwind reads three fields off a node it
/// already has rather than walking an <see cref="ImmutableArray{T}"/> of descriptors while an
/// incident is in progress — the same reason
/// <see cref="ExecutionPlan.CompensableStepIndices"/> is precomputed.
/// </para>
/// <para>
/// <strong>Where ADR-0011 comes in.</strong> The stage is
/// <see cref="PolicyStage.Consistency"/>, which is where the fixed order puts compensation,
/// and the ordering itself is <see cref="PolicyChain"/>'s and nothing else's. Executing the
/// <em>last</em> stage cannot skip an earlier one, which is what made a single-policy slice
/// safe to ship before any other stage existed. A stage in the middle of the order needs a
/// different argument, and
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md">ADR-0025</a>
/// is the one stage 4 was shipped on.
/// </para>
/// </remarks>
public sealed class CompensationPolicy
{
    private CompensationPolicy(int attempts, Backoff backoff, ImmutableArray<ErrorCategory> retryOn)
    {
        Attempts = attempts;
        Backoff = backoff;
        RetryOn = retryOn;
    }

    /// <summary>
    /// One attempt and no retry: what a compensation with no declared policy gets.
    /// </summary>
    /// <remarks>
    /// The default is "exactly what happened before this package", on purpose. A compensation
    /// is a real reversal of a real effect, and repeating one nobody asked to have repeated is
    /// a second refund.
    /// </remarks>
    public static CompensationPolicy None { get; } =
        new(1, Backoff.ExponentialJitter(), ImmutableArray<ErrorCategory>.Empty);

    /// <summary>How many times the undo may be dispatched, including the first. Never below one.</summary>
    public int Attempts { get; }

    /// <summary>The wait between attempts.</summary>
    public Backoff Backoff { get; }

    /// <summary>Which error categories are worth another attempt.</summary>
    public ImmutableArray<ErrorCategory> RetryOn { get; }

    /// <summary>True when this policy can ask for the undo a second time.</summary>
    public bool IsRetrying => Attempts > 1;

    /// <summary>
    /// Reads the <c>CompensationRetry</c> declared in a chain, or <see cref="None"/> when the
    /// chain declares none.
    /// </summary>
    /// <param name="policies">The compensation's own chain, already ordered by stage.</param>
    /// <remarks>
    /// Tolerant of a chain that carries other kinds: a set may legitimately declare an audit
    /// alongside the retry, and the stages that do not execute yet are metadata this reads
    /// past rather than rejects.
    /// </remarks>
    public static CompensationPolicy From(PolicyChain policies)
    {
        ArgumentNullException.ThrowIfNull(policies);

        foreach (var policy in policies.Ordered)
        {
            if (policy.Kind != CompensationRetryKind)
            {
                continue;
            }

            return new CompensationPolicy(
                Math.Max(1, Parameter(policy, "attempts", 1)),
                Parameter(policy, "backoff", Backoff.ExponentialJitter()),
                [.. Parameter(policy, "retryOn", Array.Empty<ErrorCategory>())]);
        }

        return None;
    }

    /// <summary>The descriptor kind <see cref="PolicySet.CompensationRetry"/> emits.</summary>
    /// <remarks>
    /// A constant rather than a literal repeated in three files, for the reason
    /// <c>FlowErrors.DeadlineExceededCode</c> is one: a safety decision keyed off a string
    /// spelled out in several places is a decision that eventually disagrees with itself.
    /// </remarks>
    public const string CompensationRetryKind = "CompensationRetry";

    /// <summary>
    /// Whether the undo is worth dispatching again after <paramref name="attemptsMade"/>
    /// attempts have failed with <paramref name="failure"/>.
    /// </summary>
    /// <param name="failure">What the last attempt reported.</param>
    /// <param name="attemptsMade">How many attempts have already been made, counting from one.</param>
    /// <remarks>
    /// Both halves of <c>docs/10-Policy-Framework.md §5</c>'s decision tree, and no more. The
    /// deadline is the caller's to check, because it is the caller that knows what the planned
    /// backoff would cost.
    /// </remarks>
    public bool AllowsAnotherAttempt(Error failure, int attemptsMade)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return attemptsMade < Attempts && RetryOn.Contains(failure.Category);
    }

    /// <summary>
    /// How long to wait before attempt <paramref name="attempt"/>, given a uniform
    /// <paramref name="sample"/> in <c>[0, 1]</c>.
    /// </summary>
    /// <param name="attempt">The attempt about to be armed, counting from one.</param>
    /// <param name="sample">The jitter draw. Ignored when the backoff declares no jitter.</param>
    /// <remarks>
    /// <para>
    /// <strong>Full jitter, exactly as specified:</strong>
    /// <c>delay = random(0, base × 2^attempt)</c>, capped at
    /// <see cref="FlowX.Backoff.MaxDelay"/>. Decorrelating the draws is what stops a shared
    /// outage producing the synchronised thundering herd that fixed backoff produces — and a
    /// compensation storm hits a dependency that is already unwell.
    /// </para>
    /// <para>
    /// <strong>The sample is a parameter rather than a field.</strong> The function is then
    /// pure and its arithmetic is pinned by a table of cases instead of by a statistical
    /// assertion, and the engine keeps its one source of randomness in one place.
    /// </para>
    /// <para>
    /// <strong>The arithmetic moved onto <see cref="FlowX.Backoff"/> and this delegates to
    /// it.</strong> <c>PollUntil</c> spaces its attempts by the same formula over the same
    /// three fields, and the second copy would have been the place the two schedules came to
    /// disagree about what <c>Exponential(PT5S, PT5M)</c> means. This method stays because it
    /// is where an unwind asks the question and because the name says which attempts it is
    /// about.
    /// </para>
    /// </remarks>
    public TimeSpan DelayBefore(int attempt, double sample) => Backoff.After(attempt, sample);

    private static T Parameter<T>(PolicyDescriptor policy, string key, T fallback) =>
        policy.Parameters.TryGetValue(key, out var value) && value is T typed ? typed : fallback;
}
