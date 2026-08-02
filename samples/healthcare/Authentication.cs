using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Healthcare;

/// <summary>
/// A bearer scheme that turns one of this sample's demonstration tokens into a
/// <see cref="ClaimsPrincipal"/>, carrying both the permissions the flow's capabilities name
/// and the clinic the deployment isolates on.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A stand-in for an OIDC handler, and not a security control.</strong> There is no
/// signature, no issuer, no audience and no expiry — the tokens are constants in
/// <see cref="ClinicTokens"/>. A real deployment deletes this file and calls
/// <c>AddAuthentication().AddJwtBearer(...)</c>; nothing else in the application moves,
/// because everything downstream reads a <see cref="ClaimsPrincipal"/> and does not care who
/// minted it. <c>samples/banking/Authentication.cs</c> makes the same argument at length.
/// </para>
/// <para>
/// <strong>What is different here is the second claim.</strong> This sample reads
/// <c>tid</c> for the tenant, like banking, and it also has a deployment region. The region
/// is <em>not</em> a claim and deliberately cannot be: a caller that could name the region its
/// data may be processed in could name this one, and the residency check would then be a
/// control the caller configures. It is deployment configuration, compared against a pin the
/// deployment also holds — see <c>Program.cs</c>.
/// </para>
/// </remarks>
public sealed class ClinicTokenHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The scheme name, used by the host and by the tests.</summary>
    public const string SchemeName = "ClinicToken";

    /// <summary>Creates the handler.</summary>
    public ClinicTokenHandler(
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
            // presented a bad one" are different facts and only the second is a rejection
            // worth logging.
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        const string prefix = "Bearer ";

        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.Fail("Expected a Bearer token."));
        }

        var token = header[prefix.Length..].Trim();

        if (!ClinicTokens.Claims.TryGetValue(token, out var claims))
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
/// Chosen so that one <c>curl</c> reaches each outcome this sample has to show: an intake that
/// is admitted, one refused for want of a grant, two clinics whose rows the database keeps
/// apart, and one clinic whose data may not be processed in this region at all.
/// </remarks>
public static class ClinicTokens
{
    /// <summary>A clinic served by this deployment's region.</summary>
    public const string BerlinTenant = "clinic-berlin";

    /// <summary>A second clinic in the same region, so two tenants' rows can be compared.</summary>
    public const string MunichTenant = "clinic-munich";

    /// <summary>A clinic pinned to a region this deployment is not in.</summary>
    /// <remarks>
    /// Every call it makes is refused with <c>tenant.residency_refused</c>, before a lease is
    /// taken and before a row exists. It is the only tenant here that never reaches a flow, and
    /// that is what it is for.
    /// </remarks>
    public const string DublinTenant = "clinic-dublin";

    /// <summary>A Berlin clinician holding every grant this flow names: the intake is admitted.</summary>
    public const string BerlinClinician = "clinician-berlin-token";

    /// <summary>
    /// A Berlin receptionist, authenticated and holding no <c>records:write</c>.
    /// </summary>
    /// <remarks>
    /// The interesting one. It passes validation and the consent check and is refused at the
    /// deduplication, so the refusal happens where a permission model is supposed to bite
    /// rather than at the door — and nothing about the patient has been written anywhere,
    /// because nothing before that step writes.
    /// </remarks>
    public const string BerlinReceptionist = "receptionist-berlin-token";

    /// <summary>The same grants under a different clinic, so two clinics' rows can be compared.</summary>
    public const string MunichClinician = "clinician-munich-token";

    /// <summary>A Dublin clinician, refused by residency before anything else is decided.</summary>
    public const string DublinClinician = "clinician-dublin-token";

    /// <summary>The claims each token carries.</summary>
    /// <remarks>
    /// <c>scope</c> is a space-delimited list because that is what an OAuth 2.0 access token
    /// carries, and reading that shape rather than one value per claim is why
    /// <c>StepAuthorization.Grants</c> splits.
    /// </remarks>
    public static IReadOnlyDictionary<string, Claim[]> Claims { get; } =
        new Dictionary<string, Claim[]>(StringComparer.Ordinal)
        {
            [BerlinReceptionist] =
            [
                new Claim(ClaimTypes.NameIdentifier, "reception-berlin-1"),
                new Claim("tid", BerlinTenant),
                new Claim("scope", "consent:read"),
            ],
            [BerlinClinician] =
            [
                new Claim(ClaimTypes.NameIdentifier, "clinician-berlin-1"),
                new Claim("tid", BerlinTenant),
                new Claim("scope", "consent:read records:write records:erase"),
            ],
            [MunichClinician] =
            [
                new Claim(ClaimTypes.NameIdentifier, "clinician-munich-1"),
                new Claim("tid", MunichTenant),
                new Claim("scope", "consent:read records:write records:erase"),
            ],
            [DublinClinician] =
            [
                new Claim(ClaimTypes.NameIdentifier, "clinician-dublin-1"),
                new Claim("tid", DublinTenant),
                new Claim("scope", "consent:read records:write records:erase"),
            ],
        };
}
