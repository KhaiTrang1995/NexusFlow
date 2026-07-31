using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace FlowX.Cli.Tests;

/// <summary>
/// Every <c>FLOWX-DIFF-nnn</c> code the CLI can emit has a row in <c>docs/22-CLI.md</c>,
/// and every row describes a code something emits.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This was written after the gap was found by hand, which is the wrong order.</strong>
/// Six codes — <c>007</c>, <c>008</c>, <c>009</c>, <c>108</c>, <c>205</c> and <c>019</c> —
/// shipped emitting into CI output with no row anywhere, and three of them are
/// <c>Breaking</c>, so a merge could be blocked by a code whose meaning was published
/// nowhere. The <c>FLOWX1xxx</c> family has had this gate since P0
/// (<c>EveryRaisedDiagnosticHasADocumentationPage</c>); this family never did, because the
/// two are governed by different documents and only one of them carried a rule.
/// </para>
/// <para>
/// <strong>Both directions, and the second is not padding.</strong> A documented code that
/// nothing emits is the failure this repository has removed repeatedly: a promise in a
/// table with no behaviour behind it. It is the cheaper half to get wrong, because a reader
/// hits it only when looking for something that was never there.
/// </para>
/// <para>
/// The source is scanned as text rather than through a constant list, deliberately. A list
/// would be a third place the set is written down, and the test would then assert that two
/// of the three agree while the emitter drifted from both.
/// </para>
/// </remarks>
public sealed class DiffCodeDocumentationTests
{
    private static readonly Regex Code = new(@"FLOWX-DIFF-\d+", RegexOptions.Compiled);

    [Fact]
    public void EveryEmittedDiffCodeIsDocumented()
    {
        var undocumented = EmittedCodes().Except(DocumentedCodes()).OrderBy(static c => c, StringComparer.Ordinal);

        undocumented.ShouldBeEmpty(
            "A code that reaches CI output with no row in docs/22-CLI.md is a finding a " +
            "reader cannot look up. Three of the six that were missing when this test was " +
            "written are Breaking, so a merge could be blocked by a code with no published " +
            "meaning.");
    }

    [Fact]
    public void EveryDocumentedDiffCodeIsEmitted()
    {
        var unemitted = DocumentedCodes().Except(EmittedCodes()).OrderBy(static c => c, StringComparer.Ordinal);

        unemitted.ShouldBeEmpty(
            "A row in docs/22-CLI.md describing a code nothing emits promises a finding " +
            "that cannot occur. Delete the row, or emit the code.");
    }

    private static HashSet<string> EmittedCodes() =>
        Directory
            .EnumerateFiles(FindDirectory("src/FlowX.Cli"), "*.cs", SearchOption.AllDirectories)
            .Where(static path => !path.Replace('\\', '/').Split('/').Any(static s => s is "obj" or "bin"))
            .SelectMany(static path => Code.Matches(File.ReadAllText(path)).Select(static m => m.Value))
            .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> DocumentedCodes() =>
        Code.Matches(File.ReadAllText(Path.Combine(FindDirectory("docs"), "22-CLI.md")))
            .Select(static m => m.Value)
            .ToHashSet(StringComparer.Ordinal);

    private static string FindDirectory(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);

            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not locate '{relativePath}' from {AppContext.BaseDirectory}.");
    }
}
