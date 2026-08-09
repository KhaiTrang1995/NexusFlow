using Shouldly;
using Xunit;

namespace FlowX.Architecture.Tests;

/// <summary>
/// A mutating HTTP address is safe to call twice.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The retry is not hypothetical.</strong> A client that times out, a load balancer
/// that retries an idle upstream, a user who double-clicks and a mobile network that replays a
/// request all produce the same thing: the identical POST body arriving twice. Nothing in the
/// request distinguishes it from two deliberate calls, so the only place the difference can be
/// stated is a key the caller sends. Without one, "transfer £500" arriving twice is two
/// transfers, and the second is indistinguishable from intent.
/// </para>
/// <para>
/// <strong>Why this is keyed on the execution profile and not on the HTTP method.</strong>
/// These samples answer reads on <c>POST</c> as well as writes — a query with a body that would
/// not fit a query string is a <c>POST</c> in every real API — so the method alone says
/// nothing. <c>ExecutionProfile.Durable</c> does say something: it is the author's declaration
/// that the flow journals its steps, and a flow journals its steps because they have effects
/// worth not repeating. That makes the profile the honest signal for "a repeat of this costs
/// something", and it is a signal the author has already had to think about.
/// </para>
/// </remarks>
public sealed class IdempotencyFitnessTests
{
    /// <summary>
    /// Mutating HTTP flows that do not yet demand a key.
    /// </summary>
    /// <remarks>
    /// <strong>Empty, and the pair of tests below is what keeps it that way.</strong> It held
    /// one entry — <c>offer.accept</c>, a durable flow that sends an offer for signature and
    /// registers a compensation to withdraw it, so a retried <c>POST</c> sent a second offer and
    /// stacked a second withdrawal. Recording it made the debt executable rather than remembered;
    /// the flow now demands a key and the row is gone, which is the only way an entry here is
    /// ever meant to leave.
    /// </remarks>
    private static readonly string[] MayMutateWithoutAKey = [];

    /// <summary>
    /// Every flow that changes something over HTTP demands an <c>Idempotency-Key</c>.
    /// </summary>
    [Fact]
    public void EveryMutatingHttpFlowRequiresAnIdempotencyKey()
    {
        var offenders = UnprotectedMutatingFlows()
            .Where(static flow => !MayMutateWithoutAKey.Contains(flow.Id, StringComparer.Ordinal))
            .Select(static flow => flow.Where)
            .Order(StringComparer.Ordinal)
            .ToArray();

        offenders.ShouldBeEmpty(
            "These flows declare ExecutionProfile.Durable — so their steps have effects worth " +
            "journalling — and answer a mutating HTTP method without demanding an " +
            "Idempotency-Key, so a retried request repeats the effect. Add `Idempotent = true` " +
            "to the [HttpTrigger]:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// The allowance names only flows that are still unprotected.
    /// </summary>
    /// <remarks>
    /// The gate above passes for anything named in the allowance whether or not it still needs
    /// to be. Without this, fixing <c>AcceptOfferFlow</c> would leave behind an excuse that
    /// silently covers the next flow to be given that id, and the debt would look permanent
    /// because nothing ever asked whether it was still owed.
    /// </remarks>
    [Fact]
    public void TheAllowanceNamesOnlyFlowsThatStillLackAKey()
    {
        var unprotected = UnprotectedMutatingFlows()
            .Select(static flow => flow.Id)
            .ToHashSet(StringComparer.Ordinal);

        var stale = MayMutateWithoutAKey
            .Where(id => !unprotected.Contains(id))
            .ToArray();

        stale.ShouldBeEmpty(
            "These flows are excused from demanding an Idempotency-Key and no longer need to " +
            "be — either they now demand one, or they no longer mutate over HTTP. Remove them " +
            "from the allowance:" + Environment.NewLine + string.Join(Environment.NewLine, stale));
    }

    /// <summary>
    /// The trigger survey still sees the flows and the addresses this gate is written against.
    /// </summary>
    /// <remarks>
    /// The gate passes on an empty set, and an empty set is what a renamed attribute, a moved
    /// sample or an argument list written in an unfamiliar style produces. The three counts are
    /// separate because they fail separately: losing <c>[Flow]</c> loses everything, losing
    /// <c>[HttpTrigger]</c> loses the addresses, and losing the <c>Idempotent</c> argument would
    /// make every endpoint look unprotected rather than protected — which fails loudly, but for
    /// the wrong reason, and this says which.
    /// </remarks>
    [Fact]
    public void TheTriggerSurveyStillSeesTheEndpoints()
    {
        FlowTriggerSurvey.Flows.Count.ShouldBeGreaterThanOrEqualTo(
            100, "the survey stopped finding [Flow] declarations.");

        FlowTriggerSurvey.Flows
            .Count(static flow => flow.HttpTriggers.Any(static t => t.IsMutating))
            .ShouldBeGreaterThanOrEqualTo(
                80, "the survey stopped finding mutating [HttpTrigger] addresses.");

        FlowTriggerSurvey.Flows
            .Count(static flow => flow.HttpTriggers.Any(static t => t.Idempotent))
            .ShouldBeGreaterThanOrEqualTo(
                50, "the survey stopped reading `Idempotent = true`, so the gate is about to " +
                    "report every endpoint as unprotected.");
    }

    /// <summary>Mutating HTTP flows with no key demanded on at least one mutating address.</summary>
    private static IEnumerable<FlowDeclaration> UnprotectedMutatingFlows() =>
        FlowTriggerSurvey.Flows
            .Where(static flow => flow.Mutates)
            .Where(static flow => flow.HttpTriggers.Any(static t => t.IsMutating && !t.Idempotent));
}
