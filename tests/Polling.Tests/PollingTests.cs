using FlowX;
using Shouldly;
using Xunit;

namespace Polling.Tests;

/// <summary>
/// <c>document.process</c> waits for a machine, and this is what that costs and buys.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every one of these runs the real engine against a real journal.</strong> Nothing
/// here hand-builds a <c>StepGraph</c>: the plan is the one the compiler wrote from the
/// sample's own <c>Define</c>, the host and the timer sweep are the shipped ones, and the
/// stores are the conformance suite's reference implementations. What the harness adds is a
/// recording dispatcher and a virtual clock.
/// </para>
/// <para>
/// <strong>The clock is the reason this class finishes in milliseconds.</strong> A poll's gap
/// is a parked row carrying an instant, so a test moves to that instant and sweeps. Four hours
/// of a flow's life cost a test nothing, which is the same fact the sample is claiming about a
/// deployment.
/// </para>
/// </remarks>
public sealed class PollingTests
{
    /// <summary>A one-page document, so the provider finishes it in one <c>perPage</c>.</summary>
    private static ProcessDocument ADocument() => new("doc-1", "application/pdf", Pages: 1);

    /// <summary>
    /// The first invocation returns with the document parked: one row, an instant, and no lease.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the sample's whole claim, asserted in the terms it makes it.</strong>
    /// "Waiting costs one database row, not a thread, a timer or a container" is four
    /// statements, and each has an assertion below: the invocation returned, the instance is
    /// <c>Suspended</c>, the row carries the instant it is next due, and the lease store holds
    /// nothing for it.
    /// </para>
    /// <para>
    /// <strong>And exactly one attempt has been made.</strong> That is the assertion that fails
    /// if the engine ever loops in-process between attempts instead of parking — which would
    /// still complete the document, still commit a row per attempt, and still pass every other
    /// test in this file while holding a thread for four hours.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task WaitingCostsOneRowAndHoldsNoLease()
    {
        var harness = DocumentHarness.Create(TimeSpan.FromMinutes(30));
        var ct = TestContext.Current.CancellationToken;

        var started = await harness.StartAsync(ADocument(), ct);

        started.IsSuspended.ShouldBeTrue(
            "the invocation returns at the poll rather than waiting inside it: " + started);

        harness.Instance.State.ShouldBe(FlowInstanceState.Suspended);

        harness.Instance.Wake.ShouldNotBeNull(
            "a parked instance with nothing scheduled to wake it is a document nobody ever " +
            "looks at again");

        harness.Instance.Wake!.Value.StepId.ShouldBe(
            ProcessDocumentFlow.Plan.Graph.Steps.Single(s => s.Kind == StepKind.Poll).Index,
            "the wake names the poll it is parked at, so a flow with two waits cannot read " +
            "the other one's instant");

        harness.HoldsLease().ShouldBeFalse(
            "a parked document holds no lease, which is what lets any node pick it up next " +
            "and what stops a thousand of them exhausting a lease store");

        started.Trace.Count("ocr.status").ShouldBe(
            1, "one attempt per invocation: the gap between two is a sweep, not a loop");
    }

    /// <summary>
    /// The document finishes, over as many attempts as the provider needs, and each one is its
    /// own invocation.
    /// </summary>
    /// <remarks>
    /// The clock moves thirty minutes of the flow's life; the test takes milliseconds. Every
    /// attempt after the first is a separate <c>ResumeAsync</c> issued by the timer sweep, on
    /// an instance that held nothing in between — so the count of sweeps is the count of
    /// attempts, and the last of them is the one whose status was terminal.
    /// </remarks>
    [Fact]
    public async Task ADocumentThatFinishesIsPolledUntilItDoes()
    {
        var harness = DocumentHarness.Create(TimeSpan.FromMinutes(30));
        var ct = TestContext.Current.CancellationToken;

        await harness.StartAsync(ADocument(), ct);

        var sweeps = 0;

        while (harness.Instance.State == FlowInstanceState.Suspended && sweeps < 50)
        {
            var woken = await harness.WakeWhenDueAsync(ct);

            woken.Report.Woken.ShouldBe(1, "the sweep found the document it parked: " + woken);
            sweeps++;
        }

        harness.Instance.State.ShouldBe(FlowInstanceState.Completed, "after " + sweeps + " sweeps");

        harness.RowsFor(PollBody).Count.ShouldBe(
            sweeps + 1,
            "one committed row per attempt, and the first attempt ran on the invocation that " +
            "started the flow");

        harness.Instance.Wake.ShouldBeNull("the wait went with the state");
    }

    /// <summary>
    /// Every attempt commits under its own scope, which is what makes the loop journalable at
    /// all.
    /// </summary>
    /// <remarks>
    /// <c>(instance, step)</c> is not unique for a poll for exactly the reason it is not unique
    /// for a <c>ForEach</c>: one range of the flat array is re-entered many times, and an
    /// append-only table cannot overwrite a row. The attempt number <em>is</em> the scope, so
    /// the rows read back as <c>0</c>, <c>1</c>, <c>2</c>… and an operator can see how many
    /// times the provider was asked without joining anything.
    /// </remarks>
    [Fact]
    public async Task EachAttemptCommitsUnderItsOwnScope()
    {
        var harness = DocumentHarness.Create(TimeSpan.FromMinutes(30));
        var ct = TestContext.Current.CancellationToken;

        await harness.StartAsync(ADocument(), ct);
        await harness.WakeWhenDueAsync(ct);
        await harness.WakeWhenDueAsync(ct);

        harness.RowsFor(PollBody)
            .Select(row => row.Key.Scope.Text)
            .ShouldBe(["0", "1", "2"]);
    }

    /// <summary>
    /// The gap between attempts grows, and it is never longer than the ceiling the schedule
    /// declares.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asserted as a bound rather than as a table, because the sample's schedule is jittered:
    /// the gap before attempt <em>n</em> is a draw from <c>[0, base × 2ⁿ⁻¹]</c>, capped. What
    /// the flow promises is the cap — a document is asked about at most as often as the base
    /// and at least as often as the ceiling — and a test that pinned each draw would be
    /// asserting against a copy of <c>Random</c>.
    /// </para>
    /// <para>
    /// The ceiling is what makes the claim about cost true at the top end. Without it the
    /// eleventh gap is an hour and a job that finished in minute three is noticed in hour four.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task NoGapBetweenAttemptsExceedsTheCeiling()
    {
        var harness = DocumentHarness.Create(TimeSpan.FromHours(3));
        var ct = TestContext.Current.CancellationToken;

        await harness.StartAsync(ADocument(), ct);

        var gaps = new List<TimeSpan>();

        for (var attempt = 0; attempt < 20 && harness.Instance.State == FlowInstanceState.Suspended; attempt++)
        {
            var parked = harness.Clock.UtcNow;

            gaps.Add(harness.Instance.Wake!.Value.At - parked);

            await harness.WakeWhenDueAsync(ct);
        }

        gaps.ShouldAllBe(gap => gap <= Waits.OcrPolling.MaxDelay);
        gaps.ShouldAllBe(gap => gap >= TimeSpan.Zero);

        gaps.Count.ShouldBeGreaterThan(
            2, "a three-hour job asked about twice would not be exercising a schedule");
    }

    /// <summary>
    /// The budget runs out, the escalation runs, and the OCR job is cancelled behind it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Three things happen here and all three are the point.</strong> The poll stops —
    /// measured from the instant the first attempt committed, four hours earlier, on a node
    /// that no longer exists as far as this instance is concerned. The escalation block runs,
    /// which puts the document in front of a person. And the failure it ends with unwinds
    /// <c>ocr.upload</c>, so the provider is told to stop working on a job nobody is waiting
    /// for any more.
    /// </para>
    /// <para>
    /// The unwind is the part that spans the wait: <c>ocr.cancel</c> went on the compensation
    /// stack on the very first invocation and is run by an invocation that has no memory of it
    /// — rebuilt from the journal's committed rows by the same frontier scan that decides which
    /// attempts to skip.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ABudgetThatRunsOutEscalatesAndCancelsTheJob()
    {
        var harness = DocumentHarness.Create(TimeSpan.FromDays(1));
        var ct = TestContext.Current.CancellationToken;

        await harness.StartAsync(ADocument(), ct);

        harness.Ocr.IsOpen("doc-1").ShouldBeTrue("the job is running while the poll is running");

        var attempts = 0;

        while (harness.Instance.State == FlowInstanceState.Suspended && attempts < 200)
        {
            await harness.WakeWhenDueAsync(ct);
            attempts++;
        }

        harness.Instance.State.ShouldBe(
            FlowInstanceState.Failed,
            "a poll that ran out ends the flow, because falling through would extract fields " +
            "from a job that never produced any");

        harness.Ocr.Reviews.ShouldContainKey(
            "doc-1", "the escalation block ran before the failure it ends with");

        harness.Ocr.IsOpen("doc-1").ShouldBeFalse(
            "and the unwind closed the OCR job the first invocation opened");
    }

    /// <summary>
    /// A resume that arrives after the poll is over does not make one more call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A poll node commits no row of its own, so nothing in the journal says "this
    /// loop is finished".</strong> An instance resumed past a completed poll — by a recovery
    /// scan, by a redelivered trigger, by an operator — arrives at the node again, and the
    /// thing that has to stop it polling once more is the same question that ended it: the last
    /// attempt's answer is in the restored state bag and the predicate still holds.
    /// </para>
    /// <para>
    /// Without that, every touch of a finished document would be one more call to a provider
    /// that bills per request, and the flow would still complete — which is why this is a test
    /// rather than a comment.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AResumePastAFinishedPollDoesNotPollAgain()
    {
        var harness = DocumentHarness.Create(TimeSpan.FromSeconds(1));
        var ct = TestContext.Current.CancellationToken;

        // One page at one second, and the schedule's first gap is up to five — so the second
        // attempt finds the job done and the poll ends on it.
        await harness.StartAsync(ADocument(), ct);

        while (harness.Instance.State == FlowInstanceState.Suspended)
        {
            await harness.WakeWhenDueAsync(ct);
        }

        harness.Instance.State.ShouldBe(FlowInstanceState.Completed);

        var attemptsWhenFinished = harness.RowsFor(PollBody).Count;

        var again = await harness.WakeAsync(ct);

        again.Trace.Count("ocr.status").ShouldBe(
            0, "the poll was over before this sweep looked: " + again);

        harness.RowsFor(PollBody).Count.ShouldBe(attemptsWhenFinished);
    }

    /// <summary>
    /// A failing attempt fails the flow rather than being counted as "not yet".
    /// </summary>
    /// <remarks>
    /// The distinction is the whole reason the predicate is separate from the outcome. "The job
    /// is not finished" is a successful call with an unfinished answer; "the status endpoint
    /// refused" is a failure, and treating it as the former would poll a broken dependency for
    /// four hours and then escalate, reporting a timeout for something that was never a
    /// timeout. Tolerating a transient fault is a <c>Retry</c> policy on the capability, which
    /// is a different declaration in a different place.
    /// </remarks>
    [Fact]
    public async Task AFailingAttemptFailsTheFlowAndUnwinds()
    {
        var harness = DocumentHarness
            .Create(TimeSpan.FromMinutes(30))
            .Substitute("ocr.status", new Error("ocr.unreachable", "no answer", ErrorCategory.Unavailable));

        var ct = TestContext.Current.CancellationToken;

        var started = await harness.StartAsync(ADocument(), ct);

        started.IsSuspended.ShouldBeFalse();
        started.Error!.Code.ShouldBe("ocr.unreachable");

        harness.Instance.State.ShouldBe(FlowInstanceState.Failed);
        started.Trace.Compensated.ShouldBe(["ocr.cancel"]);
        harness.Ocr.IsOpen("doc-1").ShouldBeFalse();
    }

    /// <summary>The flat index of the capability the poll re-enters.</summary>
    /// <remarks>
    /// Read off the compiled plan rather than written as <c>2</c>, so a step added before the
    /// poll moves this with it instead of silently making every assertion above look at the
    /// wrong rows.
    /// </remarks>
    private static int PollBody =>
        ProcessDocumentFlow.Plan.Graph.Steps.Single(step => step.Kind == StepKind.Poll).Index + 1;
}
