using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace FlowX.Architecture.Tests;

/// <summary>
/// Whether the secret scanner's allowlist still describes the thing it was written to excuse.
/// </summary>
/// <remarks>
/// <para>
/// <strong>An allowlist is a claim, and this is its evidence.</strong> <c>.gitleaks.toml</c>
/// excuses nineteen literal strings on the grounds that each is a demonstration bearer token
/// declared in a sample's authentication stand-in. Nothing but this test holds that ground: a
/// deleted sample would leave its entry behind, and a new demonstration token would be added to
/// the allowlist by whoever met the red gate, with no one checking that it is what the file says
/// it is.
/// </para>
/// <para>
/// <strong>Both directions matter, and they fail differently.</strong> An allowlist entry with no
/// declaration is a string the scanner ignores for a reason that has stopped being true. A
/// declaration with no allowlist entry is the gate going red on the next sample — which is the
/// safe direction, and is still worth failing here, because the alternative is the author
/// discovering it from a scheduled run three hours later.
/// </para>
/// <para>
/// The scanner itself is not run here. It reads 443 commits of history and takes two minutes;
/// what this asserts is the configuration's honesty, which is the half that rots.
/// </para>
/// </remarks>
public sealed partial class SecretAllowlistTests
{
    /// <summary>Where a demonstration token may be declared. Anything else is a real token.</summary>
    /// <remarks>
    /// Listed rather than discovered by pattern, because "any file that declares a string ending
    /// in -token" is exactly the set an accidental commit would join.
    /// </remarks>
    private static readonly string[] StandIns =
    [
        "samples/ai-agent/Infrastructure.cs",
        "samples/banking/Authentication.cs",
        "samples/crm/Authentication.cs",
        "samples/ecommerce/Authentication.cs",
        "samples/healthcare/Authentication.cs",
        "samples/polling/Authentication.cs",
        "samples/workflow/Authentication.cs",
        "templates/FlowX.Templates/content/FlowX.Web/Authentication.cs",
    ];

    [Fact]
    public void EveryAllowlistedTokenIsDeclaredByASample()
    {
        var orphans = Allowlisted().Except(Declared(), StringComparer.Ordinal).ToList();

        orphans.ShouldBeEmpty(
            "an allowlist entry no sample declares is a string the scanner ignores for a reason " +
            "that has stopped being true: " + string.Join(", ", orphans));
    }

    [Fact]
    public void EveryDeclaredTokenIsAllowlisted()
    {
        var missing = Declared().Except(Allowlisted(), StringComparer.Ordinal).ToList();

        missing.ShouldBeEmpty(
            "a demonstration token outside .gitleaks.toml turns the secret gate red the next time " +
            "it runs, and the finding will look exactly like a real one: " +
            string.Join(", ", missing));
    }

    /// <summary>
    /// The allowlist excuses strings, not files or rules.
    /// </summary>
    /// <remarks>
    /// The cheap repair for a red secret scanner is to exclude the paths or the rule, and both
    /// blind it to a genuine bearer token in a README — which is where a developer pastes a
    /// working request. Asserted here so the cheap repair cannot arrive quietly.
    /// </remarks>
    [Fact]
    public void TheAllowlistExcusesNoPathAndNoRule()
    {
        var config = Configuration();

        config.ShouldNotContain(
            "paths",
            Case.Sensitive,
            "excluding a path hides a real token committed into it.");

        config.ShouldNotContain(
            "stopwords",
            Case.Sensitive,
            "a stopword excuses every finding containing it, not the finding it was added for.");
    }

    /// <summary>The scanner's configuration, read from the repository root.</summary>
    /// <remarks>
    /// <c>Path.Join</c> rather than <c>Path.Combine</c>, here and in <see cref="Declared"/>:
    /// <c>Combine</c> discards everything before a rooted segment, so a second argument that
    /// began with a separator would silently read from the filesystem root instead. Both
    /// arguments are literals today and neither is rooted — but the two forms differ in what
    /// they promise, and this one promises concatenation, which is what is meant.
    /// </remarks>
    private static string Configuration() =>
        File.ReadAllText(Path.Join(RepositoryLayout.Root.FullName, ".gitleaks.toml"));

    /// <summary>The literal strings the allowlist excuses, read out of its anchored regexes.</summary>
    private static IEnumerable<string> Allowlisted() => AnchoredLiteral()
        .Matches(Configuration())
        .Select(static m => m.Groups["token"].Value)
        .OrderBy(static token => token, StringComparer.Ordinal);

    /// <summary>Every token literal a stand-in declares.</summary>
    private static IEnumerable<string> Declared() => StandIns
        .Select(path => Path.Join(RepositoryLayout.Root.FullName, path))
        .Where(File.Exists)
        .SelectMany(path => TokenLiteral().Matches(File.ReadAllText(path)))
        .Select(static m => m.Groups["token"].Value)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(static token => token, StringComparer.Ordinal);

    [GeneratedRegex(@"'''\^(?<token>[a-z][a-z0-9-]*-token)\$'''")]
    private static partial Regex AnchoredLiteral();

    [GeneratedRegex("\"(?<token>[a-z][a-z0-9-]*-token)\"")]
    private static partial Regex TokenLiteral();
}
