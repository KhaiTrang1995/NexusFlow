using System;
using System.IO;
using System.Linq;
using FlowX.Compiler.Analysis;
using Microsoft.CodeAnalysis;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// Every policy rule against <c>samples/banking</c>'s real source, read off disk.
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
/// <strong>Each of the five rules is proved against a one-line edit of this file.</strong>
/// Reusing the ledger set on the settlement step is FLOWX1033; applying
/// <c>PolicySet.CompensationDefault</c> beside it is FLOWX1034 — the repair FLOWX1033's page
/// used to recommend and no build could perform; dropping the ledger retry to one attempt is
/// FLOWX1035; and compiling this sample's own <c>Policies.cs</c> into a referenced assembly,
/// which is the shape a second application reaches for, is FLOWX1036 on all seven calls.
/// Each edit is one an author would plausibly make, which is what separates proof that a rule
/// fires from proof that it can be made to fire.
/// </para>
/// <para>
/// <strong>The sample carries no suppression at all, and that is asserted rather than
/// assumed.</strong> <c>ExecuteTransferFlow</c> used to carry an argued
/// <c>#pragma warning disable FLOWX1032</c>, which this file stripped before analysing —
/// Roslyn filters a suppressed diagnostic out of an analyzer run, so reading the file verbatim
/// would have asserted that nothing is reported, which was true and said nothing. FLOWX1032 is
/// deleted and every pragma that pointed at it went with it, so the file is read verbatim now
/// and <see cref="TheSampleSuppressesNothing"/> is what fails if a suppression ever comes
/// back — which is the same guard, aimed at the state the sample is supposed to be in.
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
    /// <summary>The four sample files the policy rules read, verbatim.</summary>
    private static (string Path, string Source)[] Sample(
        Func<string, string>? editPolicies = null,
        Func<string, string>? editFlow = null)
    {
        var flow = Read("ExecuteTransferFlow.cs");
        var policies = Read("Policies.cs");

        return
        [
            ("/samples/banking/ExecuteTransferFlow.cs", editFlow is null ? flow : editFlow(flow)),
            ("/samples/banking/Policies.cs", editPolicies is null ? policies : editPolicies(policies)),
            ("/samples/banking/Capabilities.cs", Read("Capabilities.cs")),
            ("/samples/banking/Contracts.cs", Read("Contracts.cs")),
        ];
    }

    private static Diagnostic[] Report(
        Func<string, string>? editPolicies = null,
        Func<string, string>? editFlow = null) =>
        [.. GeneratorHarness.Report(
            GeneratorHarness.CompilationOf(Sample(editPolicies, editFlow)),
            new DeclaredPolicyAnalyzer())];

    // ------------------------------------------------------------------ it fires

    /// <summary>
    /// None of the reference saga's seven <c>.WithPolicy(...)</c> calls is reported.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This asserted seven, then four, then three, and is now none.</strong> Seven was
    /// the state before any policy executed. Seven to four was the policy engine landing: the
    /// three calls naming <c>Policies.ExternalRead</c> — two of them inside <c>Case</c> blocks
    /// — went quiet when their <c>Timeout</c>, <c>Retry</c> and <c>CircuitBreaker</c> started
    /// running.
    /// </para>
    /// <para>
    /// Four to three was stage 1 and stage 3 landing, and it happened in two ways at once on
    /// one call. <c>Policies.Admission</c> declared a <c>RateLimit</c> and an
    /// <c>Idempotency</c> window; the limit is executed now, and the window was
    /// <em>deleted</em> rather than executed, because <c>ExecuteTransfer</c> marks two IBANs
    /// <c>[Sensitive]</c> and <c>FLOWX1040</c> refuses a window whose recorded result would
    /// carry <c>[redacted]</c> where an account number was.
    /// </para>
    /// <para>
    /// Three to none was stage 5 and stage 7's audit landing: <c>LedgerPost</c> twice and
    /// <c>SettlementRegister</c> once went quiet, because an <c>Audit</c> was the only thing
    /// any of the three still declared that nothing applied. There is no fourth number, which
    /// is why FLOWX1032 is deleted rather than narrowed again — and this assertion is what
    /// would fail if a rule of its shape came back.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoPolicyCallInTheReferenceSagaIsReported()
    {
        var reports = Report();

        reports.ShouldBeEmpty(
            "samples/banking declares seven .WithPolicy(...) calls across four named sets, " +
            "and every kind any of them declares is applied. Reported:\n" + Describe(reports));
    }

    /// <summary>
    /// The ledger legs are told about nothing at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Inverted, and it is the sample assertion the audit work exists for.</strong>
    /// This test read "the ledger legs are told about <c>Audit</c> and about nothing else",
    /// and it was the one report in this sample where getting it wrong would have been
    /// actively harmful: <c>Policies.LedgerPost</c> carries the <c>CompensationRetry</c> that
    /// unwinds a failed transfer and the <c>Timeout</c> that bounds the ledger write, and
    /// naming either would tell a payments team a control is off when it is on.
    /// </para>
    /// <para>
    /// <c>Audit</c> has now made the same journey <c>Timeout</c> made: it was the kind this
    /// report had to name, and it is now a kind nothing may name. Every kind
    /// <c>Policies.LedgerPost</c> declares is applied, so the correct number of reports on
    /// both ledger legs is none — and the sample's two <c>#pragma warning disable
    /// FLOWX1032</c> lines about the audit went with the rule.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheLedgerLegsAreToldNothing() =>
        Report()
            .Select(static d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture))
            .Where(static message => message.Contains("Policies.LedgerPost", StringComparison.Ordinal))
            .ShouldBeEmpty(
                "Timeout, Audit and CompensationRetry are all applied. A report here would " +
                "tell a payments team three controls are off when all three are on.");

    /// <summary>
    /// No declared <c>Audit</c> is reported, although it shares a stage with a kind that a
    /// rule here does report.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Inverted, and the fact it pins is untouched.</strong> This asserted that the
    /// sample's three audits <em>were</em> reported although they were stage 7 — the evidence
    /// that the deleted rule's cut was never a range of stages, because
    /// <c>CompensationRetry</c> shares that stage and runs.
    /// </para>
    /// <para>
    /// Nothing here is decided by stage number, and the sample still demonstrates it:
    /// <c>Audit</c> and <c>CompensationRetry</c> are both stage 7, and the one edit that makes
    /// this sample report at all — moving <c>CompensationRetry</c> onto a step with no undo —
    /// touches one of them and not the other.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoDeclaredAuditIsReported() =>
        Report()
            .Count(static d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)
                .Contains("Audit", StringComparison.Ordinal))
            .ShouldBe(0, "LedgerPost twice and SettlementRegister once declare an Audit, and " +
                         "all three are now written.");

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
    /// analyzer starts reporting any id here, this test is the one that says so. It asserted
    /// <c>["FLOWX1032"]</c> while that rule had something left to name; the list is empty now,
    /// which is a strictly stronger statement about the same run.
    /// </para>
    /// </remarks>
    [Fact]
    public void NothingIsReportedOnTheSampleAsWritten()
    {
        var reports = Report();

        reports
            .Select(static d => d.Id)
            .Distinct()
            .ShouldBeEmpty("Reported:\n" + Describe(reports));
    }

    /// <summary>The sample suppresses no FlowX diagnostic anywhere.</summary>
    /// <remarks>
    /// <strong>This replaces the guard that used to sit inside <c>Sample</c>.</strong> That
    /// helper asserted the flow still carried its <c>#pragma warning disable FLOWX1032</c>
    /// before stripping it, so that a sample which quietly stopped carrying one could not turn
    /// every assertion in this file into a vacuous pass. There is no pragma to strip and none
    /// to argue for: every kind the sample declares is applied. The guard is kept, pointed the
    /// other way — a suppression appearing here would mean the reference application had gone
    /// back to silencing a rule rather than satisfying it, and every other test in this file
    /// would start passing for the wrong reason.
    /// </remarks>
    [Fact]
    public void TheSampleSuppressesNothing()
    {
        foreach (var file in new[]
                 {
                     "ExecuteTransferFlow.cs", "Policies.cs", "Capabilities.cs", "Contracts.cs",
                 })
        {
            Read(file).ShouldNotContain(
                "#pragma warning disable FLOWX",
                Case.Sensitive,
                $"samples/banking/{file} silences a FlowX rule. Every policy kind it declares " +
                "is applied, so a suppression here hides a real finding — and it makes every " +
                "assertion in this file pass against a filtered analyzer run.");
        }
    }

    // ------------------------------------- the three later rules, against the sample

    /// <summary>
    /// Applying <c>PolicySet.CompensationDefault</c> beside the ledger set — the edit
    /// FLOWX1033's page used to recommend — is FLOWX1034.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The repair that could not work, on the real saga. <c>StepModel.WithPolicy</c> assigns,
    /// so the second call deletes <c>Policies.LedgerPost</c> from the plan and from the
    /// manifest: the ledger leg loses its five-second timeout and its financial audit in
    /// exchange for a compensation retry it already had. A payments team following the
    /// documented advice would have shipped that.
    /// </para>
    /// <para>
    /// One report, not two. Only the superseded call is discarded, and only it is reported.
    /// </para>
    /// </remarks>
    [Fact]
    public void ApplyingASecondSetToALedgerLegIsReported()
    {
        var reports = Report(editFlow: static flow => WithSecondPolicyOnTheDebitLeg(flow))
            .Where(static d => d.Id == "FLOWX1034")
            .ToList();

        reports.Count.ShouldBe(1, "Only the superseded call is discarded.\n" + Describe(reports));
        reports[0].Severity.ShouldBe(DiagnosticSeverity.Error);

        var message = reports[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture);

        message.ShouldContain("Policies.LedgerPost");
        message.ShouldContain("PolicySet.CompensationDefault");
    }

    /// <summary>
    /// Dropping the ledger set's five attempts to one is FLOWX1035.
    /// </summary>
    /// <remarks>
    /// <c>Policies.LedgerPost</c>'s own remarks call its compensation retry "the one line in
    /// the file that runs". At <c>attempts: 1</c> it does not: the plan's
    /// <c>HasCompensationPolicies</c> stays false, the engine takes
    /// <c>CompensationPolicy.None</c>, and the manifest still publishes the kind — which in
    /// this bank is a published promise that a failed reversal is retried, over a reversal
    /// dispatched once.
    /// </remarks>
    [Fact]
    public void DroppingTheLedgerRetryToOneAttemptIsReported()
    {
        var reports = Report(static policies => WithSingleAttempt(policies))
            .Where(static d => d.Id == "FLOWX1035")
            .ToList();

        reports.Count.ShouldBe(
            2,
            "Both ledger legs declare Policies.LedgerPost, and both undos stop being " +
            "retried.\n" + Describe(reports));

        reports[0].Severity.ShouldBe(DiagnosticSeverity.Warning);

        var message = reports[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture);

        message.ShouldContain("Policies.LedgerPost");
        message.ShouldContain("1");
    }

    /// <summary>
    /// Moving this bank's policy file into a referenced assembly is FLOWX1036, on every call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shape a second application would reach for on its own: one shared
    /// <c>Policies</c> library, referenced by several flow projects. Compiled rather than
    /// linked, the file the sample already has stops being readable — its symbols carry no
    /// syntax — so all seven declarations reach no plan node and no manifest entry, and the
    /// <c>CompensationRetry</c> that is "the one line in the file that runs" stops running.
    /// </para>
    /// <para>
    /// FLOWX1033, FLOWX1035 and FLOWX1040 go quiet in the same breath, which is the point of
    /// reporting this separately: the compiler cannot say anything about a set it could not
    /// read, so every rule that describes this bank's controls would disappear along with the
    /// controls, and only the one report that is about the *reading* is honest here.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheSamplesPolicyFileInAReferencedAssemblyIsReported()
    {
        var library = CompiledPolicyLibrary();

        var compilation = GeneratorHarness.CompilationOf(
            "Banking",
            [library],
            ("/samples/banking/ExecuteTransferFlow.cs", Read("ExecuteTransferFlow.cs")),
            ("/samples/banking/Capabilities.cs", Read("Capabilities.cs")),
            ("/samples/banking/Contracts.cs", Read("Contracts.cs")));

        var reports = GeneratorHarness.Report(compilation, new DeclaredPolicyAnalyzer());

        reports.Select(static d => d.Id).Distinct().ShouldBe(
            ["FLOWX1036"],
            "Nothing can be said about the kinds in a set the compiler cannot read.\n" +
            Describe(reports));

        reports.Length.ShouldBe(7, "One per .WithPolicy(...) call.\n" + Describe(reports));
        reports.ShouldAllBe(static d => d.Severity == DiagnosticSeverity.Warning);
    }

    /// <summary>
    /// <c>PolicySet.CompensationDefault</c> reaches the ledger leg's undo, from metadata.
    /// </summary>
    /// <remarks>
    /// The set arrives from <c>FlowX.Abstractions</c> with no syntax behind it, and the rules
    /// that ask what is in a set answer correctly all the same: silent on the compensable
    /// ledger leg — it declares the compensation retry and nothing else — and FLOWX1033 on
    /// the settlement step, which has no undo for it to wrap. Before it resolved, both were
    /// silent, and so was the emitter.
    /// </remarks>
    [Theory]
    [InlineData("PostDebit", new string[0])]
    [InlineData("RecordSettlement", new[] { "FLOWX1033" })]
    public void TheDocumentedDefaultIsUnderstoodOnTheRealSaga(string step, string[] expected) =>
        Report(editFlow: flow => WithDocumentedDefaultOn(flow, step))
            .Where(d => d.Id != "FLOWX1034")
            .Select(d => d.Id)
            .Distinct()
            .ShouldBe(expected);

    /// <summary>
    /// The sample as it is on disk reports nothing at all.
    /// </summary>
    /// <remarks>
    /// <strong>This was <c>TheSamplesOwnSuppressionSilencesIt</c></strong>, which asserted that
    /// the sample's <c>#pragma warning disable FLOWX1032</c> took effect — a suppression that
    /// does not suppress is an argument written in a file for nobody. There is no pragma; the
    /// same run is empty because there is nothing to report, which is the state the pragma was
    /// an apology for. Kept because it is what keeps <c>dotnet build -c Release</c> at zero
    /// warnings honest from inside the test suite rather than only from CI.
    /// </remarks>
    [Fact]
    public void TheSampleOnDiskReportsNothing() =>
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
                    .Audit("financial", "DebitEntryId", "CreditEntryId")
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

    /// <summary>The debit leg's policy set, as the sample declares it.</summary>
    /// <remarks>
    /// Anchored on <c>ReverseDebit</c> rather than on the <c>.WithPolicy</c> line, because
    /// both ledger legs name <c>Policies.LedgerPost</c> and only the compensation tells the
    /// two calls apart.
    /// </remarks>
    private const string DebitLegPolicy =
        """
        .CompensateWith<ReverseDebit>()
                        .WithPolicy(Policies.LedgerPost)
        """;

    /// <summary>The settlement step's policy set, as the sample declares it.</summary>
    private const string SettlementPolicy = ".WithPolicy(Policies.SettlementRegister)";

    /// <summary>The repair FLOWX1033's page used to recommend, applied to the debit leg.</summary>
    private static string WithSecondPolicyOnTheDebitLeg(string flow)
    {
        flow.ShouldContain(
            DebitLegPolicy,
            Case.Sensitive,
            "The debit leg is no longer declared the way this test edits it.");

        return flow.Replace(
            DebitLegPolicy,
            DebitLegPolicy + "\n                .WithPolicy(PolicySet.CompensationDefault)",
            StringComparison.Ordinal);
    }

    /// <summary>The documented default in place of a named set, on one step.</summary>
    private static string WithDocumentedDefaultOn(string flow, string step)
    {
        var declared = step == "PostDebit" ? DebitLegPolicy : SettlementPolicy;

        flow.ShouldContain(declared, Case.Sensitive, $"{step} is no longer declared the way this test edits it.");

        return flow.Replace(
            declared,
            declared.Replace(
                declared.Contains("LedgerPost", StringComparison.Ordinal)
                    ? "Policies.LedgerPost"
                    : "Policies.SettlementRegister",
                "PolicySet.CompensationDefault",
                StringComparison.Ordinal),
            StringComparison.Ordinal);
    }

    /// <summary>The ledger set's five attempts, dropped to the one that retries nothing.</summary>
    private static string WithSingleAttempt(string policies)
    {
        const string Declared = ".CompensationRetry(attempts: 5)";

        policies.ShouldContain(
            Declared,
            Case.Sensitive,
            "Policies.LedgerPost no longer declares the attempt count this test edits.");

        return policies.Replace(Declared, ".CompensationRetry(attempts: 1)", StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>samples/banking/Policies.cs</c>, compiled to an image and referenced as one.
    /// </summary>
    private static PortableExecutableReference CompiledPolicyLibrary()
    {
        var compilation = GeneratorHarness.CompilationOf(
            "Banking.Policies",
            [],
            // The sample builds with ImplicitUsings; this compilation does not, and System is
            // the only one its policy declarations need.
            ("/shared/Policies.cs", "using System;\n" + Read("Policies.cs")));

        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);

        emitted.Success.ShouldBeTrue(string.Join(
            "\n",
            emitted.Diagnostics.Where(static d => d.Severity == DiagnosticSeverity.Error)));

        return MetadataReference.CreateFromImage(image.ToArray());
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
