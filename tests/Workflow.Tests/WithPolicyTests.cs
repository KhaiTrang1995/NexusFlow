using System.Text.Json;
using FlowX;
using FlowX.Generated;
using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace Workflow.Tests;

/// <summary>
/// What <c>.WithPolicy(...)</c> reaches, and what it still does not.
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/10-Policy-Framework.md</c> says exactly one policy is applied at run time —
/// <c>CompensationRetry</c>, at stage 7 — and that on the forward path there is no policy
/// execution at all. The first half has always been true of the <em>engine</em>: it reads
/// <c>StepNode.CompensationRetry</c> and honours attempts, backoff and retryable categories,
/// which <c>CompensationPolicyTests</c> proves against a hand-built plan.
/// </para>
/// <para>
/// <strong>It was not true of a flow written in the DSL, and now it is.</strong> Nothing put
/// a policy chain on a <c>StepNode</c>: <c>FlowX.Compiler</c>'s <c>FlowEmitter</c> emitted no
/// <c>PolicyChain</c> anywhere, so every generated <c>StepNode.ForCapability(...)</c> call
/// carried an id, a descriptor and at most a compensation. The manifest is written by a
/// different class and did publish the set — which is why a reader of
/// <c>flowx.manifest.json</c> would reasonably have concluded the policy was in force, and
/// been wrong. The emitter now splits the declared set in two and passes both halves.
/// </para>
/// <para>
/// So these tests are still a pair, and still only meaningful together: the set is published,
/// and the plan agrees. The third is the one that was never a gap — the build-time half —
/// and the fourth is the behaviour the first three only describe.
/// </para>
/// </remarks>
public sealed class WithPolicyTests
{
    private static readonly JsonDocument Manifest = JsonDocument.Parse(FlowXManifest.Json);

    /// <summary>The declared set reaches the manifest, with the stage each policy runs in.</summary>
    [Fact]
    public void ThePolicySetIsPublishedInTheManifest()
    {
        var step = Manifest.RootElement
            .GetProperty("flows").EnumerateArray()
            .Single(f => f.GetProperty("id").GetString() == "employee.onboard")
            .GetProperty("steps").EnumerateArray()
            .Single(s => s.TryGetProperty("capability", out var c) &&
                         c.GetString() == "identity.create@1.0.0");

        // Ordinally sorted rather than in declaration order, which is the manifest being a
        // deterministic build artifact: two machines compiling identical source must produce
        // identical bytes, and declaration order is not something a reader of the document
        // needs — the stage below is what determines when a policy runs.
        step.GetProperty("policies").EnumerateArray()
            .Select(p => p.GetProperty("kind").GetString())
            .ShouldBe(["CircuitBreaker", "Retry", "Timeout"]);

        step.GetProperty("policies").EnumerateArray()
            .Select(p => p.GetProperty("stage").GetString())
            .ShouldAllBe(stage => stage == "Resilience");
    }

    /// <summary>
    /// And the compiled plan the engine actually walks carries it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Every assertion in this test used to say the opposite.</strong> The forward set
    /// on <c>identity.create</c> and the compensation set on <c>workspace.allocate_desk</c>
    /// were both absent, and the second was the sharper finding: <c>CompensationRetry</c> is
    /// the one policy kind the engine knows how to execute, attached to the one kind of step it
    /// applies to, and it never arrived.
    /// </para>
    /// <para>
    /// The consequence for this sample was that its declared resilience was documentation. A
    /// reader copying the flow got the timeout, the retry, the breaker and the compensation
    /// retry they wrote in the manifest, in the diagram generated from it, and nowhere in the
    /// running system. The compensation retry is now in the running system too;
    /// <see cref="TheEngineRetriesAFailingUndoUnderTheDeclaredPolicy"/> is the proof.
    /// </para>
    /// </remarks>
    [Fact]
    public void AndTheCompiledPlanCarriesIt()
    {
        var identity = OnboardEmployeeFlow.Plan.Graph.Steps
            .Single(s => s.Capability?.Id == "identity.create");

        // Flipped from `ShouldBe(PolicyChain.Empty)`.
        identity.Policies.Ordered
            .Select(p => p.Kind)
            .ShouldBe(["Timeout", "Retry", "CircuitBreaker"],
                "The generator emits the chain, so the timeout, the retry and the breaker are " +
                "in the manifest and in the plan. Ordered by stage, then by declaration — " +
                "all three are Resilience, so ADR-0011's stable sort keeps them as written.");

        identity.CompensationPolicies.ShouldBe(
            PolicyChain.Empty,
            "Policies.DirectoryService says nothing about the undo, so nothing is put on it. " +
            "A chain is split by what each policy wraps, not by the stage it runs in.");

        var desk = ProvisionWorkspaceFlow.Plan.Graph.Steps
            .Single(s => s.Capability?.Id == "workspace.allocate_desk");

        // Flipped from `ShouldBe(PolicyChain.Empty)`. This is the one the engine can run.
        desk.CompensationPolicies.Ordered
            .Select(p => p.Kind)
            .ShouldBe([CompensationPolicy.CompensationRetryKind]);

        // Flipped from `ShouldBe(CompensationPolicy.None)`. Three attempts, which is the
        // number Policies.FacilitiesUndo declares and deliberately not the documented default
        // of five — so the assertion states something the fallback could not supply.
        desk.CompensationRetry.Attempts.ShouldBe(3);
        desk.CompensationRetry.IsRetrying.ShouldBeTrue();

        desk.Policies.ShouldBe(
            PolicyChain.Empty,
            "and the forward half of that set is empty, because the set declares nothing that " +
            "wraps the allocation itself.");

        // The flag the engine reads before it does any retry bookkeeping at all. False for
        // every compiled plan until the emitter started passing chains.
        ProvisionWorkspaceFlow.Plan.HasCompensationPolicies.ShouldBeTrue();
    }

    /// <summary>
    /// A flow that declares no compensation policy still pays nothing for the ones that do.
    /// </summary>
    /// <remarks>
    /// <c>HasCompensationPolicies</c> exists so the unwind of a plain saga stays the single
    /// dispatch per entry it always was — no clock read, no retry bookkeeping, one predictable
    /// always-false comparison. Making it true more often must not make it true for a flow
    /// that asked for nothing, and <c>employee.onboard</c> is compensable on six steps and
    /// declares a compensation retry on none of them.
    /// </remarks>
    [Fact]
    public void AFlowThatDeclaresNoCompensationPolicyKeepsTheFlagFalse()
    {
        OnboardEmployeeFlow.Plan.HasCompensation.ShouldBeTrue("six steps name an inverse.");

        OnboardEmployeeFlow.Plan.HasCompensationPolicies.ShouldBeFalse(
            "and not one of them declares a CompensationRetry, so this flow's unwind takes " +
            "exactly the path it took before the emitter carried chains at all.");
    }

    /// <summary>The build-time half of a policy is real, and this is the part that was never a gap.</summary>
    /// <remarks>
    /// <c>Policies.DirectoryService</c> declares a retry, and <c>FLOWX1014</c> refuses a retry
    /// on a capability that does not declare itself idempotent. The rule ran whether or not
    /// anything armed the retry, so the safety property shipped before the behaviour did —
    /// which is the distinction worth keeping straight when reading §10. <c>PolicyChain</c>
    /// enforces the same rule when the plan is built, which is what now makes the emitted
    /// <c>ForStep(Policies.DirectoryService, …)</c> above safe to construct.
    /// </remarks>
    [Fact]
    public void TheCapabilityTheRetryIsAttachedToDeclaresItselfIdempotent()
    {
        OnboardEmployeeFlow.Plan.Graph.Steps
            .Single(s => s.Capability?.Id == "identity.create")
            .Capability!.IsIdempotent
            .ShouldBeTrue("Otherwise Policies.DirectoryService's Retry would be FLOWX1014.");
    }

    /// <summary>
    /// And the engine retries the failing undo, three times, exactly as declared.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the assertion the other three only describe.</strong> The chain reaching
    /// the plan is worth nothing on its own; what WP-57 promised is that a compensation whose
    /// dependency is briefly unreachable is asked again rather than reported as an
    /// unrecoverable business inconsistency. <c>workspace.release_desk</c> fails twice with an
    /// <see cref="ErrorCategory.Unavailable"/> — in <c>Policies.FacilitiesUndo</c>'s retryable
    /// set — and succeeds on the third attempt, which is the last one three attempts allow.
    /// </para>
    /// <para>
    /// The failure is injected on the <em>child</em> flow's compensation, which is where the
    /// policy is declared, and the trace records it across the sub-flow boundary. Before the
    /// emitter carried the chain this ran once, reported <c>PartiallyFailed</c>, and left the
    /// desk held.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheEngineRetriesAFailingUndoUnderTheDeclaredPolicy()
    {
        var attempts = 0;

        var harness = OnboardingHarness.Create()
            .Substitute("welcome.send", new Error("post.unavailable", "the post room is shut", ErrorCategory.Unavailable))
            .Substitute("workspace.release_desk", (_, _) =>
            {
                attempts++;

                return ValueTask.FromResult(attempts < 3
                    ? StepOutcome.Failed(new Error(
                        "facilities.unavailable", "the desk booking system is down", ErrorCategory.Unavailable))
                    : StepOutcome.Success);
            });

        var run = await harness.RunAsync(Offers.Permanent(), TestContext.Current.CancellationToken);

        run.IsSuccess.ShouldBeFalse(run.ToString());

        attempts.ShouldBe(
            3,
            "Policies.FacilitiesUndo declares three attempts, and the first two failed with a " +
            "category it lists as retryable. " + run);

        run.Compensation.ShouldBe(
            CompensationOutcome.Succeeded,
            "The third attempt worked, so the unwind as a whole did. A single dispatch would " +
            "have reported PartiallyFailed and left the desk unreleased. " + run);

        run.Trace.Compensated.Count(id => id == "workspace.release_desk").ShouldBe(
            3,
            "Each attempt is a real dispatch through the dispatcher, not a loop inside the " +
            "stand-in — which is what makes it the engine's retry and not the test's. " + run);

        run.Trace.Compensated.ShouldContain(
            "workspace.cancel_pass",
            "and the rest of the unwind still ran: the retry is per compensation, not a " +
            "restart of the whole stack. " + run);
    }

    /// <summary>
    /// A retry that exhausts still gives up, and reports it.
    /// </summary>
    /// <remarks>
    /// The other half of the rule. A policy that retried forever would turn a failed undo into
    /// a hung flow, and <c>docs/11-Distributed-Runtime.md §8</c>'s "no automatic resolution"
    /// row is reachable precisely because the attempts are bounded by the number declared.
    /// </remarks>
    [Fact]
    public async Task AnUndoThatFailsEveryAttemptStopsAtTheDeclaredCount()
    {
        var attempts = 0;

        var harness = OnboardingHarness.Create()
            .Substitute("welcome.send", new Error("post.unavailable", "shut", ErrorCategory.Unavailable))
            .Substitute("workspace.release_desk", (_, _) =>
            {
                attempts++;

                return ValueTask.FromResult(StepOutcome.Failed(new Error(
                    "facilities.unavailable", "still down", ErrorCategory.Unavailable)));
            });

        var run = await harness.RunAsync(Offers.Permanent(), TestContext.Current.CancellationToken);

        attempts.ShouldBe(3, "Three, and not a fourth. " + run);

        run.Compensation.ShouldBe(
            CompensationOutcome.PartiallyFailed,
            "The undo never succeeded, and the run says so rather than hiding it. " + run);
    }
}
