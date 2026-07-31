using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Runtime.Tests;

/// <summary>
/// A bounded iteration as the engine sees it: a <see cref="StepKind.ForEach"/> whose body
/// is the span up to its join, executed once per element of a collection the engine never
/// looks inside.
/// </summary>
/// <remarks>
/// <para>
/// The tests are shaped around the three claims that are easy to get wrong and invisible
/// from a passing smoke test. First, that the body really runs <em>n</em> times over one
/// copy of itself — a loop that ran its body once would pass any assertion about the join.
/// Second, that each pass sees its own element and not the last one written: that is the
/// whole reason an iteration has a scope rather than a slot in the state bag. Third, that
/// what every element completed is unwound in strict reverse when a later one fails, which
/// is what makes <c>.CompensateWith</c> mean anything inside a loop.
/// </para>
/// <para>
/// <c>Executed</c> is asserted as a sequence where the bound is one and as a set where it
/// is not, for the reason <c>ParallelTests</c> gives: completion order under concurrency is
/// the thread pool's business, not the engine's.
/// </para>
/// </remarks>
public sealed class ForEachTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static FlowEngine Engine() => new(new FakeClock(T0));

    private static readonly Error Declined =
        new("payment.declined", "declined", ErrorCategory.Conflict);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>One line of an order — the element type the documented example iterates.</summary>
    private sealed record Line(string Sku);

    private static readonly Line[] ThreeLines = [new("a"), new("b"), new("c")];

    private static readonly Line[] TwoLines = [new("a"), new("b")];

    private static readonly string[] ThreeTags = ["x", "y", "z"];

    private static RecordingDispatcher Over(params Line[] lines) =>
        new RecordingDispatcher().IterateOver(1, lines);

    // ---------------------------------------------------------------- the body runs n times

    [Fact]
    public async Task TheBodyRunsOncePerElementOverOneCopyOfItself()
    {
        var dispatcher = Over(ThreeLines);

        var result = await Engine().ExecuteAsync(Plans.ForEach(), dispatcher, Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeTrue();
        dispatcher.Executed.ShouldBe([0, 2, 3, 2, 3, 2, 3, 4],
            "The body is steps 2 and 3, laid out once in the array and entered three times. " +
            "A loop that unrolled its body would need three copies in the plan, and a loop " +
            "that ran it once would be indistinguishable from no loop at all.");
        result.CompletedSteps.ShouldBe(8,
            "Every pass over the body really ran, so every pass counts.");
    }

    [Fact]
    public async Task TheCollectionSelectorRunsExactlyOnce()
    {
        // Not once per element. In a durable flow the selector has to answer identically on
        // replay, and reading it per element is both a cost and an invitation to a
        // collection that changes under the loop.
        var dispatcher = Over(ThreeLines);

        await Engine().ExecuteAsync(Plans.ForEach(), dispatcher, Plans.Invocation, Ct);

        dispatcher.Iterated.ShouldBe([1]);
    }

    [Fact]
    public async Task AnEmptyCollectionRunsNothingAndContinuesAfterTheLoop()
    {
        // A loop over nothing does nothing, exactly as a `When` nobody took does. It is not
        // an error and there is no diagnostic.
        var dispatcher = new RecordingDispatcher();

        var result = await Engine().ExecuteAsync(Plans.ForEach(), dispatcher, Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeTrue();
        dispatcher.Executed.ShouldBe([0, 4]);
    }

    [Fact]
    public async Task EachPassSeesItsOwnElementAndTheLoopLeavesNoneBehind()
    {
        var dispatcher = Over(ThreeLines);

        var result = await Engine().ExecuteAsync(Plans.ForEach(), dispatcher, Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeTrue();

        // The scopes are not pooled, so unlike the flow context they can be read after the
        // fact — each still holds the element its pass was for.
        var seen = dispatcher.ContextsSeen
            .Where(ctx => ctx.TryGet<Line>(out _))
            .Select(ctx => ctx.Get<Line>().Sku)
            .ToList();

        seen.ShouldBe(["a", "a", "b", "b", "c", "c"],
            "Both steps of each pass see that pass's line. A shared slot keyed by " +
            "typeof(Line) would have shown the last element to everyone.");

        dispatcher.ContextsSeen[0].TryGet<Line>(out _).ShouldBeFalse(
            "The step before the loop cannot see an element, because there is not one yet.");
        dispatcher.ContextsSeen[^1].TryGet<Line>(out _).ShouldBeFalse(
            "And the step after the loop cannot see the last one, which would be a leftover " +
            "rather than a value the flow produced.");
    }

    // ------------------------------------------------------------------------ ContinueOnError

    [Fact]
    public async Task TheFirstFailingElementStopsTheIterationByDefault()
    {
        // ContinueOnError = false, which is the documented default. The elements after the
        // failure never run; the ones before it are not re-run either.
        var dispatcher = Over(ThreeLines).FailAt(3, Declined);

        var result = await Engine().ExecuteAsync(Plans.ForEach(), dispatcher, Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("payment.declined",
            "The loop reports the failing element's own error. Replacing it with a count " +
            "would throw away the reason, and there is exactly one reason.");
        dispatcher.Executed.ShouldBe([0, 2, 3],
            "The first element's step 2 succeeded and its step 3 failed, so no second " +
            "element was started and the step after the loop never ran.");
    }

    [Fact]
    public async Task WorkAlreadyDoneByEarlierElementsIsCompensatedInStrictReverse()
    {
        // Step 2 is compensable and step 3 is not; the third element's step 3 is the one
        // that fails, so three reservations happened and all three have to come back,
        // newest first. The failure is pinned to a *visit* rather than an index, because
        // inside a loop one index runs many times.
        var dispatcher = Over(ThreeLines).FailAtNthVisit(3, 3, Declined);

        var result = await Engine().ExecuteAsync(Plans.ForEach(), dispatcher, Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeFalse();
        result.Compensation.ShouldBe(CompensationOutcome.Succeeded);

        dispatcher.Compensated.ShouldBe([2, 2, 2],
            "One undo per element that completed the compensable step, and the loop's body " +
            "is one step in the array however many times it ran.");

        dispatcher.CompensationScopes
            .Select(ctx => ctx.Get<Line>().Sku)
            .ShouldBe(["c", "b", "a"],
                "Strict reverse, and each undo binds to the line its own pass reserved. " +
                "Binding to the flow's context instead would have released line 'c' three " +
                "times and left 'a' and 'b' reserved.");
    }

    [Fact]
    public async Task ContinueOnErrorRunsEveryElementAndPublishesWhatFailed()
    {
        var dispatcher = Over(ThreeLines).FailAtNthVisit(3, 2, Declined);

        var result = await Engine().ExecuteAsync(
            Plans.ForEach(continueOnError: true), dispatcher, Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeTrue(
            "The author said a failing element is a result rather than an end, so the flow " +
            "carries on and the step after the loop decides what a partial success means.");
        dispatcher.Executed.ShouldBe([0, 2, 3, 2, 3, 2, 3, 4]);

        var outcome = dispatcher.OutcomeAfterTheLoop
            .ShouldNotBeNull("The loop publishes a ForEachOutcome for the next step to read.");
        outcome.ElementCount.ShouldBe(3);
        outcome.SucceededCount.ShouldBe(2);
        outcome.ErrorFor(1)!.Code.ShouldBe("payment.declined");
        outcome.ErrorFor(0).ShouldBeNull();
        outcome.AllSucceeded.ShouldBeFalse();
    }

    // ------------------------------------------------------------------------- concurrency

    [Fact]
    public async Task ElementsOverlapWhenTheirStepsActuallyYield()
    {
        // The same narrow claim `Parallel` makes: an iteration is started eagerly on the
        // calling thread, so a pass whose every step completes synchronously finishes
        // before the next one starts. Only a step that yields lets a sibling in.
        var dispatcher = Over(ThreeLines).YieldAt(2).YieldAt(3);

        var result = await Engine().ExecuteAsync(
            Plans.ForEach(maxDegreeOfParallelism: 3), dispatcher, Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeTrue();
        dispatcher.PeakConcurrency.ShouldBeGreaterThan(1,
            "A bound above one that never overlaps is a sequential loop wearing a costume.");
        dispatcher.Executed.Count.ShouldBe(8);
    }

    [Fact]
    public async Task TheBoundIsAWindowRatherThanATaskPerElement()
    {
        var lines = Enumerable.Range(0, 12).Select(i => new Line(i.ToString(
            System.Globalization.CultureInfo.InvariantCulture))).ToArray();

        var dispatcher = new RecordingDispatcher().IterateOver(1, lines).YieldAt(2).YieldAt(3);

        var result = await Engine().ExecuteAsync(
            Plans.ForEach(maxDegreeOfParallelism: 2), dispatcher, Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeTrue();
        dispatcher.Executed.Count.ShouldBe(1 + (12 * 2) + 1);
        dispatcher.PeakConcurrency.ShouldBeLessThanOrEqualTo(2,
            "A thousand-line order must not start a thousand reservations. That the bound " +
            "is required rather than optional is the whole point of ForEachOptions.");
    }

    [Fact]
    public async Task AFailingElementCancelsTheRestAndEveryStartedOneIsStillDrained()
    {
        var lines = Enumerable.Range(0, 8).Select(i => new Line(i.ToString(
            System.Globalization.CultureInfo.InvariantCulture))).ToArray();

        var dispatcher = new RecordingDispatcher()
            .IterateOver(1, lines)
            .YieldAt(3)
            .FailAtNthVisit(3, 1, Declined);

        var result = await Engine().ExecuteAsync(
            Plans.ForEach(maxDegreeOfParallelism: 3), dispatcher, Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeFalse();
        dispatcher.Executed.Count.ShouldBeLessThan(1 + (8 * 2) + 1,
            "Cancellation has to actually stop the window from starting more elements, or " +
            "the bound buys nothing on the failure path.");
        dispatcher.Executed.ShouldNotContain(4,
            "The step after the loop must not run: draining the elements in flight is not " +
            "the same as the loop having succeeded.");
        result.Compensation.ShouldNotBe(CompensationOutcome.NotRequired,
            "Whatever the cancelled elements had already reserved still has to come back.");
    }

    [Fact]
    public async Task ASequentialLoopLeavesTheStateBagUnguarded()
    {
        // The property that keeps budget B2 a hard zero for everything that does not fork:
        // a loop bounded at one cannot be reached by two threads, so it must not buy the
        // lock that a bound above one needs.
        Plans.ForEach(maxDegreeOfParallelism: 1).HasParallel.ShouldBeFalse();
        Plans.ForEach(maxDegreeOfParallelism: 2).HasParallel.ShouldBeTrue();

        var dispatcher = Over(ThreeLines);
        var result = await Engine().ExecuteAsync(Plans.ForEach(), dispatcher, Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeTrue();
    }

    // ----------------------------------------------------------------------------- nesting

    [Fact]
    public async Task ALoopInsideALoopSeesBothElementsEachOnItsOwnType()
    {
        // Nesting works by chaining scopes, so a body two levels deep resolves the inner
        // element on its own type and the outer one on its. A collection of the same type
        // nested inside itself would resolve to the innermost, which is what the same code
        // written with two C# foreach loops would do.
        var dispatcher = new RecordingDispatcher()
            .IterateOver(0, TwoLines)
            .IterateOver(1, ThreeTags);

        var result = await Engine().ExecuteAsync(Plans.NestedForEach(), dispatcher, Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeTrue();

        dispatcher.Executed.ShouldBe([2, 2, 2, 3, 2, 2, 2, 3, 4],
            "Two outer elements, three inner ones each: step 2 runs six times and step 3 — " +
            "which follows the inner loop inside the outer body — runs twice.");

        dispatcher.Iterated.ShouldBe([0, 1, 1],
            "The outer selector runs once; the inner runs once per outer element, because " +
            "it is a step of the outer's body and may legitimately depend on its element.");

        var pairs = dispatcher.ContextsSeen
            .Where(ctx => ctx.TryGet<string>(out _))
            .Select(ctx => ctx.Get<Line>().Sku + ctx.Get<string>())
            .ToList();

        pairs.ShouldBe(["ax", "ay", "az", "bx", "by", "bz"]);
    }

    // ------------------------------------------------------------------------- the selector

    [Fact]
    public async Task ASelectorThatThrowsFailsTheFlowAndCompensatesWhatRanBeforeIt()
    {
        var dispatcher = new RecordingDispatcher { ThrowAtIteration = 1 };

        var result = await Engine().ExecuteAsync(Plans.ForEach(), dispatcher, Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("flow.iteration_failed",
            "Its own code, not the switch selector's: 'the switch selector at step 1 threw' " +
            "would be actively misleading when step 1 is a loop.");
        dispatcher.Executed.ShouldBe([0],
            "No element ran, because there was never a collection to run over.");
    }

    [Fact]
    public async Task ContinueOnErrorDoesNotSwallowASelectorThatThrew()
    {
        // ContinueOnError is a statement about elements failing, not about the loop being
        // unable to produce one. A flow that carried on here would be iterating a
        // collection it never read.
        var dispatcher = new RecordingDispatcher { ThrowAtIteration = 1 };

        var result = await Engine().ExecuteAsync(
            Plans.ForEach(continueOnError: true), dispatcher, Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("flow.iteration_failed");
    }
}
