using Shouldly;
using Xunit;

namespace FlowX.Core.Tests;

/// <summary>
/// The one policy the runtime executes, and the rules that keep it from becoming a policy
/// engine.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a compensation retry is declared at <see cref="PolicyStage.Consistency"/>
/// and not at <see cref="PolicyStage.Resilience"/>.</strong> Stage 7 is where
/// <c>docs/10-Policy-Framework.md §2</c> puts compensation registration, and the unwind is
/// the same stage's obligation discharged later. A retry declared at stage 4 would be a
/// wrapper around a <em>forward</em> step, and executing one of those means executing stage 4
/// without stages 1–3 — which is precisely the ordering ADR-0011 exists to make
/// unexpressible. Confining the executed policy to the last stage is what lets this ship
/// while the rest of the framework is still declaration-only.
/// </para>
/// <para>
/// Ordering itself is <see cref="PolicyChain"/>'s, unchanged. Nothing here introduces a
/// second way to say what runs before what.
/// </para>
/// </remarks>
public sealed class CompensationPolicyTests
{
    [Fact]
    public void ACompensationRetryIsDeclaredAtTheConsistencyStage()
    {
        var declared = PolicySet.Named("undo").CompensationRetry(attempts: 5);

        declared.Policies.Single().Stage.ShouldBe(PolicyStage.Consistency,
            "The unwind is stage 7 work — 'compensation registration' is what ADR-0011 puts " +
            "there. Declaring the retry at Resilience would make it a wrapper round a " +
            "forward step, and running stage 4 without stages 1-3 is the ordering bug the " +
            "fixed order exists to forbid.");
    }

    [Fact]
    public void TheChainStillOrdersByStageAndNothingElse()
    {
        var declared = PolicySet.Named("backwards")
            .CompensationRetry(attempts: 3)
            .Timeout(TimeSpan.FromSeconds(2))
            .RateLimit(100, TimeSpan.FromSeconds(1));

        var chain = PolicyChain.Create(declared, Fixtures.ReleaseInventory);

        chain.Ordered.Select(static p => p.Stage).ShouldBe(
            [PolicyStage.Admission, PolicyStage.Resilience, PolicyStage.Consistency],
            "One ordering mechanism, and it is the stage. A compensation retry declared " +
            "first still runs last.");
    }

    [Fact]
    public void ACompensationRetryOnANonIdempotentCompensationIsRefused()
    {
        var declared = PolicySet.Named("undo").CompensationRetry(attempts: 5);

        var refused = Should.Throw<InvalidFlowPlanException>(
            () => PolicyChain.Create(declared, Fixtures.CapturePayment));

        // FLOWX1014's rule applies to whatever is being retried, and the thing being retried
        // here is the compensating capability — so it is the compensating capability that has
        // to be safe to run twice. Running a reversal twice is a second reversal.
        refused.Message.ShouldContain("payment.capture");
        refused.Message.ShouldContain("Idempotent = false");
    }

    [Fact]
    public void AStepWithNoCompensationCannotCarryACompensationPolicy()
    {
        var chain = PolicyChain.Create(
            PolicySet.Named("undo").CompensationRetry(attempts: 3), Fixtures.ReleaseInventory);

        var refused = Should.Throw<InvalidFlowPlanException>(
            () => StepNode.ForCapability(0, Fixtures.ReserveInventory, compensationPolicies: chain));

        refused.Message.ShouldContain("no compensation");
    }

    [Fact]
    public void AStepWithoutADeclaredPolicyIsCompensatedExactlyOnce()
    {
        var step = StepNode.ForCapability(0, Fixtures.ReserveInventory, Fixtures.ReleaseInventory);

        step.CompensationRetry.Attempts.ShouldBe(1,
            "A policy applies because it was declared. Retrying every undo five times " +
            "whether or not anybody asked would be the policy engine P4 is for, arriving " +
            "early and undeclared.");

        step.CompensationRetry.IsRetrying.ShouldBeFalse();
    }

    [Fact]
    public void TheResolvedPolicyCarriesWhatWasDeclared()
    {
        var chain = PolicyChain.Create(
            PolicySet.Named("undo").CompensationRetry(
                attempts: 4,
                backoff: Backoff.Exponential(TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(1)),
                retryOn: [ErrorCategory.Unavailable]),
            Fixtures.ReleaseInventory);

        var step = StepNode.ForCapability(
            0, Fixtures.ReserveInventory, Fixtures.ReleaseInventory, compensationPolicies: chain);

        step.CompensationRetry.Attempts.ShouldBe(4);
        step.CompensationRetry.Backoff.BaseDelay.ShouldBe(TimeSpan.FromMilliseconds(50));
        step.CompensationRetry.Backoff.Jitter.ShouldBeFalse();
        step.CompensationRetry.RetryOn.ShouldBe([ErrorCategory.Unavailable]);
    }

    [Fact]
    public void TheDefaultSetIsFiveAttempts()
    {
        // docs/06-Execution-Engine.md §7 rule 2: compensation retry is more aggressive than
        // forward retry by default — five attempts against three.
        var policy = CompensationPolicy.From(
            PolicyChain.Create(PolicySet.CompensationDefault, Fixtures.ReleaseInventory));

        policy.Attempts.ShouldBe(5);
        policy.Backoff.Jitter.ShouldBeTrue("Full jitter is the documented default backoff.");
    }

    [Theory]
    [InlineData(ErrorCategory.Validation, false)]
    [InlineData(ErrorCategory.NotFound, false)]
    [InlineData(ErrorCategory.Forbidden, false)]
    [InlineData(ErrorCategory.Conflict, true)]
    [InlineData(ErrorCategory.Unavailable, true)]
    [InlineData(ErrorCategory.Internal, true)]
    public void OnlyTheDeclaredCategoriesAreRetried(ErrorCategory category, bool retried)
    {
        var policy = CompensationPolicy.From(
            PolicyChain.Create(PolicySet.CompensationDefault, Fixtures.ReleaseInventory));

        var failure = new Error("undo.failed", "no", category);

        policy.AllowsAnotherAttempt(failure, attemptsMade: 1).ShouldBe(retried,
            "docs/10-Policy-Framework.md §5: Validation, NotFound and Forbidden are " +
            "terminal. Retrying a compensation the downstream has permanently rejected " +
            "burns the budget an operator could have spent on the ones that are transient.");
    }

    [Fact]
    public void TheAttemptBudgetIsExhaustedRatherThanUnbounded()
    {
        var policy = CompensationPolicy.From(
            PolicyChain.Create(PolicySet.CompensationDefault, Fixtures.ReleaseInventory));

        var failure = new Error("undo.failed", "no", ErrorCategory.Unavailable);

        policy.AllowsAnotherAttempt(failure, attemptsMade: 4).ShouldBeTrue();
        policy.AllowsAnotherAttempt(failure, attemptsMade: 5).ShouldBeFalse(
            "Five attempts means five, and the fifth failure is what makes the instance " +
            "CompensationFailed rather than what buys a sixth.");
    }

    /// <summary>
    /// Full jitter, exactly as <c>docs/10-Policy-Framework.md §5</c> writes it:
    /// <c>delay = random(0, base × 2^attempt)</c>, capped.
    /// </summary>
    [Theory]
    [InlineData(1, 1.0, 200)]
    [InlineData(2, 1.0, 400)]
    [InlineData(3, 1.0, 800)]
    [InlineData(3, 0.5, 400)]
    [InlineData(3, 0.0, 0)]
    public void FullJitterIsARandomPointBelowTheExponentialCeiling(int attempt, double sample, int expectedMs)
    {
        var policy = CompensationPolicy.From(
            PolicyChain.Create(PolicySet.CompensationDefault, Fixtures.ReleaseInventory));

        policy.DelayBefore(attempt, sample).ShouldBe(TimeSpan.FromMilliseconds(expectedMs),
            "Decorrelated retries are what stop a shared outage producing a synchronised " +
            "thundering herd, which is what fixed backoff produces.");
    }

    [Fact]
    public void TheCeilingIsCapped()
    {
        var chain = PolicyChain.Create(
            PolicySet.Named("undo").CompensationRetry(
                attempts: 20,
                backoff: Backoff.Exponential(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4))),
            Fixtures.ReleaseInventory);

        CompensationPolicy.From(chain).DelayBefore(attempt: 10, sample: 1.0)
            .ShouldBe(TimeSpan.FromSeconds(4),
                "Without the cap, 2^10 seconds is a compensation that finishes some time " +
                "next week.");
    }

    [Fact]
    public void APlanKnowsWhetherAnyStepDeclaresACompensationPolicy()
    {
        var chain = PolicyChain.Create(
            PolicySet.Named("undo").CompensationRetry(attempts: 2), Fixtures.ReleaseInventory);

        var plain = ExecutionPlan.Create(
            Fixtures.PlaceOrder,
            StepGraph.Create([
                StepNode.ForCapability(0, Fixtures.ReserveInventory, Fixtures.ReleaseInventory),
            ]));

        var retrying = ExecutionPlan.Create(
            Fixtures.PlaceOrder,
            StepGraph.Create([
                StepNode.ForCapability(
                    0, Fixtures.ReserveInventory, Fixtures.ReleaseInventory, compensationPolicies: chain),
            ]));

        plain.HasCompensationPolicies.ShouldBeFalse(
            "Precomputed for the reason HasParallel is: the failure path must not scan the " +
            "graph to discover it has nothing to do.");

        retrying.HasCompensationPolicies.ShouldBeTrue();
    }
}
