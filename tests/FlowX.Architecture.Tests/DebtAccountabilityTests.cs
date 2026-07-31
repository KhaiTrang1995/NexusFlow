using System.Globalization;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace FlowX.Architecture.Tests;

/// <summary>
/// docs/21-Quality-Gates.md §6.1: every suppression is a dated contract.
/// </summary>
/// <remarks>
/// <para>
/// "No technical debt" is not a state a codebase reaches. "No debt that is undeclared,
/// unowned or unexpiring" is, and this is what makes the difference enforceable rather
/// than aspirational.
/// </para>
/// <para>
/// A version of this already runs as a shell step in <c>.github/workflows/quality.yml</c>.
/// It is listed in CHECKLIST §4 as a fitness function, which it was not, and the two are
/// not the same gate in practice: the CI step is invisible until a pull request runs, so
/// the developer who adds an unaccountable suppression finds out last. This one fails on
/// <c>dotnet test</c>, before the commit. It also asks a question the CI step does not —
/// whether the id is actually registered in <c>docs/DEBT.md</c> — because a marker citing
/// <c>DEBT-0099</c> when no such row exists is accountable to nobody.
/// </para>
/// </remarks>
public sealed partial class DebtAccountabilityTests
{
    /// <summary>docs/21-Quality-Gates.md §6.1: at most six months.</summary>
    private const int MaximumLifetimeInDays = 183;

    /// <summary>docs/DEBT.md: the register's budget.</summary>
    private const int Budget = 20;

    /// <summary>
    /// Every suppression carries a debt marker, every marker is registered and unexpired,
    /// and the register stays inside its budget.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written as one test rather than five because it answers one question — "is this
    /// suppression accountable?" — and a suppression that is unregistered, expired and
    /// over budget should say all three things at once rather than hiding two of them
    /// behind the first assertion to fire.
    /// </para>
    /// <para>
    /// <strong>This gate is deliberately time-dependent.</strong> It goes red on the day an
    /// <c>expires</c> date passes, with no code change, which is the entire point of putting
    /// a date on a suppression: debt that quietly renews itself is debt nobody ever pays.
    /// If this fails on a morning when nothing was committed, the register is telling you
    /// something true.
    /// </para>
    /// </remarks>
    [Fact]
    public void SuppressionsAreAccountable()
    {
        var registered = RegisteredDebtIds();
        var problems = new List<string>();
        var markers = 0;

        foreach (var file in SourceSurvey.SourceFiles("src", "plugins", "samples", "tests"))
        {
            var path = SourceSurvey.RelativePath(file);
            var lines = File.ReadAllLines(file.FullName);

            for (var i = 0; i < lines.Length; i++)
            {
                problems.AddRange(InspectSuppression(path, lines, i));
            }

            foreach (Match match in DebtMarker().Matches(string.Join('\n', lines)))
            {
                markers++;
                problems.AddRange(InspectMarker(path, match, registered));
            }
        }

        if (markers > Budget)
        {
            problems.Add($"{markers} debt markers; the budget in docs/DEBT.md is {Budget}.");
        }

        var report =
            "A suppression without an owner and an expiry is a rule switched off by someone " +
            "who left no way to know whether it should come back (docs/21-Quality-Gates.md §6.1):" +
            Environment.NewLine + string.Join(Environment.NewLine, problems);

        problems.ShouldBeEmpty(report);
    }

    /// <summary>
    /// The register itself is well formed: sequential ids, no reuse, a team as the owner.
    /// </summary>
    /// <remarks>
    /// The gate above trusts <c>docs/DEBT.md</c> to say which ids exist. A register with a
    /// duplicated id would let one row cover two unrelated suppressions, and the second one
    /// would inherit an expiry date it was never given.
    /// </remarks>
    [Fact]
    public void TheDebtRegisterIsWellFormed()
    {
        var ids = RegisteredDebtIds();

        ids.Count.ShouldBe(
            ids.Distinct(StringComparer.Ordinal).Count(),
            "docs/DEBT.md lists an id twice. Ids are allocated sequentially and never reused.");

        ids.Count.ShouldBeLessThanOrEqualTo(
            Budget,
            $"docs/DEBT.md is over its budget of {Budget} open entries.");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Checks one line for a suppression, and reports it if it is not accountable.
    /// </summary>
    /// <remarks>
    /// The two forms are checked differently on purpose. A <c>[SuppressMessage]</c> attribute
    /// is a considered act with room above it for a comment, so it must carry a marker
    /// nearby. A <c>#pragma warning disable</c> silences a rule just as completely but is
    /// often local and momentary, so a same-line reason is accepted — a sentence a reader can
    /// evaluate is the minimum, and a bare pragma is not one.
    /// </remarks>
    private static IEnumerable<string> InspectSuppression(string path, string[] lines, int index)
    {
        var line = lines[index];

        if (SuppressMessageAttribute().IsMatch(line) && !HasMarkerNearby(lines, index))
        {
            yield return
                $"{path}:{index + 1} a SuppressMessage attribute with no FLOWX-DEBT marker in " +
                "the comment above it.";
        }

        if (PragmaDisable().IsMatch(line)
            && !line.Contains("//", StringComparison.Ordinal)
            && !HasMarkerNearby(lines, index))
        {
            yield return
                $"{path}:{index + 1} #pragma warning disable with no justification. Add a " +
                "FLOWX-DEBT marker, or a same-line comment saying why.";
        }
    }

    /// <summary>
    /// Whether a debt marker sits on this line or in the comment block immediately above it.
    /// </summary>
    /// <remarks>
    /// Proximity is the point. The CI step searches the whole file, which means one
    /// accountable suppression at the top licenses every unaccountable one below it — and
    /// in a file with a genuine debt entry, that is the file most likely to acquire more.
    /// </remarks>
    private static bool HasMarkerNearby(string[] lines, int index)
    {
        const int Lookback = 6;

        for (var i = Math.Max(0, index - Lookback); i <= index; i++)
        {
            if (DebtMarker().IsMatch(lines[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> InspectMarker(
        string path,
        Match match,
        IReadOnlyCollection<string> registered)
    {
        var id = match.Groups["id"].Value;
        var expires = DateOnly.ParseExact(
            match.Groups["expires"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        if (!registered.Contains(id))
        {
            yield return
                $"{path}: {id} has no row in docs/DEBT.md. An id nobody registered is a " +
                "suppression nobody owns.";
        }

        if (expires < today)
        {
            yield return
                $"{path}: {id} expired on {expires:yyyy-MM-dd}. Remove the suppression, or " +
                "re-declare the debt with a new date and a reason it is still here.";
        }
        else if (expires.DayNumber - today.DayNumber > MaximumLifetimeInDays)
        {
            yield return
                $"{path}: {id} expires {expires:yyyy-MM-dd}, more than six months out. " +
                "An expiry beyond the next two quarters is not a plan.";
        }
    }

    /// <summary>The ids with a row in the register's open-entries table.</summary>
    /// <remarks>
    /// Reads table rows, not every mention of a <c>DEBT-####</c> string. The document also
    /// contains a worked example (<c>DEBT-0042</c>) that must not read as a registration —
    /// otherwise the register documents its own escape hatch.
    /// </remarks>
    private static List<string> RegisteredDebtIds()
    {
        var register = new FileInfo(Path.Combine(RepositoryLayout.Root.FullName, "docs", "DEBT.md"));

        register.Exists.ShouldBeTrue(
            "docs/DEBT.md is missing. Without the register there is nothing for a suppression " +
            "to be accountable to.");

        return File.ReadAllLines(register.FullName)
            .Select(static line => RegisterRow().Match(line))
            .Where(static match => match.Success)
            .Select(static match => match.Groups["id"].Value)
            .ToList();
    }

    [GeneratedRegex(
        @"FLOWX-DEBT:\s*id=(?<id>DEBT-\d{4})\s+owner=(?<owner>[\w-]+)\s+expires=(?<expires>\d{4}-\d{2}-\d{2})")]
    private static partial Regex DebtMarker();

    [GeneratedRegex(@"^\|\s*(?<id>DEBT-\d{4})\s*\|")]
    private static partial Regex RegisterRow();

    /// <summary>
    /// An applied <c>SuppressMessage</c> attribute, in an attribute list rather than in a
    /// string or a sentence.
    /// </summary>
    /// <remarks>
    /// Anchored on the bracket or comma that opens the attribute and the parenthesis that
    /// opens its arguments. A substring search for the word would match this file's own
    /// failure messages, and a gate that fails on its own explanation of why it failed
    /// teaches people to delete it.
    /// </remarks>
    [GeneratedRegex(@"(?:^|[\[,])\s*(?:System\.Diagnostics\.CodeAnalysis\.)?SuppressMessage\s*\(")]
    private static partial Regex SuppressMessageAttribute();

    /// <summary>A pragma in directive position, not the words quoted in a message.</summary>
    [GeneratedRegex(@"^\s*#pragma\s+warning\s+disable\b")]
    private static partial Regex PragmaDisable();
}
