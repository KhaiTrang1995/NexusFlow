using System.Security.Claims;

namespace FlowX.Runtime;

/// <summary>
/// Derives the tenant from validated claims, and refuses a call that names one they do not
/// support.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It derives, it does not read.</strong> The distinction is the whole of this class.
/// <see cref="FlowInvocation.TenantId"/> arrives already populated — <c>HttpTriggerReader</c>
/// puts it there — and a resolver that returned it would have validated nothing and would
/// leave the platform exactly where it was: believing the caller. So the tenant is derived
/// again, here, from <see cref="FlowInvocation.Principal"/>'s claims, and what arrived is
/// treated as an <em>assertion to be checked</em> against it. <c>docs/16 §3</c> states the
/// rule in one line — "<em>a resolver returning a tenant not present in validated claims fails
/// <c>CrossTenantAccessTest</c></em>" — and this is the code that can now fail it.
/// </para>
/// <para>
/// <strong>Three inputs, and the table is small enough to state whole.</strong>
/// </para>
/// <list type="table">
///   <listheader>
///     <term>Claims say</term><description>Call asserts → outcome</description>
///   </listheader>
///   <item>
///     <term>tenant <c>A</c></term>
///     <description>nothing, or <c>A</c> → admitted as <c>A</c>.</description>
///   </item>
///   <item>
///     <term>tenant <c>A</c></term>
///     <description><c>B</c> → refused. This is the escalation the class exists for.</description>
///   </item>
///   <item>
///     <term>nothing</term>
///     <description>anything → refused. A tenant the claims cannot support is not a tenant.</description>
///   </item>
/// </list>
/// <para>
/// <strong>A continuation is exempt, and it is not a bypass.</strong> A timer sweep and a
/// recovery scan carry no claims and never will — the journal row holds a tenant and no
/// principal, by
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0028-identity-arrives-on-the-invocation.md">ADR-0028</a>'s
/// design — so deciding this against that absence would refuse every resumed instance in an
/// isolating deployment and turn a node restart into an outage. The tenant such an invocation
/// carries came from the instance's own row, which the platform wrote when it admitted the
/// call that started it, so it is derived from claims: just from the claims presented then
/// rather than now. This is exactly the distinction
/// <see cref="FlowInvocation.IsContinuation"/> already draws for authorisation, used again for
/// the same reason.
/// </para>
/// <para>
/// <strong>Bus and schedule triggers are refused in an isolating deployment, and that is
/// honest rather than finished.</strong> <c>docs/16 §3</c> gives them tenant sources of their
/// own — a producer-set message header, a schedule's declared tenant — and neither is built:
/// <c>CronTriggerAttribute.PerTenant</c> is declared and inert, and no bus header carries a
/// tenant. Until one does, such a call reaches this class with no principal and is refused
/// with <see cref="TenantErrors.TenantRequired"/>, which names what is missing. The
/// alternative — admitting it untenanted — would write rows no tenant can read back, and
/// admitting whatever it asserted would be the believing this class was written to stop.
/// </para>
/// </remarks>
public sealed class ClaimTenantResolver : ITenantResolver
{
    /// <summary>
    /// Claim types that may carry a tenant, in the order they are consulted.
    /// </summary>
    /// <remarks>
    /// The canonical list, of which <c>HttpTriggerReader.TenantClaimTypes</c> is now a
    /// projection rather than a second copy. Two lists would eventually disagree, and the
    /// transport's copy losing an entry would be a caller silently resolving to no tenant.
    /// </remarks>
    public static readonly string[] TenantClaimTypes =
    [
        "tid",
        "tenant_id",
        "http://schemas.flowx.dev/claims/tenant",
    ];

    private readonly TenantIsolation _isolation;

    /// <summary>Creates a resolver for one deployment's isolation level.</summary>
    /// <param name="isolation">What the deployment declares.</param>
    public ClaimTenantResolver(TenantIsolation isolation) => _isolation = isolation;

    /// <inheritdoc />
    public TenantResolution Resolve(in FlowInvocation invocation)
    {
        if (_isolation == TenantIsolation.None)
        {
            // The single-tenant path, and it is one comparison. No claim is walked, no string
            // is compared and nothing allocates — the bargain every gate in this repository
            // strikes for a feature its deployment did not ask for.
            return TenantResolution.NotConfigured;
        }

        if (invocation.IsContinuation)
        {
            return invocation.TenantId is { Length: > 0 } carried
                ? TenantResolution.Tenant(carried)
                : TenantResolution.NotConfigured;
        }

        var claimed = FromClaims(invocation.Principal);

        if (claimed is null)
        {
            return TenantResolution.Refuse(
                "this deployment isolates by tenant and the caller's validated claims carry " +
                "none. A tenant is never read from a header, a query string or the payload.");
        }

        if (invocation.TenantId is { Length: > 0 } asserted
            && !string.Equals(asserted, claimed, StringComparison.Ordinal))
        {
            return TenantResolution.Refuse(
                $"this call asserts tenant '{asserted}' and the caller's validated claims " +
                "place it in a different one.");
        }

        return TenantResolution.Tenant(claimed);
    }

    /// <summary>
    /// Reads the tenant from a principal's validated claims, or <c>null</c>.
    /// </summary>
    /// <param name="principal">The caller, or null for an anonymous invocation.</param>
    /// <returns>The tenant, or null when the principal carries none.</returns>
    /// <remarks>
    /// <strong>Authenticated or nothing.</strong> An unauthenticated
    /// <see cref="ClaimsPrincipal"/> can carry any claim at all — ASP.NET Core hands one to
    /// every anonymous request — so consulting its claims would be reading a header with extra
    /// steps. This is the same question <c>StepAuthorization</c> asks of the same object, and
    /// the answer has to match: whether the identity is authenticated, not whether it exists.
    /// </remarks>
    public static string? FromClaims(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        foreach (var claimType in TenantClaimTypes)
        {
            var value = principal.FindFirst(claimType)?.Value;

            // Whitespace is not a tenant. A claim of " " would otherwise scope a connection to
            // a tenant no row can carry, which fails closed but reports nothing useful.
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
