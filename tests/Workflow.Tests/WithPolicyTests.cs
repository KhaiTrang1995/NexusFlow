using System.Text.Json;
using FlowX;
using FlowX.Generated;
using Shouldly;
using Xunit;

namespace Workflow.Tests;

/// <summary>
/// What <c>.WithPolicy(...)</c> reaches, and what it does not.
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/10-Policy-Framework.md</c> says exactly one policy is applied at run time —
/// <c>CompensationRetry</c>, at stage 7 — and that on the forward path there is no policy
/// execution at all. The first half is true of the <em>engine</em>: it reads
/// <c>StepNode.CompensationRetry</c> and honours attempts, backoff and retryable categories,
/// which <c>CompensationPolicyTests</c> proves against a hand-built plan.
/// </para>
/// <para>
/// <strong>It is not true of a flow written in the DSL.</strong> Nothing puts a policy chain
/// on a <c>StepNode</c>: <c>FlowX.Compiler</c>'s <c>FlowEmitter</c> emits no
/// <c>PolicyChain</c> anywhere, so every generated <c>StepNode.ForCapability(...)</c> call
/// carries an id, a descriptor and at most a compensation. The manifest is written by a
/// different class and does publish the set — which is why a reader of
/// <c>flowx.manifest.json</c> would reasonably conclude the policy is in force.
/// </para>
/// <para>
/// So these two tests are a pair, and they are only meaningful together: the set is
/// published, and the plan is empty. <strong>Both invert the day the generator emits a policy
/// chain</strong>, which is exactly when this sample stops being wrong about it.
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
    /// And the compiled plan the engine actually walks carries no policy at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The forward set on <c>identity.create</c> and the compensation set on
    /// <c>workspace.allocate_desk</c> are both absent, and the second one is the sharper
    /// finding: <c>CompensationRetry</c> is the one policy kind the engine knows how to
    /// execute, attached to the one kind of step it applies to, and it still never arrives.
    /// </para>
    /// <para>
    /// The consequence for this sample is that its declared resilience is documentation. A
    /// reader copying this flow gets the timeout, the retry, the breaker and the compensation
    /// retry they wrote in the manifest, in the diagram generated from it, and nowhere in the
    /// running system.
    /// </para>
    /// </remarks>
    [Fact]
    public void AndTheCompiledPlanCarriesNoneOfIt()
    {
        var identity = OnboardEmployeeFlow.Plan.Graph.Steps
            .Single(s => s.Capability?.Id == "identity.create");

        identity.Policies.ShouldBe(
            PolicyChain.Empty,
            "The generator emits no PolicyChain, so the timeout, the retry and the breaker " +
            "are in the manifest and not in the plan.");

        var desk = ProvisionWorkspaceFlow.Plan.Graph.Steps
            .Single(s => s.Capability?.Id == "workspace.allocate_desk");

        desk.CompensationPolicies.ShouldBe(
            PolicyChain.Empty,
            "And neither is the one policy the engine can run.");

        desk.CompensationRetry.ShouldBe(
            CompensationPolicy.None,
            "So the unwind of this step is a single attempt, whatever Policies.FacilitiesUndo " +
            "says. Emitting the chain turns this assertion red.");
    }

    /// <summary>The build-time half of a policy is real, and this is the part that is not a gap.</summary>
    /// <remarks>
    /// <c>Policies.DirectoryService</c> declares a retry, and <c>FLOWX1014</c> refuses a retry
    /// on a capability that does not declare itself idempotent. The rule runs whether or not
    /// anything arms the retry, so the safety property ships even though the behaviour does
    /// not — which is the distinction worth keeping straight when reading §10.
    /// </remarks>
    [Fact]
    public void TheCapabilityTheRetryIsAttachedToDeclaresItselfIdempotent()
    {
        OnboardEmployeeFlow.Plan.Graph.Steps
            .Single(s => s.Capability?.Id == "identity.create")
            .Capability!.IsIdempotent
            .ShouldBeTrue("Otherwise Policies.DirectoryService's Retry would be FLOWX1014.");
    }
}
