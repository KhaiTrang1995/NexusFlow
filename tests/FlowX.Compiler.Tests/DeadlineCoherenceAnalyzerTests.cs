using FlowX.Compiler.Analysis;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// FLOWX1019 — whether a flow's deadline can contain the step timeouts declared under it.
/// </summary>
/// <remarks>
/// Both directions everywhere, and the silent cases are the load-bearing half. This rule
/// reports a number, and a number that is wrong upwards accuses a flow that actually fits
/// — so most of what follows pins the shapes it must refuse to guess at: an unresolvable
/// policy set, a set with no timeout, a step inside a branch, and the reference sample,
/// which declares a deadline and attaches no policies at all.
/// </remarks>
public sealed class DeadlineCoherenceAnalyzerTests
{
    private const string Preamble = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using FlowX;

        namespace Sample;

        public sealed record PlaceOrder(string Sku, int Quantity);
        public sealed record ValidatedOrder(string Sku, int Quantity);
        public sealed record Reservation(string ReservationId);
        public sealed record Payment(string ReceiptId);
        public sealed record OrderPlacedResult(string ReceiptId);

        [Capability("order.validate", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
        public sealed class ValidateOrder : ICapability<PlaceOrder, ValidatedOrder>
        {
            public ValueTask<Result<ValidatedOrder>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new ValidatedOrder(input.Sku, input.Quantity)));
        }

        [Capability("inventory.reserve", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
        public sealed class ReserveInventory : ICapability<ValidatedOrder, Reservation>
        {
            public ValueTask<Result<Reservation>> ExecuteAsync(ValidatedOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new Reservation("r")));
        }

        [Capability("payment.capture", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
        public sealed class CapturePayment : ICapability<Reservation, Payment>
        {
            public ValueTask<Result<Payment>> ExecuteAsync(Reservation input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new Payment("p")));
        }
        """;

    private static string With(string body) => Preamble + "\n\n" + body;

    private static string[] Analyze(string body) =>
        GeneratorHarness.Analyze(With(body), new DeadlineCoherenceAnalyzer());

    private static string[] Messages(string body) =>
        GeneratorHarness.AnalyzeWithMessages(With(body), new DeadlineCoherenceAnalyzer());

    /// <summary>
    /// The reference sample: a deadline, three steps, and no policy set anywhere.
    /// </summary>
    /// <remarks>
    /// The single most important case here. Every flow in this repository looks like this,
    /// so a rule that reported it would be a rule nobody ever saw switched on.
    /// </remarks>
    [Fact]
    public void AFlowWithNoStepPoliciesIsClean()
    {
        Analyze("""
            [Flow("order.place", Version = "1.0.0")]
            [FlowDeadline("PT30S")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow)
                    => flow
                        .Step<ValidateOrder>()
                        .Step<ReserveInventory>()
                        .Step<CapturePayment>()
                        .Return(ctx => new OrderPlacedResult(ctx.Get<Payment>().ReceiptId));
            }
            """).ShouldBeEmpty();
    }

    [Fact]
    public void AFlowWhoseStepsFitInsideItsDeadlineIsClean()
    {
        // 3 × 2 s = 6 s against a 30 s budget. The arithmetic 14 §7 recommends.
        Analyze("""
            public static class Policies
            {
                public static readonly PolicySet PaymentGateway = PolicySet.Named("payment-gateway")
                    .Timeout(TimeSpan.FromSeconds(2))
                    .Retry(attempts: 3);
            }

            [Flow("order.place", Version = "1.0.0")]
            [FlowDeadline("PT30S")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow)
                    => flow
                        .Step<CapturePayment>().WithPolicy(Policies.PaymentGateway)
                        .Return(ctx => new OrderPlacedResult(ctx.Get<Payment>().ReceiptId));
            }
            """).ShouldBeEmpty();
    }

    [Fact]
    public void ReportsFLOWX1019WhenOneStepsRetriesOutlastTheDeadline()
    {
        // The case FlowDeadlineAttribute names: three attempts at 30 s inside a 10 s SLA.
        Analyze("""
            public static class Policies
            {
                public static readonly PolicySet PaymentGateway = PolicySet.Named("payment-gateway")
                    .Timeout(TimeSpan.FromSeconds(30))
                    .Retry(attempts: 3);
            }

            [Flow("order.place", Version = "1.0.0")]
            [FlowDeadline("PT10S")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow)
                    => flow
                        .Step<CapturePayment>().WithPolicy(Policies.PaymentGateway)
                        .Return(ctx => new OrderPlacedResult(ctx.Get<Payment>().ReceiptId));
            }
            """).ShouldBe(["FLOWX1019"]);
    }

    [Fact]
    public void ReportsFLOWX1019WhenSeveralStepsAddUpPastTheDeadline()
    {
        // No single step is incoherent; the flow is. The deadline is absolute and is
        // spent by every step in turn, which is the part that is easy to miss by eye.
        Analyze("""
            public static class Policies
            {
                public static readonly PolicySet Slow = PolicySet.Named("slow")
                    .Timeout(TimeSpan.FromSeconds(9))
                    .Retry(attempts: 2);
            }

            [Flow("order.place", Version = "1.0.0")]
            [FlowDeadline("PT30S")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow)
                    => flow
                        .Step<ValidateOrder>().WithPolicy(Policies.Slow)
                        .Step<ReserveInventory>().WithPolicy(Policies.Slow)
                        .Step<CapturePayment>().WithPolicy(Policies.Slow)
                        .Return(ctx => new OrderPlacedResult(ctx.Get<Payment>().ReceiptId));
            }
            """).ShouldBe(["FLOWX1019"]);
    }

    [Fact]
    public void TheMessageWritesOutTheArithmetic()
    {
        // Knowing the sum is too large is nearly worthless next to knowing which step's
        // policy to change, so the breakdown is asserted rather than the id alone.
        Messages("""
            public static class Policies
            {
                public static readonly PolicySet PaymentGateway = PolicySet.Named("payment-gateway")
                    .Timeout(TimeSpan.FromSeconds(30))
                    .Retry(attempts: 3);
            }

            [Flow("order.place", Version = "1.0.0")]
            [FlowDeadline("PT10S")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow)
                    => flow
                        .Step<CapturePayment>().WithPolicy(Policies.PaymentGateway)
                        .Return(ctx => new OrderPlacedResult(ctx.Get<Payment>().ReceiptId));
            }
            """).ShouldHaveSingleItem()
                .ShouldSatisfyAllConditions(
                    message => message.ShouldContain("PlaceOrderFlow"),
                    message => message.ShouldContain("10s"),
                    message => message.ShouldContain("90s"),
                    message => message.ShouldContain("CapturePayment 3×30s"));
    }

    [Fact]
    public void AFlowWithNoDeadlineIsNotReported()
    {
        // There is nothing to be incoherent with. The runtime default is not a declaration
        // the flow's author made, and reporting against it would report a value they never
        // wrote and cannot see.
        Analyze("""
            public static class Policies
            {
                public static readonly PolicySet PaymentGateway = PolicySet.Named("payment-gateway")
                    .Timeout(TimeSpan.FromSeconds(30))
                    .Retry(attempts: 3);
            }

            [Flow("order.place", Version = "1.0.0")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow)
                    => flow
                        .Step<CapturePayment>().WithPolicy(Policies.PaymentGateway)
                        .Return(ctx => new OrderPlacedResult(ctx.Get<Payment>().ReceiptId));
            }
            """).ShouldBeEmpty();
    }

    [Fact]
    public void APolicySetWithNoTimeoutIsNotGuessedAt()
    {
        // A retry with no timeout is unbounded, not free. Counting it as zero would be
        // wrong; counting it as anything else would be invented.
        Analyze("""
            public static class Policies
            {
                public static readonly PolicySet Retried = PolicySet.Named("retried").Retry(attempts: 50);
            }

            [Flow("order.place", Version = "1.0.0")]
            [FlowDeadline("PT1S")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow)
                    => flow
                        .Step<CapturePayment>().WithPolicy(Policies.Retried)
                        .Return(ctx => new OrderPlacedResult(ctx.Get<Payment>().ReceiptId));
            }
            """).ShouldBeEmpty();
    }

    [Fact]
    public void APolicySetBuiltAtRunTimeIsNotGuessedAt()
    {
        // Same restriction PolicySetReader works under: a set with no initialiser in
        // source has no compile-time contents, and returning nothing is the honest answer.
        Analyze("""
            public static class Policies
            {
                public static PolicySet Build(int seconds) => PolicySet.Named("built")
                    .Timeout(TimeSpan.FromSeconds(seconds))
                    .Retry(attempts: 3);
            }

            [Flow("order.place", Version = "1.0.0")]
            [FlowDeadline("PT1S")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow)
                    => flow
                        .Step<CapturePayment>().WithPolicy(Policies.Build(30))
                        .Return(ctx => new OrderPlacedResult(ctx.Get<Payment>().ReceiptId));
            }
            """).ShouldBeEmpty();
    }

    [Fact]
    public void ATimeoutThatIsNotWrittenAsALiteralIsNotGuessedAt()
    {
        // A stated limit, pinned: the duration is read from syntax, so a constant, an
        // arithmetic expression or `new TimeSpan(...)` all read as unknown. The cost is a
        // false negative, which is the direction this rule is allowed to be wrong in.
        Analyze("""
            public static class Policies
            {
                public static readonly PolicySet PaymentGateway = PolicySet.Named("payment-gateway")
                    .Timeout(new TimeSpan(0, 0, 30))
                    .Retry(attempts: 3);
            }

            [Flow("order.place", Version = "1.0.0")]
            [FlowDeadline("PT1S")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow)
                    => flow
                        .Step<CapturePayment>().WithPolicy(Policies.PaymentGateway)
                        .Return(ctx => new OrderPlacedResult(ctx.Get<Payment>().ReceiptId));
            }
            """).ShouldBeEmpty();
    }

    [Fact]
    public void AStepInsideABranchIsNotCounted()
    {
        // A `When` block runs on some paths and not others, and a `Parallel` overlaps
        // rather than adds. Only unconditional top-level steps are summed, so this flow —
        // whose branch alone would exceed the budget — stays silent.
        Analyze("""
            public static class Policies
            {
                public static readonly PolicySet PaymentGateway = PolicySet.Named("payment-gateway")
                    .Timeout(TimeSpan.FromSeconds(30))
                    .Retry(attempts: 3);
            }

            [Flow("order.place", Version = "1.0.0")]
            [FlowDeadline("PT10S")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow)
                    => flow
                        .When(
                            ctx => ctx.Input.Quantity > 0,
                            b => b.Step<CapturePayment>().WithPolicy(Policies.PaymentGateway))
                        .Return(ctx => new OrderPlacedResult("r"));
            }
            """).ShouldBeEmpty();
    }

    [Fact]
    public void AStepWithATimeoutAndNoRetryCountsOneAttempt()
    {
        // No Retry policy means one attempt, not none: a 20 s timeout inside a 10 s
        // deadline is incoherent on its own.
        Analyze("""
            public static class Policies
            {
                public static readonly PolicySet Slow = PolicySet.Named("slow").Timeout(TimeSpan.FromSeconds(20));
            }

            [Flow("order.place", Version = "1.0.0")]
            [FlowDeadline("PT10S")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow)
                    => flow
                        .Step<CapturePayment>().WithPolicy(Policies.Slow)
                        .Return(ctx => new OrderPlacedResult(ctx.Get<Payment>().ReceiptId));
            }
            """).ShouldBe(["FLOWX1019"]);
    }

    [Fact]
    public void ASubSecondDeadlineIsCompared()
    {
        // ISO-8601 carries fractional seconds, and the millisecond rendering is what a
        // reader of a PT0.5S budget needs to see next to it.
        Messages("""
            public static class Policies
            {
                public static readonly PolicySet Slow = PolicySet.Named("slow").Timeout(TimeSpan.FromMilliseconds(800));
            }

            [Flow("order.price", Version = "1.0.0")]
            [FlowDeadline("PT0.5S")]
            public sealed partial class PriceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow)
                    => flow
                        .Step<CapturePayment>().WithPolicy(Policies.Slow)
                        .Return(ctx => new OrderPlacedResult(ctx.Get<Payment>().ReceiptId));
            }
            """).ShouldHaveSingleItem().ShouldContain("800ms");
    }
}
