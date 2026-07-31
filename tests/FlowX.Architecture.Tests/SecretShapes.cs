using System.Text.RegularExpressions;

namespace FlowX.Architecture.Tests;

/// <summary>
/// What a secret looks like, as opposed to what a secret is called.
/// </summary>
/// <remarks>
/// <para>
/// The distinction is the whole design of <see cref="SecurityFitnessTests.ManifestContainsNoSecrets"/>.
/// A manifest that lists a member called <c>PaymentToken</c> under a contract's
/// <c>sensitive</c> array is doing its job: a reviewer, <c>flowx diff</c> and an agent all
/// need to see that the field exists and is marked. A manifest that contains
/// <c>eyJhbGciOi…</c> has leaked one. A word-list cannot tell those apart, so it either
/// fails on the correct manifest or is quietly stripped of the words that fail — and the
/// second is what happens in practice.
/// </para>
/// <para>
/// Every pattern here matches a value, never a name. They are drawn from the formats that
/// actually turn up in leaked artifacts: PEM blocks, JWTs, connection-string assignments,
/// credentials embedded in a URL, bearer headers and the fixed-prefix key formats issued
/// by the large providers.
/// </para>
/// </remarks>
internal static partial class SecretShapes
{
    /// <summary>Every shape, with a name suitable for a failure message.</summary>
    public static readonly (string Name, Regex Pattern)[] All =
    [
        ("a PEM private-key block", PemPrivateKey()),
        ("a JSON Web Token", JsonWebToken()),
        ("a credential assignment", CredentialAssignment()),
        ("credentials embedded in a URL", CredentialsInUrl()),
        ("a bearer token", BearerToken()),
        ("a provider-issued access key", ProviderAccessKey()),
        ("a JSON member named for a credential, carrying a value", CredentialMember()),
    ];

    /// <summary>A private key, in the encoding everything writes them in.</summary>
    [GeneratedRegex(@"-----BEGIN[A-Z ]*PRIVATE KEY-----")]
    private static partial Regex PemPrivateKey();

    /// <summary>
    /// Three base64url segments separated by dots, beginning with the encoding of
    /// <c>{"</c> — which every JWT header does.
    /// </summary>
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}")]
    private static partial Regex JsonWebToken();

    /// <summary>
    /// <c>Password=…</c> and its relatives — the connection-string form, which is how a
    /// secret most often arrives somewhere it should not be.
    /// </summary>
    [GeneratedRegex(
        @"\b(?:password|pwd|accountkey|sharedaccesskey|api[_-]?key|client[_-]?secret|secretkey)\s*=\s*[^;""'\s,}\]]+",
        RegexOptions.IgnoreCase)]
    private static partial Regex CredentialAssignment();

    /// <summary>
    /// <c>scheme://user:password@host</c>. The character classes stop at a slash or a quote
    /// so the match cannot span two unrelated strings.
    /// </summary>
    [GeneratedRegex(@"://[^/\s""':]{1,64}:[^/\s""'@]{1,64}@")]
    private static partial Regex CredentialsInUrl();

    /// <summary>An <c>Authorization: Bearer …</c> value carried across in a copied header.</summary>
    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9._~+/=-]{16,}", RegexOptions.IgnoreCase)]
    private static partial Regex BearerToken();

    /// <summary>
    /// The fixed-prefix key formats: AWS access keys, GitHub tokens, Slack tokens, Stripe
    /// keys. Fixed prefixes are why secret scanners work at all.
    /// </summary>
    [GeneratedRegex(
        @"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b|\bgh[pousr]_[A-Za-z0-9]{20,}|\bxox[abposr]-[A-Za-z0-9-]{10,}|\bsk_(?:live|test)_[A-Za-z0-9]{16,}")]
    private static partial Regex ProviderAccessKey();

    /// <summary>
    /// A JSON member whose <em>name</em> says credential and whose value is a non-empty
    /// string. The manifest may name a sensitive member; it may not pair that name with a
    /// value.
    /// </summary>
    [GeneratedRegex(
        @"""[^""]*(?:password|passwd|secret|api[_-]?key|access[_-]?key|private[_-]?key|credential|connection[_-]?string)[^""]*""\s*:\s*""[^""]+""",
        RegexOptions.IgnoreCase)]
    private static partial Regex CredentialMember();
}
