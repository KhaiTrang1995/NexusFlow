using FlowX;

namespace Workflow;

/// <summary>
/// The policy sets this application declares, named once and applied by name.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Exactly one of these executes, and knowing which is what this file is for.</strong>
/// <c>docs/10-Policy-Framework.md</c> says exactly one policy is applied at run time —
/// <see cref="PolicySet.CompensationRetry"/>, at stage 7 — and that on the forward path there
/// is no policy execution at all. Both halves are now true end to end: <c>FlowEngine</c> reads
/// <c>StepNode.CompensationRetry</c> and honours attempts, backoff and retryable categories,
/// and <see cref="FacilitiesUndo"/> reaches that property from this file.
/// </para>
/// <para>
/// <strong>It was not always.</strong> Nothing used to put a policy chain on a
/// <c>StepNode</c>: <c>FlowX.Compiler</c>'s <c>FlowEmitter</c> emitted no <c>PolicyChain</c>
/// anywhere, so every generated <c>StepNode.ForCapability(...)</c> call took an id, a
/// descriptor and at most a compensation, and every step of every compiled flow carried
/// <c>PolicyChain.Empty</c>. <c>ManifestWriter</c> published the set regardless, which is why
/// <see cref="FacilitiesUndo"/> appeared under <c>"policies"</c> in
/// <c>flowx.manifest.json</c> and nowhere else. The emitter now splits the declared set by
/// what each policy wraps and passes both halves.
/// </para>
/// <para>
/// <strong><see cref="DirectoryService"/> still executes nothing, and that is not a
/// defect.</strong> The Policy Engine is P4; no timeout is armed, nothing is retried on the
/// forward path and no breaker opens. What changed is that the plan now <em>states</em> what
/// was declared, so a reader of the plan and a reader of the manifest see the same thing. The
/// analyzers were always real: attaching this set's retry to a capability that declared
/// <c>Idempotent = false</c> is <c>FLOWX1014</c> at build time, and that check ran whether or
/// not anything armed the retry.
/// </para>
/// <para>
/// <c>WithPolicyTests</c> pins both — the compensation retry that runs, and the forward set
/// that is carried and not run — so that whoever ships the policy engine turns a test red
/// rather than discovering the sample was quietly wrong. The README states it in the same
/// terms.
/// </para>
/// </remarks>
public static class Policies
{
    /// <summary>
    /// The directory's resilience stance: timeout, retry, breaker. Declared, published,
    /// carried into the plan — and, since the policy engine's stage 4, executed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This summary said "nothing arms it", and the remark below drew a line between "a
    /// safety diagnostic, which ships, and a runtime feature, which does not". All three
    /// kinds here run now: the timeout is per attempt and clamped to what is left of the
    /// flow's deadline, the retry backs off with full jitter and refuses a sleep that would
    /// outlive that deadline, and the breaker is per capability and per process.
    /// </para>
    /// <para>
    /// The retry is still legal only because <see cref="CreateIdentity"/> declares
    /// <c>Idempotent = true</c> — <c>FLOWX1014</c> is an error otherwise. That part of the
    /// original remark survives, and it matters more now than when it was written: it used to
    /// guard a declaration nothing executed.
    /// </para>
    /// </remarks>
    public static PolicySet DirectoryService { get; } = PolicySet
        .Named("directory-service")
        .Timeout(TimeSpan.FromSeconds(2))
        .Retry(attempts: 3)
        .CircuitBreaker(failureRatio: 0.5, breakDuration: TimeSpan.FromSeconds(10));

    /// <summary>
    /// The undo stance for the desk: attached to the one kind of step it applies to, and
    /// reaching it. *It was "the one policy kind the engine can execute" until stage 4 ran.*
    /// </summary>
    /// <remarks>
    /// Three attempts rather than the documented default of five, so that the assertion which
    /// pins it states a number the default does not already supply.
    /// <see cref="ErrorCategory.Conflict"/> is in the default retryable set for a
    /// compensation — a facilities system mid-way through another write against the same desk
    /// is exactly the case where insisting is right and giving up leaves two systems
    /// disagreeing.
    /// </remarks>
    public static PolicySet FacilitiesUndo { get; } = PolicySet
        .Named("facilities-undo")
        .CompensationRetry(attempts: 3);
}
