using System.Globalization;

namespace FlowX.Cli.Diffing;

/// <summary>Just enough SemVer to key and order manifest entries.</summary>
/// <remarks>
/// <para>
/// Not a SemVer implementation, and not trying to be. The diff needs exactly two things:
/// which major a contract belongs to, and which of two entries in the same major is the
/// newer one. Anything beyond that would be a dependency or a hundred lines of build-
/// metadata precedence rules serving no caller.
/// </para>
/// <para>
/// Everything here tolerates a version string it does not understand rather than
/// throwing. A manifest is machine-written, but it is also a published artifact that a
/// tool from a different generation may have produced, and refusing to diff because one
/// version string is unfamiliar turns a soft problem into a build failure.
/// </para>
/// </remarks>
internal static class SemVer
{
    /// <summary>The major component, which is the unit of compatibility.</summary>
    /// <remarks>
    /// Flows pin the major of every capability they compiled against
    /// (<a href="../../../docs/07-Capability-Model.md">07 §5</a>), so the major — not the
    /// exact version — is what a consumer depends on. Keying the diff by it is what makes
    /// side-by-side versions work: publishing <c>payment.capture@3.0.0</c> alongside
    /// <c>2.1.0</c> adds a contract, while publishing it instead of <c>2.1.0</c> removes
    /// one, and those must not classify the same way.
    /// </remarks>
    public static string Major(string? version)
    {
        if (string.IsNullOrEmpty(version))
        {
            return "0";
        }

        var dot = version.IndexOf('.', StringComparison.Ordinal);

        return dot < 0 ? version : version[..dot];
    }

    /// <summary>Orders two versions, newest last.</summary>
    /// <remarks>
    /// A total order, so that picking the representative of a major is deterministic even
    /// when the input is not a version this understands. Numeric components compare
    /// numerically; a release outranks its own pre-releases; everything else falls back
    /// to ordinal comparison so the answer is at least stable across runs.
    /// </remarks>
    public static int Compare(string? left, string? right)
    {
        var (leftCore, leftPre) = Split(left);
        var (rightCore, rightPre) = Split(right);

        for (var i = 0; i < 3; i++)
        {
            var comparison = Component(leftCore, i).CompareTo(Component(rightCore, i));

            if (comparison != 0)
            {
                return comparison;
            }
        }

        return (leftPre, rightPre) switch
        {
            (null, not null) => 1,
            (not null, null) => -1,
            _ => string.CompareOrdinal(left, right),
        };
    }

    private static (string Core, string? PreRelease) Split(string? version)
    {
        if (string.IsNullOrEmpty(version))
        {
            return (string.Empty, null);
        }

        var mark = version.IndexOfAny(['-', '+']);

        return mark < 0 ? (version, null) : (version[..mark], version[(mark + 1)..]);
    }

    private static long Component(string core, int index)
    {
        var parts = core.Split('.');

        return index < parts.Length
            && long.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                ? value
                : 0;
    }
}
