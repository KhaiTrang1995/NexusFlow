using System;
using System.IO;
using System.Linq;
using FlowX.Compiler.Analysis;
using Microsoft.CodeAnalysis;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// FLOWX1032 and FLOWX1033 against <c>samples/banking</c>'s real source, read off disk.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Everything else in <c>DeclaredPolicyAnalyzerTests</c> analyses source the tests
/// wrote</strong>, which means the tests and the rule share whatever assumptions the author
/// had about how a policy set is spelled. <c>samples/banking</c> shares none of them: four
/// named sets declared as static readonly fields across a separate file, seven
/// <c>.WithPolicy(...)</c> calls, two of them inside <c>Case</c> blocks and two of them
/// following a <c>.CompensateWith&lt;T&gt;()</c>, on a step whose input is mapped. It is the
/// only case here that could fail for a reason the author did not think of, and it is the
/// evidence that these rules report on real code rather than on a fixture —
/// <c>docs/21-Quality-Gates.md §2.4</c> refuses a check that passes vacuously.
/// </para>
/// <para>
/// <strong>The sample's own suppression is removed first, deliberately.</strong>
/// <c>ExecuteTransferFlow</c> carries an argued <c>#pragma warning disable FLOWX1032</c>, and
/// Roslyn filters a suppressed diagnostic out of an analyzer run — so reading the file
/// verbatim would assert that nothing is reported, which is true and says nothing. Stripping
/// the pragma is what turns the file back into the input the rule was written against, and it
/// fails loudly if the sample ever stops carrying one.
/// </para>
/// <para>
/// <strong>Compile errors are not asserted away here, unlike everywhere else in this
/// suite.</strong> The sample's flow carries <c>[HttpTrigger]</c> from
/// <c>plugins/FlowX.Http</c> and its capabilities reach an in-memory ledger in
/// <c>Infrastructure.cs</c>, neither of which this compilation references; the four files
/// below are the ones the policy question is about. An analyzer runs against whatever
/// semantic model it is given, and every symbol these two rules read — <c>IStepBuilder</c>,
/// the <c>PolicySet</c> chain, the <c>Step</c> type arguments — binds from
/// <c>FlowX.Abstractions</c> alone.
/// </para>
/// </remarks>
public sealed class ReferenceSamplePolicyTests
{
    private const string Pragma = "#pragma warning disable FLOWX1032";
    private const string Restore = "#pragma warning restore FLOWX1032";

    /// <summary>The four sample files the policy rules read, with the suppression removed.</summary>
    private static (string Path, string Source)[] Sample(Func<string, string>? editPolicies = null)
    {
        var flow = Read("ExecuteTransferFlow.cs");

        flow.ShouldContain(
            Pragma,
            Case.Sensitive,
            "samples/banking argues its FLOWX1032 suppression at the call site. If the pragma " +
            "is gone the sample has taken a different answer, and this test is no longer " +
            "removing what it thinks it is.");

        var policies = Read("Policies.cs");

        return
        [
            ("/samples/banking/ExecuteTransferFlow.cs", Unsuppressed(flow)),
            ("/samples/banking/Policies.cs", editPolicies is null ? policies : editPolicies(policies)),
            ("/samples/banking/Capabilities.cs", Read("Capabilities.cs")),
            ("/samples/banking/Contracts.cs", Read("Contracts.cs")),
        ];
    }

    private static string Unsuppressed(string source) => source
        .Replace(Pragma, string.Empty, StringComparison.Ordinal)
        .Replace(Restore, string.Empty, StringComparison.Ordinal);

    private static Diagnostic[] Report(Func<string, string>? editPolicies = null) =>
        [.. GeneratorHarness.Report(
            GeneratorHarness.CompilationOf(Sample(editPolicies)),
            new DeclaredPolicyAnalyzer())];

    // ------------------------------------------------------------------ it fires

    /// <summary>
    /// The rule reports once per <c>.WithPolicy(...)</c> in the reference saga: seven times.
    /// </summary>
    /// <remarks>
    /// The count is the assertion, not merely the presence. Six would mean one of the two
    /// calls inside a <c>Case</c> block was missed; eight would mean something is reported
    /// twice. Both are silent failures a "should contain FLOWX1032" assertion would pass.
    /// </remarks>
    [Fact]
    public void EveryPolicyCallInTheReferenceSagaIsReported()
    {
        var reported = Report().Where(static d => d.Id == "FLOWX1032").ToList();

        reported.Count.ShouldBe(
            7,
            "samples/banking declares seven .WithPolicy(...) calls, two of them inside Case " +
            "blocks. Reported:\n" + Describe(reported));

        reported.ShouldAllBe(static d => d.Severity == DiagnosticSeverity.Warning);
    }

    /// <summary>
    /// The ledger legs are told about <c>Audit</c> and <c>Timeout</c>, and not about the
    /// retry that runs.
    /// </summary>
    /// <remarks>
    /// The one report in this sample where getting it wrong would be actively harmful:
    /// <c>Policies.LedgerPost</c> is the set that carries the one policy this runtime
    /// executes, and naming its <c>CompensationRetry</c> here would tell a payments team
    /// their reversal is not retried when it is.
    /// </remarks>
    [Fact]
    public void TheLedgerLegsAreToldAboutTheAuditAndNotAboutTheRetry()
    {
        var ledger = Report()
            .Where(static d => d.Id == "FLOWX1032")
            .Select(static d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture))
            .Where(static message => message.Contains("Policies.LedgerPost", StringComparison.Ordinal))
            .ToList();

        ledger.Count.ShouldBe(2, "Both ledger legs declare Policies.LedgerPost.");

        foreach (var message in ledger)
        {
            message.ShouldContain("Audit");
            message.ShouldContain("Timeout");
            message.ShouldNotContain("CompensationRetry");
        }
    }

    /// <summary>
    /// <c>Audit</c> is reported although it runs at <c>CompensationRetry</c>'s own stage.
    /// </summary>
    /// <remarks>
    /// Asserted against the sample rather than only against a fixture, because this is the
    /// claim the sample's README made wrongly for two work packages — "no <em>forward</em>
    /// policy runs", which is a summary by stage, and <c>Audit</c> is stage 7. A rule that
    /// cut by stage would be silent on every audit this bank declares.
    /// </remarks>
    [Fact]
    public void TheDeclaredAuditsAreReportedAlthoughTheyAreStageSevenPolicies() =>
        Report()
            .Where(static d => d.Id == "FLOWX1032")
            .Count(static d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)
                .Contains("Audit", StringComparison.Ordinal))
            .ShouldBe(3, "LedgerPost twice and SettlementRegister once declare an Audit.");

    /// <summary>
    /// Reusing the ledger set on the settlement step — the tempting edit — is FLOWX1033.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The defect the sample deliberately does not commit, committed here so that the rule
    /// is proved against the shape it exists for rather than against a fixture. It is one
    /// line: <c>Policies.SettlementRegister</c> and <c>Policies.LedgerPost</c> differ by the
    /// <c>CompensationRetry</c> alone, and <c>RecordSettlement</c> is not compensable.
    /// </para>
    /// <para>
    /// Before this rule that edit compiled with no diagnostic and published a five-attempt
    /// retry over an undo the plan does not contain.
    /// </para>
    /// </remarks>
    [Fact]
    public void GivingTheSettlementStepACompensationRetryIsReported()
    {
        var reports = Report(static policies => WithRetryOnSettlement(policies))
            .Where(static d => d.Id == "FLOWX1033")
            .ToList();

        reports.Count.ShouldBe(
            1,
            "RecordSettlement is the sample's one non-compensable policy-carrying step.\n" +
            Describe(reports));

        reports[0].Severity.ShouldBe(DiagnosticSeverity.Error);

        var message = reports[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture);

        message.ShouldContain("RecordSettlement");
        message.ShouldContain("Policies.SettlementRegister");
    }

    // -------------------------------------------------------------- it stays quiet

    /// <summary>
    /// FLOWX1033 is silent on the sample as written, and that is the load-bearing half.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The sample is a saga: two compensable ledger legs carrying a <c>CompensationRetry</c>
    /// that does execute, and a third policy-carrying step that is legitimately not
    /// compensable and legitimately declares no retry. A rule that fired on any of the three
    /// would be a rule reporting on correct code, and the two ledger legs are the exact shape
    /// — <c>.CompensateWith&lt;T&gt;().WithPolicy(...)</c> on a step with a mapped input —
    /// that a walk of only the receiver chain gets wrong in one of its two legal orders.
    /// </para>
    /// <para>
    /// Written as an assertion about the whole run rather than about FLOWX1033 alone: if the
    /// analyzer starts reporting a third id here, this test is the one that says so.
    /// </para>
    /// </remarks>
    [Fact]
    public void NothingButFLOWX1032IsReportedOnTheSampleAsWritten()
    {
        var reports = Report();

        reports
            .Select(static d => d.Id)
            .Distinct()
            .ShouldBe(["FLOWX1032"], "Reported:\n" + Describe(reports));
    }

    /// <summary>
    /// And with its own suppression in place the sample reports nothing at all.
    /// </summary>
    /// <remarks>
    /// The pragma is a decision, so it is worth one assertion that the decision takes effect
    /// — a suppression that does not suppress is an argument written in a file for nobody.
    /// This is also what keeps <c>dotnet build -c Release</c> at zero warnings honest from
    /// inside the test suite rather than only from CI.
    /// </remarks>
    [Fact]
    public void TheSamplesOwnSuppressionSilencesIt() =>
        GeneratorHarness.Report(
            GeneratorHarness.CompilationOf(
                ("/samples/banking/ExecuteTransferFlow.cs", Read("ExecuteTransferFlow.cs")),
                ("/samples/banking/Policies.cs", Read("Policies.cs")),
                ("/samples/banking/Capabilities.cs", Read("Capabilities.cs")),
                ("/samples/banking/Contracts.cs", Read("Contracts.cs"))),
            new DeclaredPolicyAnalyzer())
            .ShouldBeEmpty();

    // ------------------------------------------------------------------ helpers

    /// <summary>The one-line edit the sample's README names as the tempting mistake.</summary>
    private static string WithRetryOnSettlement(string policies)
    {
        const string Declaration =
            """
            PolicySet.Named("settlement-register")
                    .Timeout(TimeSpan.FromSeconds(5))
                    .Audit("financial")
            """;

        policies.ShouldContain(
            Declaration,
            Case.Sensitive,
            "Policies.SettlementRegister is no longer declared the way this test edits it.");

        return policies.Replace(
            Declaration,
            Declaration + "\n        .CompensationRetry(attempts: 5)",
            StringComparison.Ordinal);
    }

    private static string Describe(System.Collections.Generic.IEnumerable<Diagnostic> reports) =>
        string.Join(
            "\n",
            reports.Select(static d =>
                $"{d.Id} {d.Location.GetLineSpan().StartLinePosition}: " +
                d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>
    /// One of the sample's files, read from the repository rather than from a copy.
    /// </summary>
    /// <remarks>
    /// A copy would be a copy that stops matching the original without anything failing,
    /// which is the reason <c>ReferenceSampleCodeFixTests</c> reads
    /// <c>samples/ecommerce</c> the same way.
    /// </remarks>
    private static string Read(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "samples", "banking", fileName);

            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate samples/banking/{fileName} from {AppContext.BaseDirectory}.");
    }
}
