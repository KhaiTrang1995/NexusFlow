using System.Security.Claims;

namespace FlowX;

/// <summary>
/// A step's authorisation stance, resolved out of its capability into the one question the
/// step loop asks: may <em>this</em> principal run <em>this</em> step?
/// </summary>
/// <remarks>
/// <para>
/// <strong>Resolved once, when the plan is built.</strong> It hangs off
/// <see cref="StepNode.StepAuthorization"/> and is gated by
/// <see cref="ExecutionPlan.HasAuthorizedSteps"/>, which is
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0023-policy-stages-hook-through-the-plan.md">ADR-0023</a>'s
/// shape applied to a second stage, and
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0026-authorisation-runs-in-the-step-loop.md">ADR-0026</a>
/// is why. Nothing reads a <c>[Capability]</c> attribute at run time; there is no reflection
/// on this path at all, which is what keeps it NativeAOT-clean (constraint C2).
/// </para>
/// <para>
/// <strong><see cref="CanRefuse"/> counts what can say no, never what was declared.</strong>
/// The same bargain <see cref="StepPolicy.IsActive"/> strikes: a step whose stance admits
/// every caller leaves the plan's flag false and takes the path it always took, so a flow of
/// <see cref="Authorization.Public"/> steps pays nothing and budget B2 is untouched for it.
/// </para>
/// <para>
/// <strong>Four of the five stances are decided here and the fifth is not.</strong>
/// <see cref="Authorization.Policy"/> names an ASP.NET Core authorisation policy, which only
/// <c>IAuthorizationService</c> can evaluate and which <c>FlowX.Runtime</c> may not reference.
/// It is refused at build time by <c>FLOWX1037</c> instead —
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0029-policy-stance-is-refused-at-build-time.md">ADR-0029</a>
/// — so it can never reach this type from compiled source, and
/// <see cref="Decide"/> refuses it rather than falling through to a permit if it ever does.
/// </para>
/// </remarks>
public sealed class StepAuthorization
{
    private StepAuthorization(Authorization? stance, string? value)
    {
        Stance = stance;
        Value = value;
    }

    /// <summary>
    /// Nothing to decide: what a step whose stance admits everyone gets, and what every step
    /// got before authorisation was enforced.
    /// </summary>
    public static StepAuthorization None { get; } = new(null, null);

    /// <summary>The declared stance, or <c>null</c> when the descriptor carried none.</summary>
    public Authorization? Stance { get; }

    /// <summary>The permission the stance names, or <c>null</c> when it names nothing.</summary>
    public string? Value { get; }

    /// <summary>
    /// Claim types that may carry a permission, in the order they are consulted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Claims only, and validated ones.</strong> The list is the counterpart of
    /// <c>HttpTriggerReader.TenantClaimTypes</c> and exists for the same reason
    /// <c>docs/15-Security.md §3</c> gives in Boundary 1's elevation-of-privilege row:
    /// headers are never trusted for identity, so there is deliberately no configuration
    /// hook to add a header source.
    /// </para>
    /// <para>
    /// <c>scope</c> and <c>scp</c> are the OAuth 2.0 and Microsoft identity platform
    /// spellings, and both carry a <em>space-delimited list</em> rather than one value —
    /// which is why <see cref="Grants"/> splits rather than compares. Reading only a
    /// <c>permission</c> claim would mean the stance held for almost no real access token.
    /// </para>
    /// <para>
    /// <strong>No <c>roles</c> claim, deliberately.</strong> A role is not a permission: it
    /// is a bundle somebody maps to permissions, and the mapping is a decision this list
    /// cannot see. Consulting it here would grant <c>payment.write</c> to a caller holding a
    /// role called <c>payment.write</c> and to nobody else, which is a permission model
    /// wearing a role's name — and it would silently widen every stance the day an identity
    /// provider started emitting role names that look like permissions.
    /// </para>
    /// </remarks>
    public static readonly string[] PermissionClaimTypes =
    [
        "permission",
        "permissions",
        "scope",
        "scp",
    ];

    /// <summary>
    /// True when this step can refuse a caller, and so needs to be asked at all.
    /// </summary>
    /// <remarks>
    /// The single question the step loop asks, and what makes
    /// <see cref="ExecutionPlan.HasAuthorizedSteps"/> mean "some step of this plan can say
    /// no" rather than "some step declared a stance" — a flag of the second kind would be
    /// true for every compiled flow in existence, because FLOWX1010 makes a stance
    /// mandatory, and would therefore gate nothing.
    /// </remarks>
    public bool CanRefuse => Stance is { } stance && IsDecidedAtRunTime(stance) && stance
        is not Authorization.Public and not Authorization.Internal;

    /// <summary>
    /// Whether the engine reaches a decision for <paramref name="stance"/> at run time.
    /// </summary>
    /// <remarks>
    /// Four of the five. Paired with <see cref="IsRefusedAtBuildTime"/> so that every member
    /// of <see cref="Authorization"/> is accounted for by one of the two — asserted by
    /// <c>EveryStanceIsEitherDecidedByTheEngineOrRefusedAtBuildTime</c>, which is the gate
    /// against a stance being silently unhandled, which is the defect this type was written
    /// to end.
    /// </remarks>
    public static bool IsDecidedAtRunTime(Authorization stance) => stance switch
    {
        Authorization.Public => true,
        Authorization.Authenticated => true,
        Authorization.Permission => true,
        Authorization.Internal => true,
        _ => false,
    };

    /// <summary>
    /// Whether <paramref name="stance"/> is refused by the compiler instead of decided here.
    /// </summary>
    /// <remarks>
    /// One of the five: <see cref="Authorization.Policy"/>, reported as <c>FLOWX1037</c>.
    /// </remarks>
    public static bool IsRefusedAtBuildTime(Authorization stance) =>
        stance is Authorization.Policy;

    /// <summary>Whether a stance claims a named grant, and so needs one (FLOWX1030).</summary>
    public static bool NeedsAName(Authorization stance) =>
        stance is Authorization.Permission or Authorization.Policy;

    /// <summary>
    /// Reads the stance off a capability, or <see cref="None"/> when it admits every caller.
    /// </summary>
    /// <param name="capability">The step's capability, or <c>null</c> for a step that has none.</param>
    public static StepAuthorization From(CapabilityDescriptor? capability)
    {
        if (capability?.Authorization is not { } stance)
        {
            return None;
        }

        var resolved = new StepAuthorization(stance, capability.AuthorizationValue);

        return resolved.CanRefuse ? resolved : None;
    }

    /// <summary>
    /// Decides whether <paramref name="principal"/> may run a step of
    /// <paramref name="capabilityId"/>, returning the refusal or <c>null</c> to permit.
    /// </summary>
    /// <param name="principal">
    /// The caller the trigger resolved from validated claims, or <c>null</c> for an
    /// anonymous activation.
    /// </param>
    /// <param name="capabilityId">What is being authorised, so a refusal names it.</param>
    /// <remarks>
    /// <para>
    /// <strong>A refusal is returned, never thrown.</strong>
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0007-result-over-exceptions.md">ADR-0007</a>:
    /// "you are not allowed to do that" is a business outcome the caller was always going to
    /// have to handle, and every stance here answers it as a value.
    /// </para>
    /// <para>
    /// <strong>The default arm refuses.</strong> A stance this method does not recognise —
    /// <see cref="Authorization.Policy"/>, or a member added to the enum later — must not
    /// fall through to a permit. Failing open is the defect; failing closed is at worst a
    /// flow that stops, loudly, naming the stance nobody implemented.
    /// </para>
    /// </remarks>
    public Error? Decide(ClaimsPrincipal? principal, string capabilityId) => Stance switch
    {
        null or Authorization.Public or Authorization.Internal => null,

        Authorization.Authenticated => IsAuthenticated(principal)
            ? null
            : AuthorizationErrors.NotAuthenticated(capabilityId),

        Authorization.Permission when !IsAuthenticated(principal) =>
            AuthorizationErrors.NotAuthenticated(capabilityId),

        Authorization.Permission => Grants(principal, Value!)
            ? null
            : AuthorizationErrors.PermissionDenied(capabilityId, Value!),

        _ => AuthorizationErrors.StanceNotEnforceable(capabilityId, Stance.Value),
    };

    /// <summary>
    /// Whether a principal is present <em>and</em> authenticated.
    /// </summary>
    /// <remarks>
    /// Both halves. ASP.NET Core puts a non-null <see cref="ClaimsPrincipal"/> on every
    /// request whether or not a scheme authenticated it, so a null check alone would admit
    /// every anonymous caller — which is the single most likely way for this control to be
    /// written and still fail open.
    /// </remarks>
    private static bool IsAuthenticated(ClaimsPrincipal? principal) =>
        principal?.Identity?.IsAuthenticated == true;

    /// <summary>
    /// Whether any of the principal's permission-bearing claims grants
    /// <paramref name="permission"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A whole token, never a substring. <c>payment.write.admin</c> and
    /// <c>not.payment.write</c> both contain <c>payment.write</c>, and a contains-check would
    /// grant on either — turning a permission model into a prefix model nobody chose.
    /// </para>
    /// <para>
    /// Ordinal comparison, deliberately: a permission is an identifier, and a culture-aware
    /// comparison makes the answer depend on the server's locale.
    /// </para>
    /// </remarks>
    private static bool Grants(ClaimsPrincipal? principal, string permission)
    {
        if (principal is null)
        {
            return false;
        }

        foreach (var claim in principal.Claims)
        {
            if (Array.IndexOf(PermissionClaimTypes, claim.Type) < 0)
            {
                continue;
            }

            var granted = claim.Value.AsSpan();

            foreach (var range in granted.Split(' '))
            {
                if (granted[range].SequenceEqual(permission))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
