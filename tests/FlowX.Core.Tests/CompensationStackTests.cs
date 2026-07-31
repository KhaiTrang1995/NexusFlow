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

    [Fact]
    public void TheSameStepMayCompleteOncePerIterationScope()
    {
        // The identity is the pair, not the index. A step inside a `ForEach` body
        // legitimately completes once per element, and keying on the index alone made the
        // second element throw — which is how this check would have stopped a documented
        // shape from working at all.
        var stack = new CompensationStack();
        var step = Compensable(0, Fixtures.ReserveInventory, Fixtures.ReleaseInventory);

        stack.RecordCompleted(step, new Scope());
        stack.RecordCompleted(step, new Scope());
        stack.RecordCompleted(step, new Scope());

        stack.Count.ShouldBe(3);
    }

    [Fact]
    public void UnwindingHandsBackTheScopeEachStepCompletedIn()
    {
        // Which is what makes an undo inside a loop bind to the element its own pass
        // processed, rather than to whatever the last pass left behind.
        var stack = new CompensationStack();
        var step = Compensable(0, Fixtures.ReserveInventory, Fixtures.ReleaseInventory);

        var first = new Scope();
        var second = new Scope();

        stack.RecordCompleted(step, first);
        stack.RecordCompleted(step, second);

        stack.Unwind().Select(static e => e.Scope).ShouldBe([second, first]);
    }

    [Fact]
    public void AStepOutsideAnIterationRecordsNoScopeAtAll()
    {
        var stack = new CompensationStack();

        stack.RecordCompleted(Compensable(0, Fixtures.ReserveInventory, Fixtures.ReleaseInventory));

        stack.Unwind().Single().Scope.ShouldBeNull(
            "Everything outside a loop runs under the flow's own context, and recording a " +
            "reference to it on every entry would be a field that is always the same.");
    }

    /// <summary>A stand-in for an iteration's view of the context: identity is all that matters here.</summary>
    private sealed class Scope : FlowContext
    {
        public override string CorrelationId => string.Empty;

        public override string? FlowInstanceId => null;

        public override string CapabilityId => string.Empty;

        public override string? TenantId => null;

        public override string IdempotencyKey => string.Empty;

        public override DateTimeOffset Deadline => default;

        public override DateTimeOffset UtcNow => default;

        public override Random Random => Random.Shared;

        public override string FlowId => string.Empty;

        public override string FlowVersion => string.Empty;

        public override System.Security.Claims.ClaimsPrincipal? Principal => null;

        public override TriggerEnvelope Trigger => default;

        public override Error? Error => null;

        public override Guid NewId() => Guid.Empty;

        public override T Get<T>() => throw new NotSupportedException();

        public override bool TryGet<T>(
            [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out T value)
        {
            value = default;
            return false;
        }

        public override void Set<T>(T value) => throw new NotSupportedException();
    }
}
