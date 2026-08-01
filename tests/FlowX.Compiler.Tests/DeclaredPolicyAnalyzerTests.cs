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
/// <strong><c>Audit</c> is the test that pins the correction.</strong> It is a
/// <c>PolicyStage.Consistency</c> policy — stage 7, the same stage as
/// <c>CompensationRetry</c> — and it is inert, because <c>PolicyChain.ForStep</c> moves only
/// <c>CompensationRetry</c> onto the compensation's chain and <c>CompensationPolicy.From</c>
/// reads only that kind. Every summary of this gap that says "stages 1–6 do not run" is
/// wrong, and this file is where that is checked rather than asserted in prose.
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
    /// The ordinary resilience stance: three kinds, none of them executed by anything.
    /// </summary>
    /// <remarks>
    /// <c>PolicyChain.Ordered</c> is read in exactly one place in <c>src/</c> —
    /// <c>CompensationPolicy.From</c> — and it skips every kind but
    /// <c>CompensationRetry</c>. <c>StepNode.Policies</c> is read nowhere at all.
    /// </remarks>
    [Fact]
    public void AForwardPolicySetIsReported() =>
        Analyze(FlowWith(
            ".WithPolicy(Policies.ExternalRead)",
            """
            public static readonly PolicySet ExternalRead = PolicySet.Named("external-read")
                .Timeout(TimeSpan.FromSeconds(3))
                .Retry(attempts: 3)
                .CircuitBreaker(failureRatio: 0.5, breakDuration: TimeSpan.FromSeconds(30));
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
            ".WithPolicy(Policies.ExternalRead)",
            """
            public static readonly PolicySet ExternalRead = PolicySet.Named("external-read")
                .Timeout(TimeSpan.FromSeconds(3))
                .Retry(attempts: 3)
                .CircuitBreaker(failureRatio: 0.5, breakDuration: TimeSpan.FromSeconds(30));
            """));

        messages.ShouldHaveSingleItem();
        messages[0].ShouldContain("Policies.ExternalRead");
        messages[0].ShouldContain("CircuitBreaker");
        messages[0].ShouldContain("Retry");
        messages[0].ShouldContain("Timeout");
    }

    /// <summary>
    /// <c>Audit</c> is reported, and it is a stage-7 <c>Consistency</c> policy.
    /// </summary>
    /// <remarks>
    /// The test that pins the correction. The cut is by <em>what a policy wraps</em>, not by
    /// which stage it runs in: <c>PolicyChain.ForStep</c> moves only <c>CompensationRetry</c>
    /// onto the compensation's chain, so <c>Audit</c> stays on the step's chain — which
    /// nothing reads. A rule written against "stages 1–6" would be silent here and would
    /// leave the sample's audit declarations claiming an immutable financial record nothing
    /// writes.
    /// </remarks>
    [Fact]
    public void AuditIsReportedAlthoughItRunsAtTheSameStageAsCompensationRetry()
    {
        var messages = Messages(FlowWith(
            ".WithPolicy(Policies.Audited)",
            """
            public static readonly PolicySet Audited = PolicySet.Named("audited")
                .Audit("financial", "Iban");
            """));

        messages.ShouldHaveSingleItem();
        messages[0].ShouldContain("Audit");
    }

    /// <summary>Every kind the DSL offers except one, each on its own.</summary>
    /// <remarks>
    /// A theory rather than one set with all eight in it, so that a rule which happened to
    /// recognise seven of them and miss the eighth fails on the row that names it. The
    /// exclusion is asserted separately, below.
    /// </remarks>
    [Theory]
    [InlineData("Timeout(TimeSpan.FromSeconds(1))", "Timeout")]
    [InlineData("Retry(attempts: 2)", "Retry")]
    [InlineData("CircuitBreaker(failureRatio: 0.5, breakDuration: TimeSpan.FromSeconds(1))", "CircuitBreaker")]
    [InlineData("Bulkhead(maxConcurrency: 4)", "Bulkhead")]
    [InlineData("Cache(TimeSpan.FromSeconds(1))", "Cache")]
    [InlineData("RateLimit(permits: 5, TimeSpan.FromSeconds(1))", "RateLimit")]
    [InlineData("Idempotency(TimeSpan.FromHours(1))", "Idempotency")]
    [InlineData("Audit(\"category\")", "Audit")]
    public void EveryKindExceptCompensationRetryIsReported(string declaration, string kind)
    {
        var messages = Messages(FlowWith(
            ".WithPolicy(Policies.OneKind)",
            $"""
            public static readonly PolicySet OneKind = PolicySet.Named("one-kind").{declaration};
            """));

        messages.ShouldHaveSingleItem();
        messages[0].ShouldContain(kind);
    }

    /// <summary>A mixed set names the inert half and not the half that executes.</summary>
    /// <remarks>
    /// The shape <c>samples/banking</c>'s ledger steps have: a <c>Timeout</c> that arms
    /// nothing beside a <c>CompensationRetry</c> that runs. Naming both would tell the author
    /// their retry is dead, which is the opposite of true.
    /// </remarks>
    [Fact]
    public void AMixedSetNamesTheInertKindsOnly()
    {
        var messages = Messages(FlowWith(
            ".CompensateWith<ReleaseInventory>().WithPolicy(Policies.Ledger)",
            """
            public static readonly PolicySet Ledger = PolicySet.Named("ledger")
                .Timeout(TimeSpan.FromSeconds(5))
                .CompensationRetry(attempts: 5);
            """));

        messages.ShouldHaveSingleItem();
        messages[0].ShouldContain("Timeout");
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
                bulk => bulk.Step<ReserveInventory>().WithPolicy(Policies.ExternalRead))
            """,
            """
            public static readonly PolicySet ExternalRead = PolicySet.Named("external-read")
                .Timeout(TimeSpan.FromSeconds(3));
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
    /// A set the compiler cannot resolve to a declared initialiser is silent.
    /// </summary>
    /// <remarks>
    /// <c>PolicySetReader</c> returns nothing rather than guessing, which is the restriction
    /// FLOWX1014 and FLOWX1019 already work under and the one <c>FlowEmitter</c> works under:
    /// it emits no chain for a set whose kinds it could not read. Reporting here would name
    /// policies the author cannot find, and would fire on a set assembled at run time that
    /// might contain nothing but a compensation retry.
    /// </remarks>
    [Fact]
    public void AnUnresolvableSetIsSilent() =>
        Analyze(FlowWith(
            ".WithPolicy(Policies.Build())",
            """
            public static PolicySet Build() => PolicySet.Named("built").Timeout(TimeSpan.FromSeconds(1));
            """))
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
                .Timeout(TimeSpan.FromSeconds(5))
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
    [Fact]
    public void ASetWithoutACompensationRetryDoesNotReportTheDrop() =>
        Analyze(FlowWith(
            ".WithPolicy(Policies.Forward)",
            """
            public static readonly PolicySet Forward = PolicySet.Named("forward")
                .Timeout(TimeSpan.FromSeconds(3));
            """))
            .ShouldBe(["FLOWX1032"]);

    /// <summary>An unresolvable set reports neither rule.</summary>
    /// <remarks>
    /// Silent exactly where the emitter is silent. A set the compiler cannot read produces no
    /// policy argument at all, so there is no drop to report and no carried chain to warn
    /// about.
    /// </remarks>
    [Fact]
    public void AnUnresolvableSetReportsNeitherRule() =>
        Analyze(FlowWith(
            ".WithPolicy(Policies.Build())",
            """
            public static PolicySet Build() => PolicySet.Named("built").CompensationRetry(attempts: 5);
            """))
            .ShouldBeEmpty();

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
                    .Timeout(TimeSpan.FromSeconds(5))
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
