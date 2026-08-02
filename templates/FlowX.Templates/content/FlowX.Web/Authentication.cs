using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowXStarter;

/// <summary>
/// A bearer scheme that turns one of two demonstration tokens into a
/// <see cref="ClaimsPrincipal"/>, so this project runs its own flow the moment it is
/// generated.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this file exists at all.</strong> Every capability in FlowX must declare who
/// may call it — there is no permissive default, and the compiler refuses a capability that
/// declares nothing (<c>FLOWX1010</c>). The two in <c>Capabilities.cs</c> declare
/// <see cref="Authorization.Authenticated"/> and <see cref="Authorization.Permission"/>, and
/// the engine decides both against <c>HttpContext.User</c>. Something has to put a principal
/// there, or the first step of every request is refused with
/// <c>authorization.not_authenticated</c>. This is the smallest honest something.
/// </para>
/// <para>
/// <strong>It is not a security control, and it is meant to be deleted.</strong> The tokens
/// are two constants in <see cref="Tokens"/>. There is no signature, no issuer, no audience
/// and no expiry, so anyone who can reach the port can present one. A real deployment
/// deletes this file and calls <c>AddAuthentication().AddJwtBearer(...)</c> instead —
/// and changes nothing else, because everything downstream reads a
/// <see cref="ClaimsPrincipal"/> and does not care who minted it.
/// </para>
/// <para>
/// <strong>The header is trusted here and nowhere else.</strong> Identity comes from
/// validated claims, never from a header or the payload — that is the platform's rule and it
/// is not relaxed by this file. What this class does is stand where the token validator
/// stands: it is the thing that decides a header is a principal. That it decides so by
/// dictionary lookup rather than by checking a signature is precisely the part a real
/// deployment replaces.
/// </para>
/// </remarks>
public sealed class StarterTokenHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The scheme name, used by the host and by the verification script.</summary>
    public const string SchemeName = "StarterToken";

    /// <summary>Creates the handler.</summary>
    public StarterTokenHandler(
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
            // NoResult, not Fail. "Nobody presented a credential" and "somebody presented a
            // bad one" are different facts and only the second is worth logging as a
            // rejection. Either way the user is left unauthenticated, which is what the
            // capability's stance is then decided against.
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        const string prefix = "Bearer ";

        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.Fail("Expected a Bearer token."));
        }

        var token = header[prefix.Length..].Trim();

        if (!Tokens.Claims.TryGetValue(token, out var claims))
        {
            return Task.FromResult(AuthenticateResult.Fail("Unknown token."));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

/// <summary>The two demonstration callers, and what each of them is for.</summary>
/// <remarks>
/// Chosen so that one request is refused by each stance this application declares and one
/// reaches the end. <c>ticket.validate</c> declares
/// <see cref="Authorization.Authenticated"/>; <c>ticket.record</c> declares
/// <see cref="Authorization.Permission"/> naming <c>ticket.write</c>.
/// </remarks>
public static class Tokens
{
    /// <summary>
    /// Signed in, holding <c>ticket.read</c> and not <c>ticket.write</c>.
    /// </summary>
    /// <remarks>
    /// The instructive one: it passes <c>ticket.validate</c> and is refused by
    /// <c>ticket.record</c>, which is how this project demonstrates that being
    /// authenticated is not the same as being authorised.
    /// </remarks>
    public const string Reader = "reader-token";

    /// <summary>Signed in and holding <c>ticket.write</c>: opens a ticket.</summary>
    public const string Support = "support-token";

    /// <summary>The claims each token carries.</summary>
    /// <remarks>
    /// <c>scope</c> is a space-delimited list because that is what an OAuth 2.0 access token
    /// carries, and it is one of the claim types the engine consults for a permission —
    /// alongside <c>scp</c>, <c>permission</c> and <c>permissions</c>. Notably <em>not</em>
    /// <c>roles</c>: a role is a bundle somebody maps to permissions, and that mapping is not
    /// a fact the engine can see.
    /// </remarks>
    public static IReadOnlyDictionary<string, Claim[]> Claims { get; } =
        new Dictionary<string, Claim[]>(StringComparer.Ordinal)
        {
            [Reader] =
            [
                new Claim(ClaimTypes.NameIdentifier, "reader-1"),
                new Claim("scope", "ticket.read"),
            ],
            [Support] =
            [
                new Claim(ClaimTypes.NameIdentifier, "support-1"),
                new Claim("scope", "ticket.read ticket.write"),
            ],
        };
}
