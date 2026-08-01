using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ecommerce;

/// <summary>
/// A bearer scheme that turns one of three demonstration tokens into a
/// <see cref="ClaimsPrincipal"/>, so the sample can show an authorisation stance both
/// permitting and refusing over real HTTP.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is a stand-in for an OIDC handler, and it is not a security control.</strong>
/// The tokens are three constants in <see cref="Tokens"/> and there is no signature, no
/// issuer, no audience and no expiry. A real deployment deletes this file and calls
/// <c>AddAuthentication().AddJwtBearer(...)</c> — and nothing else in the application
/// changes, because everything downstream sees a <see cref="ClaimsPrincipal"/> and does not
/// care who minted it.
/// </para>
/// <para>
/// <strong>Why not the real thing here.</strong> This project is the repository's only
/// NativeAOT-published assembly (constraint C2) and CI publishes it, so every dependency it
/// takes has to stay trim- and AOT-clean and has to earn a row in the
/// <c>docs/DEPENDENCIES.md</c> register that <c>DependencyLicencesAreCompatible</c> checks.
/// <c>AuthenticationHandler&lt;T&gt;</c> is in the ASP.NET Core shared framework, so this
/// costs neither — the same argument <c>SampleTelemetry</c> makes for hand-writing what an
/// OpenTelemetry SDK reference would otherwise provide.
/// </para>
/// <para>
/// <strong>The header is trusted here and nowhere else.</strong>
/// <c>docs/15-Security.md §3</c>'s Boundary 1 row says headers are never trusted for
/// identity, and that rule holds for the platform: <c>HttpTriggerReader</c> reads
/// <c>HttpContext.User</c> and never a header, and <c>StepAuthorization</c> reads claims and
/// never a header. What this file does is stand where the token validator stands. That it is
/// a lookup rather than a signature check is the part a real deployment replaces.
/// </para>
/// </remarks>
public sealed class DemoTokenHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The scheme name, used by the host and by the tests.</summary>
    public const string SchemeName = "DemoToken";

    /// <summary>Creates the handler.</summary>
    public DemoTokenHandler(
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
            // bad one" are different facts, and only the second is worth logging as a
            // rejection. Either way HttpContext.User is left unauthenticated, which is what
            // the capability's stance is then decided against.
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
        var principal = new ClaimsPrincipal(identity);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(principal, SchemeName)));
    }
}

/// <summary>
/// The three demonstration callers, and what each of them is for.
/// </summary>
/// <remarks>
/// Chosen so that one request can be refused by each of the two stances this application
/// declares, and one can satisfy both. <c>order.validate</c> and <c>inventory.reserve</c>
/// declare <see cref="Authorization.Authenticated"/>; <c>payment.capture</c> declares
/// <see cref="Authorization.Permission"/> naming <c>payment.write</c>.
/// </remarks>
public static class Tokens
{
    /// <summary>No token at all: refused by <c>order.validate</c>, the first step.</summary>
    public const string Anonymous = "";

    /// <summary>
    /// Signed in, holding <c>orders.read</c> and not <c>payment.write</c>.
    /// </summary>
    /// <remarks>
    /// The interesting one. It passes the first two steps and is refused by the third, which
    /// is what makes the sample demonstrate that authenticated is not authorised — and it is
    /// refused <em>after</em> the inventory hold is taken, so the compensation runs and the
    /// stock comes back. A refusal is a business outcome, and the saga unwinds behind it like
    /// any other.
    /// </remarks>
    public const string Shopper = "shopper-token";

    /// <summary>Signed in and holding <c>payment.write</c>: reaches the end.</summary>
    public const string Cashier = "cashier-token";

    /// <summary>The claims each token carries.</summary>
    /// <remarks>
    /// <c>scope</c> is a space-delimited list because that is what an OAuth 2.0 access token
    /// carries, and reading that shape is what makes <c>StepAuthorization</c>'s claim list
    /// worth having. <c>tid</c> is the tenant, resolved by <c>HttpTriggerReader</c> from the
    /// same principal and from nothing else.
    /// </remarks>
    public static IReadOnlyDictionary<string, Claim[]> Claims { get; } =
        new Dictionary<string, Claim[]>(StringComparer.Ordinal)
        {
            [Shopper] =
            [
                new Claim(ClaimTypes.NameIdentifier, "shopper-1"),
                new Claim("tid", "tenant-1"),
                new Claim("scope", "orders.read orders.write"),
            ],
            [Cashier] =
            [
                new Claim(ClaimTypes.NameIdentifier, "cashier-1"),
                new Claim("tid", "tenant-1"),
                new Claim("scope", "orders.read orders.write payment.write"),
            ],
        };
}
