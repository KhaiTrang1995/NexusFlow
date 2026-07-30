using System.Text.RegularExpressions;

namespace FlowX;

/// <summary>
/// Validation for the identity and version strings that reach the manifest.
/// </summary>
/// <remarks>
/// Centralised in one place because these formats are a published contract: they
/// appear in traces, in policy targeting, in agent tool names and in impact
/// analysis. Two slightly different regexes in two files is how a platform ends up
/// with two slightly different notions of a valid identity.
/// </remarks>
internal static partial class Identifiers
{
    /// <summary><c>&lt;domain&gt;.&lt;verb&gt;</c>, lower snake case, at least one separator.</summary>
    [GeneratedRegex(@"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentityPattern { get; }

    /// <summary>SemVer 2.0 core with optional pre-release and build metadata.</summary>
    [GeneratedRegex(
        @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-.]+)?(?:\+[0-9A-Za-z-.]+)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern { get; }

    /// <summary>Validates a business identity and returns it unchanged.</summary>
    /// <exception cref="ArgumentException">The identity is malformed.</exception>
    public static string RequireIdentity(string value, string paramName)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);

        if (!IdentityPattern.IsMatch(value))
        {
            throw new ArgumentException(
                $"'{value}' is not a valid FlowX identity. Expected <domain>.<verb> in lower " +
                "snake case, for example 'inventory.reserve'.",
                paramName);
        }

        return value;
    }

    /// <summary>Validates a semantic version and returns it unchanged.</summary>
    /// <exception cref="ArgumentException">The version is not SemVer 2.0.</exception>
    public static string RequireSemanticVersion(string value, string paramName)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);

        if (!VersionPattern.IsMatch(value))
        {
            throw new ArgumentException(
                $"'{value}' is not a semantic version. Expected MAJOR.MINOR.PATCH, for " +
                "example '2.1.0'. The version describes the contract, not the implementation.",
                paramName);
        }

        return value;
    }
}
