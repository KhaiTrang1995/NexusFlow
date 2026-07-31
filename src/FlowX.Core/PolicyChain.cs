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
/// The two rejections below duplicate analyzer diagnostics FLOWX1014 and FLOWX1018
/// on purpose. The analyzer catches the mistake in user code; this catches it in a
/// plan built any other way, so the engine's assumption holds unconditionally.
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
    public static PolicyChain Create(PolicySet policies, CapabilityDescriptor capability)
    {
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(capability);

        foreach (var policy in policies.Policies)
        {
            Validate(policy, capability);
        }

        // OrderBy is a stable sort, which matters: two policies in the same stage must
        // keep their declared order, or the emitted plan differs between builds and
        // `flowx diff` reports changes nobody made.
        return new PolicyChain([.. policies.Policies.OrderBy(static p => (int)p.Stage)]);
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
    }
}
