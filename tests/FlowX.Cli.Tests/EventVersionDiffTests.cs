using System.Text.Json;
using FlowX.Cli.Diffing;
using FlowX.Cli.Manifest;
using Shouldly;
using Xunit;

namespace FlowX.Cli.Tests;

/// <summary>
/// <c>FLOWX-DIFF-020</c>'s "schema major bumped" half, over the repository's only
/// compiler-produced manifest.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This half of a Breaking rule could not fire on any manifest FlowX produced.</strong>
/// <c>ManifestWriter</c> stamped the literal <c>1.0.0</c> on every event, so the key
/// <c>ManifestDiff</c> compares an event on — <c>type@major</c> — was the same on both sides
/// of every build. <c>ManifestDiffTests</c> exercised the rule against hand-written documents
/// and passed; the rule was still dead where it mattered, which is
/// <a href="../../docs/adr/ADR-0017-manifest-v1-freeze-criteria.md">ADR-0017</a> F2's second
/// finding and the <c>FLOWX-DIFF-015</c> shape a third time.
/// </para>
/// <para>
/// <strong>So the corpus here is the real document.</strong>
/// <c>samples/ecommerce/flowx.manifest.baseline.json</c> is committed, is produced by the
/// compiler from the sample's source, and now carries <c>order.placed</c> at <c>2.0.0</c>
/// because <c>OrderPlaced</c> declares <c>[EventSchema("2.0.0")]</c>. The baseline side of
/// each comparison is that same document with the declaration's effect undone, which is
/// byte-for-byte what this compiler emitted for this sample before the attribute existed.
/// </para>
/// </remarks>
public sealed class EventVersionDiffTests
{
    private const string Event = "order.placed";

    /// <summary>
    /// A declared major bump, against the build that declared nothing, is Breaking.
    /// </summary>
    /// <remarks>
    /// Both codes are asserted, because the pair is the whole of what the rule says: the
    /// <c>@1</c> contract is gone — which is what a subscriber pinned to it experiences — and
    /// an unrelated <c>@2</c> contract has appeared. Nothing else in the document moved, so
    /// neither finding can have come from anywhere but the declaration.
    /// </remarks>
    [Fact]
    public void BumpingTheSamplesEventMajorIsBreaking()
    {
        var report = ManifestDiff.Compare(Published(version: "1.0.0"), Published());

        report.HasBreakingChange.ShouldBeTrue();

        var removed = Single(report, "FLOWX-DIFF-020");

        removed.Subject.ShouldBe("event order.placed@1");
        removed.Severity.ShouldBe(DiffSeverity.Breaking);

        Single(report, "FLOWX-DIFF-102").Subject.ShouldBe("event order.placed@2");
        report.Findings.Count.ShouldBe(2, Describe(report));
    }

    /// <summary>
    /// A minor bump inside the declared major is not a change at all, which is the severity
    /// 22-CLI §2.1 documents for it.
    /// </summary>
    /// <remarks>
    /// "A patch or minor bump within a major is not a change at all, because a minor bump is
    /// compatible by definition; if it was not, the contract change itself is reported." The
    /// rule keys on the major, so this is the same sentence with an exit code behind it — and
    /// it is worth asserting on the real document now that the value can move for a reason
    /// other than a hand edit.
    /// </remarks>
    [Fact]
    public void AMinorBumpOfTheSamplesEventIsNotAChange()
    {
        var report = ManifestDiff.Compare(Published(), Published(version: "2.1.0"));

        report.Findings.ShouldBeEmpty(Describe(report));
        report.Compatible.ShouldBeTrue();
    }

    /// <summary>The committed baseline itself carries the declared version.</summary>
    /// <remarks>
    /// The two comparisons above would both pass against a document whose event was still at
    /// <c>1.0.0</c> — one would report nothing and the other would report a bump this test
    /// file had invented. This is what ties them to the sample.
    /// </remarks>
    [Fact]
    public void TheCommittedBaselineCarriesTheDeclaredVersion()
    {
        Published().Events
            .Single(e => e.Type == Event).SchemaVersion
            .ShouldBe("2.0.0", "samples/ecommerce declares [EventSchema(\"2.0.0\")] on OrderPlaced.");
    }

    /// <summary>
    /// The committed baseline, optionally with the event's published version replaced.
    /// </summary>
    private static ManifestDocument Published(string? version = null)
    {
        var document = JsonSerializer.Deserialize(
            File.ReadAllText(Path.Combine(FindDirectory("samples/ecommerce"), "flowx.manifest.baseline.json")),
            ManifestJsonContext.Default.ManifestDocument)!;

        if (version is not null)
        {
            document.Events.Single(e => e.Type == Event).SchemaVersion = version;
        }

        return document;
    }

    private static DiffFinding Single(DiffReport report, string code)
    {
        var matches = report.Findings.Where(f => f.Code == code).ToList();

        matches.Count.ShouldBe(1, $"Expected exactly one {code}. {Describe(report)}");

        return matches[0];
    }

    private static string Describe(DiffReport report) => report.Findings.Count == 0
        ? "The report was empty."
        : "The report held: " + string.Join("; ", report.Findings.Select(f => f.Code + " " + f.Subject));

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
