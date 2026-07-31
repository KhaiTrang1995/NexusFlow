using System.Globalization;
using System.Text.RegularExpressions;

namespace FlowX.Architecture.Tests;

/// <summary>One claim on one identifier, and the exact place the claim was written.</summary>
/// <param name="Id">The identifier as it appears — <c>0017</c>, <c>FLOWX1028</c>.</param>
/// <param name="Where">Repository-relative path, with a line number where the claim is a row.</param>
/// <param name="Target">The link target, for claims written as a Markdown link. Otherwise null.</param>
internal sealed record Allocation(string Id, string Where, string? Target = null);

/// <summary>An architecture decision record on disk, and the number its own heading claims.</summary>
internal sealed record AdrRecord(string Number, string? HeadingNumber, string Path);

/// <summary>The span of work-package numbers one phase has reserved.</summary>
internal sealed record PhaseRange(string Phase, int First, int Last, string Where);

/// <summary>A work package defined at one site in <c>PLAN.md</c>.</summary>
/// <param name="Token">The identifier after <c>WP-</c>, suffix included: <c>12a</c> is not <c>12</c>.</param>
/// <param name="Number">The leading digits, which is what a phase range is expressed in.</param>
/// <param name="Phase">The phase whose section the definition sits in, or null if it sits outside one.</param>
internal sealed record WorkPackage(string Token, int Number, string? Phase, string Where);

/// <summary>The diagnostic index's own statement about which ids exist and which is next.</summary>
internal sealed record DiagnosticIdRange(string Next, string First, string Last);

/// <summary>
/// Reads the documents that allocate identifiers in this repository, and reports every
/// claim each one makes.
/// </summary>
/// <remarks>
/// <para>
/// The documents are the allocator, so they are what gets read. Keeping a list of the
/// current ids here would make this a further place the numbers are written down, and the
/// gate would then be asserting that a copy agrees with itself while the index it was
/// copied from moved on.
/// </para>
/// <para>
/// Parsing is anchored on the shape a claim is written in — a table row, a section heading,
/// a file name — rather than on every mention of an identifier. Both indexes discuss ids in
/// prose at length, including ids that collided and were renumbered; a survey that counted
/// mentions would report those discussions as duplicate allocations, and a gate that fails
/// on the paragraph explaining why it exists is a gate somebody deletes.
/// </para>
/// </remarks>
internal static partial class IdentifierIndexes
{
    /// <summary>The ADR index, which carries one row per allocated number.</summary>
    public const string AdrIndex = "docs/adr/README.md";

    /// <summary>The diagnostics index: the catalogue, the reservations and the next free id.</summary>
    public const string DiagnosticIndex = "docs/diagnostics/README.md";

    /// <summary>The plan, which is the only authoritative source of work-package numbers.</summary>
    public const string Plan = "PLAN.md";

    private static readonly string[] ReleaseTracking =
    [
        "src/FlowX.Compiler/AnalyzerReleases.Unshipped.md",
        "src/FlowX.Compiler/AnalyzerReleases.Shipped.md",
    ];

    // ------------------------------------------------------------- ADR numbers

    /// <summary>Every record in <c>docs/adr/</c>, with the numbers its file name and heading claim.</summary>
    public static IReadOnlyList<AdrRecord> AdrRecords() =>
        Directory.EnumerateFiles(Absolute("docs/adr"), "ADR-*.md")
            .Select(static path => (Path: path, Match: AdrFileName().Match(Path.GetFileName(path))))
            .Where(static candidate => candidate.Match.Success)
            .Select(static candidate => new AdrRecord(
                candidate.Match.Groups["number"].Value,
                HeadingNumber(candidate.Path),
                Relative(candidate.Path)))
            .OrderBy(static record => record.Path, StringComparer.Ordinal)
            .ToList();

    /// <summary>Every row of the ADR index table, with the record each one links to.</summary>
    public static IReadOnlyList<Allocation> AdrIndexRows() => Rows(AdrIndex, AdrIndexRow());

    // --------------------------------------------------------- Diagnostic ids

    /// <summary>The catalogue rows — the ids the compiler is documented as raising.</summary>
    public static IReadOnlyList<Allocation> DiagnosticCatalogue() =>
        Rows(DiagnosticIndex, DiagnosticCatalogueRow());

    /// <summary>The reservation rows — ids spoken for by a rule nobody has written yet.</summary>
    public static IReadOnlyList<Allocation> DiagnosticReservations() =>
        Rows(DiagnosticIndex, DiagnosticReservationRow());

    /// <summary>Every id listed in the analyzer release-tracking files, which are public surface.</summary>
    public static IReadOnlyList<Allocation> DiagnosticReleaseRows() =>
        ReleaseTracking.SelectMany(document => Rows(document, ReleaseTrackingRow())).ToList();

    /// <summary>Every documentation page named after an id.</summary>
    public static IReadOnlyList<Allocation> DiagnosticPages() =>
        Directory.EnumerateFiles(Absolute("docs/diagnostics"), "FLOWX*.md")
            .Select(static path => (Path: path, Match: DiagnosticPageName().Match(Path.GetFileName(path))))
            .Where(static candidate => candidate.Match.Success)
            .Select(static candidate => new Allocation(
                candidate.Match.Groups["id"].Value,
                Relative(candidate.Path)))
            .OrderBy(static allocation => allocation.Where, StringComparer.Ordinal)
            .ToList();

    /// <summary>What the diagnostics index says the next free id is, and the range it may come from.</summary>
    public static DiagnosticIdRange? DiagnosticRange()
    {
        var match = DiagnosticNextFree().Match(string.Join(' ', File.ReadAllLines(Absolute(DiagnosticIndex))));

        return match.Success
            ? new DiagnosticIdRange(
                match.Groups["next"].Value,
                match.Groups["first"].Value,
                match.Groups["last"].Value)
            : null;
    }

    // ------------------------------------------------- Work-package numbers

    /// <summary>
    /// Every site in <c>PLAN.md</c> that <em>defines</em> a work package: a section heading,
    /// or a row of a phase's package table.
    /// </summary>
    /// <remarks>
    /// A heading may define a span — <c>WP-33…WP-36</c> — and every number in the span is
    /// reported, because the span is what allocated all four.
    /// </remarks>
    public static IReadOnlyList<WorkPackage> WorkPackages()
    {
        var packages = new List<WorkPackage>();
        var phase = (string?)null;

        foreach (var (text, where) in Lines(Plan))
        {
            var section = PhaseSection().Match(text);

            if (section.Success)
            {
                phase = section.Groups["phase"].Value;
            }

            packages.AddRange(DefinedAt(text, phase, where));
        }

        return packages;
    }

    /// <summary>
    /// The work-package numbers each phase has reserved: §2 for the phases that have
    /// packages, §6a's allocator table for the phases that do not have them yet.
    /// </summary>
    /// <remarks>
    /// A phase written open-ended — "WP-50 onward are P2" — is closed against the next
    /// phase's first number rather than given a bound this file invented. That is what makes
    /// the seam between the two sections load-bearing: P3 ends where P4's reservation starts,
    /// so moving one moves the other and neither can drift alone.
    /// </remarks>
    public static IReadOnlyList<PhaseRange> ReservedPhaseRanges()
    {
        var sequencing = Section(Plan, "## 2. ");

        var declared = BoundedPhase().Matches(sequencing.Text)
            .Select(match => (
                Phase: match.Groups["phase"].Value,
                First: Number(match, "first"),
                Last: (int?)Number(match, "last"),
                sequencing.Where))
            .Concat(OpenEndedPhase().Matches(sequencing.Text)
                .Select(match => (
                    Phase: match.Groups["phase"].Value,
                    First: Number(match, "first"),
                    Last: (int?)null,
                    sequencing.Where)))
            .Concat(ReservedRangeRows())
            .ToList();

        return Close(declared);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>§6a's allocator table, one row per phase that has reserved a span.</summary>
    private static IEnumerable<(string Phase, int First, int? Last, string Where)> ReservedRangeRows() =>
        Lines(Plan)
            .Select(static line => (Match: ReservedRangeRow().Match(line.Text), line.Where))
            .Where(static candidate => candidate.Match.Success)
            .Select(static candidate => (
                candidate.Match.Groups["phase"].Value,
                Number(candidate.Match, "first"),
                (int?)Number(candidate.Match, "last"),
                candidate.Where));

    /// <summary>Closes every open-ended phase against the next phase's first number.</summary>
    private static List<PhaseRange> Close(List<(string Phase, int First, int? Last, string Where)> declared)
    {
        var ordered = declared.OrderBy(static phase => phase.First).ToList();

        return ordered
            .Select((phase, index) => new PhaseRange(
                phase.Phase,
                phase.First,
                phase.Last ?? (index + 1 < ordered.Count ? ordered[index + 1].First - 1 : int.MaxValue),
                phase.Where))
            .ToList();
    }

    /// <summary>The packages one line defines, if it is a definition site at all.</summary>
    private static IEnumerable<WorkPackage> DefinedAt(string text, string? phase, string where)
    {
        var match = WorkPackageHeading().Match(text);

        if (!match.Success)
        {
            match = WorkPackageTableRow().Match(text);
        }

        if (!match.Success)
        {
            yield break;
        }

        var token = match.Groups["first"].Value;
        var first = Leading(token);
        var last = match.Groups["last"].Success ? Leading(match.Groups["last"].Value) : first;

        for (var number = first; number <= last; number++)
        {
            yield return new WorkPackage(
                number == first ? token : number.ToString(CultureInfo.InvariantCulture),
                number,
                phase,
                where);
        }
    }

    private static int Leading(string token) =>
        int.Parse(new string([.. token.TakeWhile(char.IsDigit)]), CultureInfo.InvariantCulture);

    private static int Number(Match match, string group) =>
        int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);

    /// <summary>Every line of a document matching a claim's shape, with its line number.</summary>
    private static List<Allocation> Rows(string document, Regex shape) =>
        Lines(document)
            .Select(line => (Match: shape.Match(line.Text), line.Where))
            .Where(static candidate => candidate.Match.Success)
            .Select(static candidate => new Allocation(
                candidate.Match.Groups["id"].Value,
                candidate.Where,
                candidate.Match.Groups["target"].Success ? candidate.Match.Groups["target"].Value : null))
            .ToList();

    private static IEnumerable<(string Text, string Where)> Lines(string document) =>
        File.ReadAllLines(Absolute(document))
            .Select((text, index) => (text, $"{document}:{index + 1}"));

    /// <summary>One numbered section of a Markdown document, joined into a single line.</summary>
    /// <remarks>
    /// Joined because the sentence declaring the phase ranges wraps across three lines, and a
    /// range read from half a sentence would be a range nobody wrote.
    /// </remarks>
    private static (string Text, string Where) Section(string document, string heading)
    {
        var lines = File.ReadAllLines(Absolute(document));
        var start = Array.FindIndex(lines, line => line.StartsWith(heading, StringComparison.Ordinal));

        if (start < 0)
        {
            return (string.Empty, $"{document} (no section beginning '{heading}')");
        }

        var end = start + 1;

        while (end < lines.Length && !lines[end].StartsWith("## ", StringComparison.Ordinal))
        {
            end++;
        }

        return (string.Join(' ', lines[start..end]), $"{document}:{start + 1}");
    }

    private static string? HeadingNumber(string path)
    {
        var match = AdrHeading().Match(File.ReadLines(path).FirstOrDefault() ?? string.Empty);

        return match.Success ? match.Groups["number"].Value : null;
    }

    private static string Absolute(string relativePath) =>
        Path.Combine(RepositoryLayout.Root.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string Relative(string absolutePath) =>
        Path.GetRelativePath(RepositoryLayout.Root.FullName, absolutePath).Replace('\\', '/');

    // ------------------------------------------------------------------- shapes

    [GeneratedRegex(@"^ADR-(?<number>\d{4})-.+\.md$")]
    private static partial Regex AdrFileName();

    [GeneratedRegex(@"^#\s+ADR-(?<number>\d{4})\b")]
    private static partial Regex AdrHeading();

    [GeneratedRegex(@"^\|\s*\[(?<id>\d{4})\]\((?<target>[^)]+)\)\s*\|")]
    private static partial Regex AdrIndexRow();

    [GeneratedRegex(@"^(?<id>FLOWX\d{4})\.md$")]
    private static partial Regex DiagnosticPageName();

    [GeneratedRegex(@"^\|\s*\[(?<id>FLOWX\d{4})\]\((?<target>[^)]+)\)\s*\|")]
    private static partial Regex DiagnosticCatalogueRow();

    [GeneratedRegex(@"^\|\s*`(?<id>FLOWX\d{4})`\s*\|")]
    private static partial Regex DiagnosticReservationRow();

    [GeneratedRegex(@"^(?<id>FLOWX\d{4})\s*\|")]
    private static partial Regex ReleaseTrackingRow();

    [GeneratedRegex(@"The next is `(?<next>FLOWX\d{4})`\. The range is `(?<first>FLOWX\d{4})`.`(?<last>FLOWX\d{4})`")]
    private static partial Regex DiagnosticNextFree();

    [GeneratedRegex(@"^##\s+\d+\.\s+(?<phase>P\d)\s+—")]
    private static partial Regex PhaseSection();

    [GeneratedRegex(@"^###\s+WP-(?<first>\d+[a-z]?)(?:…WP-(?<last>\d+[a-z]?))?\s+—")]
    private static partial Regex WorkPackageHeading();

    [GeneratedRegex(@"^\|\s*\*\*WP-(?<first>\d+[a-z]?)\*\*\s+—")]
    private static partial Regex WorkPackageTableRow();

    [GeneratedRegex(@"WP-(?<first>\d+) through WP-(?<last>\d+) are \*{0,2}(?<phase>P\d)")]
    private static partial Regex BoundedPhase();

    [GeneratedRegex(@"WP-(?<first>\d+) onward (?:are )?\*{0,2}(?<phase>P\d)")]
    private static partial Regex OpenEndedPhase();

    /// <summary>A row of §6a's allocator table: a phase, and the span it reserves.</summary>
    [GeneratedRegex(@"^\|\s*\*\*(?<phase>P\d)\*\*[^|]*\|\s*\*\*WP-(?<first>\d+)\s*…\s*WP-(?<last>\d+)\*\*")]
    private static partial Regex ReservedRangeRow();
}
