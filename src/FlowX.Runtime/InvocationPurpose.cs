using System.Security.Claims;

namespace FlowX.Runtime;

/// <summary>
/// Where <see cref="FlowInvocation.Purpose"/> comes from: a validated claim, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The canonical list, of which a transport's copy is a projection.</strong> The same
/// arrangement <c>ClaimTenantResolver.TenantClaimTypes</c> and
/// <c>HttpTriggerReader.TenantClaimTypes</c> already have, and for the reason that one gives:
/// two lists eventually disagree, and a transport whose copy lost an entry would produce
/// invocations that assert no purpose and are then refused by a policy the caller's credential
/// in fact satisfies.
/// </para>
/// <para>
/// <strong>Claims only, and there is deliberately no hook to add a header.</strong> A purpose
/// limitation whose input is chosen by the party being limited is not a limitation — it is a
/// field the caller fills in to unlock the step. <c>docs/15 §3</c>'s Boundary 1
/// elevation-of-privilege row is the same objection about identity, and
/// <c>StepAuthorization.PermissionClaimTypes</c> takes the same stance about a grant.
/// </para>
/// <para>
/// <strong>Authenticated or nothing</strong>, which is the half of this that is easy to get
/// wrong. ASP.NET Core hands an anonymous request a <see cref="ClaimsPrincipal"/> that can
/// carry any claim at all, so reading claims off an unauthenticated identity would be reading
/// a header with extra steps — and it would let an anonymous caller assert whatever purpose
/// opened the step. The question asked is the one <c>StepAuthorization</c> and
/// <c>ClaimTenantResolver</c> both ask of the same object.
/// </para>
/// </remarks>
public static class InvocationPurpose
{
    /// <summary>
    /// Claim types that may carry a processing purpose, in the order they are consulted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Short and deliberately not extended to the OAuth <c>scope</c> spellings that
    /// <c>StepAuthorization.PermissionClaimTypes</c> reads. A scope is a list of things a
    /// credential may <em>do</em>; a purpose is the single reason this call is being made, and
    /// treating one entry of a space-delimited scope list as a purpose would mean a token
    /// granted three capabilities asserted three purposes at once — which is the opposite of a
    /// limitation.
    /// </para>
    /// <para>
    /// The namespaced spelling matches <c>ClaimTenantResolver</c>'s third entry, so a
    /// deployment that already issues FlowX-namespaced claims writes this one the same way.
    /// </para>
    /// </remarks>
    public static readonly string[] PurposeClaimTypes =
    [
        "purpose",
        "http://schemas.flowx.dev/claims/purpose",
    ];

    /// <summary>
    /// The purpose the principal asserts, or <c>null</c> when it asserts none.
    /// </summary>
    /// <param name="principal">The caller a transport resolved from validated claims.</param>
    /// <remarks>
    /// Returns <c>null</c> rather than a default. A default purpose is a gate that opens for
    /// every caller who never heard of it, which is the whole failure mode
    /// <c>PolicySet.Consent</c> exists to close.
    /// </remarks>
    public static string? FromClaims(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        foreach (var claimType in PurposeClaimTypes)
        {
            var value = principal.FindFirst(claimType)?.Value;

            // Whitespace is not a purpose. A claim of " " would otherwise be compared against
            // a declared purpose, fail, and report "you were made for another one" — which is
            // true and useless. Read as absent, it reports the repair.
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
