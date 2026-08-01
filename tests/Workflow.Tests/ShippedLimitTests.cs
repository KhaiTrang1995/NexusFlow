using FlowX;
using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace Workflow.Tests;

/// <summary>
/// The two limits this sample's shape walks into, measured on this sample rather than
/// described.
/// </summary>
/// <remarks>
/// <para>
/// Both are stated in <c>ADR-0015</c> and pinned in <c>tests/FlowX.Runtime.Tests</c> against
/// hand-built plans. These say the same things about <c>employee.onboard</c>, because a
/// sample that forks and composes a child inherits both, and a reader deciding whether to
/// copy this flow needs them in the sample's own terms.
/// </para>
/// <para>
/// <strong>Each goes red the day its limit is fixed</strong>, which is the only kind of note
/// about a known limit that survives.
/// </para>
/// </remarks>
public sealed class ShippedLimitTests
{
    // ---------------------------------------------------------------------------------
    // Limit 1: an overlapping fork does not replay
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// When two of this fork's branches genuinely overlap, one branch's captured id is
    /// recorded on its sibling's journal row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>One pooled context is shared by every branch of a <c>Parallel</c>.</strong> The
    /// capture taken at a branch's commit takes everything minted since the last commit —
    /// including a sibling's. The interleaving here is pinned by a rendezvous rather than
    /// hoped for, so the demonstration is the same on every run: <c>hardware.order</c> mints
    /// an id and waits, <c>access.grant</c> mints its own and commits, and
    /// <c>access.grant</c>'s row carries both while <c>hardware.order</c>'s carries none.
    /// </para>
    /// <para>
    /// <strong>What it costs this sample.</strong> The three real capabilities behind this
    /// fork complete synchronously against in-memory adapters, so today they do not overlap
    /// and the instance replays. Point any one of them at a network — which is the entire
    /// reason to fork — and they do, and from then on this instance's <c>NondeterminismCapture</c>
    /// is attributed to whichever branch committed first. Nothing warns about the change.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AForkWhoseBranchesOverlapAttributesOneBranchsIdToItsSiblingsRow()
    {
        var harness = OnboardingHarness.Create();

        using var minted = new SemaphoreSlim(0, 1);
        using var released = new SemaphoreSlim(0, 1);

        harness
            .Substitute("hardware.order", async (ctx, ct) =>
            {
                // Mint, then hand over to the sibling and wait for it to commit.
                var id = ctx.NewId();

                minted.Release();
                await released.WaitAsync(ct);

                ctx.Set(new LaptopOrder(id.ToString()));

                return StepOutcome.Success;
            })
            .Substitute("access.grant", async (ctx, ct) =>
            {
                await minted.WaitAsync(ct);

                ctx.Set(new AccessGrant(ctx.NewId().ToString()));

                return StepOutcome.Success;
            });

        var run = harness.RunAsync(Offers.Permanent(), TestContext.Current.CancellationToken);

        // access.grant commits while hardware.order is still suspended, which is the ordering
        // the whole demonstration rests on.
        await Task.Delay(150, TestContext.Current.CancellationToken);
        released.Release();

        (await run).IsSuccess.ShouldBeTrue();

        var parent = harness.Journal.Instances.Single(i => i.FlowId == "employee.onboard");

        var frontier = await harness.Journal.ReadResumeFrontierAsync(
            parent.InstanceId, TestContext.Current.CancellationToken);

        // Step 9 is hardware.order, step 10 is access.grant, as the compiled plan lays them out.
        var suspended = frontier.Value.Committed.Single(row => row.Key.StepId == 9);
        var committedFirst = frontier.Value.Committed.Single(row => row.Key.StepId == 10);

        committedFirst.Nondeterminism.NewIds.Count.ShouldBe(
            2,
            "access.grant committed first, so it took everything minted since the last " +
            "commit — its own id and its suspended sibling's.");

        suspended.Nondeterminism.NewIds.ShouldBeEmpty(
            "The branch that actually minted the first id has no record of having minted " +
            "anything. Replaying this instance hands that row back faithfully, so the branch " +
            "mints a fresh id and the replay diverges at the first thing it did. A per-branch " +
            "context turns this assertion round, and this test with it.");
    }

    // ---------------------------------------------------------------------------------
    // Limit 2: a resumed parent does not rebuild a skipped child's compensation stack
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// A parent that resumes past a completed <c>workspace.provision</c> and then fails does
    /// not release the desk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the sharpest consequence of composing a child inside a saga, and it is
    /// a real hole rather than a theoretical one.</strong> The parent records the composition
    /// as one entry in its own stack, bound to the <em>child's</em> context — and that context
    /// died with the node that ran it. On resume the composition's journal row is committed,
    /// so the loop steps over it (composing the child again would re-run its effects, which is
    /// what resumption exists to avoid) and nothing puts the child's completed steps back on
    /// the unwind stack.
    /// </para>
    /// <para>
    /// So <c>A · child(X, Y) · B</c> should unwind <c>B, Y, X, A</c> and, after a resume,
    /// unwinds <c>B, A</c>. For this flow that means the desk stays held and the pass stays
    /// issued while everything the parent did is correctly reversed — a saga that is silently
    /// short by exactly the child's work.
    /// </para>
    /// <para>
    /// <strong>Why it is not simply fixed.</strong> Rebuilding the child's stack needs the
    /// child's instance id, and the only way to get it from the parent is to ask "which
    /// instances exist under this parent". <c>IFlowJournal</c> deliberately does not answer
    /// that — it is a recovery scan's query, which is why <c>IRecoveryIndex</c> was split out —
    /// and widening the journal contract would oblige every store to serve a query some
    /// deployments never run.
    /// </para>
    /// <para>
    /// The resume below is driven through <c>DurableExecution</c> directly, and re-seeds the
    /// flow's input, because no state bag is journaled yet (WP-59): a resumed instance
    /// re-enters with an empty bag, and without the input the steps after the frontier would
    /// fail for that reason instead of the one under test.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AResumedParentDoesNotUndoTheChildItSteppedOver()
    {
        var harness = OnboardingHarness.Create();
        var journal = new NodeDiesJournal(harness.Journal);
        var input = Offers.Permanent(equipment: []);
        var invocation = new FlowInvocation("corr-resume", "key-resume");
        var ct = TestContext.Current.CancellationToken;
        var instanceId = Guid.NewGuid();
        var engine = new FlowEngine(harness.Clock);

        // ---- the first node: runs past the composed child, then dies ----
        //
        // The commits, in order: offer.validate, payroll.open, identity.create, the fork's
        // three, the child's two, the composition itself — nine — and the node does not
        // survive the tenth. So workspace.provision is committed and everything after it is
        // not, which is the state a surviving node has to make sense of.
        journal.DiesOnCommit = 10;

        var begun = await DurableExecution.BeginAsync(
            journal,
            OnboardEmployeeFlow.Plan,
            invocation,
            instanceId,
            new FencingToken(1),

            // The instance row's immutable copy of the trigger input. It is a JournalPayload
            // and needs a generated JsonTypeInfo the dispatcher does not emit yet, so it is
            // left empty; the typed input is seeded into the context by the engine overload
            // used below, which is the only reason a resumed step can bind anything at all.
            input: null,
            cancellationToken: ct);

        begun.IsSuccess.ShouldBeTrue("The journal opened the instance.");

        await Should.ThrowAsync<NodeDiedException>(
            () => engine.ExecuteAsync(
                    OnboardEmployeeFlow.Plan,
                    harness.Wrap(OnboardEmployeeFlow.Plan, harness.World.Parent, new DurableTrace()),
                    invocation,
                    input,
                    begun.Value,
                    ct)
                .AsTask());

        harness.World.Facilities.HeldDesks.ShouldBe(
            1, "The child allocated a desk and the node died before anything could undo it.");

        // ---- the second node: takes it over, steps over what committed, and then fails ----
        journal.DiesOnCommit = null;

        var second = new DurableTrace();
        var resumed = await DurableExecution.ResumeAsync(journal, instanceId, new FencingToken(2), ct);

        resumed.IsSuccess.ShouldBeTrue("The fence was raised and the frontier read.");

        // The resumed run's remaining steps and every compensation are stood in for, and that
        // is not the limit being dodged — it is a *different* gap being held still so this one
        // is legible. No state bag is journaled yet (WP-59), so a resumed instance re-enters
        // with only the re-seeded input: the real screening.waive would fail binding
        // ctx.Get<Identity>(), and so would the real identity.disable and payroll.close.
        // ResumeTests measures that directly. Here the stand-ins bind nothing, so what the
        // assertions below see is purely the engine's compensation bookkeeping.
        harness
            .Substitute("screening.waive", new Error("hr.unavailable", "HR is down", ErrorCategory.Unavailable))
            .Substitute("identity.disable", (_, _) => ValueTask.FromResult(StepOutcome.Success))
            .Substitute("payroll.close", (_, _) => ValueTask.FromResult(StepOutcome.Success))
            .Substitute("hardware.cancel", (_, _) => ValueTask.FromResult(StepOutcome.Success))
            .Substitute("access.revoke", (_, _) => ValueTask.FromResult(StepOutcome.Success));

        var finished = await engine.ExecuteAsync(
            OnboardEmployeeFlow.Plan,
            harness.Wrap(OnboardEmployeeFlow.Plan, harness.World.Parent, second),
            invocation,
            input,
            resumed.Value,
            ct);

        finished.IsSuccess.ShouldBeFalse(second.ToString());
        finished.Error!.Code.ShouldBe("hr.unavailable");

        second.Entries.ShouldNotContain(
            e => e.Contains("subflow:", StringComparison.Ordinal),
            "The composition committed, so the resumed loop steps over it. Composing the " +
            "child again would re-run its effects, which is what resumption exists to avoid. " +
            second);

        second.Executed.ShouldBe(
            ["screening.waive"],
            "Everything before the frontier is stepped over; the first thing that had not " +
            "committed is where the second node picks up. " + second);

        second.Compensated.ShouldNotContain(
            "workspace.release_desk",
            "The gap: the parent recorded the composition as one entry bound to the child's " +
            "context, and that context died with the node. " + second);

        second.Compensated.ShouldNotContain("workspace.cancel_pass", second.ToString());

        second.Compensated.TakeLast(2).ShouldBe(
            ["identity.disable", "payroll.close"],
            "`A · child(X, Y) · B` should unwind B, Y, X, A. What a resumed parent unwinds " +
            "is B and A. " + second);

        finished.Compensation.ShouldBe(
            CompensationOutcome.Succeeded,
            "And it reports a clean unwind while doing it, which is what makes the gap " +
            "dangerous rather than merely present.");

        harness.World.Facilities.HeldDesks.ShouldBe(
            1,
            "The desk the child allocated is still held after a saga that reported " +
            "CompensationOutcome.Succeeded. That is the whole consequence, in the only terms " +
            "that matter. Closing the gap turns this assertion to 0.");
    }
}
