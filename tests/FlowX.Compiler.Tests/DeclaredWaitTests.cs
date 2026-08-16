using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// FLOWX1054 — a declared wait the compiler cannot fold, and the <c>timeout</c> field that
/// goes missing with it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The silent direction carries the weight here, as it does for FLOWX1036.</strong> A
/// rule that reported on every <c>.AwaitSignal</c> would be trivial to write and worthless: it
/// would fire on <c>samples/workflow</c>, which is the one flow in this repository that
/// populates the field. So the cases that must stay quiet — a literal, a named constant one
/// hop away, and the real sample read off disk — are asserted at least as heavily as the case
/// that must fire.
/// </para>
/// <para>
/// <strong>Every firing test also asserts what the manifest did with the same run.</strong>
/// The rule is raised from <c>FoldDeclaredWait</c>'s own answer precisely so that the report
/// and the omission cannot disagree, and an assertion that only counted diagnostics would not
/// notice if they ever did.
/// </para>
/// <para>
/// Through the real generator over a real compilation, for <c>TimerConstructTests</c>' reason:
/// the question is what a developer's build produces.
/// </para>
/// </remarks>
public sealed class DeclaredWaitTests
{
    private const string Signal = """
        public sealed record PaymentConfirmed(string Id);
        """;

    /// <summary>A duration this compiler folds, and one it does not, declared side by side.</summary>
    /// <remarks>
    /// <c>Constant</c> is the shape the samples use and the shape the one level of indirection
    /// exists for. <c>Configured</c> is what <c>samples/workflow</c> briefly shipped: a wait a
    /// demonstration could wind down through the environment, which is a method call, which is
    /// not foldable.
    /// </remarks>
    private const string Waits = """
        public static class Waits
        {
            public static TimeSpan Constant { get; } = TimeSpan.FromDays(7);

            public static TimeSpan Configured { get; } =
                Environment.GetEnvironmentVariable("FLOWX_SAMPLE_OFFER_WINDOW") is { } window
                    ? TimeSpan.Parse(window, System.Globalization.CultureInfo.InvariantCulture)
                    : TimeSpan.FromDays(7);
        }
        """;

    // --------------------------------------------------------------- it stays quiet

    /// <summary>A literal at the call site is silent, and the field is published.</summary>
    /// <remarks>
    /// The second half is what makes this more than an assertion about an empty list: a rule
    /// that had been disabled entirely would also report nothing here.
    /// </remarks>
    [Fact]
    public void ALiteralWaitIsSilentAndPublishesItsTimeout()
    {
        var run = GeneratorHarness.Run(Durable("""
                .AwaitSignal<PaymentConfirmed>(TimeSpan.FromDays(7))
            """));

        Reported(run).ShouldNotContain("FLOWX1054", run.Describe());

        run.ManifestJson.ShouldNotBeNull().ShouldContain(
            "\"timeout\": \"P7D\"",
            Case.Sensitive,
            "the rule is silent because the field is there — not the other way round.");
    }

    /// <summary>
    /// A named constant one hop from the call site is silent, which is the form the samples use.
    /// </summary>
    /// <remarks>
    /// The case a rule written against the call site alone would get wrong, and the one that
    /// matters most: a duration is a business decision and belongs where it can be read without
    /// opening a flow, so every wait in this repository is written this way.
    /// </remarks>
    [Fact]
    public void ANamedConstantOneHopAwayIsSilentAndPublishesItsTimeout()
    {
        var run = GeneratorHarness.Run(Durable("""
                .AwaitSignal<PaymentConfirmed>(Waits.Constant)
            """));

        Reported(run).ShouldNotContain("FLOWX1054", run.Describe());

        run.ManifestJson.ShouldNotBeNull().ShouldContain("\"timeout\": \"P7D\"", Case.Sensitive);
    }

    /// <summary>A <c>.Delay</c> the compiler cannot fold is not reported.</summary>
    /// <remarks>
    /// The rule's subject is the field, not the expression. A delay publishes no
    /// <c>timeout</c> for anyone to lose — it is a step the flow performs rather than a bound
    /// on something else happening — so there is nothing here to tell an author about, and a
    /// report would be the false positive that gets a rule suppressed everywhere.
    /// </remarks>
    [Fact]
    public void ADelayTheCompilerCannotFoldIsNotReported() =>
        Reported(GeneratorHarness.Run(Durable("""
                .Delay(Waits.Configured)
            """)))
            .ShouldNotContain("FLOWX1054");

    // -------------------------------------------------------------------- it fires

    /// <summary>An environment read is reported, and the message is the whole of the finding.</summary>
    /// <remarks>
    /// <para>
    /// Asserted verbatim rather than by id. The message is what tells the author the two things
    /// they cannot see — the field is gone, and <c>flowx diff</c> has no window to compare — and
    /// a test that read the id alone would pass against a message that had lost either.
    /// </para>
    /// <para>
    /// <strong>This is the shape that cost the repository the field.</strong> WP-63's timer half
    /// made <c>samples/workflow</c>'s <c>Waits.Countersignature</c> an environment read so a
    /// demonstration would not wait seven days; the flow went on working, the manifest stopped
    /// publishing <c>timeout</c>, and the build stayed green.
    /// </para>
    /// </remarks>
    [Fact]
    public void AWaitReadFromTheEnvironmentIsReportedWithBothConsequencesAndTheFix()
    {
        var run = GeneratorHarness.Run(Durable("""
                .AwaitSignal<PaymentConfirmed>(Waits.Configured)
            """));

        run.Ids.ShouldContain("FLOWX1054", run.Describe());

        Message(run).ShouldBe(
            "Wait 'Waits.Configured' is not a compile-time constant, so this step publishes " +
            "no timeout and flowx diff cannot compare the window — declare the duration as a " +
            "constant");
    }

    /// <summary>
    /// The report and the omission are one decision: the run that fires publishes no
    /// <c>timeout</c>.
    /// </summary>
    /// <remarks>
    /// <strong>This is the assertion the rule was built around.</strong> FLOWX1054 is raised
    /// from <c>FlowAnalyzer.FoldDeclaredWait</c>'s own answer rather than from a second reading
    /// of the expression, so a wait that is reported is exactly a wait the manifest dropped. A
    /// rule with its own copy of the foldability set would pass every other test in this file
    /// and fail this one the first time the two copies drifted.
    /// </remarks>
    [Fact]
    public void TheReportedWaitIsTheOneTheManifestOmits()
    {
        var run = GeneratorHarness.Run(Durable("""
                .AwaitSignal<PaymentConfirmed>(Waits.Configured)
            """));

        run.Ids.ShouldContain("FLOWX1054", run.Describe());

        var manifest = run.ManifestJson.ShouldNotBeNull();

        manifest.ShouldContain("\"kind\": \"AwaitSignal\"", Case.Sensitive, "the wait is published");
        manifest.ShouldNotContain("\"timeout\"", Case.Sensitive, "and its declared window is not");
    }

    /// <summary>A poll's budget is the same declaration and the same report.</summary>
    /// <remarks>
    /// A poll declares a timeout in exactly the sense a suspension point does — how long before
    /// the escalation runs — and publishes it as the same field, folded by the same method. One
    /// rule covers both because one reading of the expression decides both, which is also why
    /// there is no second id for it.
    /// </remarks>
    [Fact]
    public void APollBudgetTheCompilerCannotFoldIsReported()
    {
        var run = GeneratorHarness.Run(Durable("""
                .PollUntil<ReserveInventory>(
                    until: ctx => ctx.Get<Reservation>().Sku.Length > 0,
                    interval: Backoff.Exponential("PT5S", "PT5M"),
                    timeout: Waits.Configured)
            """));

        run.Ids.ShouldContain("FLOWX1054", run.Describe());
        Message(run).ShouldContain("Waits.Configured", Case.Sensitive);
    }

    /// <summary>
    /// A poll whose <em>interval</em> cannot be folded is not reported, and its budget is.
    /// </summary>
    /// <remarks>
    /// The interval has no manifest field at all — how often a loop asks is the same kind of
    /// tuning number as <c>MaxDegreeOfParallelism</c>, which the schema deliberately does not
    /// publish — so an unfoldable one costs the contract nothing. FLOWX1043 is the rule that
    /// cares about it, and it stays silent for its own reason.
    /// </remarks>
    [Fact]
    public void APollWhoseIntervalAloneCannotBeFoldedIsNotReported() =>
        Reported(GeneratorHarness.Run(Durable("""
                .PollUntil<ReserveInventory>(
                    until: ctx => ctx.Get<Reservation>().Sku.Length > 0,
                    interval: Schedules.Ocr,
                    timeout: TimeSpan.FromHours(4))
            """)))
            .ShouldNotContain("FLOWX1054");

    // ------------------------------------------------------------------- severity

    /// <summary>
    /// A warning, and the choice is the decision this rule turns on.
    /// </summary>
    /// <remarks>
    /// Neither of the catalogue's two escalations reaches here. The determinism set's — error
    /// where the compilation proves the code is on a durable flow's replay path — cannot apply,
    /// because an unfoldable wait executes correctly under every profile: the plan carries the
    /// expression verbatim and generated C# evaluates it. What is lost is contract visibility,
    /// which is <c>FLOWX1043</c>'s ground for a warning. An error would refuse a flow that runs.
    /// </remarks>
    [Fact]
    public void TheRuleIsRaisedAsAWarning() =>
        GeneratorHarness.Run(Durable("""
                .AwaitSignal<PaymentConfirmed>(Waits.Configured)
            """))
            .Diagnostics
            .Single(static d => d.Id == "FLOWX1054")
            .Severity
            .ShouldBe(
                DiagnosticSeverity.Warning,
                "an unfoldable wait still waits for what the source says; what it loses is a " +
                "published field, and that is a decision an author is entitled to take.");

    /// <summary>And it is suppressible the way every other FlowX warning is.</summary>
    /// <remarks>
    /// <para>
    /// One <c>.editorconfig</c> line — <c>dotnet_diagnostic.FLOWX1054.severity = none</c>, which
    /// is exactly the compilation option set here. That is the unit of decision the catalogue's
    /// severity section argues for: written down once, in the repository that took it. Under
    /// <c>TreatWarningsAsErrors</c> the alternative is a stopped build, so a rule that could not
    /// be turned off would be an error wearing a warning's name.
    /// </para>
    /// <para>
    /// <strong>Not a <c>#pragma</c>, and the page says so.</strong> This rule is reported by the
    /// generator rather than by a <c>DiagnosticAnalyzer</c>, and a generator's diagnostics are
    /// filtered against the compilation's options — where <c>.editorconfig</c> and
    /// <c>&lt;NoWarn&gt;</c> both land — with the in-source directive never consulted. Asserted
    /// here as the option route rather than documented as a pragma that does nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheRuleIsSuppressible()
    {
        var source = Durable("""
                .AwaitSignal<PaymentConfirmed>(Waits.Configured)
            """);

        var compilation = GeneratorHarness.CompilationOf(("/src/Flows/Sample.cs", source));

        GeneratorHarness.Run(compilation).Ids.ShouldContain("FLOWX1054");

        GeneratorHarness
            .Run(compilation.WithOptions(compilation.Options.WithSpecificDiagnosticOptions(
                new System.Collections.Generic.Dictionary<string, ReportDiagnostic>(StringComparer.Ordinal)
                {
                    ["FLOWX1054"] = ReportDiagnostic.Suppress,
                })))
            .Ids
            .ShouldNotContain("FLOWX1054");
    }

    // ------------------------------------------------------- against the real sample

    /// <summary>
    /// <c>samples/workflow</c> as it stands today — restored to constants — is silent, and
    /// publishes the window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The sample this rule exists because of, read off disk rather than copied.</strong>
    /// A copy is a copy that stops matching the original without anything failing, which is why
    /// <c>ReferenceSamplePolicyTests</c> reads <c>samples/banking</c> the same way.
    /// </para>
    /// <para>
    /// The <c>timeout</c> assertion is what stops this passing vacuously. A compilation in which
    /// the flow was not recognised at all would report nothing and prove nothing; one that
    /// publishes <c>P7D</c> has genuinely walked the declaration, followed
    /// <c>Waits.Countersignature</c> to its initialiser and folded it.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheWorkflowSampleIsSilentAndStillPublishesItsWindow()
    {
        var run = GeneratorHarness.Run(GeneratorHarness.CompilationOf(
            ("/samples/workflow/AcceptOfferFlow.cs", Sample("AcceptOfferFlow.cs")),
            ("/samples/workflow/Offers.cs", Sample("Offers.cs")),
            ("/samples/workflow/Contracts.cs", Sample("Contracts.cs")),
            ("/samples/workflow/Capabilities.cs", Sample("Capabilities.cs"))));

        run.ManifestJson.ShouldNotBeNull().ShouldContain(
            "\"timeout\": \"P7D\"",
            Case.Sensitive,
            "offer.accept is the repository's only producer of ADR-0021's timeout field. If " +
            "this run publishes no window, the silence asserted below means nothing.");

        run.Ids.ShouldNotContain(
            "FLOWX1054",
            "samples/workflow declares its waits as constants, with the reason recorded at the " +
            "declaration. A report here means the sample regressed to the environment read " +
            "that cost it the field.\n" + run.Describe());
    }

    /// <summary>The sample argues its waits rather than silencing the rule.</summary>
    /// <remarks>
    /// <c>ReferenceSamplePolicyTests.TheSampleSuppressesNothing</c>'s guard, aimed at this
    /// file's subject: a suppression appearing in <c>Offers.cs</c> would make the assertion
    /// above pass against a filtered run rather than against a folded duration.
    /// </remarks>
    [Fact]
    public void TheWorkflowSampleSuppressesNothing() =>
        Sample("Offers.cs").ShouldNotContain(
            "#pragma warning disable FLOWX",
            Case.Sensitive,
            "the waits are constants and need no suppression; one here would hide the finding " +
            "this rule was written for.");

    // -------------------------------------------------------------------- helpers

    /// <summary>The message of the single FLOWX1054 in a run.</summary>
    private static string Message(GeneratorRun run) => run.Diagnostics
        .Single(static d => d.Id == "FLOWX1054")
        .GetMessage(CultureInfo.InvariantCulture);

    /// <summary>
    /// Ids other than FLOWX1006, which is noise in this preamble and signal in a real project.
    /// </summary>
    /// <remarks>
    /// It fires for every contract of every <c>Durable</c> flow in a compilation declaring no
    /// source-generated <c>JsonSerializerContext</c>, and the shared preamble declares none.
    /// Filtered rather than suppressed, so an unexpected diagnostic still fails.
    /// </remarks>
    private static string[] Reported(GeneratorRun run) =>
        [.. run.Ids.Where(static id => id != "FLOWX1006")];

    /// <summary>A schedule this compiler cannot fold, so a poll's interval can be unreadable.</summary>
    private const string Schedules = """
        public static class Schedules
        {
            public static Backoff Ocr { get; } = Backoff.Exponential("PT10M", "PT30M");
        }
        """;

    /// <remarks>
    /// <c>using System;</c> is prepended and is load-bearing: the shared preamble does not
    /// import it, and this rule follows a named constant through the <em>semantic</em> model,
    /// which needs <c>TimeSpan</c> to bind.
    /// </remarks>
    private static string Durable(string steps) =>
        "using System;\n" + FlowPlanGeneratorTests.WithFlow(Signal + "\n\n" + Waits + "\n\n" + Schedules + "\n\n" +
            $$"""
            [Flow("order.place", Profile = ExecutionProfile.Durable)]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
            {{steps}}
                    .Return(ctx => new OrderResult("id"));
            }
            """);

    /// <summary>One of the workflow sample's files, read from the repository.</summary>
    private static string Sample(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "samples", "workflow", fileName);

            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate samples/workflow/{fileName} from {AppContext.BaseDirectory}.");
    }
}
