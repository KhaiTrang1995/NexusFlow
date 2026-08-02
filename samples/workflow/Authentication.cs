using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Workflow;

/// <summary>
/// A bearer scheme that turns one of this sample's demonstration tokens into a
/// <see cref="ClaimsPrincipal"/>, so the onboarding flow's six permissions are held by
/// somebody.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Without this file the sample could not onboard anybody.</strong> Every capability
/// declares a stance and the engine decides each one against <c>HttpContext.User</c> before the
/// step is dispatched, so a host registering no scheme answers
/// <c>403 authorization.not_authenticated</c> at <c>offer.validate</c> — the first step of the
/// first flow. The README's transcripts documented a sample that served those requests, and
/// this is what makes them true again rather than a note saying they used to be.
/// </para>
/// <para>
/// <strong>The flow that nobody calls needs none of it, and that is the interesting half.</strong>
/// <c>offer.window.close</c> is started by a <c>[CronTrigger]</c>, and an occurrence has no
/// caller to authenticate. Its capability declares <c>Authorization.Internal</c> — the honest
/// stance for a step nothing outside the platform can address, because a trigger addresses a
/// <em>flow</em> and never a capability (ADR-0004) — so the schedule fires and settles with no
/// principal anywhere in the path. A permission on that capability would have made a cron
/// schedule undeployable, which is the modelling this sample is showing.
/// </para>
/// <para>
/// <strong>A stand-in for an OIDC handler, and not a security control.</strong> No signature, no
/// issuer, no audience, no expiry. A real deployment deletes this file and calls
/// <c>AddAuthentication().AddJwtBearer(...)</c>, and nothing else moves.
/// </para>
/// </remarks>
public sealed class PeopleOpsTokenHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The scheme name, used by the host and by the tests.</summary>
    public const string SchemeName = "PeopleOpsToken";

    /// <summary>Creates the handler.</summary>
    public PeopleOpsTokenHandler(
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

        var token = header[prefix.Length..].Trim();

        if (!PeopleOpsTokens.Claims.TryGetValue(token, out var claims))
        {
            return Task.FromResult(AuthenticateResult.Fail("Unknown token."));
        }

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName)));
    }
}

/// <summary>The two demonstration callers, and what each of them is for.</summary>
/// <remarks>
/// One holds every grant <c>employee.onboard</c> needs and one holds none, which is the smallest
/// pair that shows the difference between authenticated and authorised. The second reaches
/// <c>offer.validate</c> — an <c>Authorization.Authenticated</c> step — and is refused three
/// steps later at the payroll write, so the refusal is a permission decision rather than a
/// door.
/// </remarks>
public static class PeopleOpsTokens
{
    /// <summary>No token: refused at <c>offer.validate</c>, the first step.</summary>
    public const string Anonymous = "";

    /// <summary>
    /// A recruiter: signed in, and holding none of the six write permissions.
    /// </summary>
    /// <remarks>
    /// Enough to send an offer — <c>offer.send</c> declares
    /// <see cref="Authorization.Authenticated"/> — and not enough to onboard anybody, which is
    /// the split the two flows have between them.
    /// </remarks>
    public const string Recruiter = "recruiter-token";

    /// <summary>A people-ops operator, holding every grant the onboarding flow names.</summary>
    public const string PeopleOps = "people-ops-token";

    /// <summary>The claims each token carries.</summary>
    /// <remarks>
    /// The six are listed rather than granted wholesale, so a capability added with a seventh
    /// permission fails naming the grant it needs instead of being waved through.
    /// </remarks>
    public static IReadOnlyDictionary<string, Claim[]> Claims { get; } =
        new Dictionary<string, Claim[]>(StringComparer.Ordinal)
        {
            [Recruiter] =
            [
                new Claim(ClaimTypes.NameIdentifier, "recruiter-1"),
                new Claim("scope", "offers.read offers.write"),
            ],
            [PeopleOps] =
            [
                new Claim(ClaimTypes.NameIdentifier, "people-ops-1"),
                new Claim(
                    "scope",
                    "payroll.write supplier.write identity.write access.write " +
                    "equipment.approve screening.write"),
            ],
        };
}
