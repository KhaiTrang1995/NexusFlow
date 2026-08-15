using System;
using System.Linq;
using FlowX.Compiler.Analysis;
using Microsoft.CodeAnalysis;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// FLOWX1033 through FLOWX1036 and FLOWX1040 — what a <c>.WithPolicy(...)</c> declares, and
/// the ways it can reach less than it looks like it does.
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
/// <strong>The whole of the silence used to be FLOWX1032's subject, and this file is where
/// its deletion is checked.</strong> That rule reported a declared kind nothing applied; it
/// was narrowed twice and then deleted, because every kind <c>PolicySet</c> offers is now
/// read by <c>StepPolicy.From</c>, <c>StepAudit.From</c> or <c>CompensationPolicy.From</c>.
/// Each of its firing tests has been kept and inverted rather than dropped, so the sets that
/// once stood for "a declaration nothing applies" are now the sets that must produce nothing —
/// which is the assertion that would fail if a rule of that shape came back.
/// </para>
/// <para>
/// <strong>No line drawn by stage number ever separated anything here, and that stays
/// checked.</strong> <c>Audit</c> and <c>CompensationRetry</c> are both
/// <c>PolicyStage.Consistency</c> and reach the engine through two different resolvers, one of
/// which needs a compensation to wrap and one of which does not; FLOWX1033 is about exactly
/// that difference and about no stage at all.
/// </para>
/// <para>
/// The severities are pinned by tests of their own, because the split is the decision these
/// rules turn on and the one a later reader is most likely to tidy into agreement. The
/// reasoning is on the descriptors and on the pages.
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

    // --------------------------------------------------- no declared kind is inert

    /// <summary>
    /// The set that was this file's example of a declaration nothing applied.
    /// </summary>
    /// <remarks>
    /// <strong>This was <c>AnInertPolicySetIsReported</c>, and before that
    /// <c>AForwardPolicySetIsReported</c> over <c>Timeout · Retry · CircuitBreaker</c>.</strong>
    /// Each time a stage landed, the set that stood for "the ordinary case this rule is about"
    /// became a set the rule had to be silent on, and a new one was written. There is no new
    /// one: stage 5 consults the cache and stage 7 writes the record, so the assertion is
    /// inverted rather than re-aimed, and <c>FLOWX1032</c> is deleted rather than narrowed a
    /// third time.
    /// </remarks>
    [Fact]
    public void TheLastInertPolicySetIsSilent() =>
        Analyze(FlowWith(
            ".WithPolicy(Policies.Deferred)",
            """
            public static readonly PolicySet Deferred = PolicySet.Named("deferred")
                .Cache(TimeSpan.FromSeconds(10))
                .Audit("financial");
            """))
            .ShouldBeEmpty();

    /// <summary>A set spanning every executed stage reports nothing at all.</summary>
    /// <remarks>
    /// <strong>This was <c>TheMessageNamesTheSetAndEveryInertKind</c></strong>, which asserted
    /// that a mixed set's message named the inert kinds and not the executed ones. There is no
    /// message, because there are no inert kinds; what is worth keeping from it is the fixture
    /// — a set reaching stages 1, 3, 4, 5 and 7 at once — and the assertion that nothing in it
    /// is reported. A rule that came back for any one of these would be telling an author an
    /// enforced control is decorative, which is the failure the whole deletion is about.
    /// </remarks>
    [Fact]
    public void ASetSpanningEveryExecutedStageIsSilent() =>
        Analyze(FlowWith(
            ".WithPolicy(Policies.Deferred)",
            """
            public static readonly PolicySet Deferred = PolicySet.Named("deferred")
                .RateLimit(permits: 5, TimeSpan.FromSeconds(1))
                .Idempotency(TimeSpan.FromHours(1))
                .Timeout(TimeSpan.FromSeconds(1))
                .Cache(TimeSpan.FromSeconds(10))
                .Audit("financial");
            """))
            .ShouldBeEmpty();

    /// <summary>
    /// A set whose every kind is stage 4 is silent, which is what the first narrowing meant.
    /// </summary>
    /// <remarks>
    /// The set <c>docs/10 §4</c>, the deleted <c>FLOWX1032</c>'s own page and
    /// <c>samples/banking</c> all used as the example of a declaration nothing applies. It is
    /// applied: the timeout is armed, the three attempts are made and the breaker opens. A rule
    /// that still reported here would be telling a payments team their resilience stance is
    /// decorative when it is not, which is worse than the rule not existing.
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
    /// does not, because the two stage-7 kinds still reach the engine through two different
    /// resolvers — <c>StepAudit</c> and <c>CompensationPolicy</c> — and one of them still needs
    /// a compensation to wrap while the other does not. No line drawn by stage number ever
    /// separated anything here.
    /// </para>
    /// <para>
    /// The redact list is kept in the declaration deliberately: it is the argument that used to
    /// name nothing, and a set carrying one must be as silent as one without.
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

    /// <summary>Each of the eight kinds the engine applies to a forward step, on its own.</summary>
    /// <remarks>
    /// <para>
    /// <strong>Every row of this theory was once a row of a theory that asserted the
    /// opposite.</strong> The four stage-4 kinds moved when the policy engine landed;
    /// <c>RateLimit</c> and <c>Idempotency</c> when stages 1 and 3 did; <c>Cache</c> and
    /// <c>Audit</c> when stages 5 and 7 did. The theory they moved out of is empty and is
    /// gone with the rule it belonged to. The compensation retry has its own silence test
    /// further down, because it is silent for a different reason — it needs a compensation to
    /// wrap, and FLOWX1033 reports when it has none.
    /// </para>
    /// <para>
    /// A theory rather than one set with all eight in it, so an analyzer that regressed on a
    /// single kind fails on the row that names it. This is the half that goes wrong quietly:
    /// a rule reporting a policy that runs tells an author their enforced control is
    /// decorative, and the first thing they do about it is suppress the catalogue.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("Timeout(TimeSpan.FromSeconds(1))")]
    [InlineData("Retry(attempts: 2)")]
    [InlineData("CircuitBreaker(failureRatio: 0.5, breakDuration: TimeSpan.FromSeconds(1))")]
    [InlineData("Bulkhead(maxConcurrency: 4)")]
    [InlineData("RateLimit(permits: 5, TimeSpan.FromSeconds(1))")]
    [InlineData("Idempotency(TimeSpan.FromHours(1))")]
    [InlineData("Cache(TimeSpan.FromSeconds(1))")]
    [InlineData("Audit(\"category\")")]
    public void EveryExecutedKindIsSilent(string declaration) =>
        Analyze(FlowWith(
            ".WithPolicy(Policies.OneKind)",
            $"""
            public static readonly PolicySet OneKind = PolicySet.Named("one-kind").{declaration};
            """))
            .ShouldBeEmpty();

    /// <summary>The mixed set <c>samples/banking</c>'s ledger steps carry is wholly silent.</summary>
    /// <remarks>
    /// <strong>This was <c>AMixedSetNamesTheInertKindsOnly</c></strong>, which asserted that a
    /// message named the <c>RateLimit</c> and not the <c>Timeout</c>, <c>Audit</c> and
    /// <c>CompensationRetry</c> beside it. The set is unchanged and the assertion is now that
    /// there is no message at all: four kinds, four stages, and every one of them applied.
    /// Naming any of them would tell the author a control is off when it is on.
    /// </remarks>
    [Fact]
    public void TheBankingLedgerSetIsWhollySilent() =>
        Messages(FlowWith(
            ".CompensateWith<ReleaseInventory>().WithPolicy(Policies.Ledger)",
            """
            public static readonly PolicySet Ledger = PolicySet.Named("ledger")
                .Timeout(TimeSpan.FromSeconds(5))
                .RateLimit(permits: 5, TimeSpan.FromSeconds(1))
                .Audit("financial")
                .CompensationRetry(attempts: 5);
            """))
            .ShouldBeEmpty();

    /// <summary>A set declaring every kind at once reports nothing.</summary>
    /// <remarks>
    /// <strong>This was <c>ReportedOncePerCallRatherThanOncePerPolicy</c></strong>, which
    /// pinned that a set of eight produced one report rather than eight — the argument
    /// <c>ExecutionProfileAnalyzer</c> makes for reporting once at the declaration. The
    /// property survives on the rules that still fire: <c>EverySupersededCallIsReported</c>
    /// pins it for FLOWX1034. What is left to assert about this fixture is that the largest
    /// declarable set in the DSL is entirely quiet, which is the whole of what deleting
    /// FLOWX1032 means.
    /// </remarks>
    [Fact]
    public void ASetDeclaringEveryKindReportsNothing() =>
        Messages(FlowWith(
            ".CompensateWith<ReleaseInventory>().WithPolicy(Policies.Everything)",
            """
            public static readonly PolicySet Everything = PolicySet.Named("everything")
                .RateLimit(permits: 5, TimeSpan.FromSeconds(1))
                .Idempotency(TimeSpan.FromHours(1))
                .Timeout(TimeSpan.FromSeconds(3))
                .Retry(attempts: 3)
                .CircuitBreaker(failureRatio: 0.5, breakDuration: TimeSpan.FromSeconds(30))
                .Bulkhead(maxConcurrency: 4)
                .Cache(TimeSpan.FromSeconds(10))
                .Audit("category")
                .CompensationRetry(attempts: 5);
            """))
            .ShouldBeEmpty();

    /// <summary>Reported inside a conditional block, which is where half a real flow lives.</summary>
    /// <remarks>
    /// <strong>Re-aimed from FLOWX1032 to FLOWX1033, and the property it pins is
    /// unchanged.</strong> A rule that only walked the top-level chain would be silent on every
    /// step inside a <c>When</c>, a <c>Case</c> or a <c>Parallel</c> branch — and
    /// <c>samples/banking</c> declares its most expensive policy set inside two <c>Case</c>
    /// blocks. FLOWX1032 was the rule that used to demonstrate the reach; it is deleted, so the
    /// demonstration moves to a rule that still fires on a set rather than being lost with it.
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
                .RateLimit(permits: 5, TimeSpan.FromSeconds(30))
                .CompensationRetry(attempts: 3);
            """))
            .ShouldBe(["FLOWX1033"]);

    // ------------------------------------------- no rule fires on a well-formed set

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
    /// it emits no chain for a set whose kinds it could not read. A rule naming a kind here
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

    /// <summary>
    /// The compensation half is reported on a mixed set, and the executed half is not.
    /// </summary>
    /// <remarks>
    /// <strong>This was <c>AMixedSetOnANonCompensableStepReportsBoth</c></strong>, and it
    /// asserted that FLOWX1032 and FLOWX1033 fired together and independently. Only one of the
    /// two findings is available now: the <c>RateLimit</c> beside the retry is carried
    /// <em>and applied</em>, so the report is FLOWX1033's alone. The fixture is kept because
    /// the risk it covers is unchanged — a rule that reported on the whole set rather than on
    /// the one kind it is about would fail here.
    /// </remarks>
    [Fact]
    public void AMixedSetOnANonCompensableStepReportsTheDropOnly() =>
        Analyze(FlowWith(
            ".WithPolicy(Policies.Ledger)",
            """
            public static readonly PolicySet Ledger = PolicySet.Named("ledger")
                .RateLimit(permits: 5, TimeSpan.FromSeconds(1))
                .CompensationRetry(attempts: 5);
            """))
            .ShouldBe(["FLOWX1033"]);

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
    /// <strong>The set carried a kind FLOWX1032 would report, so that the assertion was a
    /// statement about FLOWX1033's silence rather than about an empty report</strong> — which
    /// would pass against an analyzer that had stopped running. No such kind exists any more,
    /// so the second <c>.WithPolicy</c> below supplies the same guard: the analyzer is proved
    /// alive by FLOWX1034 on the same source, and FLOWX1033's absence is a real absence.
    /// </remarks>
    [Fact]
    public void ASetWithoutACompensationRetryDoesNotReportTheDrop() =>
        Analyze(FlowWith(
            ".WithPolicy(Policies.Forward).WithPolicy(Policies.Forward)",
            """
            public static readonly PolicySet Forward = PolicySet.Named("forward")
                .RateLimit(permits: 5, TimeSpan.FromSeconds(1));
            """))
            .ShouldBe(["FLOWX1034"]);

    /// <summary>An unresolvable set reports neither of the rules that read its contents.</summary>
    /// <remarks>
    /// Silent exactly where the emitter is silent, on both of the rules that name what is in
    /// a set: the compiler cannot see the <c>CompensationRetry</c>, so it cannot say the drop
    /// happened, and FLOWX1040 cannot ask about an <c>Idempotency</c> it cannot see either.
    /// What it can say — that the whole set reaches nothing — is FLOWX1036 and is one report
    /// rather than two guesses.
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
    /// FLOWX1033 is an error and FLOWX1035 is a warning, and the split is the decision.
    /// </summary>
    /// <remarks>
    /// <strong>This pinned FLOWX1032's warning against FLOWX1033's error while both
    /// existed.</strong> FLOWX1032 described a platform gap a later release would close, so an
    /// error's only repair would have deleted the declaration that release needed to find; it
    /// is deleted with the gap, and the remaining pair on the same DSL call carries the same
    /// shape of split. FLOWX1033 describes a policy attached to nothing, which no release
    /// executes — and <c>StepNode.ForCapability</c> already refuses the shape with an
    /// <c>InvalidFlowPlanException</c>, exactly as <c>PolicyChain</c> refuses FLOWX1014's and
    /// FLOWX1018's, both of which are errors. FLOWX1035 describes a count the author can
    /// change without deleting anything.
    /// </remarks>
    [Fact]
    public void TheTwoSeveritiesDiffer()
    {
        var dropped = GeneratorHarness.Report(
            FlowWith(
                ".WithPolicy(Policies.Ledger)",
                """
                public static readonly PolicySet Ledger = PolicySet.Named("ledger")
                    .RateLimit(permits: 5, TimeSpan.FromSeconds(1))
                    .CompensationRetry(attempts: 5);
                """),
            new DeclaredPolicyAnalyzer());

        dropped.Single(d => d.Id == "FLOWX1033").Severity.ShouldBe(
            DiagnosticSeverity.Error,
            "No release executes a compensation retry that has no compensation, and " +
            "StepNode.ForCapability already refuses the shape.");

        var single = GeneratorHarness.Report(
            FlowWith(
                ".CompensateWith<ReleaseInventory>().WithPolicy(Policies.Undo)",
                """
                public static readonly PolicySet Undo = PolicySet.Named("undo")
                    .CompensationRetry(attempts: 1);
                """),
            new DeclaredPolicyAnalyzer());

        single.Single(d => d.Id == "FLOWX1035").Severity.ShouldBe(
            DiagnosticSeverity.Warning,
            "The repair is one token, and the declaration is not something the author has to " +
            "delete to silence the rule.");
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
    /// <strong>And the discarded set is not read by any other rule.</strong> Both sets below
    /// declare something a content rule could speak about, so an analyzer that inspected the
    /// superseded one too would report twice here. It must not: every other rule in this file
    /// says something about what the plan and the manifest carry, and neither carries anything
    /// from a set the compiler threw away.
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

        Analyze(FlowWith(step, policies)).ShouldBe(["FLOWX1034"]);

        Messages(FlowWith(step, policies))
            .Count(m => m.StartsWith("FLOWX1034", StringComparison.Ordinal))
            .ShouldBe(1, "One superseded call, one report.");
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
    /// value the author owns — which is the argument the deleted FLOWX1032 made for its own
    /// warning.
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

    // -------------------------------------------------------- FLOWX1057 must fire

    /// <summary>A <c>Consent</c> with no purpose to compare against is reported.</summary>
    /// <remarks>
    /// <strong>The rule fires in the direction the mistake fails in, which is not the obvious
    /// one.</strong> An empty purpose reads like a gate nobody can pass; it is the opposite.
    /// <c>StepPolicy.HasConsent</c> treats a blank purpose as no consent declared, so the step
    /// is dispatched to every caller while <c>ManifestWriter</c> goes on publishing an
    /// <c>Identity</c> policy on it — a control that reads as present and is not.
    /// <c>ABlankDeclaredPurposeLeavesTheStepUngated</c> is the run-time half of the same fact.
    /// </remarks>
    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("purpose: \"\"")]
    [InlineData("null!")]
    public void AConsentWithNoPurposeIsReported(string argument) =>
        Analyze(FlowWith(
            ".WithPolicy(Policies.Clinical)",
            $"""
            public static readonly PolicySet Clinical = PolicySet.Named("clinical")
                .Consent({argument});
            """))
            .ShouldBe(["FLOWX1057"]);

    /// <summary>The message names the set the author would go and edit.</summary>
    [Fact]
    public void TheBlankPurposeMessageNamesTheSet()
    {
        var message = Messages(FlowWith(
            ".WithPolicy(Policies.Clinical)",
            """
            public static readonly PolicySet Clinical = PolicySet.Named("clinical")
                .Consent("");
            """))
            .Single(m => m.StartsWith("FLOWX1057", StringComparison.Ordinal));

        message.ShouldContain("Policies.Clinical");
    }

    // ------------------------------------------------------ FLOWX1057 must not fire

    /// <summary>A named purpose is the ordinary declaration and is silent.</summary>
    /// <remarks>
    /// The control this rule is worth having only because of. A rule that fired on every
    /// <c>.Consent(...)</c> would be as easy to write as it is worthless.
    /// </remarks>
    [Theory]
    [InlineData("\"treatment\"")]
    [InlineData("purpose: \"research\"")]
    [InlineData("\" treatment \"")]
    public void ANamedPurposeIsSilent(string argument) =>
        Analyze(FlowWith(
            ".WithPolicy(Policies.Clinical)",
            $"""
            public static readonly PolicySet Clinical = PolicySet.Named("clinical")
                .Consent({argument});
            """))
            .ShouldBeEmpty();

    /// <summary>A purpose that is not a literal is silent.</summary>
    /// <remarks>
    /// FLOWX1035's restriction, and the same bargain: RS1030 forbids an analyzer from asking
    /// the compilation for another tree's semantic model, so each spelling this does not
    /// recognise costs a false negative rather than a wrong report on a set that is fine.
    /// The run-time floor is what covers it.
    /// </remarks>
    [Fact]
    public void ANonLiteralPurposeIsSilent() =>
        Analyze(FlowWith(
            ".WithPolicy(Policies.Clinical)",
            """
            public static string Configured => "";

            public static readonly PolicySet Clinical = PolicySet.Named("clinical")
                .Consent(Configured);
            """))
            .ShouldBeEmpty();

    /// <summary>A set declaring no consent at all is silent.</summary>
    [Fact]
    public void ASetWithNoConsentIsSilent() =>
        Analyze(FlowWith(
            ".WithPolicy(Policies.Gateway)",
            """
            public static readonly PolicySet Gateway = PolicySet.Named("gateway")
                .Timeout(TimeSpan.FromSeconds(2))
                .Retry(attempts: 3);
            """))
            .ShouldBeEmpty();

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
