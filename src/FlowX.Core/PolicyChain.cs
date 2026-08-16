using System.Collections.Immutable;

namespace FlowX;

/// <summary>
/// A capability's policies, resolved into execution order and validated against the
/// capability they wrap.
/// </summary>
/// <remarks>
/// <para>
/// This is where <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0011-fixed-policy-stage-order.md">ADR-0011</a>
/// stops being a document and becomes a data structure. The author's declaration
/// order is discarded; <see cref="PolicyStage"/> decides. That is not a
/// convenience — it is the safety property.
/// </para>
/// <para>
/// The four rejections below duplicate analyzer diagnostics FLOWX1014, FLOWX1018,
/// FLOWX1051 and FLOWX1053 on purpose. The analyzer catches the mistake in user code;
/// this catches it in a plan built any other way, so the engine's assumption holds
/// unconditionally.
/// </para>
/// </remarks>
public sealed class PolicyChain
{
    private PolicyChain(ImmutableArray<PolicyDescriptor> ordered) => Ordered = ordered;

    /// <summary>A chain with no policies. Valid for every capability.</summary>
    public static PolicyChain Empty { get; } = new([]);

    /// <summary>The policies in execution order: by stage, then by declaration.</summary>
    public ImmutableArray<PolicyDescriptor> Ordered { get; }

    /// <summary>True when no policy is attached.</summary>
    public bool IsEmpty => Ordered.IsEmpty;

    /// <summary>
    /// Resolves a declared policy set against the capability it will wrap.
    /// </summary>
    /// <param name="policies">The declared set. Declaration order does not survive.</param>
    /// <param name="capability">The capability being wrapped; its properties gate what is allowed.</param>
    /// <exception cref="InvalidFlowPlanException">
    /// A retry is attached to a non-idempotent capability, or a cache to one with
    /// side effects.
    /// </exception>
    /// <remarks>
    /// Every descriptor is validated against the one capability handed in, so this is the
    /// right entry point for a set that already describes a single call. A set an author
    /// declared on a <em>step</em> may describe two — the step and its compensation — and wants
    /// <see cref="ForStep"/> and <see cref="ForCompensation"/> instead; that is what the
    /// generated plan uses.
    /// </remarks>
    public static PolicyChain Create(PolicySet policies, CapabilityDescriptor capability)
    {
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(capability);

        return Build(policies.Policies, capability);
    }

    /// <summary>
    /// The half of a declared set that wraps the step itself: everything except the
    /// compensation retry.
    /// </summary>
    /// <param name="policies">The set the author named on the step.</param>
    /// <param name="capability">The capability the step invokes.</param>
    /// <param name="fallback">
    /// The capability a declared <c>Fallback&lt;TCapability&gt;()</c> names, resolved. Supplied
    /// by the generated plan, which read the type's <c>[Capability]</c> declaration at build
    /// time; <c>null</c> for a step whose fallback is a constant or absent.
    /// </param>
    /// <exception cref="InvalidFlowPlanException">
    /// A retry is attached to a non-idempotent capability, a cache or a fallback to one with
    /// side effects, or a fallback capability that has them.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <strong>One <c>.WithPolicy(...)</c> describes two capabilities.</strong> A step and its
    /// compensation are different calls with different idempotency declarations, and a set may
    /// legitimately speak to both — <c>Timeout</c> bounds the capture, <c>CompensationRetry</c>
    /// bounds the refund. <see cref="Create"/> validates every descriptor against the single
    /// capability it is handed, so it cannot express that pairing: handed the set and
    /// <c>payment.capture</c> it refuses the compensation retry, and refuses it for the wrong
    /// capability's idempotency. Splitting first is what makes the ordinary saga expressible.
    /// </para>
    /// <para>
    /// <strong>Only <c>CompensationRetry</c> moves.</strong> <c>Audit</c> is also a
    /// <see cref="PolicyStage.Consistency"/> policy and stays here, because it runs after the
    /// step succeeded rather than over its undo — the split is by what a policy wraps, not by
    /// which stage it runs in.
    /// </para>
    /// <para>
    /// <strong>A third capability arrives here too, and only here.</strong>
    /// <c>PolicySet.Fallback&lt;TCapability&gt;()</c> can name a type and nothing more — a set
    /// is built with no step in sight and reflecting over the type at run time is what C2
    /// refuses — so the declaration it leaves in the descriptor is bound to
    /// <paramref name="fallback"/> as the chain is built. That is the same service this method
    /// already performs for the step's own capability, one level further in.
    /// </para>
    /// </remarks>
    public static PolicyChain ForStep(
        PolicySet policies,
        CapabilityDescriptor capability,
        CapabilityDescriptor? fallback = null)
    {
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(capability);

        return Build(
            policies.Policies
                .Where(static p => p.Kind != CompensationPolicy.CompensationRetryKind)
                .Select(p => Bind(p, fallback)),
            capability);
    }

    /// <summary>
    /// Replaces a fallback's declared type with the descriptor the plan resolved for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every other descriptor passes through untouched, and a fallback that named a constant
    /// does too — <see cref="FallbackCapability"/> is the marker, so the rewrite reaches
    /// exactly the declaration it is for.
    /// </para>
    /// <para>
    /// A capability fallback whose descriptor was <em>not</em> supplied keeps the declaration,
    /// and <c>StepPolicy.From</c> then resolves no capability from it, so the step runs with no
    /// fallback rather than with one the engine cannot name on a journal row. That is
    /// unreachable from a compiled plan — the emitter writes the two together — and is the
    /// honest reading for a chain built by hand: a degraded path nothing can record is worse
    /// than no degraded path.
    /// </para>
    /// </remarks>
    private static PolicyDescriptor Bind(PolicyDescriptor policy, CapabilityDescriptor? fallback)
    {
        if (fallback is null ||
            policy.Kind != StepPolicy.FallbackKind ||
            !policy.Parameters.TryGetValue("capability", out var declared) ||
            declared is not FallbackCapability)
        {
            return policy;
        }

        if (fallback.HasSideEffects)
        {
            throw new InvalidFlowPlanException(
                $"Capability '{fallback.Id}' declares side effects " +
                $"[{string.Join(", ", fallback.SideEffects)}], so it cannot be a step's " +
                "Fallback. A fallback runs because a dependency has just failed, so it is the " +
                "least-exercised path in the system running at the worst moment; an effect " +
                "made there sits under a step whose own capability produced none, and the " +
                "unwind stack has nowhere to record whose undo would reverse it. A degraded " +
                "mode that has to write is a branch in the flow. FLOWX1053 refuses this at " +
                "build time and this is the same rule at plan construction.");
        }

        return policy with { Parameters = policy.Parameters.SetItem("capability", fallback) };
    }

    /// <summary>
    /// The half of a declared set that wraps the step's <em>compensation</em>: the compensation
    /// retry and nothing else.
    /// </summary>
    /// <param name="policies">The set the author named on the step.</param>
    /// <param name="compensation">The compensating capability — the one that would run twice.</param>
    /// <exception cref="InvalidFlowPlanException">
    /// The compensating capability does not declare itself idempotent.
    /// </exception>
    /// <remarks>
    /// The validation is FLOWX1014's rule applied to the capability the retry would actually
    /// re-dispatch. A non-idempotent <c>payment.capture</c> may carry an idempotent
    /// <c>payment.refund</c>; it is the refund's declaration that decides whether asking twice
    /// is safe, and running a reversal twice is a second reversal.
    /// </remarks>
    public static PolicyChain ForCompensation(PolicySet policies, CapabilityDescriptor compensation)
    {
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(compensation);

        return Build(
            policies.Policies.Where(static p => p.Kind == CompensationPolicy.CompensationRetryKind),
            compensation);
    }

    private static PolicyChain Build(
        IEnumerable<PolicyDescriptor> policies, CapabilityDescriptor capability)
    {
        var kept = ImmutableArray.CreateBuilder<PolicyDescriptor>();

        foreach (var policy in policies)
        {
            Validate(policy, capability);
            kept.Add(policy);
        }

        if (kept.Count == 0)
        {
            return Empty;
        }

        // OrderBy is a stable sort, which matters: two policies in the same stage must
        // keep their declared order, or the emitted plan differs between builds and
        // `flowx diff` reports changes nobody made.
        return new PolicyChain([.. kept.OrderBy(static p => (int)p.Stage)]);
    }

    private static void Validate(PolicyDescriptor policy, CapabilityDescriptor capability)
    {
        if (policy.Kind == "Retry" && !capability.IsIdempotent)
        {
            throw new InvalidFlowPlanException(
                $"Capability '{capability.Id}' declares Idempotent = false, so a Retry policy " +
                "cannot be attached to it. Retrying a non-idempotent operation duplicates its " +
                "effect — for a payment capture, that is a duplicate charge. Either make the " +
                "capability idempotent and declare it, or handle the failure in the flow.");
        }

        if (policy.Kind == CompensationPolicy.CompensationRetryKind && !capability.IsIdempotent)
        {
            throw new InvalidFlowPlanException(
                $"Capability '{capability.Id}' declares Idempotent = false, so a " +
                "CompensationRetry policy cannot be attached to it. The chain a compensation " +
                "carries wraps the compensating capability, not the step it undoes — so it is " +
                "the compensating capability that has to be safe to run twice, and running a " +
                "reversal twice is a second reversal. Either make it idempotent and declare " +
                "it, or accept a single attempt and the CompensationFailed that follows.");
        }

        if (policy.Kind == "Cache" && capability.HasSideEffects)
        {
            throw new InvalidFlowPlanException(
                $"Capability '{capability.Id}' declares side effects " +
                $"[{string.Join(", ", capability.SideEffects)}], so a Cache policy cannot be " +
                "attached to it. A cache hit returns a success without performing the effect.");
        }

        if (policy.Kind == StepPolicy.HedgeKind && !capability.IsIdempotent)
        {
            throw new InvalidFlowPlanException(
                $"Capability '{capability.Id}' declares Idempotent = false, so a Hedge policy " +
                "cannot be attached to it. A hedge issues a second call while the first is " +
                "still running, under the same idempotency key — so the effect can happen " +
                "twice at once, and the answer the flow keeps may be either call's. That is " +
                "the promise Idempotent = true makes, and FLOWX1014 requires of a retry for " +
                "the sequential version of the same reason.");
        }

        if (policy.Kind == StepPolicy.FallbackKind && capability.HasSideEffects)
        {
            throw new InvalidFlowPlanException(
                $"Capability '{capability.Id}' declares side effects " +
                $"[{string.Join(", ", capability.SideEffects)}], so a Fallback policy cannot " +
                "be attached to it. A fallback returns a success without performing the " +
                "effect — Cache's objection exactly — and the degraded step registers no " +
                "compensation, so an effect that half happened would be left with nothing " +
                "pointing at it.");
        }
    }
}
