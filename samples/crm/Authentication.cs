using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Crm;

/// <summary>
/// A bearer scheme that turns one of this sample's demonstration tokens into a
/// <see cref="ClaimsPrincipal"/>, carrying the tenant the deployment isolates on.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It is a stand-in for an OIDC handler and not a security control.</strong> There is
/// no signature, no issuer, no audience and no expiry — the tokens are constants in
/// <see cref="CrmTokens"/>. A real deployment deletes this file and calls
/// <c>AddAuthentication().AddJwtBearer(…)</c>; nothing else in the application moves, because
/// everything downstream reads a <see cref="ClaimsPrincipal"/> and does not care who minted it.
/// </para>
/// <para>
/// <strong>The header is trusted here and nowhere else.</strong> What this file does is stand
/// where the token validator stands. That it is a dictionary lookup rather than a signature
/// check is the part a deployment replaces; that the tenant then comes off a validated claim
/// rather than off a header is ADR-0046, and is not.
/// </para>
/// </remarks>
public sealed class CrmTokenHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The scheme name, used by the host and by the tests.</summary>
    public const string SchemeName = "CrmToken";

    /// <summary>Creates the handler.</summary>
    /// <param name="options">Scheme options, supplied by the framework.</param>
    /// <param name="logger">Logger factory, supplied by the framework.</param>
    /// <param name="encoder">URL encoder, supplied by the framework.</param>
    public CrmTokenHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();

        if (string.IsNullOrWhiteSpace(header))
        {
            // NoResult rather than Fail: "nobody presented a credential" and "somebody
            // presented a bad one" are different facts and only the second is worth logging.
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        const string Prefix = "Bearer ";

        if (!header.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.Fail("Expected a Bearer token."));
        }

        var token = header[Prefix.Length..].Trim();

        if (!CrmTokens.Claims.TryGetValue(token, out var claims))
        {
            return Task.FromResult(AuthenticateResult.Fail("Unknown token."));
        }

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)),
                SchemeName)));
    }
}

/// <summary>The demonstration callers, and what each of them is for.</summary>
/// <remarks>
/// Two tenants, because one tenant cannot demonstrate an isolation. They are what the
/// <c>curl</c> pair in the README of package 12 will use, and what
/// <c>SchemaProbeEndpointTests</c> uses now.
/// </remarks>
public static class CrmTokens
{
    /// <summary>The tenant <see cref="Northwind"/> belongs to.</summary>
    public const string NorthwindTenant = "crm-northwind";

    /// <summary>The tenant <see cref="Contoso"/> belongs to.</summary>
    public const string ContosoTenant = "crm-contoso";

    /// <summary>A representative of the Northwind tenant.</summary>
    public const string Northwind = "rep-northwind-token";

    /// <summary>The same grants under a different tenant, so two tenants' rows can be compared.</summary>
    public const string Contoso = "rep-contoso-token";

    /// <summary>
    /// A manager of the Northwind tenant, holding the discount grant a representative does not.
    /// </summary>
    /// <remarks>
    /// Nothing in this package reads <c>crm.discount.approve</c> — §5.2 puts it on the quote
    /// approval that package 9 builds. The token is here because the grant is a property of the
    /// deployment's callers rather than of the flow that first names one, and because a package
    /// that adds a token to make its own test pass is how a sample ends up with nine of them.
    /// </remarks>
    public const string NorthwindManager = "manager-northwind-token";

    /// <summary>A director of the same tenant.</summary>
    /// <remarks>
    /// <strong>A fourth token, because the third could not answer the question.</strong>
    /// <c>ManagementStore</c> resolves a director's scope to the empty list — meaning every
    /// seller rather than a named few — and until this existed there was no way to reach that
    /// path: the client's director rode the manager's token and saw a manager's two reports.
    /// A role whose whole behaviour is "sees more than a manager" cannot be demonstrated by
    /// borrowing a manager's credentials.
    /// </remarks>
    public const string NorthwindDirector = "director-northwind-token";

    /// <summary>The claims each token carries.</summary>
    /// <remarks>
    /// <c>scope</c> is a space-delimited list because that is what an OAuth 2.0 access token
    /// carries. <c>tid</c> is the tenant and it is a claim, deliberately:
    /// <c>ClaimTenantResolver</c> reads <c>tid</c>, <c>tenant_id</c> and one URI form, and
    /// reads no header at all (ADR-0046).
    /// </remarks>
    public static IReadOnlyDictionary<string, Claim[]> Claims { get; } =
        new Dictionary<string, Claim[]>(StringComparer.Ordinal)
        {
            [Northwind] =
            [
                new Claim(ClaimTypes.NameIdentifier, "rep-northwind-1"),
                new Claim("tid", NorthwindTenant),
                new Claim("scope", "crm.read crm.write"),
            ],
            [Contoso] =
            [
                new Claim(ClaimTypes.NameIdentifier, "rep-contoso-1"),
                new Claim("tid", ContosoTenant),
                new Claim("scope", "crm.read crm.write"),
            ],
            [NorthwindDirector] =
            [
                new Claim(ClaimTypes.NameIdentifier, "director-northwind-1"),
                new Claim("tid", NorthwindTenant),

                // No `crm.discount.approve`. A director is not in the approval chain for a
                // discount — the manager is — and a token that held every grant would make the
                // three personas indistinguishable, which is the opposite of what they exist for.
                new Claim("scope", "crm.read crm.write crm.admin"),
            ],
            [NorthwindManager] =
            [
                new Claim(ClaimTypes.NameIdentifier, "manager-northwind-1"),
                new Claim("tid", NorthwindTenant),
                // crm.admin is the grant that changes the shape of the data rather than the data:
                // declaring a custom field, an object or a relationship. It is separate from
                // crm.write because a representative who may write a lead must not be able to
                // add a required field that every future lead has to carry.
                new Claim("scope", "crm.read crm.write crm.discount.approve crm.admin"),
            ],
        };
}
