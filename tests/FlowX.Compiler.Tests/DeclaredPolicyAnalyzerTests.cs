using System;
using System.Linq;
using FlowX.Compiler.Analysis;
using Microsoft.CodeAnalysis;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// FLOWX1032 and FLOWX1033 — the policies a <c>.WithPolicy(...)</c> declares and the
/// runtime does not apply.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The silent direction carries more weight here than the firing one.</strong> A
/// rule that reported on <em>every</em> <c>.WithPolicy(...)</c> would be trivially easy to
/// write and worthless: it would fire on <c>PolicySet.CompensationDefault</c>, which is the
/// one set in the whole DSL that does exactly what it says. So the cases that must stay
/// quiet — a compensation-only set, a compensable step, a set the compiler cannot read —
/// are asserted at least as heavily as the cases that must fire.
/// </para>
/// <para>
/// <strong><c>Audit</c> is the test that pins the correction, and the correction now has two
/// halves.</strong> <c>Audit</c> is a <c>PolicyStage.Consistency</c> policy — stage 7, the
/// same stage as the <c>CompensationRetry</c> that runs — and it is inert; <c>RateLimit</c>
/// is stage 1 and inert while <c>Timeout</c> is stage 4 and applied. So no line drawn by
/// stage number separates what this rule reports from what it must not, in either direction,
/// and this file is where that is checked rather than asserted in prose.
/// </para>
/// <para>
/// The two severities are pinned by tests of their own, because the split is the decision
/// these rules turn on and the one a later reader is most likely to tidy into agreement.
/// The reasoning is on the descriptors and on the two pages.
/// </para>
/// </remarks>
public sealed class DeclaredPolicyAnalyzerTests
{
    private const string Preamble = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using FlowX;

        namespace Sample;

        public sealed record PlaceOrder(string Sku, int Quantity);
        public sealed record OrderResult(string Id);
        public sealed record Released(string Id);

        [Capability("inventory.reserve", Version = "1.0.0", Authorization = Authorization.Authenticated, Idempotent = true)]
        public sealed class ReserveInventory : ICapability<PlaceOrder, OrderResult>
        {
            public ValueTask<Result<OrderResult>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new OrderResult(input.Sku)));
        }

        [Capability("inventory.release", Version = "1.0.0", Authorization = Authorization.Authenticated, Idempotent = true)]
        public sealed class ReleaseInventory : ICapability<OrderResult, Released>
        {
            public ValueTask<Result<Released>> ExecuteAsync(OrderResult input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new Released(input.Id)));
        }
        """;

    /// <summary>A flow whose single step carries <paramref name="step"/>'s builder calls.</summary>
    private static string FlowWith(string step, string policies) =>
        Preamble + "\n\n" + $$"""
        public static class Policies
        {
            {{policies}}
        }

        [Flow("order.place", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "orders")]
        public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
        {
            protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                .Step<ReserveInventory>(){{step}}
                .Return(ctx => new OrderResult("id"));
        }
        """;

    private static string[] Analyze(string source)
    {
        GeneratorHarness.CompileErrorsIn(source).ShouldBeEmpty();

        return GeneratorHarness.Analyze(source, new DeclaredPolicyAnalyzer());
    }

    private static string[] Messages(string source)
    {
        GeneratorHarness.CompileErrorsIn(source).ShouldBeEmpty();

        return GeneratorHarness.AnalyzeWithMessages(source, new DeclaredPolicyAnalyzer());
    }

    // ------------------------------------------------------- FLOWX1032 must fire

    /// <summary>
    /// The ordinary admission stance: two kinds, neither of them executed by anything.
    /// </summary>
    /// <remarks>
    /// <strong>This was <c>AForwardPolicySetIsReported</c> and its set was
    /// <c>Timeout · Retry · CircuitBreaker</c>.</strong> All three of those are applied now,
    /// so the set that once stood for "the ordinary case this rule is about" is exactly the
    /// set the rule must be silent on — asserted below. What is left of FLOWX1032 is stage 1,
    /// stage 3, stage 5 and the audit.
    /// </remarks>
    [Fact]
    public void AnInertPolicySetIsReported() =>
        Analyze(FlowWith(
            ".WithPolicy(Policies.Admission)",
            """
            public static readonly PolicySet Admission = PolicySet.Named("admission")
                .RateLimit(permits: 5, TimeSpan.FromSeconds(1))
                .Idempotency(TimeSpan.FromHours(1));
            """))
            .ShouldBe(["FLOWX1032"]);

    /// <summary>The message names the set the author wrote and every kind it drops.</summary>
    /// <remarks>
    /// Naming the kinds is what stops the message being generically true of every policy
    /// set: an author who declared four policies and is told three of them are inert can see
    /// which one is not.
    /// </remarks>
    [Fact]
    public void TheMessageNamesTheSetAndEveryInertKind()
    {
        var messages = Messages(FlowWith(
            ".WithPolicy(Policies.Admission)",
            """
            public static readonly PolicySet Admission = PolicySet.Named("admission")
                .RateLimit(permits: 5, TimeSpan.FromSeconds(1))
                .Idempotency(TimeSpan.FromHours(1))
                .Cache(TimeSpan.FromSeconds(10));
            """));

        messages.ShouldHaveSingleItem();
        messages[0].ShouldContain("Policies.Admission");
        messages[0].ShouldContain("RateLimit");
        messages[0].ShouldContain("Idempotency");

        messages[0].Contains("Cache", StringComparison.Ordinal).ShouldBeFalse(
            "The Cache in the set above is deliberate: this test is about the message naming " +
            "the kinds that are dropped and only those, and stage 5 now executes.");
    }

    /// <summary>
    /// A set whose every kind is stage 4 is silent, which is what the rule narrowing means.
    /// </summary>
    /// <remarks>
    /// The set <c>docs/10 §4</c>, <c>FLOWX1032</c>'s own page and <c>samples/banking</c> all
    /// use as the example of a declaration nothing applies. It is applied: the timeout is
    /// armed, the three attempts are made and the breaker opens. A rule that still reported
    /// here would be telling a payments team their resilience stance is decorative when it is
    /// not, which is worse than the rule not existing.
    /// </remarks>
    [Fact]
    public void AWhollyExecutedResilienceSetIsSilent() =>
        Analyze(FlowWith(
            ".WithPolicy(Policies.ExternalRead)",
            """
            public static readonly PolicySet ExternalRead = PolicySet.Named("external-read")
                .Timeout(TimeSpan.FromSeconds(3))
                .Retry(attempts: 3)
                .CircuitBreaker(failureRatio: 0.5, breakDuration: TimeSpan.FromSeconds(30))
                .Bulkhead(maxConcurrency: 4);
            """))
            .ShouldBeEmpty();

    /// <summary>
    /// <c>Audit</c> is silent, and it is still a stage-7 <c>Consistency</c> policy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Inverted, and the correction it was written to pin is unaffected.</strong> This
    /// test asserted that <c>Audit</c> <em>was</em> reported although it shared a stage with
    /// <c>CompensationRetry</c>, which runs — the evidence that the cut was never a range of
    /// stages. Stage 7's audit now runs too, so the assertion flips; the fact it was pinning
    /// does not, because <c>RateLimit</c> at stage 1 is still reported while <c>Cache</c> at
    /// stage 5 is not. No line drawn by stage number has ever separated the two halves, and
    /// after this change it separates them in the other direction as well.
    /// </para>
    /// <para>
    /// The redact list is kept in the declaration deliberately: it is the argument that used to
    /// name nothing, and a set carrying one must now be as silent as one without.
    /// </para>
    /// </remarks>
    [Fact]
    public void AuditIsSilentAlthoughItRunsAtTheSameStageAsCompensationRetry() =>
        Analyze(FlowWith(
            ".WithPolicy(Policies.Audited)",
            """
            public static readonly PolicySet Audited = PolicySet.Named("audited")
                .Audit("financial", "Iban");
            """))
            .ShouldBeEmpty();

    /// <summary>Each of the two kinds nothing applies, on its own.</summary>
    /// <remarks>
    /// A theory rather than one set with both in it, so that a rule which happened to
    /// recognise one of them and miss the other fails on the row that names it. The kinds that
    /// are applied get the same treatment in the theory below, which is the half that goes
    /// wrong silently: a rule reporting a policy that runs looks like a rule working.
    /// <para>
    /// <strong>Two rows shorter than it was.</strong> <c>Cache</c> and <c>Audit</c> moved to
    /// the silence theory when stage 5 and stage 7's audit landed.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("RateLimit(permits: 5, TimeSpan.FromSeconds(1))", "RateLimit")]
    [InlineData("Idempotency(TimeSpan.FromHours(1))", "Idempotency")]
    public void EveryKindNothingAppliesIsReported(string declaration, string kind)
    {
        var messages = Messages(FlowWith(
            ".WithPolicy(Policies.OneKind)",
            $"""
            public static readonly PolicySet OneKind = PolicySet.Named("one-kind").{declaration};
            """));

        messages.ShouldHaveSingleItem();
        messages[0].ShouldContain(kind);
    }

    /// <summary>Each kind the engine now applies, on its own.</summary>
    /// <remarks>
    /// <strong>Every row of this theory used to be a row of the one above.</strong> The four
    /// stage-4 kinds left it when the policy engine landed; <c>Cache</c> and <c>Audit</c> left
    /// it when stage 5 and stage 7's audit did. The compensation retry has its own silence test
    /// further down, because it is silent for a different reason — it needs a compensation to
    /// wrap, and FLOWX1033 reports when it has none.
    /// </remarks>
    [Theory]
    [InlineData("Timeout(TimeSpan.FromSeconds(1))")]
    [InlineData("Retry(attempts: 2)")]
    [InlineData("CircuitBreaker(failureRatio: 0.5, breakDuration: TimeSpan.FromSeconds(1))")]
    [InlineData("Bulkhead(maxConcurrency: 4)")]
    [InlineData("Cache(TimeSpan.FromSeconds(1))")]
    [InlineData("Audit(\"category\")")]
    public void EveryExecutedKindIsSilent(string declaration) =>
        Analyze(FlowWith(
            ".WithPolicy(Policies.OneKind)",
            $"""
            public static readonly PolicySet OneKind = PolicySet.Named("one-kind").{declaration};
            """))
            .ShouldBeEmpty();

    /// <summary>A mixed set names the inert half and not the half that executes.</summary>
    /// <remarks>
    /// The shape <c>samples/banking</c>'s ledger steps had until stage 7's audit landed: one
    /// kind that does nothing beside a <c>Timeout</c> that is armed, an <c>Audit</c> that is
    /// now written and a <c>CompensationRetry</c> that runs. Naming any of the three that work
    /// would tell the author a control is off when it is on.
    /// </remarks>
    [Fact]
    public void AMixedSetNamesTheInertKindsOnly()
    {
        var messages = Messages(FlowWith(
            ".CompensateWith<ReleaseInventory>().WithPolicy(Policies.Ledger)",
            """
            public static readonly PolicySet Ledger = PolicySet.Named("ledger")
                .Timeout(TimeSpan.FromSeconds(5))
                .RateLimit(permits: 5, TimeSpan.FromSeconds(1))
                .Audit("financial")
                .CompensationRetry(attempts: 5);
            """));

        messages.ShouldHaveSingleItem();
        messages[0].ShouldContain("RateLimit");
        messages[0].ShouldNotContain("Timeout");
        messages[0].ShouldNotContain("Audit");
        messages[0].ShouldNotContain("CompensationRetry");
    }

    /// <summary>One report per <c>.WithPolicy(...)</c>, whatever the set's size.</summary>
    /// <remarks>
    /// A set of eight reported eight times on one line is how a catalogue gets suppressed
    /// wholesale — the argument <c>ExecutionProfileAnalyzer</c> already makes for reporting
    /// once at the declaration. Asserted through the message list, because the id list is
    /// de-duplicated and would pass against an analyzer reporting eight times.
    /// </remarks>
    [Fact]
    public void ReportedOncePerCallRatherThanOncePerPolicy() =>
        Messages(FlowWith(
            ".WithPolicy(Policies.Everything)",
            """
            public static readonly PolicySet Everything = PolicySet.Named("everything")
                .RateLimit(permits: 5, TimeSpan.FromSeconds(1))
                .Idempotency(TimeSpan.FromHours(1))
                .Timeout(TimeSpan.FromSeconds(3))
                .Retry(attempts: 3)
                .CircuitBreaker(failureRatio: 0.5, breakDuration: TimeSpan.FromSeconds(30))
                .Bulkhead(maxConcurrency: 4)
                .Cache(TimeSpan.FromSeconds(10))
                .Audit("category");
            """))
            .ShouldHaveSingleItem();

    /// <summary>Reported inside a conditional block, which is where half a real flow lives.</summary>
    /// <remarks>
    /// A rule that only walked the top-level chain would be silent on every step inside a
    /// <c>When</c>, a <c>Case</c> or a <c>Parallel</c> branch — and <c>samples/banking</c>
    /// declares its most expensive policy set inside two <c>Case</c> blocks.
    /// </remarks>
    [Fact]
    public void ReportedInsideAConditionalBlock() =>
        Analyze(FlowWith(
            """
            .When(
                ctx => ctx.Input.Quantity > 1,
                bulk => bulk.Step<ReserveInventory>().WithPolicy(Policies.Admitted))
            """,
            """
            public static readonly PolicySet Admitted = PolicySet.Named("admitted")
                .RateLimit(permits: 5, TimeSpan.FromSeconds(30));
            """))
            .ShouldBe(["FLOWX1032"]);

    // ------------------------------------------------------ FLOWX1032 must not fire

    /// <summary>A compensation-only set is silent — it is the one set that does what it says.</summary>
    /// <remarks>
    /// The shape <c>PolicySet.CompensationDefault</c> has: <c>CompensationRetry(attempts: 5)</c>
    /// and nothing else. A rule that reported on it would be telling authors that the one
    /// policy this runtime executes does not execute.
    /// </remarks>
    [Fact]
    public void ACompensationRetryOnlySetIsSilent() =>
        Analyze(FlowWith(
            ".CompensateWith<ReleaseInventory>().WithPolicy(Policies.Undo)",
            """
            public static readonly PolicySet Undo = PolicySet.Named("undo")
                .CompensationRetry(attempts: 3);
            """))
            .ShouldBeEmpty();

    /// <summary>A step that declares no policy set is silent.</summary>
    [Fact]
    public void AStepWithNoPolicyIsSilent() =>
        Analyze(FlowWith(string.Empty, "public static readonly int Unused = 0;"))
            .ShouldBeEmpty();

    /// <summary>
    /// A set the compiler cannot resolve to a declared initialiser names no inert kinds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>PolicySetReader</c> returns nothing rather than guessing, which is the restriction
    /// FLOWX1014 and FLOWX1019 already work under and the one <c>FlowEmitter</c> works under:
    /// it emits no chain for a set whose kinds it could not read. FLOWX1032 naming a kind here
    /// would name a policy the author cannot find, and could name a set that in fact holds
    /// nothing but a compensation retry.
    /// </para>
    /// <para>
    /// <strong>It is not silent any more, and that is the change.</strong> This case used to
    /// assert an empty report, which recorded five rules and two artifacts agreeing to say
    /// nothing about a whole declared set. <a href="../../docs/diagnostics/FLOWX1036.md">FLOWX1036</a>
    /// is the one report that is honest here: not <em>these kinds do not execute</em>, which
    /// the compiler cannot know, but <em>none of this reaches anything</em>, which it can.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnUnresolvableSetNamesNoInertKinds() =>
        Analyze(FlowWith(
            ".WithPolicy(Policies.Build())",
            """
            public static PolicySet Build() => PolicySet.Named("built").Timeout(TimeSpan.FromSeconds(1));
            """))
            .ShouldBe(["FLOWX1036"]);

    /// <summary>
    /// <c>PolicySet.CompensationDefault</c> is silent, and it arrives as metadata.
    /// </summary>
    /// <remarks>
    /// The set this file's own header calls "the one set in the whole DSL that does exactly
    /// what it says". It is declared in <c>FlowX.Abstractions</c> and so has no
    /// <c>DeclaringSyntaxReferences</c> in any consuming compilation;
    /// <c>PolicySetReader</c> knows its composition anyway. Silent on all three of the rules
    /// that could speak: it declares no inert kind, the step has a compensation, and it is
    /// not unreadable.
    /// </remarks>
    [Fact]
    public void TheDocumentedDefaultCompensationSetIsSilent() =>
        Analyze(FlowWith(
            ".CompensateWith<ReleaseInventory>().WithPolicy(PolicySet.CompensationDefault)",
            "public static readonly int Unused = 0;"))
            .ShouldBeEmpty();

    // ------------------------------------------------------- FLOWX1033 must fire

    /// <summary>
    /// A compensation retry on a step with no compensation: dropped by the emitter, published
    /// by the manifest.
    /// </summary>
    /// <remarks>
    /// <c>FlowEmitter.PolicyArguments</c> emits <c>compensationPolicies:</c> only when
    /// <c>step.IsCompensable</c>, and its own comment says the drop wants a diagnostic.
    /// </remarks>
    [Fact]
    public void ACompensationRetryOnANonCompensableStepIsReported() =>
        Analyze(FlowWith(
            ".WithPolicy(Policies.Undo)",
            """
            public static readonly PolicySet Undo = PolicySet.Named("undo")
                .CompensationRetry(attempts: 5);
            """))
            .ShouldBe(["FLOWX1033"]);

    /// <summary>The message names the step and the set that declares the retry.</summary>
    [Fact]
    public void TheCompensationMessageNamesTheStepAndTheSet()
    {
        var messages = Messages(FlowWith(
            ".WithPolicy(Policies.Undo)",
            """
            public static readonly PolicySet Undo = PolicySet.Named("undo")
                .CompensationRetry(attempts: 5);
            """));

        messages.ShouldHaveSingleItem();
        messages[0].ShouldContain("ReserveInventory");
        messages[0].ShouldContain("Policies.Undo");
    }

    /// <summary>Both rules fire on a mixed set attached to a step with no compensation.</summary>
    /// <remarks>
    /// The two findings are independent and both are true: the forward half is carried and
    /// unapplied, and the compensation half is not carried at all. Collapsing them into one
    /// report would leave whichever id was dropped un-suppressible on its own.
    /// </remarks>
    [Fact]
    public void AMixedSetOnANonCompensableStepReportsBoth() =>
        Analyze(FlowWith(
            ".WithPolicy(Policies.Ledger)",
            """
            public static readonly PolicySet Ledger = PolicySet.Named("ledger")
                .RateLimit(permits: 5, TimeSpan.FromSeconds(1))
                .CompensationRetry(attempts: 5);
            """))
            .ShouldBe(["FLOWX1032", "FLOWX1033"]);

    // ------------------------------------------------------ FLOWX1033 must not fire

    /// <summary>A compensable step is silent, in either order of the two calls.</summary>
    /// <remarks>
    /// <c>.CompensateWith&lt;T&gt;()</c> and <c>.WithPolicy(...)</c> both return
    /// <c>IStepBuilder</c>, so both orders are legal C#. A rule that read only the receiver
    /// chain would fire on the second of these and be a rule that reports on correct code
    /// half the time — the same trap <c>ReportCompensationPolicyConflicts</c> documents for
    /// FLOWX1014.
    /// </remarks>
    [Theory]
    [InlineData(".CompensateWith<ReleaseInventory>().WithPolicy(Policies.Undo)")]
    [InlineData(".WithPolicy(Policies.Undo).CompensateWith<ReleaseInventory>()")]
    public void ACompensableStepIsSilentInEitherOrder(string step) =>
        Analyze(FlowWith(
            step,
            """
            public static readonly PolicySet Undo = PolicySet.Named("undo")
                .CompensationRetry(attempts: 3);
            """))
            .ShouldBeEmpty();

    /// <summary>A set with no compensation retry never reports this rule.</summary>
    /// <remarks>
    /// The set is an <c>Audit</c> rather than the <c>Timeout</c> it used to be, so that
    /// FLOWX1032 still speaks and the assertion stays a statement about FLOWX1033's silence
    /// rather than about an empty report — which would pass against an analyzer that had
    /// stopped running.
    /// </remarks>
    [Fact]
    public void ASetWithoutACompensationRetryDoesNotReportTheDrop() =>
        Analyze(FlowWith(
            ".WithPolicy(Policies.Forward)",
            """
            public static readonly PolicySet Forward = PolicySet.Named("forward")
                .RateLimit(permits: 5, TimeSpan.FromSeconds(1));
            """))
            .ShouldBe(["FLOWX1032"]);

    /// <summary>An unresolvable set reports neither FLOWX1032 nor FLOWX1033.</summary>
    /// <remarks>
    /// Silent exactly where the emitter is silent, on both of the rules that name what is in
    /// a set: the compiler cannot see the <c>CompensationRetry</c>, so it cannot say the drop
    /// happened, and it cannot list a carried kind either. What it can say — that the whole
    /// set reaches nothing — is FLOWX1036 and is one report rather than two guesses.
    /// </remarks>
    [Fact]
    public void AnUnresolvableSetReportsNeitherContentRule() =>
        Analyze(FlowWith(
            ".WithPolicy(Policies.Build())",
            """
            public static PolicySet Build() => PolicySet.Named("built").CompensationRetry(attempts: 5);
            """))
            .ShouldBe(["FLOWX1036"]);

    // ------------------------------------------------------------------ severity

    /// <summary>
    /// FLOWX1032 is a warning and FLOWX1033 is an error, and the split is the decision.
    /// </summary>
    /// <remarks>
    /// FLOWX1032 describes a platform gap P4 closes, so an error's only repair would delete
    /// the declaration P4 needs to find. FLOWX1033 describes a policy attached to nothing,
    /// which no release executes — and <c>StepNode.ForCapability</c> already refuses the
    /// shape with an <c>InvalidFlowPlanException</c>, exactly as <c>PolicyChain</c> refuses
    /// FLOWX1014's and FLOWX1018's, both of which are errors.
    /// </remarks>
    [Fact]
    public void TheTwoSeveritiesDiffer()
    {
        var reports = GeneratorHarness.Report(
            FlowWith(
                ".WithPolicy(Policies.Ledger)",
                """
                public static readonly PolicySet Ledger = PolicySet.Named("ledger")
                    .RateLimit(permits: 5, TimeSpan.FromSeconds(1))
                    .CompensationRetry(attempts: 5);
                """),
            new DeclaredPolicyAnalyzer());

        reports.Single(d => d.Id == "FLOWX1032").Severity.ShouldBe(
            DiagnosticSeverity.Warning,
            "An error's only repair is deleting the declaration P4 will need to find.");

        reports.Single(d => d.Id == "FLOWX1033").Severity.ShouldBe(
            DiagnosticSeverity.Error,
            "No release executes a compensation retry that has no compensation, and " +
            "StepNode.ForCapability already refuses the shape.");
    }

    // -------------------------------------------------------- FLOWX1034 must fire

    /// <summary>A second <c>.WithPolicy(...)</c> on one step is reported.</summary>
    /// <remarks>
    /// <para>
    /// <c>StepModel.WithPolicy</c> assigns rather than accumulates, so the first set reaches
    /// no plan node and no manifest entry. <c>FlowPlanGeneratorTests</c> asserts the loss on
    /// the emitted artifacts; this asserts that the build says so.
    /// </para>
    /// <para>
    /// <strong>And FLOWX1032 speaks once, about the surviving set only.</strong> Both sets
    /// declare an <c>Audit</c>, so a rule that reported the discarded one too would report
    /// twice here. It must not: FLOWX1032's message says the plan and the manifest carry the
    /// kinds it names, and neither carries anything from a set the compiler threw away.
    /// </para>
    /// </remarks>
    [Fact]
    public void ASecondPolicySetOnOneStepIsReported()
    {
        var step = ".CompensateWith<ReleaseInventory>()" +
                   ".WithPolicy(Policies.Ledger).WithPolicy(Policies.Undo)";

        const string policies = """
            public static readonly PolicySet Ledger = PolicySet.Named("ledger")
                .RateLimit(permits: 5, TimeSpan.FromSeconds(1));

            public static readonly PolicySet Undo = PolicySet.Named("undo")
                .Idempotency(TimeSpan.FromHours(1))
                .CompensationRetry(attempts: 5);
            """;

        Analyze(FlowWith(step, policies)).ShouldBe(["FLOWX1032", "FLOWX1034"]);

        Messages(FlowWith(step, policies))
            .Count(m => m.StartsWith("FLOWX1032", StringComparison.Ordinal))
            .ShouldBe(1, "The discarded set is carried by nothing, so nothing carries it unapplied.");
    }

    /// <summary>The message names the set that is dropped and the one that replaces it.</summary>
    /// <remarks>
    /// Both, because either alone is unactionable: the author has written two names on one
    /// step and needs to be told which of the two the compiler kept.
    /// </remarks>
    [Fact]
    public void TheSupersededMessageNamesBothSets()
    {
        var message = Messages(FlowWith(
            ".WithPolicy(Policies.Ledger).WithPolicy(Policies.Admission)",
            """
            public static readonly PolicySet Ledger = PolicySet.Named("ledger")
                .Timeout(TimeSpan.FromSeconds(5));

            public static readonly PolicySet Admission = PolicySet.Named("admission")
                .RateLimit(permits: 5, TimeSpan.FromSeconds(1));
            """))
            .Single(m => m.StartsWith("FLOWX1034", StringComparison.Ordinal));

        message.ShouldContain("Policies.Ledger");
        message.ShouldContain("Policies.Admission");
    }

    /// <summary>The report lands on the call that is dropped, not on the one that survives.</summary>
    /// <remarks>
    /// Reporting on the survivor would put the message on the line that is working, and a
    /// <c>#pragma</c> aimed at it would silence the wrong call.
    /// </remarks>
    [Fact]
    public void TheSupersededReportLandsOnTheDroppedCall()
    {
        var source = FlowWith(
            ".WithPolicy(Policies.Ledger).WithPolicy(Policies.Admission)",
            """
            public static readonly PolicySet Ledger = PolicySet.Named("ledger")
                .Timeout(TimeSpan.FromSeconds(5));

            public static readonly PolicySet Admission = PolicySet.Named("admission")
                .RateLimit(permits: 5, TimeSpan.FromSeconds(1));
            """);

        var report = GeneratorHarness.Report(source, new DeclaredPolicyAnalyzer())
            .Single(d => d.Id == "FLOWX1034");

        source[report.Location.SourceSpan.Start..].ShouldStartWith(
            "WithPolicy(Policies.Ledger)",
            customMessage: "The first WithPolicy is the one whose set is thrown away, so it " +
                           "is the one to point at.");
    }

    /// <summary>Three calls report twice: every set but the last is lost.</summary>
    [Fact]
    public void EverySupersededCallIsReported() =>
        Messages(FlowWith(
            ".WithPolicy(Policies.A).WithPolicy(Policies.B).WithPolicy(Policies.C)",
            """
            public static readonly PolicySet A = PolicySet.Named("a").Timeout(TimeSpan.FromSeconds(1));
            public static readonly PolicySet B = PolicySet.Named("b").Timeout(TimeSpan.FromSeconds(2));
            public static readonly PolicySet C = PolicySet.Named("c").Timeout(TimeSpan.FromSeconds(3));
            """))
            .Count(m => m.StartsWith("FLOWX1034", StringComparison.Ordinal))
            .ShouldBe(2);

    // ------------------------------------------------------ FLOWX1034 must not fire

    /// <summary>One set per step is silent, whichever side of the compensation it sits on.</summary>
    [Theory]
    [InlineData(".CompensateWith<ReleaseInventory>().WithPolicy(Policies.Undo)")]
    [InlineData(".WithPolicy(Policies.Undo).CompensateWith<ReleaseInventory>()")]
    public void OneSetPerStepIsSilent(string step) =>
        Analyze(FlowWith(
            step,
            """
            public static readonly PolicySet Undo = PolicySet.Named("undo")
                .CompensationRetry(attempts: 3);
            """))
            .ShouldBeEmpty();

    /// <summary>Two sets on two steps is the ordinary case and reports nothing.</summary>
    /// <remarks>
    /// The walk is bounded by <c>IStepBuilder</c>'s two methods, so a <c>Step</c> between the
    /// two calls ends the segment. Without that bound this rule would fire on every flow that
    /// declares a policy twice, which is every flow in <c>samples/banking</c>.
    /// </remarks>
    [Fact]
    public void TwoSetsOnTwoStepsAreSilent()
    {
        var source = Preamble + "\n\n" + """
            public static class Policies
            {
                public static readonly PolicySet Undo = PolicySet.Named("undo")
                    .CompensationRetry(attempts: 3);
            }

            [Flow("order.place", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "orders")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>().CompensateWith<ReleaseInventory>().WithPolicy(Policies.Undo)
                    .Step<ReserveInventory>().CompensateWith<ReleaseInventory>().WithPolicy(Policies.Undo)
                    .Return(ctx => new OrderResult("id"));
            }
            """;

        Analyze(source).ShouldBeEmpty();
    }

    // -------------------------------------------------------- FLOWX1035 must fire

    /// <summary>A compensation retry of one attempt is reported.</summary>
    /// <remarks>
    /// <c>CompensationPolicy.IsRetrying</c> is <c>Attempts &gt; 1</c>, so the plan's
    /// <c>HasCompensationPolicies</c> stays false and the engine takes
    /// <c>CompensationPolicy.None</c> — while the manifest publishes the kind with no
    /// parameters and reads as a retried undo.
    /// </remarks>
    [Theory]
    [InlineData("attempts: 1")]
    [InlineData("1")]
    [InlineData("attempts: 0")]
    [InlineData("attempts: -1")]
    public void ACompensationRetryThatRetriesNothingIsReported(string argument) =>
        Analyze(FlowWith(
            ".CompensateWith<ReleaseInventory>().WithPolicy(Policies.Undo)",
            $"""
            public static readonly PolicySet Undo = PolicySet.Named("undo")
                .CompensationRetry({argument});
            """))
            .ShouldBe(["FLOWX1035"]);

    /// <summary>The message names the step, the set and the count that was written.</summary>
    [Fact]
    public void TheSingleAttemptMessageNamesTheStepTheSetAndTheCount()
    {
        var message = Messages(FlowWith(
            ".CompensateWith<ReleaseInventory>().WithPolicy(Policies.Undo)",
            """
            public static readonly PolicySet Undo = PolicySet.Named("undo")
                .CompensationRetry(attempts: 1);
            """))
            .Single(m => m.StartsWith("FLOWX1035", StringComparison.Ordinal));

        message.ShouldContain("ReserveInventory");
        message.ShouldContain("Policies.Undo");
        message.ShouldContain("1");
    }

    // ------------------------------------------------------ FLOWX1035 must not fire

    /// <summary>Two attempts is a retry, and the one policy an undo can carry.</summary>
    [Theory]
    [InlineData("attempts: 2")]
    [InlineData("attempts: 5")]
    public void ARealCompensationRetryIsSilent(string argument) =>
        Analyze(FlowWith(
            ".CompensateWith<ReleaseInventory>().WithPolicy(Policies.Undo)",
            $"""
            public static readonly PolicySet Undo = PolicySet.Named("undo")
                .CompensationRetry({argument});
            """))
            .ShouldBeEmpty();

    /// <summary>
    /// An attempt count that is not a literal is silent.
    /// </summary>
    /// <remarks>
    /// A set whose count comes from configuration is exactly the case <c>FlowEmitter</c>
    /// copies verbatim rather than folding, and the restriction FLOWX1019 works under: each
    /// spelling this does not recognise costs a false negative, never a wrong number.
    /// </remarks>
    [Fact]
    public void ANonLiteralAttemptCountIsSilent() =>
        Analyze(FlowWith(
            ".CompensateWith<ReleaseInventory>().WithPolicy(Policies.Undo)",
            """
            public static int Configured => 1;

            public static readonly PolicySet Undo = PolicySet.Named("undo")
                .CompensationRetry(Configured);
            """))
            .ShouldBeEmpty();

    /// <summary>
    /// A single attempt on a step with no compensation reports FLOWX1033 and not this.
    /// </summary>
    /// <remarks>
    /// FLOWX1033 is the stronger statement and the error: the declaration reaches no plan
    /// node at all, so how many attempts it asked for is not the interesting part. Two
    /// reports on one line would leave the author choosing which to act on.
    /// </remarks>
    [Fact]
    public void ASingleAttemptOnANonCompensableStepReportsTheDropOnly() =>
        Analyze(FlowWith(
            ".WithPolicy(Policies.Undo)",
            """
            public static readonly PolicySet Undo = PolicySet.Named("undo")
                .CompensationRetry(attempts: 1);
            """))
            .ShouldBe(["FLOWX1033"]);

    // -------------------------------------------------------- FLOWX1036 must fire

    /// <summary>A set declared in a referenced assembly is reported.</summary>
    /// <remarks>
    /// The case the rule exists for and the one no single-compilation test can reach: the
    /// symbol has no <c>DeclaringSyntaxReferences</c>, so the emitter writes no chain, the
    /// manifest publishes no policies, and the set's <c>CompensationRetry</c> — the one policy
    /// this runtime executes — does not run.
    /// </remarks>
    [Fact]
    public void ASetFromAReferencedAssemblyIsReported() =>
        AnalyzeConsumer(".CompensateWith<ReleaseInventory>().WithPolicy(Shared.Ledger)")
            .ShouldBe(["FLOWX1036"]);

    /// <summary>The message names the expression the author wrote.</summary>
    [Fact]
    public void TheUnreadableMessageNamesTheExpression() =>
        MessagesFromConsumer(".CompensateWith<ReleaseInventory>().WithPolicy(Shared.Ledger)")
            .Single(m => m.StartsWith("FLOWX1036", StringComparison.Ordinal))
            .ShouldContain("Shared.Ledger");

    // ------------------------------------------------------ FLOWX1036 must not fire

    /// <summary>
    /// <c>PolicySet.CompensationDefault</c> comes from a referenced assembly and resolves.
    /// </summary>
    /// <remarks>
    /// The exemption, asserted against a real metadata reference rather than against the
    /// in-tree source: <c>PolicySetReader</c> carries the composition of <c>PolicySet</c>'s
    /// own well-known sets, and <c>PolicySetContentsAreThePinnedOnes</c> is what keeps that
    /// carried copy honest.
    /// </remarks>
    [Fact]
    public void TheDocumentedDefaultIsNotReportedAsUnreadable() =>
        AnalyzeConsumer(".CompensateWith<ReleaseInventory>().WithPolicy(PolicySet.CompensationDefault)")
            .ShouldBeEmpty();

    /// <summary>A set declared in the compilation being built is silent.</summary>
    [Fact]
    public void ASetDeclaredInThisCompilationIsNotReportedAsUnreadable() =>
        Analyze(FlowWith(
            ".CompensateWith<ReleaseInventory>().WithPolicy(Policies.Undo)",
            """
            public static readonly PolicySet Undo = PolicySet.Named("undo")
                .CompensationRetry(attempts: 5);
            """))
            .ShouldBeEmpty();

    /// <summary>
    /// An argument that does not bind to a policy set at all is silent.
    /// </summary>
    /// <remarks>
    /// The compilation already reports it. A second message about a half-typed expression is
    /// noise on every keystroke between <c>.WithPolicy(</c> and the name.
    /// </remarks>
    [Fact]
    public void AnArgumentThatDoesNotBindIsSilent() =>
        GeneratorHarness.Analyze(
            FlowWith(".WithPolicy(Policies.NoSuchSet)", "public static readonly int Unused = 0;"),
            new DeclaredPolicyAnalyzer())
            .ShouldBeEmpty();

    // ------------------------------------------------------------- the severities

    /// <summary>
    /// FLOWX1034 is an error; FLOWX1035 and FLOWX1036 are warnings.
    /// </summary>
    /// <remarks>
    /// FLOWX1034 deletes a declared control from both published artifacts and has no
    /// legitimate program, which is FLOWX1033's argument. The other two leave the source
    /// correct — a shared policy library is a reasonable design, and an attempt count is a
    /// value the author owns — which is FLOWX1032's.
    /// </remarks>
    [Fact]
    public void TheNewSeveritiesFollowTheirNeighbours()
    {
        GeneratorHarness.Report(
            FlowWith(
                ".WithPolicy(Policies.A).WithPolicy(Policies.B)",
                """
                public static readonly PolicySet A = PolicySet.Named("a").Timeout(TimeSpan.FromSeconds(1));
                public static readonly PolicySet B = PolicySet.Named("b").Timeout(TimeSpan.FromSeconds(2));
                """),
            new DeclaredPolicyAnalyzer())
            .Single(d => d.Id == "FLOWX1034").Severity.ShouldBe(DiagnosticSeverity.Error);

        GeneratorHarness.Report(
            FlowWith(
                ".CompensateWith<ReleaseInventory>().WithPolicy(Policies.Undo)",
                """
                public static readonly PolicySet Undo = PolicySet.Named("undo")
                    .CompensationRetry(attempts: 1);
                """),
            new DeclaredPolicyAnalyzer())
            .Single(d => d.Id == "FLOWX1035").Severity.ShouldBe(DiagnosticSeverity.Warning);

        GeneratorHarness.Report(
            ConsumerCompilation(".WithPolicy(Shared.Ledger)"),
            new DeclaredPolicyAnalyzer())
            .Single(d => d.Id == "FLOWX1036").Severity.ShouldBe(DiagnosticSeverity.Warning);
    }

    // ----------------------------------------- a policy set in a referenced assembly

    /// <summary>A shared policy library, compiled to an image and referenced as one.</summary>
    private static readonly Lazy<PortableExecutableReference> Library = new(CompileLibrary);

    private static PortableExecutableReference CompileLibrary()
    {
        var compilation = GeneratorHarness.CompilationOf("Shared.Policies", [], ("Shared.cs", """
            using System;
            using FlowX;

            namespace SharedLibrary;

            public static class Shared
            {
                public static readonly PolicySet Ledger = PolicySet.Named("ledger")
                    .Timeout(TimeSpan.FromSeconds(5))
                    .CompensationRetry(attempts: 5);
            }
            """));

        using var image = new System.IO.MemoryStream();
        var emitted = compilation.Emit(image);

        emitted.Success.ShouldBeTrue(string.Join(
            "\n",
            emitted.Diagnostics.Where(static d => d.Severity == DiagnosticSeverity.Error)));

        return MetadataReference.CreateFromImage(image.ToArray());
    }

    private static Microsoft.CodeAnalysis.CSharp.CSharpCompilation ConsumerCompilation(string step)
    {
        var compilation = GeneratorHarness.CompilationOf(
            "FlowX.AnalyzerTests",
            [Library.Value],
            ("/src/Flows/Sample.cs", Preamble.Replace(
                "namespace Sample;",
                "using SharedLibrary;\n\nnamespace Sample;",
                StringComparison.Ordinal) + "\n\n" + $$"""
                [Flow("order.place", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "orders")]
                public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
                {
                    protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                        .Step<ReserveInventory>(){{step}}
                        .Return(ctx => new OrderResult("id"));
                }
                """));

        compilation.GetDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
            .Select(static d => d.Id + ": " + d.GetMessage(System.Globalization.CultureInfo.InvariantCulture))
            .ShouldBeEmpty();

        return compilation;
    }

    private static string[] AnalyzeConsumer(string step) =>
        [.. GeneratorHarness.Report(ConsumerCompilation(step), new DeclaredPolicyAnalyzer())
            .Select(static d => d.Id)
            .Distinct()
            .OrderBy(static id => id, StringComparer.Ordinal)];

    private static string[] MessagesFromConsumer(string step) =>
        [.. GeneratorHarness.Report(ConsumerCompilation(step), new DeclaredPolicyAnalyzer())
            .Select(static d => d.Id + ": " + d.GetMessage(System.Globalization.CultureInfo.InvariantCulture))];

    /// <summary>Both reports land on the <c>WithPolicy</c> identifier, not on the chain.</summary>
    /// <remarks>
    /// A fluent chain nests its receiver inside every later call, so an invocation's span
    /// begins at the head of the chain — reporting there would put every finding in a flow on
    /// the first line of <c>Define</c>, which is <c>ChainLink.CallLocation</c>'s own reason
    /// for existing.
    /// </remarks>
    [Fact]
    public void BothReportsLandOnTheWithPolicyCall()
    {
        var source = FlowWith(
            ".WithPolicy(Policies.Ledger)",
            """
            public static readonly PolicySet Ledger = PolicySet.Named("ledger")
                .Timeout(TimeSpan.FromSeconds(5))
                .CompensationRetry(attempts: 5);
            """);

        foreach (var report in GeneratorHarness.Report(source, new DeclaredPolicyAnalyzer()))
        {
            var span = report.Location.SourceSpan;

            source.Substring(span.Start, span.Length).ShouldBe(
                "WithPolicy",
                $"{report.Id} should point at the call the author would edit.");
        }
    }
}
