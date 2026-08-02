using System.Linq;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// What <c>.PollUntil(...)</c> compiles to, and what it refuses to compile.
/// </summary>
/// <remarks>
/// Through the real generator over a real compilation, for <c>TimerConstructTests</c>'
/// reason: the question is what a developer's build produces, and a model-layer assertion
/// cannot answer it.
/// </remarks>
public sealed class PollConstructTests
{
    /// <summary>
    /// A poll occupies its own index and its attempt occupies the next, with the schedule and
    /// the budget copied verbatim.
    /// </summary>
    /// <remarks>
    /// The layout is the whole of the construct: the body is at <c>index + 1</c> and is implied
    /// rather than stored, so the node carries no target for it and the engine re-enters that
    /// one index per attempt. Nothing in the array points backwards, which is what leaves the
    /// termination argument where it was.
    /// </remarks>
    [Fact]
    public void APollIsANodeWhoseAttemptIsTheNextIndex()
    {
        var run = GeneratorHarness.Run(Durable("""
                .PollUntil<ReserveInventory>(
                    until: ctx => ctx.Get<Reservation>().Sku.Length > 0,
                    interval: Backoff.Exponential("PT5S", "PT5M"),
                    timeout: TimeSpan.FromHours(4))
                .Step<CapturePayment>()
            """));

        Reported(run).ShouldBeEmpty(run.Describe());

        run.Plan.ShouldContainText(
            "StepNode.ForPoll(0, Backoff.Exponential(\"PT5S\", \"PT5M\"), TimeSpan.FromHours(4))",
            "the schedule and the budget are the author's expressions: the generator does not " +
            "constant-fold, so the plan means what the source means.");

        run.Plan.ShouldContainText(
            "StepNode.ForCapability(1, Descriptors.Step1)",
            "the attempt is the next index, which is what the engine re-enters.");

        run.Plan.ShouldContainText(
            "StepNode.ForCapability(2, Descriptors.Step2)",
            "and the step after the poll takes the index after the attempt.");
    }

    /// <summary>
    /// The <c>until:</c> predicate reaches the same <c>Conditions</c> class a <c>When</c>'s
    /// does, and the same <c>Evaluate</c> switch.
    /// </summary>
    /// <remarks>
    /// One seam for two constructs, because both are a pure predicate over the same context
    /// reached by the same step index. A second seam would have been a second thing every
    /// generated dispatcher has to keep in step, for a question that is already answered.
    /// </remarks>
    [Fact]
    public void ThePollsConditionIsEmittedAsAConditionOnItsOwnIndex()
    {
        var run = GeneratorHarness.Run(Durable("""
                .PollUntil<ReserveInventory>(
                    until: ctx => ctx.Get<Reservation>().Sku.Length > 0,
                    interval: Backoff.Exponential("PT5S", "PT5M"),
                    timeout: TimeSpan.FromHours(4))
            """));

        run.Plan.ShouldContainText(
            "Step0 = ctx => ctx.Get<Reservation>().Sku.Length > 0",
            "copied verbatim onto the poll's own index.");

        run.Plan.ShouldContainText(
            "return Conditions.Step0(Typed(ctx));",
            "and reached through Evaluate, which is the seam a conditional already uses.");
    }

    /// <summary>
    /// An <c>.OnTimeout</c> block is laid out after the attempt, and the satisfied path jumps
    /// over it.
    /// </summary>
    /// <remarks>
    /// An <c>AwaitSignal</c>'s layout with one more block in front of the escalation, and the
    /// target means the same thing in both: where control goes when the wait was
    /// <em>satisfied</em>. The two paths rejoin at that index, which is why a block that should
    /// end the flow has to say so with a <c>.Fail</c>.
    /// </remarks>
    [Fact]
    public void AnEscalationIsLaidOutAfterTheAttemptAndTheSatisfiedPathSkipsIt()
    {
        var run = GeneratorHarness.Run(Durable("""
                .PollUntil<ReserveInventory>(
                    until: ctx => ctx.Get<Reservation>().Sku.Length > 0,
                    interval: Backoff.Exponential("PT5S", "PT5M"),
                    timeout: TimeSpan.FromHours(4))
                    .OnTimeout(f => f.Step<ReleaseInventory>())
                .Step<CapturePayment>()
            """));

        Reported(run).ShouldBeEmpty(run.Describe());

        run.Plan.ShouldContainText(
            "satisfiedTarget: 3",
            "index 1 is the attempt, 2 is the escalation, and 3 is where both paths rejoin.");

        run.Plan.ShouldContainText(
            "StepNode.ForCapability(2, Descriptors.Step2)",
            "the escalation block sits between the attempt and the join.");
    }

    /// <summary>A poll with no escalation carries no target at all.</summary>
    /// <remarks>
    /// The absence is read by the engine as "there is nowhere for this timeout to go", which
    /// ends the flow with <c>flow.poll_not_satisfied</c> and unwinds behind it. A target equal
    /// to the index after the attempt would instead read as an escalation that runs nothing,
    /// and would run the steps after the poll against the last unfinished answer.
    /// </remarks>
    [Fact]
    public void APollWithNoEscalationCarriesNoTarget() =>
        GeneratorHarness.Run(Durable("""
                .PollUntil<ReserveInventory>(
                    until: ctx => ctx.Get<Reservation>().Sku.Length > 0,
                    interval: Backoff.Exponential("PT5S", "PT5M"),
                    timeout: TimeSpan.FromHours(4))
            """))
            .Plan.ShouldNotContainText(
                "satisfiedTarget:",
                "no block, no target — the engine reads the absence rather than a number.");

    /// <summary>The manifest publishes the poll, its attempt, its escalation and its budget.</summary>
    /// <remarks>
    /// <para>
    /// The kind is what makes a flow that polls distinguishable from one that calls once, which
    /// is the fact a reader of a manifest most needs about this construct. The branches are the
    /// two blocks, in layout order, so the capabilities in the top-level list have somewhere in
    /// the step tree that runs them.
    /// </para>
    /// <para>
    /// The interval is deliberately absent. It is a tuning number in exactly the sense
    /// <c>MaxDegreeOfParallelism</c> is one, and the schema publishes structure.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheManifestPublishesThePollItsBlocksAndItsBudget()
    {
        var json = GeneratorHarness.Run(Durable("""
                .PollUntil<ReserveInventory>(
                    until: ctx => ctx.Get<Reservation>().Sku.Length > 0,
                    interval: Backoff.Exponential("PT5S", "PT5M"),
                    timeout: TimeSpan.FromHours(4))
                    .OnTimeout(f => f.Step<ReleaseInventory>())
            """)).ManifestJson;

        json.ShouldNotBeNull();

        json.ShouldContainText(
            "\"kind\": \"Poll\"",
            "which is what makes a flow that polls distinguishable from one that calls once.");

        json.ShouldContainText("\"timeout\": \"PT4H\"", "the budget, folded for a reader of JSON.");

        json.ShouldContainText(
            "\"capability\": \"inventory.reserve@1.2.0\"",
            "the attempt is the first branch.");

        json.ShouldContainText(
            "\"capability\": \"inventory.release@1.2.0\"",
            "and the escalation is the second.");

        json!.ShouldNotContain(
            "interval",
            Case.Insensitive,
            "how often a loop asks is a tuning number, and the schema publishes structure.");
    }

    /// <summary>FLOWX1017 — polling outside <c>Durable</c>, named as the construct it is.</summary>
    /// <remarks>
    /// The third construct on one rule. A poll parks between attempts and reads which attempt
    /// it is on out of the journal that parked it, so outside one it has neither anywhere to
    /// record when the next call is due nor any way to count the ones already made.
    /// </remarks>
    [Fact]
    public void PollingOutsideDurableIsRefused()
    {
        var run = GeneratorHarness.Run(FlowPlanGeneratorTests.WithFlow("""
            [Flow("order.place", Profile = ExecutionProfile.Ephemeral)]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .PollUntil<ReserveInventory>(
                        until: ctx => ctx.Get<Reservation>().Sku.Length > 0,
                        interval: Backoff.Exponential("PT5S", "PT5M"),
                        timeout: TimeSpan.FromHours(4))
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Ids.ShouldContain("FLOWX1017", run.Describe());

        run.Diagnostics
            .Single(d => d.Id == "FLOWX1017")
            .GetMessage(System.Globalization.CultureInfo.InvariantCulture)
            .ShouldContain("PollUntil", Case.Sensitive);
    }

    /// <summary>FLOWX1044 — polling a capability that has not declared repetition safe.</summary>
    /// <remarks>
    /// <c>FLOWX1014</c>'s argument reached by a different door, and the stronger case of the
    /// two: a retry repeats after a failure, a poll repeats after every success.
    /// </remarks>
    [Fact]
    public void PollingANonIdempotentCapabilityIsRefused()
    {
        var run = GeneratorHarness.Run(Durable("""
                .PollUntil<CapturePayment>(
                    until: ctx => ctx.Get<OrderResult>().Id.Length > 0,
                    interval: Backoff.Exponential("PT5S", "PT5M"),
                    timeout: TimeSpan.FromHours(4))
            """));

        run.Ids.ShouldContain("FLOWX1044", run.Describe());

        run.Diagnostics
            .Single(d => d.Id == "FLOWX1044")
            .GetMessage(System.Globalization.CultureInfo.InvariantCulture)
            .ShouldContain("payment.capture", Case.Sensitive);
    }

    /// <summary>FLOWX1043 — a first gap longer than the budget, so the loop is a single call.</summary>
    /// <remarks>
    /// A warning rather than an error, on <c>FLOWX1019</c>'s argument: the flow runs, and an
    /// author who genuinely wants one attempt and a fallback has written it in an obscure way.
    /// What makes it worth reporting is that the failure is invisible — one attempt followed by
    /// an escalation reads in a journal exactly like a dependency that never answered.
    /// </remarks>
    [Fact]
    public void APollWhoseFirstGapOutlastsItsBudgetIsReported()
    {
        var run = GeneratorHarness.Run(Durable("""
                .PollUntil<ReserveInventory>(
                    until: ctx => ctx.Get<Reservation>().Sku.Length > 0,
                    interval: Backoff.Exponential("PT10M", "PT30M"),
                    timeout: TimeSpan.FromMinutes(5))
            """));

        run.Ids.ShouldContain("FLOWX1043", run.Describe());

        run.Diagnostics
            .Single(d => d.Id == "FLOWX1043")
            .GetMessage(System.Globalization.CultureInfo.InvariantCulture)
            .ShouldContain("PT10M", Case.Sensitive);
    }

    /// <summary>
    /// And it says nothing when either duration is one the compiler cannot evaluate.
    /// </summary>
    /// <remarks>
    /// The stance every folding rule in this compiler takes: a schedule assembled at run time,
    /// or read from configuration, is one a build-time rule has no opinion about. Guessing
    /// would fire on flows that are correct.
    /// </remarks>
    [Fact]
    public void APollWhoseScheduleCannotBeReadIsNotReported() =>
        GeneratorHarness.Run(Durable("""
                .PollUntil<ReserveInventory>(
                    until: ctx => ctx.Get<Reservation>().Sku.Length > 0,
                    interval: Schedules.Ocr,
                    timeout: TimeSpan.FromMinutes(5))
            """))
            .Ids.ShouldNotContain("FLOWX1043");

    /// <summary>
    /// FLOWX1011 — a condition that reads the ambient clock, on the construct where it does
    /// the most damage.
    /// </summary>
    /// <remarks>
    /// A poll's <c>until</c> is asked again on every resume, so one that reads
    /// <c>DateTime.UtcNow</c> answers differently on the node that picks the instance up than
    /// it did on the one that parked it — and the flow either walks past a poll that never
    /// succeeded or polls until its budget runs out.
    /// </remarks>
    [Fact]
    public void APollsConditionMayNotReadTheAmbientClock() =>
        GeneratorHarness.Analyze(
            Durable("""
                .PollUntil<ReserveInventory>(
                    until: ctx => System.DateTime.UtcNow.Hour > 3,
                    interval: Backoff.Exponential("PT5S", "PT5M"),
                    timeout: TimeSpan.FromHours(4))
            """),
            new FlowX.Compiler.Analysis.PredicatePurityAnalyzer())
            .ShouldContain("FLOWX1011");

    /// <summary>
    /// <c>.OrSignal&lt;T&gt;()</c> puts the signal on the poll's own node and spends no index.
    /// </summary>
    /// <remarks>
    /// One wait with two endings is one node: the attempt is still <c>index + 1</c>, the
    /// escalation still follows it, and the identity rides on the node the engine already asks
    /// about. A second index would be a second wait, which is the fork ADR-0058 refused.
    /// </remarks>
    [Fact]
    public void ASecondEndingRidesOnThePollsOwnNodeAndSpendsNoIndex()
    {
        var run = GeneratorHarness.Run(Durable("""
                .PollUntil<ReserveInventory>(
                    until: ctx => ctx.Get<Reservation>().Sku.Length > 0,
                    interval: Backoff.Exponential("PT5S", "PT5M"),
                    timeout: TimeSpan.FromHours(4))
                    .OrSignal<OrderPlaced>()
                    .OnTimeout(f => f.Step<ReleaseInventory>())
                .Step<CapturePayment>()
            """));

        Reported(run).ShouldBeEmpty(run.Describe());

        run.Plan.ShouldContainText(
            "satisfiedTarget: 3, signalType: \"order.placed\"",
            "the signal is a modifier on the poll's node: index 1 is still the attempt, 2 the " +
            "escalation, 3 where both endings rejoin.");

        run.Plan.ShouldContainText(
            "StepNode.ForCapability(3, Descriptors.Step3)",
            "and the step after the poll is where the layout already put it, so `.OrSignal` " +
            "moved nothing.");
    }

    /// <summary>A poll with a signal and no escalation writes the signal and no target.</summary>
    /// <remarks>
    /// The two optional arguments are independent: declaring a second way out of the wait says
    /// nothing about what happens when the budget runs out, and a target written to carry a
    /// signal would claim a block that is not there.
    /// </remarks>
    [Fact]
    public void APollWithASignalAndNoEscalationStillCarriesNoTarget()
    {
        var plan = GeneratorHarness.Run(Durable("""
                .PollUntil<ReserveInventory>(
                    until: ctx => ctx.Get<Reservation>().Sku.Length > 0,
                    interval: Backoff.Exponential("PT5S", "PT5M"),
                    timeout: TimeSpan.FromHours(4))
                    .OrSignal<OrderPlaced>()
            """)).Plan;

        plan.ShouldContainText(
            "signalType: \"order.placed\"",
            "the second ending is on the node whether or not there is an escalation.");
        plan.ShouldNotContainText("satisfiedTarget:", "no block, no target — unchanged.");
    }

    /// <summary>
    /// The signal's contract joins the state bag's membership, so the row that records the
    /// ending carries the payload.
    /// </summary>
    /// <remarks>
    /// The ending a delivery causes commits the poll node's own row, and the snapshot on it is
    /// the only thing that carries the delivered value past the next node death. A contract
    /// missing from <c>StateBag</c> would be a row written with the payload silently absent.
    /// </remarks>
    [Fact]
    public void ThePollsSignalContractIsJournaledLikeAWaitsIs()
    {
        var plan = GeneratorHarness.Run(WithJsonContext("""
                .PollUntil<ReserveInventory>(
                    until: ctx => ctx.Get<Reservation>().Sku.Length > 0,
                    interval: Backoff.Exponential("PT5S", "PT5M"),
                    timeout: TimeSpan.FromHours(4))
                    .OrSignal<OrderPlaced>()
            """)).Plan;

        plan.ShouldContainText(
            "ctx.TryGet<Sample.OrderPlaced>(out var",
            "the state-bag snapshot writes the signal's contract, which is what a resume " +
            "reads back through RestoreState.");
    }

    /// <summary>The manifest publishes the poll's second ending as the address it is.</summary>
    /// <remarks>
    /// The same <c>signal</c> field an <c>AwaitSignal</c> publishes, for its reason: it is how
    /// the flow is reached from outside, which is the one kind of fact about a step
    /// <c>flowx diff</c> compares. A poll with one ending publishes no field at all, which is
    /// the difference a reader should see.
    /// </remarks>
    [Fact]
    public void TheManifestPublishesThePollsSignalAndOmitsItWhenThereIsNone()
    {
        GeneratorHarness.Run(Durable("""
                .PollUntil<ReserveInventory>(
                    until: ctx => ctx.Get<Reservation>().Sku.Length > 0,
                    interval: Backoff.Exponential("PT5S", "PT5M"),
                    timeout: TimeSpan.FromHours(4))
                    .OrSignal<OrderPlaced>()
            """))
            .ManifestJson!
            .ShouldContainText(
                "\"signal\": \"order.placed\"",
                "the identity a transport addresses a delivery to, published where a reader " +
                "compares it between versions.");

        GeneratorHarness.Run(Durable("""
                .PollUntil<ReserveInventory>(
                    until: ctx => ctx.Get<Reservation>().Sku.Length > 0,
                    interval: Backoff.Exponential("PT5S", "PT5M"),
                    timeout: TimeSpan.FromHours(4))
            """))
            .ManifestJson!
            .ShouldNotContain(
                "\"signal\"",
                Case.Sensitive,
                "a poll with one ending has no inbound address, and publishing one would " +
                "advertise a route nothing serves.");
    }

    /// <summary>
    /// FLOWX1050 — a step after the poll binding a contract only the delivery ending leaves.
    /// </summary>
    /// <remarks>
    /// The flow compiles as C#, runs when the webhook fires, and throws when the polling does
    /// its job — which is the ordinary path. It is <c>FLOWX1020</c>'s argument narrowed to the
    /// one construct that produces conditionally, and is reported instead of it: the type
    /// genuinely is in the bag on one of the two paths, so the older rule's advice would be
    /// wrong.
    /// </remarks>
    [Fact]
    public void AStepBindingOnlyThePollsSignalIsRefused()
    {
        var reported = GeneratorHarness.Analyze(
            WithReceiver("""
                .PollUntil<ReserveInventory>(
                    until: ctx => ctx.Get<Reservation>().Sku.Length > 0,
                    interval: Backoff.Exponential("PT5S", "PT5M"),
                    timeout: TimeSpan.FromHours(4))
                    .OrSignal<OrderPlaced>()
                .Step<RecordPlacement>()
            """),
            new FlowX.Compiler.Analysis.StepBindingAnalyzer());

        reported.ShouldContain("FLOWX1050");
        reported.ShouldNotContain("FLOWX1020", "the two would give opposite advice on one line.");
    }

    /// <summary>
    /// And a step binding what both endings leave is not reported.
    /// </summary>
    /// <remarks>
    /// A signal can only end a wait an instance is parked in, and parking follows a committed
    /// attempt — so the polled capability's output is in the bag on both paths. A rule that
    /// fired here would refuse the shape the construct is for.
    /// </remarks>
    [Fact]
    public void AStepBindingWhatBothEndingsLeaveIsNotReported() =>
        GeneratorHarness.Analyze(
            WithReceiver("""
                .PollUntil<ReserveInventory>(
                    until: ctx => ctx.Get<Reservation>().Sku.Length > 0,
                    interval: Backoff.Exponential("PT5S", "PT5M"),
                    timeout: TimeSpan.FromHours(4))
                    .OrSignal<OrderPlaced>()
                .Step<ReleaseInventory>()
            """),
            new FlowX.Compiler.Analysis.StepBindingAnalyzer())
            .ShouldBeEmpty();

    /// <summary>
    /// What the generator reported, minus the state-bag rule this scaffold cannot satisfy.
    /// </summary>
    /// <remarks>
    /// <c>FLOWX1006</c> fires for every contract of every <c>Durable</c> flow in a compilation
    /// that declares no source-generated <c>JsonSerializerContext</c>, and the shared preamble
    /// declares none — so it is noise here in exactly the way it is signal in a real project.
    /// Filtered rather than suppressed, so a <em>different</em> unexpected diagnostic still
    /// fails the assertion.
    /// </remarks>
    private static string[] Reported(GeneratorRun run) =>
        [.. run.Ids.Where(static id => id != "FLOWX1006")];

    /// <summary>A schedule this compiler cannot fold, so FLOWX1043 has nothing to read.</summary>
    private const string Schedules = """
        public static class Schedules
        {
            public static Backoff Ocr { get; } = Backoff.Exponential("PT10M", "PT30M");
        }
        """;

    /// <remarks>
    /// <c>using System;</c> is prepended, and it is load-bearing rather than tidy: the shared
    /// preamble does not import it, and without <c>TimeSpan</c> resolving the whole
    /// <c>PollUntil</c> call fails overload resolution — which leaves the generator working
    /// from syntax and reporting its layout correctly while every <em>semantic</em> analyzer,
    /// <c>FLOWX1011</c> among them, silently sees nothing to check.
    /// </remarks>
    private static string Durable(string steps) =>
        "using System;\n" + FlowPlanGeneratorTests.WithFlow(Schedules + "\n\n" +
            $$"""
            [Flow("order.place", Profile = ExecutionProfile.Durable)]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
            {{steps}}
                    .Return(ctx => new OrderResult("id"));
            }
            """);

    /// <summary>A capability whose input is the signal contract, so a step can bind it.</summary>
    /// <remarks>
    /// Declared here rather than in the shared preamble because it exists for exactly one rule:
    /// nothing else in this repository has a reason for a capability that consumes an event
    /// contract, and putting one in the preamble would make every other flow's binding table
    /// larger for it.
    /// </remarks>
    private const string Receiver = """
        [Capability("order.record", Version = "1.0.0",
            Authorization = Authorization.Internal, Idempotent = true)]
        public sealed class RecordPlacement : ICapability<OrderPlaced, OrderResult>
        {
            public ValueTask<Result<OrderResult>> ExecuteAsync(OrderPlaced input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new OrderResult(input.Sku)));
        }
        """;

    /// <summary>The <see cref="Durable"/> scaffold with a capability that binds the signal.</summary>
    private static string WithReceiver(string steps) => Durable(steps).Replace(
        Schedules, Schedules + "\n\n" + Receiver, System.StringComparison.Ordinal);

    /// <summary>
    /// The <see cref="Durable"/> scaffold with a source-generated serialiser context, so the
    /// state-bag half of the generated dispatcher is emitted at all.
    /// </summary>
    /// <remarks>
    /// Without a context declaring the contracts, <c>JournaledContracts</c> finds none and
    /// <c>DescribeStep</c> falls to its "this flow journals nothing serialisable" arm — which is
    /// truthful, and would make an assertion about the snapshot pass or fail for the wrong
    /// reason.
    /// </remarks>
    private static string WithJsonContext(string steps) => Durable(steps).Replace(
        Schedules,
        Schedules + "\n\n" + """
            [System.Text.Json.Serialization.JsonSerializable(typeof(PlaceOrder))]
            [System.Text.Json.Serialization.JsonSerializable(typeof(OrderResult))]
            [System.Text.Json.Serialization.JsonSerializable(typeof(Reservation))]
            [System.Text.Json.Serialization.JsonSerializable(typeof(OrderPlaced))]
            public sealed partial class SampleJsonContext : System.Text.Json.Serialization.JsonSerializerContext
            {
            }
            """,
        System.StringComparison.Ordinal);
}
