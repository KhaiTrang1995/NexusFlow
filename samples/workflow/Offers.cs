using FlowX;

namespace Workflow;

/// <summary>The waits this application declares, named once and applied by name.</summary>
/// <remarks>
/// <para>
/// Named properties rather than literals at the call site, for the reason
/// <see cref="Policies"/> gives about policy sets: a duration is a business decision and
/// belongs where it can be read and changed without opening a flow. They also demonstrate the
/// half of the compiler's behaviour that matters — the generator copies the author's
/// <em>expression</em> into the plan rather than folding it, so <c>StepNode.SignalTimeout</c>
/// and <c>StepNode.Delay</c> are whatever these properties are and the plan means what the
/// source means.
/// </para>
/// <para>
/// <strong>Both are armed now.</strong> A suspended instance records which wait it is parked
/// at and when it is due, and <c>FlowTimerScan</c> sweeps for the ones whose instant has
/// passed — so a countersignature that never arrives runs
/// <c>offer.accept</c>'s <c>.OnTimeout</c> block rather than waiting for
/// <c>[FlowDeadline("P30D")]</c>, and the settling period after a signature is a real wait
/// that costs one row.
/// </para>
/// <para>
/// <strong>Overridable, and that is a property of the sample rather than of the DSL.</strong>
/// The defaults are the business values, and a demonstration cannot wait seven days for the
/// thing it exists to demonstrate. So <c>dotnet run</c> and <c>tests/Workflow.Tests</c> can
/// wind them down to seconds through the environment, exactly as the connection string is
/// read, and a reader is told which number is the business one. A duration folded to a
/// constant at build time could not be overridden at all — which is a second reason the
/// generator copies the expression instead.
/// </para>
/// </remarks>
public static class Waits
{
    /// <summary>How long the offer is open for countersignature. Seven days.</summary>
    public static TimeSpan Countersignature { get; } =
        TimeSpan.FromDays(7);

    /// <summary>
    /// How long onboarding holds off after the signature. One day.
    /// </summary>
    /// <remarks>
    /// A real pattern rather than a place to put a <c>.Delay</c>: payroll's nightly sync has
    /// to have seen the countersigned contract before onboarding creates an identity against
    /// it, and "wait until tomorrow" is how that dependency is expressed when the upstream
    /// system publishes nothing to wait for. It is exactly the case a durable timer exists
    /// for — no thread is held, and the instance survives the deployment that happens
    /// overnight.
    /// </remarks>
    public static TimeSpan Settling { get; } =
        TimeSpan.FromDays(1);

    // These two were read from the environment — FLOWX_SAMPLE_OFFER_WINDOW and
    // FLOWX_SAMPLE_SETTLING — so a demonstration run could watch a seven-day wait elapse in
    // twenty seconds. That is gone, and the reason is worth more than the convenience was.
    //
    // A declared wait reaches two artifacts. The plan carries the expression verbatim and
    // generated C# evaluates it, so an environment read works there. The manifest carries the
    // duration FOLDED, at build time, because a consumer reading flowx.manifest.json has never
    // seen this assembly — and a method call is not foldable. So the override silently cost
    // this flow its `timeout` field: `flowx diff` could no longer report a changed window, and
    // ADR-0021's new field lost the only producer in the repository, which is precisely the
    // "producer on paper and none in practice" failure ADR-0017's F1 exists to catch.
    //
    // Nothing said so. The compiler publishes nothing it cannot fold and is silent about it —
    // right for `merge`, wrong here. Recorded as an open item rather than patched in a sample.
}

/// <summary>The signal identities this application delivers, as the plan carries them.</summary>
/// <remarks>
/// <para>
/// <strong>A signal is addressed by identity, not by type</strong>, because the thing that
/// delivers one is a transport: a route, a queue message, a webhook. The identity is what
/// <c>StepNode.SignalType</c> holds and what an endpoint reads out of
/// <c>/signals/{signalType}</c>, and the compiler derives it from the contract's name — so
/// this constant is a copy of a decision made in <c>FlowAnalyzer</c>.
/// </para>
/// <para>
/// A copy of a decision is a chance to disagree with it, so
/// <c>SuspensionTests.TheSignalIdentityThisApplicationSendsIsTheOneThePlanWaitsFor</c> reads
/// the identity off the compiled plan and compares. If the convention ever changes, that test
/// fails rather than this application quietly delivering to a name nothing waits for.
/// </para>
/// </remarks>
public static class Signals
{
    /// <summary>What <c>offer.accept</c> waits for at its suspension point.</summary>
    public const string OfferCountersigned = "offer.countersigned";
}

/// <summary>An offer that has been made and is waiting to be signed.</summary>
/// <param name="CandidateId">Who the offer is for.</param>
/// <param name="Role">What they are being offered.</param>
/// <param name="Site">Which office, so onboarding knows where to start them.</param>
public sealed record OfferToAccept(string CandidateId, string Role, string Site);

/// <summary>An offer that has gone out for signature.</summary>
public sealed record OfferSent(string CandidateId, string EnvelopeId);

/// <summary>
/// The countersignature itself: what a person did, days after the flow suspended.
/// </summary>
/// <param name="EnvelopeId">Which envelope was signed.</param>
/// <param name="SignedBy">Who signed it.</param>
/// <param name="SignedAt">When.</param>
/// <remarks>
/// <strong>This is a signal contract, and it is in the state bag like any step's output.</strong>
/// The engine seeds it under this type when the signal is delivered, the commit that records
/// the suspension point journals it with the rest of the bag, and <c>RestoreState</c> reads it
/// back — so it is subject to <c>FLOWX1006</c> and has to be in
/// <c>WorkflowJsonContext</c> for exactly the reason a step's result does.
/// </remarks>
public sealed record OfferCountersigned(string EnvelopeId, string SignedBy, DateTimeOffset SignedAt);

/// <summary>Onboarding, started for a candidate who signed.</summary>
public sealed record OnboardingStarted(string OnboardingId, string SignedBy);

/// <summary>What the caller gets once the offer has been accepted and onboarding has begun.</summary>
public sealed record AcceptedOffer(string CandidateId, string OnboardingId);

/// <summary>
/// What the caller gets back from the request that <em>starts</em> the flow: an instance to
/// address the countersignature to.
/// </summary>
/// <param name="InstanceId">The waiting instance. This is what a signal is delivered to.</param>
/// <param name="AwaitingSignal">Which signal it is waiting for, as the plan names it.</param>
/// <remarks>
/// <strong>The id is not decoration; without it the instance is unreachable.</strong> It is
/// minted inside <c>FlowHost</c> when the lease is taken, so a caller that was not told it has
/// no way to name the flow it just started — which is why <c>FlowExecutionResult</c> carries
/// it now.
/// </remarks>
public sealed record OfferPending(Guid InstanceId, string AwaitingSignal);

/// <summary>Errors <c>offer.accept</c> can produce.</summary>
public static class OfferErrors
{
    /// <summary>The offer names no candidate.</summary>
    public static Error MissingCandidate() =>
        new("offer.missing_candidate", "The offer names no candidate.", ErrorCategory.Validation);

    /// <summary>The countersignature never arrived, and the offer window has closed.</summary>
    /// <remarks>
    /// <para>
    /// Raised by <c>offer.accept</c>'s own <c>.OnTimeout</c> block rather than by the engine.
    /// The engine's <c>flow.signal_not_received</c> is what a wait with no block declared ends
    /// with; a flow that declares one is saying "this is what happens instead", and what
    /// happens here is a business outcome with a business code.
    /// </para>
    /// <para>
    /// <strong>Ending the block with a failure is what withdraws the offer.</strong> The
    /// unwind runs <c>offer.withdraw</c>, which was put on the compensation stack before the
    /// flow suspended and rebuilt from the journal's committed rows when it woke — so the
    /// envelope that went out seven days ago is closed by the timer that fired today.
    /// </para>
    /// </remarks>
    public static Error NotCountersigned() =>
        new Error(
            "offer.not_countersigned",
            $"The offer was open for {Waits.Countersignature} and was not countersigned.",
            ErrorCategory.Unavailable)
            .With("window", Waits.Countersignature);

    /// <summary>Onboarding would not accept the signed offer.</summary>
    public static Error OnboardingRefused(string reason) =>
        new Error("offer.onboarding_refused", $"Onboarding refused the offer: {reason}.", ErrorCategory.Conflict)
            .With("reason", reason);
}

/// <summary>Where offers are sent and withdrawn. In memory here; the capabilities do not care.</summary>
public interface IOfferDesk
{
    /// <summary>Sends an offer out for signature and returns the envelope it went out in.</summary>
    string Send(string candidateId, string role, string idempotencyKey);

    /// <summary>Withdraws an envelope that is still open. The inverse of <see cref="Send"/>.</summary>
    void Withdraw(string envelopeId);

    /// <summary>Starts onboarding for a signed offer.</summary>
    string StartOnboarding(string envelopeId, string signedBy, string idempotencyKey);
}

/// <summary>
/// Sends the offer out for countersignature.
/// </summary>
/// <remarks>
/// Compensable, and its inverse is the whole reason this step is in front of a suspension
/// point rather than behind it: the entry it puts on the unwind stack has to survive the wait,
/// and the only thing that survives a wait is a journal row.
/// </remarks>
[Capability("offer.send", Version = "1.0.0",
    Authorization = Authorization.Authenticated,
    Idempotent = true, SideEffects = ["e-signature"])]
public sealed class SendOfferForSignature : ICapability<OfferToAccept, OfferSent>
{
    private readonly IOfferDesk _desk;

    /// <summary>Creates the capability over the offer desk.</summary>
    public SendOfferForSignature(IOfferDesk desk) => _desk = desk;

    /// <inheritdoc />
    public ValueTask<Result<OfferSent>> ExecuteAsync(
        OfferToAccept input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (string.IsNullOrWhiteSpace(input.CandidateId))
        {
            return ValueTask.FromResult(Result.Fail<OfferSent>(OfferErrors.MissingCandidate()));
        }

        var envelope = _desk.Send(input.CandidateId, input.Role, ctx.IdempotencyKey);

        return ValueTask.FromResult(Result.Ok(new OfferSent(input.CandidateId, envelope)));
    }
}

/// <summary>Withdraws an offer that was sent. The inverse of <see cref="SendOfferForSignature"/>.</summary>
/// <remarks>
/// <para>
/// It binds <see cref="OfferToAccept"/>, the <em>input</em> of the step it undoes, because
/// that is what a compensation is invoked with (<c>docs/06-Execution-Engine.md §7</c>). The
/// envelope it has to withdraw is derived from the candidate the same way
/// <see cref="SendOfferForSignature"/> derived it, which is what makes the undo addressable
/// without a second lookup.
/// </para>
/// <para>
/// <strong>On a resumed instance that input comes out of the journaled state bag.</strong>
/// The node that sent the offer is gone by the time an unwind after the wait happens, so the
/// only place the value can come from is the snapshot committed with the step — which is why
/// an unwind across a suspension point works at all, and why <see cref="OfferToAccept"/> is in
/// <c>WorkflowJsonContext</c>.
/// </para>
/// </remarks>
[Capability("offer.withdraw", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true, SideEffects = ["e-signature"])]
public sealed class WithdrawOffer : ICapability<OfferToAccept, OfferSent>
{
    private readonly IOfferDesk _desk;

    /// <summary>Creates the capability over the offer desk.</summary>
    public WithdrawOffer(IOfferDesk desk) => _desk = desk;

    /// <inheritdoc />
    public ValueTask<Result<OfferSent>> ExecuteAsync(
        OfferToAccept input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var envelope = "env-" + input.CandidateId;

        _desk.Withdraw(envelope);

        return ValueTask.FromResult(Result.Ok(new OfferSent(input.CandidateId, envelope)));
    }
}

/// <summary>
/// Starts onboarding, from the countersignature the flow waited for.
/// </summary>
/// <remarks>
/// <strong>It binds <see cref="OfferCountersigned"/>, which no step produced.</strong> The
/// signal did — the engine seeds a delivered signal into the state bag under the contract the
/// flow declared, before it dispatches the suspension point — so a step after the wait reads
/// it exactly as it reads any earlier step's output, and nothing in this class knows there
/// was a wait at all.
/// </remarks>
[Capability("onboarding.start", Version = "1.0.0",
    Authorization = Authorization.Authenticated,
    Idempotent = true, SideEffects = ["hr-system"])]
public sealed class StartOnboarding : ICapability<OfferCountersigned, OnboardingStarted>
{
    private readonly IOfferDesk _desk;

    /// <summary>Creates the capability over the offer desk.</summary>
    public StartOnboarding(IOfferDesk desk) => _desk = desk;

    /// <inheritdoc />
    public ValueTask<Result<OnboardingStarted>> ExecuteAsync(
        OfferCountersigned input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var onboarding = _desk.StartOnboarding(input.EnvelopeId, input.SignedBy, ctx.IdempotencyKey);

        return ValueTask.FromResult(Result.Ok(new OnboardingStarted(onboarding, input.SignedBy)));
    }
}

/// <summary>Offers, envelopes and onboardings, in memory.</summary>
/// <remarks>
/// Every write is keyed on the idempotency key, which is what the capabilities'
/// <c>Idempotent = true</c> promises and what makes a re-dispatched step safe. It matters more
/// here than elsewhere in this sample: a signal delivered twice re-enters the instance, and
/// while the journal's frontier is what actually stops the step running again, a capability
/// that could not survive being asked twice would be relying on it.
/// </remarks>
public sealed class InMemoryOfferDesk : IOfferDesk
{
    private readonly Dictionary<string, string> _envelopes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _onboardings = new(StringComparer.Ordinal);
    private readonly HashSet<string> _withdrawn = new(StringComparer.Ordinal);
    private readonly List<string> _signatories = [];
    private readonly Lock _sync = new();

    /// <summary>Envelopes that are open right now. Read by the tests to check an unwind landed.</summary>
    public int OpenEnvelopes
    {
        get
        {
            lock (_sync)
            {
                return _envelopes.Count - _withdrawn.Count;
            }
        }
    }

    /// <summary>Onboardings that have been started. One per accepted offer, never two.</summary>
    public int StartedOnboardings
    {
        get
        {
            lock (_sync)
            {
                return _onboardings.Count;
            }
        }
    }

    /// <summary>
    /// Who signed each offer that reached onboarding, in the order they did.
    /// </summary>
    /// <remarks>
    /// Recorded because it is the one value that can only have come from the signal: no step
    /// of this flow produces a signatory, so a test reading it back off the desk is measuring
    /// that the delivered payload reached the capability that binds it.
    /// </remarks>
    public IReadOnlyList<string> OnboardingsBySignatory
    {
        get
        {
            lock (_sync)
            {
                return [.. _signatories];
            }
        }
    }

    /// <inheritdoc />
    public string Send(string candidateId, string role, string idempotencyKey)
    {
        lock (_sync)
        {
            if (_envelopes.TryGetValue(idempotencyKey, out var existing))
            {
                return existing;
            }

            var envelope = "env-" + candidateId;

            _envelopes[idempotencyKey] = envelope;

            return envelope;
        }
    }

    /// <inheritdoc />
    public void Withdraw(string envelopeId)
    {
        lock (_sync)
        {
            _withdrawn.Add(envelopeId);
        }
    }

    /// <inheritdoc />
    public string StartOnboarding(string envelopeId, string signedBy, string idempotencyKey)
    {
        lock (_sync)
        {
            if (_onboardings.TryGetValue(idempotencyKey, out var existing))
            {
                return existing;
            }

            var onboarding = "onb-" + envelopeId;

            _onboardings[idempotencyKey] = onboarding;
            _signatories.Add(signedBy);

            return onboarding;
        }
    }
}
