using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace FlowX.Architecture.Tests;

/// <summary>
/// Constraint C6 — <em>Apache-2.0, no copyleft dependencies</em> — made executable, against the
/// definition of "compatible" that <see href="../../docs/adr/ADR-0012-apache-2-license.md">
/// ADR-0012</see> states rather than one invented here.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is a fitness function and not a CI job.</strong> The obvious shape for a
/// licence scan is a workflow step: <c>dotnet list package --include-transitive</c>, then a
/// lookup against a licence service. The reason it is not that here is that the two things such
/// a job would fetch are already on disk after <c>dotnet restore</c>, and reading them locally
/// is strictly better on three counts. NuGet writes the fully resolved transitive graph to
/// <c>obj/project.assets.json</c>, so "sees only what is declared" — the usual and correct
/// objection to a static read — is simply not true of this one: it sees the same closure the
/// CI job would see. The graph names the folder holding every restored package, and each
/// package's <c>.nuspec</c> carries the licence the publisher asserted, which is the document a
/// redistributor is bound by rather than what a third-party index believes about it. And a
/// developer meets the failure on <c>dotnet test</c>, before the commit, rather than on a red
/// tick twenty minutes later.
/// </para>
/// <para>
/// <strong>What the CI job is still for.</strong> One thing, and it is small enough to name
/// exactly: restoring the projects that are not in <c>FlowX.slnx</c>, so that their transitive
/// closure exists to be read at all. <c>ci.yml</c> does that immediately before running these
/// gates. It is not a second implementation of the rule — this repository has already deleted
/// one of those (see <see cref="DebtAccountabilityTests"/> and the comment on the <c>debt</c>
/// job in <c>quality.yml</c>: two gates for one rule disagree eventually, and the weaker one is
/// what a developer meets first). It widens this gate's input; it does not re-decide anything.
/// </para>
/// <para>
/// <strong>What this gate cannot see</strong> is written down in docs/DEPENDENCIES.md §3 rather
/// than only here, because a limitation a reviewer has to open a test file to discover is one
/// most reviewers will not discover. In short: a <c>.nuspec</c> that misstates its own licence;
/// licences of code vendored inside a package; the closure of an unrestored project; anything
/// that is not a NuGet <c>PackageReference</c> — the shared framework, and the npm, pip and
/// <c>dotnet tool</c> packages CI installs; dual-licensed packages, which have no
/// representation in the register and fail as unclassified; and every obligation a licence
/// imposes other than the redistribution question, attribution and NOTICE files included.
/// </para>
/// <para>
/// <c>AbstractionsHasNoDependencies</c> is the nearest relative and is <em>not</em> this gate.
/// ADR-0012 says so itself: it "proves the core has nothing to scan, which is not the same
/// claim". One project having no dependencies says nothing about the ninety-nine packages the
/// rest of the solution resolves.
/// </para>
/// </remarks>
public sealed class DependencyLicenceTests
{
    private const string Register = "docs/DEPENDENCIES.md";

    /// <summary>
    /// Every package this repository declares or resolves carries a licence Apache-2.0
    /// redistribution permits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written as one test over two readings. <em>Declared</em> catches a dependency the moment
    /// somebody adds it, in every tree including the ones that are never restored;
    /// <em>resolved</em> is the real transitive closure, which is what constraint C6's stated
    /// implication — "vets every transitive dependency" — actually asks for. A package failing
    /// either way is the same failure and should be reported once.
    /// </para>
    /// <para>
    /// The single exception to "permissive or nothing" is narrow and is not a human assertion:
    /// a <c>restricted</c> licence is tolerated only where the resolved graph shows the package
    /// contributing no compile-time and no run-time assembly — an analyzer, or a targeting-pack
    /// placeholder whose only asset is <c>_._</c>. Two packages use it today and both are
    /// surprising enough to be worth naming: <c>SonarAnalyzer.CSharp</c> is under the SONAR
    /// Source-Available Licence, and <c>Microsoft.NETCore.Platforms</c> 1.1.0 is under the
    /// proprietary MICROSOFT .NET LIBRARY terms rather than MIT. Neither reaches a consumer.
    /// </para>
    /// </remarks>
    [Fact]
    public void DependencyLicencesAreCompatible()
    {
        var register = LicenceRegister.Load();
        var resolved = ResolvedById();
        var declared = DeclaredById();
        var problems = new List<string>();

        foreach (var id in declared.Keys.Concat(resolved.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase))
        {
            problems.AddRange(Inspect(
                register,
                id,
                Origin(id, declared, resolved),
                // A package nobody resolved cannot be shown to contribute nothing, so it is
                // judged as though it does. That is the safe direction for the one case it
                // arises in: a dependency of a project outside FlowX.slnx (§5 of the register).
                contributesAssemblies: !resolved.TryGetValue(id, out var contributes) || contributes,
                declaredPrivately: IsDeclaredPrivately(id)));
        }

        LicenceSurvey.Resolved.Count.ShouldBeGreaterThan(
            0,
            "No resolved package graph was found. Restore the solution before running the " +
            "architecture gates — a licence scan with nothing to scan is not a passing gate.");

        problems.ShouldBeEmpty(
            "A dependency carries a licence this project cannot redistribute under Apache-2.0, " +
            $"or one nobody has classified (constraint C6, ADR-0012). {Register} says what to do:" +
            Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// Every row in the register still agrees with what the package says about itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this, the register is a list of licences somebody once believed, and a version
    /// bump that changes a licence — which happens, and is exactly the event this constraint
    /// exists to catch — would pass silently. The register records no versions on purpose (a
    /// table edited by every weekly Dependabot PR is a table nobody reads); this check is what
    /// makes that safe, because it re-reads the licence of whatever version is actually
    /// restored, every run.
    /// </para>
    /// <para>
    /// A <c>read</c> row is the case a machine cannot settle: the package declares no SPDX
    /// expression, only a licence file or a deprecated <c>licenseUrl</c>, so a person read the
    /// prose. That verdict is still held to something — the named file must still exist inside
    /// the package and still contain the classification's fingerprint. And a <c>read</c> row is
    /// rejected once the package starts declaring an expression, so a hand-read verdict cannot
    /// outlive the reason it was needed.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheLicenceRegisterMatchesWhatThePackagesDeclare()
    {
        var register = LicenceRegister.Load();
        var problems = new List<string>();
        var verified = 0;

        foreach (var (id, version) in RestoredPackages())
        {
            if (!register.Packages.TryGetValue(id, out var row))
            {
                continue;   // DependencyLicencesAreCompatible reports the missing row.
            }

            verified++;
            problems.AddRange(Verify(register, row, id, version));
        }

        problems.AddRange(VerifyFirstPartyRows(register));

        verified.ShouldBeGreaterThan(
            0,
            $"Not one package in {Register} could be checked against its own .nuspec. The " +
            "packages are not restored, so this gate is comparing the register with nothing.");

        problems.ShouldBeEmpty(
            $"{Register} disagrees with what a package declares about itself. A register that " +
            "is not re-read is a list of licences somebody once believed:" +
            Environment.NewLine + string.Join(Environment.NewLine, problems.Order(StringComparer.Ordinal)));
    }

    /// <summary>
    /// The register names no package this repository does not use.
    /// </summary>
    /// <remarks>
    /// A hand-maintained list needs a rule about the list — the same reasoning as
    /// <c>EverySourceProjectIsCoveredByTheLayeringRule</c>, and the same one-way check. A stale
    /// row cannot hide a dependency, but it can make the register look like a considered
    /// inventory when half of it describes packages that left years ago, and the next reader
    /// then trusts the other half more than it deserves.
    /// <para>
    /// The licence table of §1.1 is deliberately exempt: it classifies licences nothing here
    /// carries, including every copyleft licence, because that is the definition ADR-0012 gives
    /// and a definition that names only what is already present is a description.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheLicenceRegisterHasNoRowsNothingReferences()
    {
        var register = LicenceRegister.Load();

        var used = LicenceSurvey.Declared.Select(static p => p.Id)
            .Concat(LicenceSurvey.Resolved.Select(static p => p.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var orphans = register.Packages.Keys
            .Where(id => !used.Contains(id))
            .Order(StringComparer.Ordinal)
            .ToList();

        orphans.ShouldBeEmpty(
            $"[{string.Join(", ", orphans)}] has a row in {Register} and is referenced by " +
            "nothing. Remove the row — an inventory half of which describes packages that are " +
            "gone is one nobody can tell the live half of.");
    }

    /// <summary>
    /// No project falls outside both readings, and the ones this gate can only half-see are the
    /// ones the register says they are.
    /// </summary>
    /// <remarks>
    /// This is the gate's own coverage, asserted rather than assumed. A project with no
    /// <c>obj/project.assets.json</c> has its declared references checked and its transitive
    /// closure unchecked, which is a real hole — small today, and only stays small if adding a
    /// fourth out-of-solution project is a failure that says so. Checked as a subset because CI
    /// restores more than a local build does, so the honest direction is "nothing outside the
    /// list", not "exactly the list".
    /// </remarks>
    [Fact]
    public void EveryProjectIsCoveredByTheLicenceGate()
    {
        var register = LicenceRegister.Load();

        LicenceSurvey.Projects.Count.ShouldBeGreaterThan(
            0, "No project files were found at all, so this gate inspected nothing.");

        var unexpected = LicenceSurvey.ProjectsWithoutAssets
            .Where(path => !register.UnvettedProjects.Contains(path))
            .Order(StringComparer.Ordinal)
            .ToList();

        unexpected.ShouldBeEmpty(
            $"[{string.Join(", ", unexpected)}] has no obj/project.assets.json, so only its " +
            "declared references are vetted and whatever they drag in behind them is not. " +
            $"Either restore it before the gates run, or record it in {Register} §5 with the " +
            "reason it is outside the solution.");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Package id to whether any project resolves a real assembly from it.</summary>
    private static Dictionary<string, bool> ResolvedById() =>
        LicenceSurvey.Resolved
            .GroupBy(static p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static g => g.Key,
                static g => g.Any(static p => p.ContributesAssemblies),
                StringComparer.OrdinalIgnoreCase);

    /// <summary>Package id to the project files that name it.</summary>
    private static Dictionary<string, List<string>> DeclaredById() =>
        LicenceSurvey.Declared
            .GroupBy(static p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static g => g.Key,
                static g => g.Select(static p => p.DeclaredIn)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);

    /// <summary>Where a failure message should send the reader to remove the dependency.</summary>
    private static string Origin(
        string id,
        Dictionary<string, List<string>> declared,
        Dictionary<string, bool> resolved)
    {
        if (declared.TryGetValue(id, out var files))
        {
            return $"declared by {string.Join(", ", files)}";
        }

        var projects = LicenceSurvey.Resolved
            .Where(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            .Select(static p => p.Project)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        return resolved.ContainsKey(id)
            ? $"pulled in transitively by {projects.Count} project(s), including {projects[0]}"
            : "origin unknown";
    }

    /// <summary>
    /// Whether every declaration of this package keeps it out of the packed <c>.nuspec</c>.
    /// </summary>
    /// <remarks>
    /// True for a package nobody declares — it is transitive, and a transitive package's
    /// presence in a dependency group is decided by the package that pulls it in, not here.
    /// The distinction matters only for the <c>restricted</c> verdict: contributing no assembly
    /// is not on its own enough to say a package does not reach a consumer, because a
    /// <c>PackageReference</c> without <c>PrivateAssets="all"</c> becomes a dependency of every
    /// package packed from that project whether it carries an assembly or not.
    /// </remarks>
    private static bool IsDeclaredPrivately(string id) =>
        LicenceSurvey.Declared
            .Where(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            .All(static p => p.Private);

    private static IEnumerable<string> Inspect(
        LicenceRegister register,
        string id,
        string where,
        bool contributesAssemblies,
        bool declaredPrivately)
    {
        if (!register.Packages.TryGetValue(id, out var row))
        {
            yield return
                $"{id} ({where}) has no row in {Register}. Read its licence out of the restored " +
                ".nuspec and add one; a dependency nobody vetted is one nobody can vouch for.";

            yield break;
        }

        if (!register.Licences.TryGetValue(row.Licence, out var licence))
        {
            yield return
                $"{id} is recorded as {row.Licence}, which {Register} §1.1 does not classify. " +
                "Classify it there — against ADR-0012's definition, not a guess.";

            yield break;
        }

        if (licence.Verdict == "forbidden")
        {
            yield return
                $"{id} ({where}) is {row.Licence}, which is copyleft. ADR-0012: \"no GPL/AGPL " +
                "dependencies, ever, even transitively\". There is no exception for this and " +
                "no debt entry can buy one — the answer is a different package.";
        }
        else if (licence.Verdict == "restricted" && contributesAssemblies)
        {
            yield return
                $"{id} ({where}) is {row.Licence}, which is not redistributable on permissive " +
                "terms, and it contributes a real assembly rather than only an analyzer or an " +
                "'_._' placeholder — so it reaches whoever restores a FlowX package. " +
                $"See {Register} §2.";
        }
        else if (licence.Verdict == "restricted" && !declaredPrivately)
        {
            yield return
                $"{id} ({where}) is {row.Licence}, which is not redistributable on permissive " +
                "terms. It contributes no assembly, but it is declared without " +
                "PrivateAssets=\"all\", so NuGet writes it into the dependency group of every " +
                "package packed from that project and a consumer restores it anyway. Add " +
                $"PrivateAssets=\"all\" — {Register} §2 is the whole reason it is tolerated.";
        }
    }

    private static IEnumerable<string> Verify(
        LicenceRegister register,
        RegisteredPackage row,
        string id,
        string version)
    {
        var declared = LicenceSurvey.NuspecLicenceExpression(id, version);

        if (row.Determined == "nuspec")
        {
            if (declared is null)
            {
                yield return
                    $"{id} {version} is recorded as read from its .nuspec, but the .nuspec " +
                    "declares no SPDX expression. Read the licence file it points at instead " +
                    "and change the row to `read`, citing that file.";
            }
            else if (!declared.Equals(row.Licence, StringComparison.OrdinalIgnoreCase))
            {
                yield return
                    $"{id} {version} declares '{declared}' and {Register} says '{row.Licence}'. " +
                    "The package's licence changed under us — re-vet it before updating the row.";
            }

            yield break;
        }

        foreach (var problem in VerifyReadRow(register, row, id, version, declared))
        {
            yield return problem;
        }
    }

    private static IEnumerable<string> VerifyReadRow(
        LicenceRegister register,
        RegisteredPackage row,
        string id,
        string version,
        string? declared)
    {
        if (row.Determined != "read")
        {
            yield break;   // `first-party` rows are checked against Directory.Build.props.
        }

        if (declared is not null)
        {
            yield return
                $"{id} {version} now declares '{declared}' in its .nuspec, so the hand-read " +
                $"row in {Register} is obsolete. Change it to `nuspec` and drop the evidence.";

            yield break;
        }

        var evidence = LicenceSurvey.PackageFile(id, version, row.Evidence);

        if (evidence is null)
        {
            yield return
                $"{id} {version} is recorded as hand-read from '{row.Evidence}', and that file " +
                "is not in the package. The verdict rests on a document nobody can open.";

            yield break;
        }

        var fingerprint = register.Licences[row.Licence].Fingerprint;

        if (!File.ReadAllText(evidence.FullName).Contains(fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            yield return
                $"{id} {version}: '{row.Evidence}' no longer contains \"{fingerprint}\", so it " +
                $"is no longer the {row.Licence} text somebody read. Read it again.";
        }
    }

    /// <summary>
    /// The packages built from this repository are Apache-2.0 because
    /// <c>Directory.Build.props</c> says so, and the register must not disagree with it.
    /// </summary>
    /// <remarks>
    /// A first-party package has no third-party <c>.nuspec</c> to consult — it is whatever this
    /// build produces. The template's <c>content/</c> project references five of them by name,
    /// so they need rows, and the only honest source for those rows is the property that
    /// stamps the licence onto every package here.
    /// </remarks>
    private static IEnumerable<string> VerifyFirstPartyRows(LicenceRegister register)
    {
        var licence = LicenceSurvey.FirstPartyLicence();

        foreach (var row in register.Packages.Values.Where(static r => r.Determined == "first-party"))
        {
            if (!string.Equals(licence, row.Licence, StringComparison.Ordinal))
            {
                yield return
                    $"{row.Id} is recorded as {row.Licence}, but Directory.Build.props stamps " +
                    $"'{licence ?? "nothing"}' onto every package this repository builds. " +
                    "ADR-0012 is the decision; one of these two has drifted from it.";
            }
        }
    }

    /// <summary>Distinct id/version pairs that are actually on disk to be read.</summary>
    private static IEnumerable<(string Id, string Version)> RestoredPackages() =>
        LicenceSurvey.Resolved
            .Select(static p => (p.Id, p.Version))
            .Distinct()
            .Where(static p => LicenceSurvey.IsRestored(p.Id, p.Version))
            .OrderBy(static p => p.Id, StringComparer.Ordinal);
}

/// <summary>
/// docs/DEPENDENCIES.md, parsed.
/// </summary>
/// <remarks>
/// The register is a document rather than a C# array for the same reason docs/DEBT.md is: the
/// people who need to read a licence inventory are not all people who read test code, and a
/// legal review that has to be run past a reviewer as a source file is a review that does not
/// happen. The gate parses table rows, so the prose around them is free to explain itself.
/// </remarks>
internal sealed partial class LicenceRegister
{
    private LicenceRegister(
        IReadOnlyDictionary<string, RegisteredLicence> licences,
        IReadOnlyDictionary<string, RegisteredPackage> packages,
        IReadOnlySet<string> unvettedProjects)
    {
        Licences = licences;
        Packages = packages;
        UnvettedProjects = unvettedProjects;
    }

    /// <summary>§1.1: what each licence id means for this project.</summary>
    public IReadOnlyDictionary<string, RegisteredLicence> Licences { get; }

    /// <summary>§4: one row per package, keyed case-insensitively as NuGet ids are.</summary>
    public IReadOnlyDictionary<string, RegisteredPackage> Packages { get; }

    /// <summary>§5: the projects whose transitive closure this gate does not read.</summary>
    public IReadOnlySet<string> UnvettedProjects { get; }

    public static LicenceRegister Load()
    {
        var file = new FileInfo(Path.Combine(
            RepositoryLayout.Root.FullName, "docs", "DEPENDENCIES.md"));

        file.Exists.ShouldBeTrue(
            "docs/DEPENDENCIES.md is missing. Without the register there is nothing for a " +
            "dependency's licence to be accountable to, and constraint C6 is back to being an " +
            "observation about today's dependency set.");

        var lines = File.ReadAllLines(file.FullName);

        var licences = Match(lines, LicenceRow())
            .ToDictionary(
                static m => m.Groups["licence"].Value,
                static m => new RegisteredLicence(
                    m.Groups["licence"].Value,
                    m.Groups["verdict"].Value,
                    m.Groups["fingerprint"].Value.Trim()),
                StringComparer.OrdinalIgnoreCase);

        var packages = Match(lines, PackageRow())
            .ToDictionary(
                static m => m.Groups["id"].Value,
                static m => new RegisteredPackage(
                    m.Groups["id"].Value,
                    m.Groups["licence"].Value,
                    m.Groups["determined"].Value,
                    m.Groups["evidence"].Value.Trim().Trim('`', '—').Trim()),
                StringComparer.OrdinalIgnoreCase);

        var unvetted = Match(lines, UnvettedProjectRow())
            .Select(static m => m.Groups["path"].Value)
            .ToHashSet(StringComparer.Ordinal);

        return new LicenceRegister(licences, packages, unvetted);
    }

    private static IEnumerable<Match> Match(string[] lines, Regex pattern) =>
        lines.Select(line => pattern.Match(line)).Where(static m => m.Success);

    /// <summary>A §1.1 row: a backticked licence id, a bare verdict, a fingerprint.</summary>
    [GeneratedRegex(
        @"^\|\s*`(?<licence>[^`]+)`\s*\|\s*(?<verdict>permissive|restricted|forbidden)\s*\|(?<fingerprint>[^|]*)\|")]
    private static partial Regex LicenceRow();

    /// <summary>
    /// A §4 row: a backticked package id, a backticked licence id, and how it was determined.
    /// </summary>
    /// <remarks>
    /// Both of the first two cells must be <em>entirely</em> backticked, which is what keeps
    /// this off the summary tables in §4.1 and §5 — those put prose in the second column, and a
    /// looser pattern would read "none, by gate" as a licence id.
    /// </remarks>
    [GeneratedRegex(
        @"^\|\s*`(?<id>[^`]+)`\s*\|\s*`(?<licence>[^`]+)`\s*\|\s*(?<determined>nuspec|read|first-party)\s*\|(?<evidence>[^|]*)\|")]
    private static partial Regex PackageRow();

    /// <summary>A §5 row: a backticked repository-relative path to a project file.</summary>
    [GeneratedRegex(@"^\|\s*`(?<path>[^`]+\.csproj)`\s*\|")]
    private static partial Regex UnvettedProjectRow();
}

/// <summary>One row of docs/DEPENDENCIES.md §1.1.</summary>
/// <param name="Id">The SPDX (or, where none exists, locally coined) licence id.</param>
/// <param name="Verdict"><c>permissive</c>, <c>restricted</c> or <c>forbidden</c>.</param>
/// <param name="Fingerprint">Text that must appear in a hand-read licence file.</param>
internal sealed record RegisteredLicence(string Id, string Verdict, string Fingerprint);

/// <summary>One row of docs/DEPENDENCIES.md §4.</summary>
/// <param name="Id">The package id.</param>
/// <param name="Licence">The licence id, which must have a row in §1.1.</param>
/// <param name="Determined"><c>nuspec</c>, <c>read</c> or <c>first-party</c>.</param>
/// <param name="Evidence">For a <c>read</c> row, the licence file's path inside the package.</param>
internal sealed record RegisteredPackage(string Id, string Licence, string Determined, string Evidence);
