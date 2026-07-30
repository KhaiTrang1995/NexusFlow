using Shouldly;
using Xunit;

namespace FlowX.Core.Tests;

/// <summary>
/// Compensation ordering is the one thing a saga cannot get wrong. These tests pin
/// the ordering down explicitly rather than trusting the collection type, because
/// "it happens to be reversed" and "it is guaranteed to be reversed" are different
/// properties and only one of them survives a refactor.
/// </summary>
public sealed class CompensationStackTests
{
    private static StepNode Compensable(int index, CapabilityDescriptor capability, CapabilityDescriptor inverse)
        => StepNode.ForCapability(index, capability, inverse);

    [Fact]
    public void UnwindsInStrictReverseOrderOfCompletion()
    {
        var stack = new CompensationStack();
        var first = Compensable(0, Fixtures.ValidateOrder, Fixtures.ReleaseInventory);
        var second = Compensable(1, Fixtures.ReserveInventory, Fixtures.ReleaseInventory);
        var third = Compensable(2, Fixtures.CapturePayment, Fixtures.ReleaseInventory);

        stack.RecordCompleted(first);
        stack.RecordCompleted(second);
        stack.RecordCompleted(third);

        stack.Unwind().Select(static s => s.Index).ShouldBe([2, 1, 0],
            "Undo runs newest-first. Releasing inventory before refunding the payment " +
            "that reserved it leaves the two systems disagreeing.");
    }

    [Fact]
    public void IgnoresStepsThatDeclareNoCompensation()
    {
        var stack = new CompensationStack();

        stack.RecordCompleted(StepNode.ForCapability(0, Fixtures.ValidateOrder));
        stack.RecordCompleted(Compensable(1, Fixtures.ReserveInventory, Fixtures.ReleaseInventory));
        stack.RecordCompleted(StepNode.ForEmit(2, "order.placed"));

        stack.Count.ShouldBe(1);
        stack.Unwind().Single().Index.ShouldBe(1);
    }

    [Fact]
    public void IsEmptyBeforeAnythingCompletes()
    {
        var stack = new CompensationStack();

        stack.IsEmpty.ShouldBeTrue();
        stack.Unwind().ShouldBeEmpty();
    }

    [Fact]
    public void UnwindingDrainsTheStackSoCompensationCannotRunTwice()
    {
        var stack = new CompensationStack();
        stack.RecordCompleted(Compensable(0, Fixtures.ReserveInventory, Fixtures.ReleaseInventory));

        stack.Unwind().Count().ShouldBe(1);

        stack.IsEmpty.ShouldBeTrue(
            "A second unwind — from a retry of the failure path, say — must not " +
            "re-release inventory that was already released.");
        stack.Unwind().ShouldBeEmpty();
    }

    [Fact]
    public void RejectsARecordedStepWithADuplicateIndex()
    {
        var stack = new CompensationStack();
        var step = Compensable(0, Fixtures.ReserveInventory, Fixtures.ReleaseInventory);

        stack.RecordCompleted(step);

        Should.Throw<InvalidOperationException>(() => stack.RecordCompleted(step));
        // The same step completing twice means the engine's loop is broken. Failing
        // loudly beats compensating it twice.
    }
}
