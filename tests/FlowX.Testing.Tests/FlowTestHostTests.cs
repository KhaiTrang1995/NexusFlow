using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Testing.Tests;

/// <summary>
/// What <see cref="FlowTestHost"/> is for: running a compiled flow with capabilities
/// substituted, and being able to say what it did.
/// </summary>
/// <remarks>
/// Each of these asserts on a property a flow test would otherwise have to stand up a web
/// host to observe, or could not observe at all. Where a test pins an ordering, the
/// ordering is the point — an assertion that only checks the returned error cannot tell a
/// saga that unwound correctly from one that unwound backwards.
/// </remarks>
public sealed class FlowTestHostTests
{
    [Fact]
    public async Task ASubstitutedCapabilityAnswersInsteadOfTheRealOne()
    {
        var steps = Fixtures.SagaSteps();

        var host = FlowTestHost.For(Fixtures.Saga(), steps)
            .Substitute("payment.capture", Fixtures.Declined)
            .Build();

        var run = await host.RunAsync(new Order("SKU-1", 2), TestContext.Current.CancellationToken);

        run.Error!.Code.ShouldBe("payment.declined");

        // The real step body never ran. Without this the test would pass just as well if
        // the substitution had been ignored and the capability had failed on its own.
        steps.Calls.ShouldNotContain("capture");
        steps.Calls.ShouldContain("reserve");
    }

    [Fact]
    public async Task ASubstitutedCapabilitysValueReachesTheNextStep()
    {
        var host = FlowTestHost.For(Fixtures.Saga(), Fixtures.SagaSteps())
            .Substitute("inventory.reserve", _ => Result.Ok(new Reservation("res-substituted")))
            .Build();

        var run = await host.RunAsync(
            new Order("SKU-1", 2),
            ctx => new OrderReceipt(ctx.Get<Reservation>().Id, ctx.Get<Payment>().Receipt),
            TestContext.Current.CancellationToken);

        run.IsSuccess.ShouldBeTrue(run.ToString());

        // A stand-in that did not write its output into the context would leave this
        // reading whatever the real capability produced — or throw, two steps later.
        run.Output.ReservationId.ShouldBe("res-substituted");
        run.Output.ReceiptId.ShouldBe("receipt-1");
    }

    [Fact]
    public async Task CompensationUnwindsInStrictReverseOrder()
    {
        // Two compensable steps complete, then a third step fails. Reverse order is the
        // saga's safety property: undo newest-first, or two systems end up disagreeing
        // about what happened in between. With one compensation this assertion would hold
        // whichever direction the engine walked.
        var host = FlowTestHost.For(Fixtures.Saga(), Fixtures.SagaSteps())
            .Substitute("shipping.dispatch", Fixtures.NoCarrier)
            .Build();

        var run = await host.RunAsync(new Order("SKU-1", 2), TestContext.Current.CancellationToken);

        run.Error!.Code.ShouldBe("shipping.no_carrier");
        run.Compensation.ShouldBe(CompensationOutcome.Succeeded);
        run.Trace.Executed.ShouldBe(["inventory.reserve", "payment.capture", "shipping.dispatch"]);
        run.Trace.Compensated.ShouldBe(["payment.refund", "inventory.release"], run.ToString());
    }

    [Fact]
    public async Task ASuccessfulFlowCompensatesNothing()
    {
        var host = FlowTestHost.For(Fixtures.Saga(), Fixtures.SagaSteps()).Build();

        var run = await host.RunAsync(new Order("SKU-1", 2), TestContext.Current.CancellationToken);

        run.IsSuccess.ShouldBeTrue(run.ToString());
        run.Compensation.ShouldBe(CompensationOutcome.NotRequired);
        run.Trace.Compensated.ShouldBeEmpty();
        run.Trace.Executed.ShouldBe([
            "inventory.reserve", "payment.capture", "shipping.dispatch", "emit:order.placed"
        ]);
    }

    [Theory]
    [InlineData(true, new[] { "order.validate", "inventory.reserve", "payment.capture", "emit:order.reviewed" })]
    [InlineData(false, new[] { "order.validate", "shipping.dispatch", "emit:order.reviewed" })]
    public async Task TheTraceSaysWhichBranchRan(bool taken, string[] expected)
    {
        var steps = new ScriptedDispatcher().Predicate(1, _ => taken);

        var host = FlowTestHost.For(Fixtures.Conditional(), steps).Build();

        var run = await host.RunAsync(new Order("SKU-1", 2), TestContext.Current.CancellationToken);

        run.IsSuccess.ShouldBeTrue(run.ToString());
        run.Trace.Executed.ShouldBe(expected, run.ToString());
        run.Trace.Entries.ShouldContain(
            e => e.Kind == FlowTestEntryKind.Branch && e.Name == (taken ? "branch:then" : "branch:otherwise"));
    }

    [Theory]
    [InlineData(0, "inventory.reserve", "switch:0")]
    [InlineData(1, "payment.capture", "switch:1")]
    [InlineData(-1, "shipping.dispatch", "switch:default")]
    public async Task TheTraceSaysWhichSwitchArmRan(int arm, string expected, string recorded)
    {
        var steps = new ScriptedDispatcher().Selector(1, _ => arm);

        var host = FlowTestHost.For(Fixtures.Switching(), steps).Build();

        var run = await host.RunAsync(new Order("SKU-1", 2), TestContext.Current.CancellationToken);

        run.Trace.Executed.ShouldBe(["order.validate", expected, "emit:order.routed"], run.ToString());
        run.Trace.Entries.ShouldContain(e => e.Kind == FlowTestEntryKind.Switch && e.Name == recorded);
    }

    [Fact]
    public async Task EveryBranchOfAForkRunsAndTheTraceCountsThem()
    {
        var host = FlowTestHost.For(Fixtures.Fork(MergeStrategy.AllMustSucceed), new ScriptedDispatcher())
            .Build();

        var run = await host.RunAsync(new Order("SKU-1", 2), TestContext.Current.CancellationToken);

        run.IsSuccess.ShouldBeTrue(run.ToString());

        // Counted rather than ordered: the branches genuinely interleave, so their
        // relative order is the thread pool's answer and not the flow's.
        run.Trace.TimesExecuted("inventory.reserve").ShouldBe(1);
        run.Trace.TimesExecuted("payment.capture").ShouldBe(1);
        run.Trace.TimesExecuted("shipping.dispatch").ShouldBe(1);
    }

    [Fact]
    public async Task AllSettledKeepsGoingWhereAllMustSucceedStops()
    {
        // The two strategies differ only in what a failed branch means, so the same
        // substitution run under both is what makes the difference visible.
        var strict = await RunForkAsync(MergeStrategy.AllMustSucceed);
        var settled = await RunForkAsync(MergeStrategy.AllSettled);

        strict.IsFailure.ShouldBeTrue();
        strict.Trace.Executed.ShouldNotContain("emit:order.enriched");

        settled.IsSuccess.ShouldBeTrue(settled.ToString());
        settled.Trace.Executed.ShouldContain("emit:order.enriched");
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public async Task AQuorumIsSatisfiedByEnoughBranches(int required, bool satisfied)
    {
        // One of three branches is substituted to fail, so a quorum of one or two holds
        // and a quorum of three does not.
        var run = await RunForkAsync(MergeStrategy.Quorum(required));

        run.IsSuccess.ShouldBe(satisfied, run.ToString());
    }

    [Fact]
    public async Task FirstSuccessSucceedsWithOneGoodBranch()
    {
        var run = await RunForkAsync(MergeStrategy.FirstSuccess);

        run.IsSuccess.ShouldBeTrue(run.ToString());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task ABoundedIterationRunsItsBodyOncePerElement(int maxDegreeOfParallelism)
    {
        var steps = new ScriptedDispatcher()
            .IterateOver(0, [new Line("A"), new Line("B"), new Line("C")]);

        var host = FlowTestHost.For(Fixtures.Loop(maxDegreeOfParallelism), steps).Build();

        var run = await host.RunAsync(new Order("SKU-1", 3), TestContext.Current.CancellationToken);

        run.IsSuccess.ShouldBeTrue(run.ToString());

        // The count is the assertion, at both bounds. At four the iterations overlap and
        // the trace order is arbitrary; the number of times the body ran is not.
        run.Trace.TimesExecuted("inventory.reserve").ShouldBe(3);
        run.Trace.TimesExecuted("payment.capture").ShouldBe(3);
        run.Trace.Entries.ShouldContain(e => e.Kind == FlowTestEntryKind.Iteration && e.Name == "foreach:3");
    }

    [Fact]
    public async Task AFailingElementUnwindsTheElementsThatCompleted()
    {
        var elements = 0;

        var steps = new ScriptedDispatcher()
            .IterateOver(0, [new Line("A"), new Line("B"), new Line("C")])
            .Compensates(1, "release");

        var host = FlowTestHost.For(Fixtures.Loop(maxDegreeOfParallelism: 1), steps)
            .Substitute<Payment>(
                "payment.capture",
                _ => ++elements == 3 ? Fixtures.Declined : Result.Ok(new Payment("receipt")))
            .Build();

        var run = await host.RunAsync(new Order("SKU-1", 3), TestContext.Current.CancellationToken);

        run.Error!.Code.ShouldBe("payment.declined");

        // Three reservations were made, the third element failed, and all three holds are
        // given back — including the failing element's own, which had completed.
        run.Trace.TimesExecuted("inventory.reserve").ShouldBe(3);
        run.Trace.TimesCompensated("inventory.release").ShouldBe(3);
    }

    [Fact]
    public async Task ContinueOnErrorRunsEveryElementAndReportsWhatFailed()
    {
        var elements = 0;

        var steps = new ScriptedDispatcher()
            .IterateOver(0, [new Line("A"), new Line("B"), new Line("C")]);

        var host = FlowTestHost.For(Fixtures.Loop(maxDegreeOfParallelism: 1, continueOnError: true), steps)
            .Substitute<Payment>(
                "payment.capture",
                _ => ++elements == 2 ? Fixtures.Declined : Result.Ok(new Payment("receipt")))
            .Build();

        var run = await host.RunAsync(new Order("SKU-1", 3), TestContext.Current.CancellationToken);

        // The default stops at the first failing element; this does not, and the
        // difference is invisible in the returned error — only the trace shows it.
        run.IsSuccess.ShouldBeTrue(run.ToString());
        run.Trace.TimesExecuted("payment.capture").ShouldBe(3);
        run.Trace.TimesExecuted("inventory.reserve").ShouldBe(3);
    }

    [Fact]
    public async Task AComposedFlowsStepsAreInTheSameTrace()
    {
        var child = new ScriptedDispatcher().Produces<Payment>(0, "capture", _ => new Payment("receipt-child"));

        var parent = new ScriptedDispatcher()
            .ComposeAt(0, Fixtures.Child(), child, new Order("SKU-1", 2));

        var host = FlowTestHost.For(Fixtures.Composing(SubFlowMode.Inline), parent).Build();

        var run = await host.RunAsync(new Order("SKU-1", 2), TestContext.Current.CancellationToken);

        run.IsSuccess.ShouldBeTrue(run.ToString());
        run.Trace.Executed.ShouldBe([
            "payment.capture", "emit:payment.settled", "shipping.dispatch", "emit:order.fulfilled"
        ]);

        // Attributed to the flow they belong to, which is what lets a parent's undo be
        // told from a child's when both use the same capability.
        run.Trace.Entries
            .Where(e => e.Kind == FlowTestEntryKind.Step && e.Name == "payment.capture")
            .Select(e => e.FlowId)
            .ShouldBe(["payment.settle"]);
    }

    [Fact]
    public async Task ASubstitutionReachesIntoAComposedFlow()
    {
        var child = new ScriptedDispatcher().Produces<Payment>(0, "capture", _ => new Payment("receipt-child"));

        var parent = new ScriptedDispatcher()
            .ComposeAt(0, Fixtures.Child(), child, new Order("SKU-1", 2));

        var host = FlowTestHost.For(Fixtures.Composing(SubFlowMode.Inline), parent)
            .Substitute("payment.capture", Fixtures.Declined)
            .Build();

        var run = await host.RunAsync(new Order("SKU-1", 2), TestContext.Current.CancellationToken);

        run.IsFailure.ShouldBeTrue();
        child.Calls.ShouldNotContain("capture");
        run.UnusedSubstitutions.ShouldBeEmpty();
    }

    [Fact]
    public async Task AChildsCompletedStepsUnwindWhenTheParentLaterFails()
    {
        // The cross-boundary case. The child succeeded and returned; its effects are still
        // standing when the parent's next step fails, and the child's own dispatcher — not
        // the parent's — has to undo them.
        var child = new ScriptedDispatcher()
            .Produces<Payment>(0, "capture", _ => new Payment("receipt-child"))
            .Compensates(0, "refund");

        var parent = new ScriptedDispatcher()
            .ComposeAt(0, Fixtures.Child(), child, new Order("SKU-1", 2));

        var host = FlowTestHost.For(Fixtures.Composing(SubFlowMode.Inline), parent)
            .Substitute("shipping.dispatch", Fixtures.NoCarrier)
            .Build();

        var run = await host.RunAsync(new Order("SKU-1", 2), TestContext.Current.CancellationToken);

        run.Error!.Code.ShouldBe("shipping.no_carrier");
        run.Trace.Compensated.ShouldBe(["payment.refund"], run.ToString());
        child.Calls.ShouldContain("refund");
    }

    [Fact]
    public async Task ADetachedChildIsDrainedBeforeTheRunIsReported()
    {
        // A detached child outlives its parent by design, so a host that returned the
        // moment the parent finished would report a trace whose contents depended on
        // scheduling. The gate is not that it usually passes.
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var child = new ScriptedDispatcher();

        var parent = new ScriptedDispatcher()
            .ComposeAt(0, Fixtures.Child(), child, new Order("SKU-1", 2));

        var host = FlowTestHost.For(Fixtures.Composing(SubFlowMode.Detached), parent)
            .Substitute<Payment>("payment.capture", async (_, _) =>
            {
                started.TrySetResult();
                await release.Task.ConfigureAwait(false);

                return new Payment("receipt-detached");
            })
            .Build();

        var run = host.RunAsync(new Order("SKU-1", 2), TestContext.Current.CancellationToken);

        // The parent's own steps are done long before the child is allowed to finish.
        await started.Task;
        release.SetResult();

        var settled = await run;

        settled.IsSuccess.ShouldBeTrue(settled.ToString());
        settled.Trace.Executed.ShouldContain("emit:payment.settled");
    }

    [Fact]
    public async Task AFailStepUnwindsWhateverCompletedBeforeIt()
    {
        var steps = new ScriptedDispatcher()
            .Produces<Reservation>(0, "reserve", _ => new Reservation("res-1"))
            .Fails(1, "reject", new Error("order.screened_out", "Refused.", ErrorCategory.Validation))
            .Compensates(0, "release");

        var host = FlowTestHost.For(Fixtures.Rejecting(), steps).Build();

        var run = await host.RunAsync(new Order("SKU-1", 2), TestContext.Current.CancellationToken);

        run.Error!.Code.ShouldBe("order.screened_out");
        run.Trace.Executed.ShouldBe(["inventory.reserve", "fail"]);
        run.Trace.Compensated.ShouldBe(["inventory.release"]);
    }

    [Fact]
    public async Task TwoRunsOnOneHostDoNotSeeEachOthersContext()
    {
        // The context is pooled and reset on return, so the second run must be handed a
        // clean one. A host that leaked it would produce exactly the cross-test
        // contamination it exists to prevent — and would do so silently, because the
        // leaked value is a plausible one.
        var seen = new List<string>();

        var steps = new ScriptedDispatcher()
            .Produces<Reservation>(0, "reserve", ctx => new Reservation("res-" + ctx.Get<Order>().Sku))
            .Step(1, "capture", ctx =>
            {
                seen.Add(ctx.TryGet<Shipment>(out var shipment) ? shipment.Tracking : "(none)");
                ctx.Set(new Payment("receipt"));

                return StepOutcome.Success;
            })
            .Produces<Shipment>(2, "dispatch", ctx => new Shipment("track-" + ctx.Get<Order>().Sku));

        var host = FlowTestHost.For(Fixtures.Saga(), steps).Build();

        await host.RunAsync(new Order("SKU-1", 1), TestContext.Current.CancellationToken);
        await host.RunAsync(new Order("SKU-2", 1), TestContext.Current.CancellationToken);

        seen.ShouldBe(["(none)", "(none)"]);
    }

    [Fact]
    public async Task EachRunGetsItsOwnTrace()
    {
        var host = FlowTestHost.For(Fixtures.Saga(), Fixtures.SagaSteps()).Build();

        var first = await host.RunAsync(new Order("SKU-1", 1), TestContext.Current.CancellationToken);
        var second = await host.RunAsync(new Order("SKU-2", 1), TestContext.Current.CancellationToken);

        first.Trace.ShouldNotBeSameAs(second.Trace);
        first.Trace.Executed.Count.ShouldBe(4);
        second.Trace.Executed.Count.ShouldBe(4);
    }

    [Fact]
    public async Task ASubstitutionTheRunNeverReachedIsReported()
    {
        // The failure this catches is a test that passes for the wrong reason: the branch
        // carrying the substituted step was not taken, so the assertion about the flow's
        // outcome is about a path the substitution never touched.
        var steps = new ScriptedDispatcher().Predicate(1, _ => false);

        var host = FlowTestHost.For(Fixtures.Conditional(), steps)
            .Substitute("inventory.reserve", Fixtures.Declined)
            .Build();

        var run = await host.RunAsync(new Order("SKU-1", 2), TestContext.Current.CancellationToken);

        run.IsSuccess.ShouldBeTrue(run.ToString());
        run.UnusedSubstitutions.ShouldBe(["inventory.reserve"]);
    }

    [Fact]
    public void SubstitutingACapabilityTheFlowDoesNotUseIsRefused()
    {
        var build = () => FlowTestHost.For(Fixtures.Saga(), Fixtures.SagaSteps())
            .Substitute("payment.captured", Fixtures.Declined)
            .Build();

        var error = Should.Throw<InvalidOperationException>(build);

        error.Message.ShouldContain("payment.captured");
        error.Message.ShouldContain("payment.capture");
    }

    [Fact]
    public async Task AdvancingTheClockPastTheDeadlineStopsTheFlow()
    {
        var clock = new FlowTestClock();

        var steps = new ScriptedDispatcher()
            .Produces<Reservation>(0, "reserve", _ => new Reservation("res-1"))
            .Step(1, "capture", ctx =>
            {
                clock.Advance(TimeSpan.FromMinutes(1));
                ctx.Set(new Payment("receipt"));

                return StepOutcome.Success;
            })
            .Compensates(0, "release")
            .Compensates(1, "refund");

        var host = FlowTestHost.For(Fixtures.Saga(TimeSpan.FromSeconds(30)), steps)
            .WithClock(clock)
            .Build();

        var run = await host.RunAsync(new Order("SKU-1", 2), TestContext.Current.CancellationToken);

        run.Error!.Code.ShouldBe("flow.deadline_exceeded");

        // The deadline is a failure like any other, so what completed is still undone.
        run.Trace.Compensated.ShouldBe(["payment.refund", "inventory.release"]);
    }

    [Fact]
    public async Task TheInvocationsIdempotencyKeyReachesTheSteps()
    {
        string? seen = null;

        var steps = new ScriptedDispatcher()
            .Produces<Reservation>(0, "reserve", ctx =>
            {
                seen = ctx.IdempotencyKey;

                return new Reservation(seen);
            });

        var host = FlowTestHost.For(Fixtures.Saga(), steps)
            .WithInvocation(new FlowInvocation("corr-1", "key-1", TenantId: "tenant-a"))
            .Build();

        await host.RunAsync(new Order("SKU-1", 2), TestContext.Current.CancellationToken);

        seen.ShouldBe("key-1");
    }

    [Fact]
    public async Task TheTraceReadsAsSomethingAFailureMessageCanPrint()
    {
        var host = FlowTestHost.For(Fixtures.Saga(), Fixtures.SagaSteps())
            .Substitute("shipping.dispatch", Fixtures.NoCarrier)
            .Build();

        var run = await host.RunAsync(new Order("SKU-1", 2), TestContext.Current.CancellationToken);

        var text = run.ToString();

        text.ShouldContain("failed: shipping.no_carrier");
        text.ShouldContain("order.place[0] Step: inventory.reserve");
        text.ShouldContain("order.place[1] Compensation: payment.refund");
    }

    private static async Task<FlowTestRun> RunForkAsync(MergeStrategy merge)
    {
        var host = FlowTestHost.For(Fixtures.Fork(merge), new ScriptedDispatcher())
            .Substitute("shipping.dispatch", Fixtures.NoCarrier)
            .Build();

        return await host.RunAsync(new Order("SKU-1", 2), TestContext.Current.CancellationToken);
    }
}
