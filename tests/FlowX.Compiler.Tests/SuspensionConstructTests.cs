using System;
using System.IO;
using System.Linq;
using FlowX.Compiler.Diagnostics;
using FlowX.Compiler.Emit;
using FlowX.Compiler.Model;
using Microsoft.CodeAnalysis;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// FLOWX1031 — the suspension constructs the compiler still cannot honour, and the one it
/// now can.
/// </summary>
/// <remarks>
/// <para>
/// <c>AwaitSignal</c>, <c>Delay</c> and <c>OnTimeout</c> are declared on
/// <c>IFlowBuilder</c>, documented in <c>docs/08-Flow-Definition.md §3.5</c>, and until
/// this rule they compiled with no diagnostic of any kind. Two of them produced no step;
/// the third produced one that completes immediately, carrying a timeout constant nobody
/// wrote.
/// </para>
/// <para>
/// <strong>WP-63 narrowed the rule to two of the three, and did not delete it.</strong>
/// <c>AwaitSignal</c> is honoured: the engine suspends at it, the author's declared duration
/// reaches the plan, and a delivered signal resumes the instance. <c>Delay</c> and
/// <c>OnTimeout</c> are unchanged — there is still no timer — so the rule keeps the half
/// that is still true, exactly as <c>FLOWX1028</c> was narrowed to <c>Streaming</c> rather
/// than deleted on the day <c>Durable</c> started running. Deleting it outright would have
/// handed a discarded <c>OnTimeout</c> block the silence <c>AwaitSignal</c> used to have.
/// </para>
/// <para>
/// Every test here goes through the real generator over a real compilation, because the
/// question is what a developer's build prints and a model-layer assertion cannot answer
/// that.
/// </para>
/// </remarks>
public sealed class SuspensionConstructTests
{
    private const string Signal = """
        public sealed record PaymentConfirmed(string Id);
        """;

    // ------------------------------------------------------------- it fires

    /// <summary>A durable flow that awaits a signal is no longer reported at all.</summary>
    /// <remarks>
    /// The half of this rule WP-63 discharged. <c>FLOWX1017</c> still refuses
    /// <c>AwaitSignal</c> under every other profile — an in-memory wait does not survive a
    /// deployment — so <c>Durable</c> is the one profile it can declare, and it is now the
    /// profile under which the wait actually happens.
    /// </remarks>
    [Fact]
    public void IsSilentOnADurableFlowThatAwaitsASignal()
    {
        var run = GeneratorHarness.Run(Durable("""
                .Step<ReserveInventory>()
                .AwaitSignal<PaymentConfirmed>(TimeSpan.FromDays(7))
            """));

        run.Ids.ShouldNotContain("FLOWX1031", run.Describe());
    }

    /// <summary>The report names the construct and points at the call, not at the class.</summary>
    [Fact]
    public void TheDelayReportPointsAtTheCall()
    {
        var diagnostic = Only(Durable("""
                .Step<ReserveInventory>()
                .Delay(TimeSpan.FromDays(1))
            """));

        diagnostic.Location.IsInSource.ShouldBeTrue(
            "A diagnostic with no source span shows up at the top of a build log rather " +
            "than on the line that has to change.");

        Message(diagnostic).ShouldContain("Delay");
    }

    /// <summary>A <c>Delay</c> is reported, under a durable profile or any other.</summary>
    /// <remarks>
    /// <c>Delay</c> has no FLOWX1017 of its own, so there is no profile under which
    /// anything already speaks for it. Reporting it only under <c>Durable</c> would leave
    /// the rule silent on the default profile, which is the one nearly every flow has.
    /// </remarks>
    [Fact]
    public void ReportsFLOWX1031ForADelayUnderEitherProfile()
    {
        Ids(Durable("""
                .Step<ReserveInventory>()
                .Delay(TimeSpan.FromDays(1))
            """)).ShouldContain("FLOWX1031");

        Ids(Ephemeral("""
                .Step<ReserveInventory>()
                .Delay(TimeSpan.FromDays(1))
            """)).ShouldContain("FLOWX1031");
    }

    /// <summary>An <c>OnTimeout</c> block is reported where it is written.</summary>
    /// <remarks>
    /// Its own report rather than a rider on the <c>AwaitSignal</c> above it, because it
    /// is a different loss: the steps inside the block reach no plan, no dispatcher and no
    /// manifest, so a reader auditing what the flow can do never sees them at all.
    /// </remarks>
    [Fact]
    public void ReportsFLOWX1031ForADiscardedOnTimeoutBlock()
    {
        var run = GeneratorHarness.Run(Durable("""
                .Step<ReserveInventory>()
                .AwaitSignal<PaymentConfirmed>(TimeSpan.FromDays(7))
                    .OnTimeout(f => f.Step<ReleaseInventory>())
            """));

        run.Diagnostics
            .Where(d => d.Id == "FLOWX1031")
            .Select(Message)
            .ShouldContain(m => m.Contains("OnTimeout", StringComparison.Ordinal), run.Describe());
    }

    /// <summary>A construct nested inside a conditional is reported too.</summary>
    /// <remarks>
    /// The trap FLOWX1017's own remarks describe: a rule that only reads the top level
    /// quietly stops applying the day someone wraps the call in a <c>When</c>.
    /// </remarks>
    [Fact]
    public void ReportsFLOWX1031ForADelayInsideAConditionalBlock() =>
        Ids(Durable("""
                .Step<ReserveInventory>()
                .When(ctx => ctx.Input.Sku.Length > 0, b => b.Delay(TimeSpan.FromDays(1)))
            """)).ShouldContain("FLOWX1031");

    // ------------------------------------------------- and stays silent otherwise

    /// <summary>A flow that declares none of the three is silent.</summary>
    /// <remarks>
    /// The half that matters more. A rule with false positives is suppressed everywhere
    /// and then protects nothing.
    /// </remarks>
    [Fact]
    public void IsSilentOnAFlowThatDeclaresNoSuspensionConstruct()
    {
        Ids(Durable("""
                .Step<ReserveInventory>()
                .Step<CapturePayment>()
            """)).ShouldNotContain("FLOWX1031");

        Ids(Ephemeral("""
                .Step<ReserveInventory>()
                .When(ctx => ctx.Input.Sku.Length > 0, b => b.Step<CapturePayment>())
                .Otherwise(b => b.Step<ReleaseInventory>())
            """)).ShouldNotContain("FLOWX1031");
    }

    /// <summary>The flow's own deadline is not a suspension construct.</summary>
    /// <remarks>
    /// <c>[FlowDeadline]</c> is the one timeout that works — an absolute budget checked at
    /// every step boundary — and a rule that reported it would be reporting the feature
    /// this one exists to distinguish itself from.
    /// </remarks>
    [Fact]
    public void IsSilentOnAFlowThatOnlyDeclaresADeadline() =>
        Ids(Durable("""
                .Step<ReserveInventory>()
            """)).ShouldNotContain("FLOWX1031");

    // ---------------------------------------------------------- the severities

    /// <summary>
    /// What is left of the rule is warnings, and the error it used to raise is gone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The split used to be the line between a construct the compiler omitted and one it
    /// falsified: <c>AwaitSignal</c> reached the plan carrying a timeout the compiler
    /// invented, so an error — no plan at all — was the only ending that published nothing
    /// untrue. The model carries the author's duration now, so there is nothing to falsify
    /// and nothing to be an error about.
    /// </para>
    /// <para>
    /// <c>Delay</c> and <c>OnTimeout</c> are still omissions, which is the category
    /// <c>FLOWX1027</c> occupies at the severity C# gives <c>CS0162</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheTwoConstructsThatAreLeftAreWarnings()
    {
        Only(Durable("""
                .Step<ReserveInventory>()
                .Delay(TimeSpan.FromDays(1))
            """)).Severity.ShouldBe(DiagnosticSeverity.Warning);

        Only(Durable("""
                .Step<ReserveInventory>()
                .AwaitSignal<PaymentConfirmed>(TimeSpan.FromDays(7))
                    .OnTimeout(f => f.Step<ReleaseInventory>())
            """)).Severity.ShouldBe(DiagnosticSeverity.Warning);
    }

    // ------------------------------------------------------ and nothing is emitted

    /// <summary>The plan carries the duration the author declared, verbatim.</summary>
    /// <remarks>
    /// <para>
    /// This was the second defect, and it was separate from whether the construct works.
    /// <c>FlowEmitter</c> wrote <c>TimeSpan.FromHours(1)</c> for every <c>AwaitSignal</c>,
    /// whatever the author declared — a flow written to wait seven days produced a plan that
    /// said one hour — because the compiler's step model had no field to carry a timeout and
    /// <c>StepNode.ForAwaitSignal</c> demands one. It has the field now, and what reaches the
    /// plan is the author's own expression copied across, the same treatment a
    /// <c>ForEachOptions</c> and a <c>.Fail(...)</c> error already get.
    /// </para>
    /// <para>
    /// The expression rather than an evaluated <c>TimeSpan</c>: the generator does not
    /// constant-fold, so a duration written as <c>Policies.OfferWindow</c> reaches the plan as
    /// that, and the plan means what the source means.
    /// </para>
    /// </remarks>
    [Fact]
    public void ThePlanCarriesTheDurationTheAuthorDeclared()
    {
        var run = GeneratorHarness.Run(Durable("""
                .Step<ReserveInventory>()
                .AwaitSignal<PaymentConfirmed>(TimeSpan.FromDays(7))
                .Step<CapturePayment>()
            """));

        run.Plan.ShouldContainText(
            "StepNode.ForAwaitSignal(1, \"payment.confirmed\", TimeSpan.FromDays(7))",
            "seven days is what the author wrote, and it is what the plan says.");
    }

    /// <summary>And the signal's contract is journaled like a step's output.</summary>
    /// <remarks>
    /// A delivered signal is seeded into the state bag under the contract
    /// <c>.AwaitSignal&lt;TSignal&gt;(...)</c> named, and recorded by the same commit that
    /// records the suspension point. Leaving it out of the snapshot would mean an instance
    /// that resumed on a signal and then crashed came back without the value the signal
    /// carried — the wait satisfied and its result lost.
    /// </remarks>
    [Fact]
    public void TheSignalsContractIsJournaledLikeAStepsOutput()
    {
        var waiting = new FlowModel(
            flowId: "offer.accept",
            version: "1.0.0",
            profile: "Durable",
            deadline: "P30D",
            containingNamespace: "Sample.Flows",
            typeName: "AcceptOfferFlow",
            inputTypeName: "Sample.Contracts.Offer",
            outputTypeName: "Sample.Contracts.Acceptance",
            steps:
            [
                StepModel.AwaitSignal(
                    0,
                    "payment.confirmed",
                    timeoutExpression: "TimeSpan.FromDays(7)",
                    contractTypeName: "Sample.Contracts.PaymentConfirmed"),
            ]);

        var context = new JsonContextModel(
            "Sample.SampleJson", ["Sample.Contracts.PaymentConfirmed", "Sample.Contracts.Offer"]);

        var source = FlowEmitter.Emit(waiting, [context]);

        source.ShouldContainText(
            "JournalState.Read<Sample.Contracts.PaymentConfirmed>",
            "RestoreState reads the signal back, so a second crash does not lose it.");

        source.ShouldContainText(
            "ctx.TryGet<Sample.Contracts.PaymentConfirmed>(out var described)",
            "and the commit that records the suspension point carries it, exactly as a " +
            "capability step's own output is carried.");
    }

    /// <summary>A flow that only delays still gets its plan, minus the delay.</summary>
    /// <remarks>
    /// The warning has to leave the build able to produce something, or it is an error
    /// wearing a warning's severity.
    /// </remarks>
    [Fact]
    public void APlanIsStillEmittedForAFlowThatOnlyDelays()
    {
        var run = GeneratorHarness.Run(Durable("""
                .Step<ReserveInventory>()
                .Delay(TimeSpan.FromDays(1))
                .Step<CapturePayment>()
            """));

        run.Plan.ShouldContainText("StepNode.ForCapability(1, Descriptors.Step1)",
            "The step after the absent delay takes the index the delay would have had.");
    }

    /// <summary>The emitter no longer contains a fabricated duration.</summary>
    /// <remarks>
    /// A source-level guard, deliberately, because the behavioural test above can only
    /// observe the constant's absence through a plan that is no longer emitted. If a later
    /// change starts emitting <c>AwaitSignal</c> plans again, this is what says the
    /// duration has to be the author's.
    /// </remarks>
    [Fact]
    public void TheEmitterFabricatesNoTimeout() =>
        SourceOf("src/FlowX.Compiler/Emit/FlowEmitter.cs")
            .ShouldNotContain(
                "TimeSpan.FromHours(1)",
                Case.Sensitive,
                "A value the author wrote must not be replaced by a constant.");

    /// <summary>
    /// The emitter still refuses an <c>AwaitSignal</c> it has no duration for — and nothing
    /// produces one any more.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The refusal was made unnecessary rather than deleted.</strong> It exists
    /// because the only alternative to emitting the author's duration is emitting one they
    /// did not write, which is the defect the whole rule was raised over. What changed at
    /// WP-63 is the reason it never fires: the model carries the duration, and
    /// <c>AwaitSignal&lt;TSignal&gt;(TimeSpan timeout)</c> has no overload without one, so a
    /// model reaching here empty means a half-typed buffer in which C# is already saying
    /// something more useful.
    /// </para>
    /// <para>
    /// Asserted rather than left as untested dead code, on purpose. If a later change loses
    /// the duration on the way to the emitter, the choice in front of whoever made it should
    /// be "carry the author's duration" and not "put a constant back"; a loud failure is what
    /// puts that choice in front of them.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheEmitterStillRefusesAnAwaitSignalItHasNoDurationFor()
    {
        var suspending = new FlowModel(
            flowId: "offer.accept",
            version: "1.0.0",
            profile: "Durable",
            deadline: "P30D",
            containingNamespace: "Sample.Flows",
            typeName: "AcceptOfferFlow",
            inputTypeName: "Sample.Contracts.Offer",
            outputTypeName: "Sample.Contracts.Acceptance",
            steps: [StepModel.AwaitSignal(0, "payment.confirmed", timeoutExpression: null)]);

        Should.Throw<InvalidOperationException>(() => FlowEmitter.Emit(suspending))
            .Message.ShouldContain("the duration the author declared");
    }

    /// <summary>Reads a repository file, wherever the test binary happens to be built.</summary>
    private static string SourceOf(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);

            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate '{relativePath}'.");
    }

    // ------------------------------------------------------------- the descriptor

    /// <summary>The descriptor is on by default and names the work package that fixes it.</summary>
    [Fact]
    public void TheDescriptorIsEnabledAndNamesItsRemedy()
    {
        FlowXDiagnostics.SuspensionIsNotHonoured.IsEnabledByDefault.ShouldBeTrue(
            "A rule off by default reports nothing to the people who have not heard of it.");

        FlowXDiagnostics.SuspensionIsNotHonoured.Description
            .ToString(System.Globalization.CultureInfo.InvariantCulture)
            .ShouldContain("WP-63");
    }

    // ------------------------------------------------------------------ helpers

    // ------------------------------------------------- what the wait publishes

    /// <summary>
    /// The declared wait reaches the manifest as a duration, through a named constant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The indirection is the assertion.</strong> <c>DeclaredDurationTests</c> proves
    /// the folder evaluates <c>TimeSpan.FromDays(7)</c>; this proves the compiler gets from
    /// <c>Waits.Countersignature</c> to that expression, which needs a semantic model and
    /// therefore cannot be asserted anywhere but against a real compilation.
    /// </para>
    /// <para>
    /// It matters because <c>samples/workflow</c> writes the wait exactly this way — a
    /// duration is a business decision and belongs where it can be read without opening a
    /// flow — so without this hop the repository's only waiting flow would publish no
    /// <c>timeout</c> at all, and the field would have a producer on paper and none in
    /// practice.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheDeclaredWaitReachesTheManifestThroughANamedConstant()
    {
        var run = GeneratorHarness.Run(Durable("""
                .Step<ReserveInventory>()
                .AwaitSignal<PaymentConfirmed>(Waits.Countersignature)
            """).Replace(
            "public sealed record PaymentConfirmed(string Id);",
            """
            public sealed record PaymentConfirmed(string Id);

            public static class Waits
            {
                public static TimeSpan Countersignature { get; } = TimeSpan.FromDays(7);
            }
            """,
            StringComparison.Ordinal));

        run.ManifestJson.ShouldNotBeNull(run.Describe());
        run.ManifestJson!.ShouldContain("\"signal\": \"payment.confirmed\"", Case.Sensitive);
        run.ManifestJson!.ShouldContain("\"timeout\": \"P7D\"", Case.Sensitive);
    }

    /// <summary>
    /// A wait the compiler cannot evaluate publishes its identity and no duration.
    /// </summary>
    /// <remarks>
    /// The plan is unaffected — it carries the expression verbatim and means what the source
    /// means. Only the manifest loses the number, and omitting it is the same choice
    /// <c>merge</c> makes for a strategy that could not be read statically.
    /// </remarks>
    [Fact]
    public void AWaitTheCompilerCannotEvaluatePublishesNoDuration()
    {
        var run = GeneratorHarness.Run(Durable("""
                .Step<ReserveInventory>()
                .AwaitSignal<PaymentConfirmed>(Waits.Window())
            """).Replace(
            "public sealed record PaymentConfirmed(string Id);",
            """
            public sealed record PaymentConfirmed(string Id);

            public static class Waits
            {
                public static TimeSpan Window() => TimeSpan.FromDays(7);
            }
            """,
            StringComparison.Ordinal));

        run.ManifestJson.ShouldNotBeNull(run.Describe());
        run.ManifestJson!.ShouldContain("\"signal\": \"payment.confirmed\"", Case.Sensitive);
        run.ManifestJson!.ShouldNotContain("\"timeout\"", Case.Sensitive);
    }

    private static string Durable(string steps) => Flow("Profile = ExecutionProfile.Durable", steps);

    private static string Ephemeral(string steps) => Flow(null, steps);

    private static string Flow(string? profile, string steps) =>
        FlowPlanGeneratorTests.WithFlow(Signal + "\n\n" +
            $$"""
            [Flow("order.place"{{(profile is null ? string.Empty : ", " + profile)}})]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
            {{steps}}
                    .Return(ctx => new OrderResult("id"));
            }
            """);

    private static string[] Ids(string source) => GeneratorHarness.Run(source).Ids;

    private static Diagnostic Only(string source)
    {
        var run = GeneratorHarness.Run(source);

        return run.Diagnostics.SingleOrDefault(d => d.Id == "FLOWX1031")
            ?? throw new InvalidOperationException(
                $"Expected exactly one FLOWX1031. Got: {run.Describe()}");
    }

    private static string Message(Diagnostic diagnostic) =>
        diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
}
