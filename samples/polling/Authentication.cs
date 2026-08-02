using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Polling;

/// <summary>
/// A bearer scheme that turns this sample's one demonstration token into a
/// <see cref="ClaimsPrincipal"/>, so the two permissions this flow names are held by somebody.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Without this file the sample could not process anything.</strong> Every capability
/// declares a stance and the engine decides each one against <c>HttpContext.User</c> before the
/// step is dispatched, so a host registering no scheme answers
/// <c>403 authorization.not_authenticated</c> at <c>ocr.upload</c> — the first step.
/// </para>
/// <para>
/// <strong>And the attempts after the first are decided by nothing, which is the interesting
/// half here.</strong> A poll's second attempt is the platform continuing an instance it
/// already admitted: a timer sweep resumes it with no caller asking for anything and no claims
/// on the journal row to ask about. The engine does not re-decide a stance on a continuation —
/// see the comment beside <c>plan.HasAuthorizedSteps</c> in <c>FlowEngine</c> — which is what
/// stops a four-hour poll becoming a construct no flow could place after an authenticated step.
/// </para>
/// <para>
/// <strong>A stand-in for an OIDC handler, and not a security control.</strong> No signature, no
/// issuer, no audience, no expiry. A real deployment deletes this file and calls
/// <c>AddAuthentication().AddJwtBearer(...)</c>, and nothing else moves.
/// </para>
/// </remarks>
public sealed class DocumentTokenHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The scheme name, used by the host and by the tests.</summary>
    public const string SchemeName = "DocumentToken";

    /// <summary>Creates the handler.</summary>
    public DocumentTokenHandler(
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
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        const string prefix = "Bearer ";

        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.Fail("Expected a Bearer token."));
        }

        if (!DocumentTokens.Claims.TryGetValue(header[prefix.Length..].Trim(), out var claims))
        {
            return Task.FromResult(AuthenticateResult.Fail("Unknown token."));
        }

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName)));
    }
}

/// <summary>The two demonstration callers, and what each of them is for.</summary>
public static class DocumentTokens
{
    /// <summary>An intake operator, holding both grants this flow names.</summary>
    public const string Intake = "intake-token";

    /// <summary>
    /// A reader: signed in, and holding <c>document.read</c> only.
    /// </summary>
    /// <remarks>
    /// Refused at <c>ocr.upload</c>, the first step, which is a permission decision rather than
    /// a door — and refused there rather than four hours later, because the stance is decided
    /// before the step is dispatched.
    /// </remarks>
    public const string Reader = "reader-token";

    /// <summary>The claims each token carries.</summary>
    public static IReadOnlyDictionary<string, Claim[]> Claims { get; } =
        new Dictionary<string, Claim[]>(StringComparer.Ordinal)
        {
            [Intake] =
            [
                new Claim(ClaimTypes.NameIdentifier, "intake-1"),
                new Claim("scope", "document.read document.write"),
            ],
            [Reader] =
            [
                new Claim(ClaimTypes.NameIdentifier, "reader-1"),
                new Claim("scope", "document.read"),
            ],
        };
}
