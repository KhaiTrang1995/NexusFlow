using System.Globalization;
using Shouldly;
using Xunit;

namespace FlowX.Architecture.Tests;

/// <summary>
/// Every identifier this repository allocates by convention — an ADR number, a
/// <c>FLOWX</c> diagnostic id, a work-package number — is allocated exactly once.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The convention has failed four times, and the fourth is why this is code rather
/// than another paragraph.</strong> Two diagnostics were authored against
/// <c>FLOWX1028</c> in separate branches; work executed after P1 took WP-49 and WP-70 to
/// WP-73, which §6 had reserved for P3's transports; endpoint generation was executed as
/// WP-74, which means Azure Service Bus; and on 2026-07-31 two records were both authored
/// as <c>ADR-0017</c> in separate worktrees. In the last one <em>both authors followed the
/// rule</em>: each claimed the number in the index first, in its own commit, exactly as
/// instructed. They claimed it from the same base commit, so neither claim was visible to
/// the other. A claim-first rule serialises nothing when the claimants branch from one
/// commit, and re-teaching it a fifth time would produce a fifth collision.
/// </para>
/// <para>
/// <strong>What these gates catch: a duplicate that is present in one working tree.</strong>
/// That is the moment two claims meet in a single checkout — a merge, a rebase onto a branch
/// that took the number first, or one author writing both halves. All four collisions were
/// found by a human reading a merge; what was missing was a check that read it instead.
/// </para>
/// <para>
/// <strong>What they do not catch, stated so that nobody relies on it: a duplicate that
/// exists only across two unmerged branches.</strong> A test sees the tree it was built
/// from. Asking git about the other claim would need the other ref to be present — CI
/// clones one branch, and "which branches are live" is not a fact on disk anywhere — so the
/// answer would be right on a developer's machine and vacuous in the build that matters. A
/// gate is worth having only where it means the same thing everywhere it runs, so these
/// report at the merge and make no claim about the moment before it.
/// </para>
/// <para>
/// They also say nothing about <em>references</em> to an identifier from documents that are
/// not its index. A cross-reference carrying the wrong number is a real defect and a
/// different one: it is a stale pointer, not a second allocation, and the fix is to correct
/// the reference rather than to renumber anything.
/// </para>
/// </remarks>
public sealed class IdentifierAllocationTests
{
    /// <summary>
    /// One number, one record: in <c>docs/adr/</c> and in the index that allocates from it.
    /// </summary>
    /// <remarks>
    /// Both halves are checked because a collision shows up in whichever half the two
    /// authors edited. Two files claiming one number never conflicts in git at all — the
    /// file names differ — and two rows added at different points in the table merge
    /// cleanly into a table that lists the number twice.
    /// </remarks>
    [Fact]
    public void NoAdrNumberIsAllocatedTwice()
    {
        var records = IdentifierIndexes.AdrRecords();
        var rows = IdentifierIndexes.AdrIndexRows();
        var problems = new List<string>();

        records.ShouldNotBeEmpty("No record was found in docs/adr/. A gate handed nothing to inspect passes for the wrong reason.");
        rows.ShouldNotBeEmpty($"No row was read out of {IdentifierIndexes.AdrIndex}. The index is the allocator; an unreadable one allocates nothing.");

        problems.AddRange(Duplicates(
            records,
            static record => record.Number,
            static record => record.Path,
            "docs/adr/"));

        problems.AddRange(Duplicates(
            rows,
            static row => row.Id,
            static row => row.Where,
            IdentifierIndexes.AdrIndex));

        problems.ShouldBeEmpty(Report(
            "An ADR number allocated twice is two decisions with one name. Every reference " +
            "to it in the repository becomes ambiguous, and one of the two records has to be " +
            "renumbered after it has already been read and cited. Take the next free number " +
            $"from the bottom of {IdentifierIndexes.AdrIndex} and renumber the later record:",
            problems));
    }

    /// <summary>
    /// The record on disk, its own heading and its index row all name the same number.
    /// </summary>
    /// <remarks>
    /// This is the half that catches a renumbering done in a hurry. When the second
    /// <c>ADR-0017</c> became <c>ADR-0018</c> the file was renamed, the heading rewritten and
    /// the row repointed — three edits, in three places, with nothing checking that all three
    /// happened. A record whose heading still claims the number it was renumbered out of is
    /// the same collision, just quieter.
    /// </remarks>
    [Fact]
    public void EveryAdrRecordAndItsIndexRowAgreeOnItsNumber()
    {
        var records = IdentifierIndexes.AdrRecords();
        var rows = IdentifierIndexes.AdrIndexRows();
        var problems = new List<string>();

        problems.AddRange(Missing(
            records.Select(static record => record.Number),
            rows.Select(static row => row.Id),
            $"is a record in docs/adr/ with no row in {IdentifierIndexes.AdrIndex}"));

        problems.AddRange(Missing(
            rows.Select(static row => row.Id),
            records.Select(static record => record.Number),
            $"has a row in {IdentifierIndexes.AdrIndex} and no record in docs/adr/"));

        problems.AddRange(records
            .Where(static record => record.HeadingNumber != record.Number)
            .Select(static record =>
                $"{record.Path} is named ADR-{record.Number} and its heading says " +
                $"ADR-{record.HeadingNumber ?? "nothing"}."));

        problems.AddRange(rows
            .Where(static row => row.Target?.StartsWith($"ADR-{row.Id}-", StringComparison.Ordinal) != true)
            .Select(static row => $"{row.Where} numbers a row {row.Id} and links to {row.Target}."));

        problems.ShouldBeEmpty(Report(
            "A record, its heading and its index row are three places one number is written, " +
            "and a renumbering that reaches two of them leaves the third pointing at a " +
            "decision that no longer holds the number:",
            problems));
    }

    /// <summary>
    /// One <c>FLOWX</c> id, one rule: in the catalogue, in the reservations, and in the
    /// release-tracking file that makes the id public surface.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The catalogue and the reservation table are checked against each other as well as
    /// against themselves. An id that is both raised and reserved is allocated twice in the
    /// way that matters most: the index's own instruction is that a new rule takes the next
    /// id above the catalogue and <em>never</em> a reserved one, so an id sitting in both
    /// lists is an invitation to the next author to take it a third time.
    /// </para>
    /// <para>
    /// The descriptors in <c>FlowXDiagnostics</c> are not re-read here.
    /// <c>DiagnosticIdsAreUnique</c> in <c>FlowX.Compiler.Tests</c> already holds them to
    /// this rule, and <c>EveryDiagnosticIsReleaseTracked</c> ties each descriptor to the
    /// release-tracking file this gate then ties to the catalogue. Duplicating either would
    /// give one rule two implementations to keep in step by hand.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoDiagnosticIdIsAllocatedTwice()
    {
        var catalogue = IdentifierIndexes.DiagnosticCatalogue();
        var reserved = IdentifierIndexes.DiagnosticReservations();
        var problems = new List<string>();

        catalogue.ShouldNotBeEmpty(
            $"No catalogue row was read out of {IdentifierIndexes.DiagnosticIndex}. That table " +
            "is where an author reads which ids are taken, so a gate that cannot read it is " +
            "not checking anything.");

        problems.AddRange(Duplicates(catalogue, Id, Where, $"{IdentifierIndexes.DiagnosticIndex}'s catalogue"));
        problems.AddRange(Duplicates(reserved, Id, Where, $"{IdentifierIndexes.DiagnosticIndex}'s reservations"));
        problems.AddRange(Duplicates(IdentifierIndexes.DiagnosticReleaseRows(), Id, Where, "analyzer release tracking"));

        problems.AddRange(catalogue
            .Join(reserved, Id, Id, static (row, reservation) =>
                $"{row.Id} is catalogued at {row.Where} and reserved at {reservation.Where}.")
            .Order(StringComparer.Ordinal));

        problems.ShouldBeEmpty(Report(
            "A diagnostic id allocated twice is two rules teams cannot tell apart: a " +
            "suppression written against one silences the other, and the id is public " +
            "surface, so the mistake is not repairable by renaming later. Claim the id the " +
            $"end of {IdentifierIndexes.DiagnosticIndex} names as next, and raise that:",
            problems));
    }

    /// <summary>
    /// The four places a <c>FLOWX</c> id can be allocated hold the same set of ids.
    /// </summary>
    /// <remarks>
    /// An id that exists on one surface and not another is what makes the next collision
    /// possible rather than merely being untidy: the author who reads the catalogue to find
    /// the next free number does not see the page, the release row or the reservation that
    /// already spoke for it.
    /// </remarks>
    [Fact]
    public void EveryDiagnosticSurfaceAgreesOnWhichIdsAreAllocated()
    {
        var catalogue = IdentifierIndexes.DiagnosticCatalogue();
        var pages = IdentifierIndexes.DiagnosticPages();
        var released = IdentifierIndexes.DiagnosticReleaseRows();
        var problems = new List<string>();

        pages.ShouldNotBeEmpty("No page was found in docs/diagnostics/. Four surfaces that all read as empty agree with each other about nothing.");

        problems.AddRange(Missing(catalogue.Select(Id), pages.Select(Id), "is catalogued and has no page"));
        problems.AddRange(Missing(pages.Select(Id), catalogue.Select(Id), "has a page and no catalogue row"));
        problems.AddRange(Missing(catalogue.Select(Id), released.Select(Id), "is catalogued and not release-tracked"));
        problems.AddRange(Missing(released.Select(Id), catalogue.Select(Id), "is release-tracked and not catalogued"));

        problems.AddRange(catalogue
            .Where(static row => row.Target != $"{row.Id}.md")
            .Select(static row => $"{row.Where} numbers a row {row.Id} and links to {row.Target}."));

        problems.ShouldBeEmpty(Report(
            "A diagnostic id is allocated across four surfaces at once — the catalogue, the " +
            "page, the release-tracking file and the reservation list — and the next author " +
            "reads only one of them. An id present on some of them and absent from the rest " +
            "is the state the next collision starts from:",
            problems));
    }

    /// <summary>
    /// The next free id the diagnostics index advertises is genuinely free, and above
    /// everything already taken.
    /// </summary>
    /// <remarks>
    /// This is the rule that catches the <c>FLOWX1028</c> shape once the two branches meet.
    /// Two authors reading "the next is <c>FLOWX1031</c>" both take it and both advance the
    /// pointer; the merged tree then holds two claims on one id and a pointer that is stale
    /// against at least one of them. It is also the only rule here that fails on a claim
    /// made <em>correctly but not written down</em> — an id raised without moving the
    /// pointer leaves the pointer aimed at an id somebody is already using.
    /// </remarks>
    [Fact]
    public void TheNextFreeDiagnosticIdIsAboveEveryIdAlreadyAllocated()
    {
        var range = IdentifierIndexes.DiagnosticRange();

        range.ShouldNotBeNull(
            $"{IdentifierIndexes.DiagnosticIndex} no longer states which id is next and which " +
            "range ids come from. That sentence is the allocator; without it there is nothing " +
            "for the next author to read and nothing for this gate to check.");

        var allocated = IdentifierIndexes.DiagnosticCatalogue()
            .Concat(IdentifierIndexes.DiagnosticReservations())
            .Concat(IdentifierIndexes.DiagnosticReleaseRows())
            .Concat(IdentifierIndexes.DiagnosticPages())
            .ToList();

        var problems = allocated
            .Where(allocation => Ordinal(allocation.Id) < Ordinal(range.First)
                || Ordinal(allocation.Id) > Ordinal(range.Last))
            .Select(allocation =>
                $"{allocation.Id} at {allocation.Where} is outside the declared range " +
                $"{range.First}–{range.Last}.")
            .ToList();

        problems.AddRange(allocated
            .Where(allocation => Ordinal(allocation.Id) >= Ordinal(range.Next))
            .Select(allocation =>
                $"{allocation.Id} at {allocation.Where} is already allocated, and the index " +
                $"says the next free id is {range.Next}.")
            .Order(StringComparer.Ordinal));

        problems.ShouldBeEmpty(Report(
            "The next free id is the one sentence an author reads before writing a rule. A " +
            "pointer that names an id somebody already took hands the same number to the " +
            "next two people who read it, which is exactly how two rules came to be authored " +
            "as FLOWX1028. Advance it in the same commit that claims an id:",
            problems));
    }

    /// <summary>
    /// One work-package number, one package, across every table and heading in the plan.
    /// </summary>
    /// <remarks>
    /// A definition site is a section heading or a row of a phase's package table — the
    /// places a package is <em>declared</em>, as opposed to the many places one is
    /// referenced. A heading may declare a span, and every number in the span counts as
    /// declared by it.
    /// </remarks>
    [Fact]
    public void NoWorkPackageNumberIsAllocatedTwice()
    {
        var packages = IdentifierIndexes.WorkPackages();

        packages.ShouldNotBeEmpty(
            $"No work package could be read out of {IdentifierIndexes.Plan}. The plan is the " +
            "only authority for these numbers, so a gate that reads none of them is not " +
            "checking anything.");

        var problems = Duplicates(
            packages,
            static package => $"WP-{package.Token}",
            static package => package.Where,
            IdentifierIndexes.Plan).ToList();

        problems.ShouldBeEmpty(Report(
            "A work-package number naming two packages makes the plan unreadable as a record " +
            "of what was done: the checklist, the commit messages and the branch names all " +
            $"point at a number that means two things. Claim the next free number in " +
            $"{IdentifierIndexes.Plan} before the work starts:",
            problems));
    }

    /// <summary>
    /// Every work package sits inside the range its phase reserved.
    /// </summary>
    /// <remarks>
    /// This is the rule the WP-70 to WP-74 collisions needed. None of them duplicated a
    /// number that had already been <em>used</em> — they took numbers a later phase had
    /// <em>reserved</em> and had not spent yet, which no check on duplicates can see. The
    /// reservation is the allocation; spending outside it is the failure.
    /// </remarks>
    [Fact]
    public void EveryWorkPackageIsInsideItsPhasesReservedRange()
    {
        var ranges = IdentifierIndexes.ReservedPhaseRanges();
        var problems = new List<string>();

        foreach (var package in IdentifierIndexes.WorkPackages())
        {
            var range = ranges.FirstOrDefault(candidate => candidate.Phase == package.Phase);

            if (package.Phase is null)
            {
                problems.Add($"WP-{package.Token} at {package.Where} is defined outside any phase's section.");
            }
            else if (range is null)
            {
                problems.Add($"WP-{package.Token} at {package.Where} belongs to {package.Phase}, which reserves nothing.");
            }
            else if (package.Number < range.First || package.Number > range.Last)
            {
                problems.Add(
                    $"WP-{package.Token} at {package.Where} is in {package.Phase}'s section, and " +
                    $"{package.Phase} reserves WP-{range.First} to WP-{range.Last}.");
            }
        }

        problems.ShouldBeEmpty(Report(
            "A phase's reserved range is where its numbers are allocated from, and a package " +
            "outside it has taken a number another phase is holding. That is how WP-70 to " +
            "WP-74 were spent twice — once by the phase that reserved them and once by work " +
            "that read the next free number and not the reservation:",
            problems));
    }

    /// <summary>
    /// The reserved ranges themselves are an allocation, and no two phases hold the same
    /// numbers.
    /// </summary>
    /// <remarks>
    /// The ranges are read from two sections that have to meet exactly: §2 says where the
    /// phases with packages begin, §6a's table reserves the spans for the phases that have
    /// none yet. An overlap between them would make the rule above unfalsifiable, because a
    /// number could be inside two phases at once and outside neither.
    /// </remarks>
    [Fact]
    public void TheReservedWorkPackageRangesDoNotOverlap()
    {
        var ranges = IdentifierIndexes.ReservedPhaseRanges();

        ranges.ShouldNotBeEmpty(
            $"No reserved range was read out of {IdentifierIndexes.Plan}. §2 says where the " +
            "phases with packages begin and §6a reserves the spans for the ones without; if " +
            "neither can be read, nothing is allocating these numbers.");

        ranges.Select(static range => range.Phase).Distinct(StringComparer.Ordinal).Count().ShouldBe(
            ranges.Count,
            $"A phase reserves its numbers once. {IdentifierIndexes.Plan} reserves a range for " +
            "the same phase twice, so a package can be inside its phase's range and outside " +
            "it at the same time.");

        var problems = ranges
            .Zip(ranges.Skip(1), static (earlier, later) => (earlier, later))
            .Where(static pair => pair.later.First <= pair.earlier.Last)
            .Select(static pair =>
                $"{pair.earlier.Phase} reserves WP-{pair.earlier.First} to WP-{pair.earlier.Last} " +
                $"({pair.earlier.Where}) and {pair.later.Phase} reserves from WP-{pair.later.First} " +
                $"({pair.later.Where}).")
            .ToList();

        problems.ShouldBeEmpty(Report(
            "Two phases reserving the same work-package numbers is the collision one level " +
            "up: whichever phase spends the number first takes it from the other, and the " +
            "range check cannot report it because the number is legitimately inside a " +
            "reservation:",
            problems));
    }

    // ------------------------------------------------------------------ helpers

    private static string Id(Allocation allocation) => allocation.Id;

    private static string Where(Allocation allocation) => allocation.Where;

    /// <summary>Ids claimed more than once on one surface, with every place each was claimed.</summary>
    private static IEnumerable<string> Duplicates<T>(
        IEnumerable<T> claims,
        Func<T, string> id,
        Func<T, string> where,
        string surface) =>
        claims
            .GroupBy(id, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(group =>
                $"{group.Key} is allocated {group.Count()} times in {surface}: " +
                $"{string.Join(", ", group.Select(where))}.");

    /// <summary>Ids present in the first set and absent from the second.</summary>
    private static IEnumerable<string> Missing(
        IEnumerable<string> expected,
        IEnumerable<string> actual,
        string complaint) =>
        expected
            .Except(actual, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(id => $"{id} {complaint}.");

    private static int Ordinal(string diagnosticId) =>
        int.Parse(diagnosticId[5..], CultureInfo.InvariantCulture);

    private static string Report(string headline, IReadOnlyList<string> problems) =>
        headline + Environment.NewLine + string.Join(Environment.NewLine, problems);
}
