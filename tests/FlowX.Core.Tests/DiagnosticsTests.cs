using Shouldly;
using Xunit;

namespace FlowX.Core.Tests;

/// <summary>
/// The parts of the model a developer meets while debugging: exception shapes and
/// <c>ToString</c> output. Untested diagnostics are the ones that turn out to be
/// useless at 3am.
/// </summary>
public sealed class DiagnosticsTests
{
    [Fact]
    public void StepNodeDescribesItselfByKind()
    {
        StepNode.ForCapability(0, Fixtures.CapturePayment).ToString()
            .ShouldBe("[0] payment.capture@2.1.0");

        StepNode.ForEmit(1, "order.placed").ToString()
            .ShouldBe("[1] emit order.placed");

        StepNode.ForAwaitSignal(2, "payment.confirmed", TimeSpan.FromHours(1)).ToString()
            .ShouldStartWith("[2] await payment.confirmed");
    }

    [Fact]
    public void DescriptorsRenderAsTheirQualifiedName()
    {
        Fixtures.CapturePayment.ToString().ShouldBe("payment.capture@2.1.0");
        Fixtures.CapturePayment.QualifiedName.ShouldBe("payment.capture@2.1.0");

        Fixtures.PlaceOrder.ToString().ShouldBe("order.place@1.0.0 (Ephemeral)");
    }

    [Fact]
    public void AnExecutionPlanSummarisesItsSize()
    {
        var plan = ExecutionPlan.Create(
            Fixtures.PlaceOrder,
            StepGraph.Create([StepNode.ForCapability(0, Fixtures.ValidateOrder)]));

        plan.ToString().ShouldBe("order.place@1.0.0: 1 step(s)");
    }

    [Fact]
    public void InvalidFlowPlanExceptionSupportsTheStandardShapes()
    {
        // CA1032 requires these. They are exercised here so the requirement is met
        // by working code rather than by unreachable stubs.
        new InvalidFlowPlanException().ShouldNotBeNull();
        new InvalidFlowPlanException("boom").Message.ShouldBe("boom");

        var cause = new InvalidOperationException("root cause");
        var wrapped = new InvalidFlowPlanException("boom", cause);

        wrapped.Message.ShouldBe("boom");
        wrapped.InnerException.ShouldBe(cause);
    }

    [Fact]
    public void StepFactoriesRejectANegativeIndex()
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => StepNode.ForCapability(-1, Fixtures.ValidateOrder));
        Should.Throw<ArgumentOutOfRangeException>(() => StepNode.ForEmit(-1, "a.b"));
        Should.Throw<ArgumentOutOfRangeException>(
            () => StepNode.ForAwaitSignal(-1, "a.b", TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void AwaitSignalRejectsANonPositiveTimeout()
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => StepNode.ForAwaitSignal(0, "a.b", TimeSpan.Zero));
        Should.Throw<ArgumentOutOfRangeException>(
            () => StepNode.ForAwaitSignal(0, "a.b", TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void FactoriesRejectNullArguments()
    {
        Should.Throw<ArgumentNullException>(() => StepNode.ForCapability(0, null!));
        Should.Throw<ArgumentNullException>(() => StepGraph.Create(null!));
        Should.Throw<ArgumentNullException>(() => ExecutionPlan.Create(null!, StepGraph.Create([StepNode.ForEmit(0, "a.b")])));
        Should.Throw<ArgumentNullException>(() => ExecutionPlan.Create(Fixtures.PlaceOrder, null!));
        Should.Throw<ArgumentNullException>(() => PolicyChain.Create(null!, Fixtures.ValidateOrder));
        Should.Throw<ArgumentNullException>(() => PolicyChain.Create(PolicySet.Empty, null!));
        Should.Throw<ArgumentNullException>(() => new CompensationStack().RecordCompleted(null!));
    }

    [Fact]
    public void AnEmptyPolicyChainReportsItself()
    {
        PolicyChain.Empty.IsEmpty.ShouldBeTrue();
        PolicyChain.Create(PolicySet.Named("x").Timeout(TimeSpan.FromSeconds(1)), Fixtures.ValidateOrder)
            .IsEmpty.ShouldBeFalse();
    }
}
