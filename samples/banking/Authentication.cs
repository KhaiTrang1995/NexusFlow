using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Banking;

/// <summary>
/// A bearer scheme that turns one of this sample's demonstration tokens into a
/// <see cref="ClaimsPrincipal"/>, carrying both the permissions the flow's capabilities name
/// and the tenant the deployment isolates on.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Without this file the sample could not move money, and that is why it exists.</strong>
/// Every capability in <c>Capabilities.cs</c> declares a stance — <c>transfer.validate</c> admits
/// any authenticated caller, the two ledger legs require <c>ledger:post</c> — and the engine
/// decides each one against <c>HttpContext.User</c> before the step is dispatched. Until
/// authorisation was enforced, a host that registered no scheme served transfers to anybody;
/// afterwards the same host answered every transfer <c>403 authorization.not_authenticated</c> at
/// the first step. The README's transcripts documented the first behaviour and the sample had the
/// second, which is the exact failure a reference application exists to make impossible.
/// </para>
/// <para>
/// <strong>It is a stand-in for an OIDC handler and not a security control.</strong> There is no
/// signature, no issuer, no audience and no expiry — the tokens are constants in
/// <see cref="BankTokens"/>. A real deployment deletes this file and calls
/// <c>AddAuthentication().AddJwtBearer(...)</c>; nothing else in the application moves, because
/// everything downstream reads a <see cref="ClaimsPrincipal"/> and does not care who minted it.
/// </para>
/// <para>
/// <strong>The header is trusted here and nowhere else.</strong> <c>docs/15-Security.md §3</c>'s
/// Boundary 1 says headers are never trusted for identity, and the platform keeps that rule:
/// <c>HttpTriggerReader</c> reads <c>HttpContext.User</c>, <c>StepAuthorization</c> reads claims,
/// and <c>ClaimTenantResolver</c> reads claims. What this file does is stand where the token
/// validator stands. That it is a dictionary lookup rather than a signature check is the part a
/// deployment replaces.
/// </para>
/// </remarks>
public sealed class BankTokenHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The scheme name, used by the host and by the tests.</summary>
    public const string SchemeName = "BankToken";

    /// <summary>Creates the handler.</summary>
    public BankTokenHandler(
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
            // NoResult rather than Fail: "nobody presented a credential" and "somebody presented
            // a bad one" are different facts and only the second is a rejection worth logging.
            // Either way HttpContext.User is left unauthenticated, which is what the capability's
            // stance is then decided against.
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        const string prefix = "Bearer ";

        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.Fail("Expected a Bearer token."));
        }

        var token = header[prefix.Length..].Trim();

        if (!BankTokens.Claims.TryGetValue(token, out var claims))
        {
            return Task.FromResult(AuthenticateResult.Fail("Unknown token."));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

/// <summary>
/// The demonstration callers, and what each of them is for.
/// </summary>
/// <remarks>
/// <para>
/// Chosen so that one <c>curl</c> can reach each of the three outcomes this sample's stances
/// produce — refused for being anonymous, refused for holding the wrong grant, and settled — and
/// so that two of them belong to different tenants, which is what makes the isolation
/// <c>Program.cs</c> declares observable rather than configured.
/// </para>
/// <para>
/// <strong><c>tid</c> is the tenant and it is a claim, deliberately.</strong>
/// <c>ClaimTenantResolver</c> reads <c>tid</c>, <c>tenant_id</c> and one URI form, and reads no
/// header at all — <c>ADR-0046</c>. A tenant supplied by the caller in a header is the shape of a
/// cross-tenant read, so there is no configuration hook that would add one.
/// </para>
/// </remarks>
public static class BankTokens
{
    /// <summary>The tenant <see cref="Frankfurt"/> and <see cref="FrankfurtClerk"/> belong to.</summary>
    public const string FrankfurtTenant = "bank-de";

    /// <summary>The tenant <see cref="London"/> belongs to.</summary>
    public const string LondonTenant = "bank-uk";

    /// <summary>No token at all: refused by <c>transfer.validate</c>, the first step.</summary>
    public const string Anonymous = "";

    /// <summary>
    /// A clerk of the Frankfurt tenant, authenticated and holding no <c>ledger:post</c>.
    /// </summary>
    /// <remarks>
    /// The interesting one. It passes validation and screening and is refused at the debit, so
    /// the refusal happens where a permission model is supposed to bite rather than at the door
    /// — and no money moves, because nothing before the debit does.
    /// </remarks>
    public const string FrankfurtClerk = "clerk-de-token";

    /// <summary>A Frankfurt operator holding every grant this flow names: the transfer settles.</summary>
    public const string Frankfurt = "operator-de-token";

    /// <summary>The same grants under a different tenant, so two tenants' rows can be compared.</summary>
    public const string London = "operator-uk-token";

    /// <summary>The claims each token carries.</summary>
    /// <remarks>
    /// <c>scope</c> is a space-delimited list because that is what an OAuth 2.0 access token
    /// carries, and reading that shape rather than one value per claim is why
    /// <c>StepAuthorization.Grants</c> splits.
    /// </remarks>
    public static IReadOnlyDictionary<string, Claim[]> Claims { get; } =
        new Dictionary<string, Claim[]>(StringComparer.Ordinal)
        {
            [FrankfurtClerk] =
            [
                new Claim(ClaimTypes.NameIdentifier, "clerk-de-1"),
                new Claim("tid", FrankfurtTenant),
                new Claim("scope", "compliance:screen correspondent:read"),
            ],
            [Frankfurt] =
            [
                new Claim(ClaimTypes.NameIdentifier, "operator-de-1"),
                new Claim("tid", FrankfurtTenant),
                new Claim("scope", "compliance:screen correspondent:read ledger:post settlement:write"),
            ],
            [London] =
            [
                new Claim(ClaimTypes.NameIdentifier, "operator-uk-1"),
                new Claim("tid", LondonTenant),
                new Claim("scope", "compliance:screen correspondent:read ledger:post settlement:write"),
            ],
        };
}
