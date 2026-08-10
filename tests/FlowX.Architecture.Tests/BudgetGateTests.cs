using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace FlowX.Architecture.Tests;

/// <summary>
/// The <c>Gate</c> column of <c>docs/14-Performance.md</c> says what enforces each budget.
/// This checks it is true.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this rule exists.</strong> The table had fourteen rows. Four of them named a
/// CI job that does not measure them, four named a nightly job that does not exist, and the
/// paragraph above the table said "measures them in CI on every pull request... Nothing here
/// is aspirational". Ten rows were aspirational. The table's whole purpose is to say what is
/// enforced, so a table that says it wrongly is worse than no table — a reader trusts it and
/// stops asking.
/// </para>
/// <para>
/// <strong>What it checks, and what it cannot.</strong> A row claiming CI must name a budget
/// that a benchmark class or the committed baseline actually produces a number for. It cannot
/// check that the number is <em>good</em>, or that the gate is wired to the right threshold —
/// those are the jobs' own business. What it catches is the failure that happened: a row
/// claiming an enforcement nothing performs.
/// </para>
/// <para>
/// <strong>The honest column is "none".</strong> A budget with no measurement is not a
/// failure — publishing a number before the code exists is deliberate here. Writing "CI" over
/// it is the failure, because that is the word that stops anybody looking.
/// </para>
/// </remarks>
public sealed partial class BudgetGateTests
{
    /// <summary>Matches one row of the budget table: number, then the last cell.</summary>
    [GeneratedRegex(@"^\| (?<id>B[0-9]+p?) \|(?<middle>.*)\| (?<gate>[^|]*)\|\s*$", RegexOptions.Multiline)]
    private static partial Regex BudgetRow();

    /// <summary>
    /// A budget whose gate column claims CI is one something in this repository measures.
    /// </summary>
    /// <remarks>
    /// Measurement is looked for in two places, because the repository keeps it in two: a
    /// BenchmarkDotNet class under <c>tests/FlowX.Benchmarks</c>, and an entry in
    /// <c>docs/benchmarks/baseline.json</c> — which is what the gating job compares against and
    /// therefore the stronger evidence of the two.
    /// </remarks>
    [Fact]
    public void EveryBudgetClaimingAGateHasSomethingThatMeasuresIt()
    {
        var document = new FileInfo(Path.Combine(
            RepositoryLayout.Root.FullName, "docs", "14-Performance.md"));

        document.Exists.ShouldBeTrue($"{document.FullName} declares the budgets this checks.");

        // TWO PLACES, BECAUSE THE REPOSITORY KEEPS ITS EVIDENCE IN TWO. A budget is either
        // compared against a committed figure in baseline.json, or asserted directly by a CI
        // job — B2 and B6's allocation half are hard-zero assertions and have no baseline row,
        // which is not the same as being unmeasured. Reading only the baseline reported both as
        // ungated on this rule's first run.
        var evidence = File.ReadAllText(Path.Combine(
            RepositoryLayout.Root.FullName, "docs", "benchmarks", "baseline.json"))
            + File.ReadAllText(Path.Combine(
                RepositoryLayout.Root.FullName, ".github", "workflows", "performance.yml"));

        var rows = BudgetRow().Matches(File.ReadAllText(document.FullName));

        rows.Count.ShouldBeGreaterThan(
            10,
            "The budget table was not parsed. Either its shape changed or this pattern is "
            + "wrong, and a rule that reads no rows passes whatever the table says.");

        var problems = new List<string>();

        foreach (Match row in rows)
        {
            var id = row.Groups["id"].Value;
            var gate = row.Groups["gate"].Value;

            // A cell that opens with "none" is this table being honest, and the sentence after
            // it explains what is missing — often by naming the job that does not exist. Reading
            // that explanation as a claim reported the honest rows as the broken ones, which is
            // the rule punishing exactly the behaviour it exists to encourage.
            var claimsEnforcement =
                !gate.TrimStart('*', ' ').StartsWith("none", StringComparison.OrdinalIgnoreCase)
                && (gate.Contains("CI", StringComparison.Ordinal)
                    || gate.Contains("nightly", StringComparison.OrdinalIgnoreCase));

            if (!claimsEnforcement)
            {
                continue;
            }

            if (!evidence.Contains(id, StringComparison.Ordinal))
            {
                problems.Add(
                    $"{id}'s gate column says \"{gate.Trim()}\", and neither "
                    + "docs/benchmarks/baseline.json nor .github/workflows/performance.yml "
                    + "mentions it. Nothing measures it, so the column is a promise rather than "
                    + "a statement.");
            }
        }

        problems.ShouldBeEmpty(
            "A budget claims an enforcement nothing performs. The column exists to tell a "
            + "reader which numbers are held to and which are still promises; writing the "
            + "wrong word there is what stops them asking:"
            + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }
}
