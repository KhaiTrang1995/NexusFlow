using FlowX;

namespace AiAgent;

/// <summary>Errors this application can produce.</summary>
public static class SupportErrors
{
    /// <summary>No ticket of that id.</summary>
    public static Error TicketNotFound(string ticketId) =>
        new Error("support.ticket_not_found", $"No ticket '{ticketId}'.", ErrorCategory.NotFound)
            .With("ticketId", ticketId);

    /// <summary>The provider would not reverse the charge.</summary>
    public static Error RefundRejected(string reason) =>
        new Error("payment.refund_rejected", $"The refund was rejected: {reason}.", ErrorCategory.Conflict)
            .With("reason", reason);
}

/// <summary>Finds the ticket a refund is against.</summary>
/// <remarks>
/// A read. <c>Authenticated</c> rather than <c>Public</c>, so an anonymous agent is refused at
/// this step and never reaches the one that moves money — and so the refusal is about identity
/// rather than about the refund, which is a more useful thing for a model to be told.
/// </remarks>
[Capability("ticket.load", Version = "1.0.0",
    Authorization = Authorization.Authenticated,
    Idempotent = true)]
public sealed class LoadTicket : ICapability<IssueRefund, Ticket>
{
    private readonly ITicketDesk _desk;

    /// <summary>Creates the capability.</summary>
    public LoadTicket(ITicketDesk desk)
    {
        ArgumentNullException.ThrowIfNull(desk);
        _desk = desk;
    }

    /// <inheritdoc />
    public async ValueTask<Result<Ticket>> ExecuteAsync(
        IssueRefund input, CapabilityContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var ticket = await _desk.FindAsync(input.TicketId, ct).ConfigureAwait(false);

        return ticket is null ? SupportErrors.TicketNotFound(input.TicketId) : ticket;
    }
}

/// <summary>Reverses a charge.</summary>
/// <remarks>
/// <para>
/// The declaration that everything in this sample turns on. <c>SideEffects</c> is what makes the
/// tool's descriptor publish <c>confirmationRequired: true</c> and what the confirmation prompt
/// names; <c>Permission</c> is what the step loop decides against the caller's claims, over
/// whichever transport the caller arrived on; <c>Idempotent = false</c> is what makes attaching a
/// retry policy a build error, because retrying a refund is a second refund.
/// </para>
/// <para>
/// Nothing here mentions an agent, MCP, or a prompt. That is the claim: the agent-facing
/// behaviour of this application is a projection of these four attribute arguments.
/// </para>
/// </remarks>
[Capability("payment.refund", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "payment.refund",
    Idempotent = false,
    SideEffects = ["payment-gateway", "ledger"])]
public sealed class RefundPayment : ICapability<Ticket, Refund>
{
    private readonly IPaymentGateway _gateway;

    /// <summary>Creates the capability.</summary>
    public RefundPayment(IPaymentGateway gateway)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        _gateway = gateway;
    }

    /// <inheritdoc />
    public async ValueTask<Result<Refund>> ExecuteAsync(
        Ticket input, CapabilityContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var reference = await _gateway
            .RefundAsync(input.TicketId, input.ChargedAmount, ctx.IdempotencyKey, ct)
            .ConfigureAwait(false);

        return reference is null
            ? SupportErrors.RefundRejected("the provider declined the reversal")
            : new Refund(input.TicketId, input.ChargedAmount, reference);
    }
}

/// <summary>Finds tickets matching a query.</summary>
/// <remarks>
/// No side effects, so the tool it is reached by publishes <c>confirmationRequired: false</c> and
/// runs unelicited even where the deployment enforces confirmation. That is the half of the
/// consent story that matters for an agent to be usable at all: a gate on everything is a gate
/// nobody reads.
/// </remarks>
[Capability("ticket.search", Version = "1.0.0",
    Authorization = Authorization.Authenticated,
    Idempotent = true)]
public sealed class SearchTicketDesk : ICapability<SearchTickets, TicketMatches>
{
    private readonly ITicketDesk _desk;

    /// <summary>Creates the capability.</summary>
    public SearchTicketDesk(ITicketDesk desk)
    {
        ArgumentNullException.ThrowIfNull(desk);
        _desk = desk;
    }

    /// <inheritdoc />
    public async ValueTask<Result<TicketMatches>> ExecuteAsync(
        SearchTickets input, CapabilityContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var matches = await _desk.SearchAsync(input.Query, ct).ConfigureAwait(false);

        return new TicketMatches(matches.Count, matches);
    }
}

/// <summary>
/// Reviews this application's own manifest, and borrows the caller's model to write it up.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The other direction of the sample: the agent is the reader, not the operator.</strong>
/// <c>FlowX.Ai</c> is handed <c>FlowXManifest.Json</c> — the build's own document, compiled in —
/// and produces findings. No repository is read, no store is opened and no telemetry is
/// consulted, because this capability holds nothing that could open any of them: its two
/// constructor parameters are a manifest and a sampler.
/// </para>
/// <para>
/// <strong>Scoped, and that is a visible cost rather than a detail.</strong>
/// <c>IAgentSampler</c> is the channel back to <em>this call's</em> client, so it cannot be
/// captured by a singleton — which means this capability and the dispatcher that holds it are
/// registered per request in <c>Program.cs</c>. A capability that talks to its caller is a
/// different kind of thing from one that talks to a database, and the container says so.
/// </para>
/// <para>
/// <strong>It answers without a model.</strong> Reached over HTTP, or by an MCP client that
/// serves no <c>sampling/createMessage</c>, <see cref="IAgentSampler"/> reports the caller has no
/// model and the deterministic rendering is returned with <c>narrated: false</c>. A capability
/// that only worked for one transport would be exactly the transport-attached behaviour the
/// platform's whole trigger model exists to avoid.
/// </para>
/// </remarks>
[Capability("ops.review", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "ops.read",
    Idempotent = true)]
public sealed class ReviewApplication : ICapability<ReviewApplicationRequest, ApplicationReview>
{
    /// <summary>What the model is told it is doing.</summary>
    /// <remarks>
    /// Fixed here rather than taken from the request, so the only variable material in a
    /// completion request is <c>ManifestReview.ToPrompt()</c> — which is a pure function of the
    /// build's manifest. See <see cref="ReviewApplicationRequest"/>.
    /// </remarks>
    private const string SystemPrompt =
        "You are reviewing a service's architecture from its build manifest. Summarise the " +
        "findings below for an engineer who has not seen this service, in at most one short " +
        "paragraph per finding. Do not invent findings and do not speculate about code you " +
        "cannot see.";

    private readonly IApplicationReviewer _reviewer;
    private readonly IAgentSampler _sampler;

    /// <summary>Creates the capability.</summary>
    public ReviewApplication(IApplicationReviewer reviewer, IAgentSampler sampler)
    {
        ArgumentNullException.ThrowIfNull(reviewer);
        ArgumentNullException.ThrowIfNull(sampler);

        _reviewer = reviewer;
        _sampler = sampler;
    }

    /// <inheritdoc />
    public async ValueTask<Result<ApplicationReview>> ExecuteAsync(
        ReviewApplicationRequest input, CapabilityContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var review = _reviewer.Review();

        if (!input.Narrate)
        {
            return new ApplicationReview(review.Findings, review.Warnings, Narrated: false, review.Report);
        }

        var sample = await _sampler
            .SampleAsync(new AgentSamplingRequest(SystemPrompt, review.Prompt, MaxTokens: 800), ct)
            .ConfigureAwait(false);

        // A model that was reachable and failed is reported as a failure of the flow rather than
        // silently downgraded, because the caller asked for narration and a quiet fallback would
        // make "the model is broken" indistinguishable from "there is no model".
        if (sample.Error is { } error)
        {
            return Result.Fail<ApplicationReview>(error);
        }

        return sample.Text is { Length: > 0 } narration
            ? new ApplicationReview(review.Findings, review.Warnings, Narrated: true, narration)
            : new ApplicationReview(review.Findings, review.Warnings, Narrated: false, review.Report);
    }
}

/// <summary>What reviewing this application produced.</summary>
/// <param name="Findings">How many the review raised.</param>
/// <param name="Warnings">How many of them are defects rather than notes.</param>
/// <param name="Report">The findings, rendered for a human.</param>
/// <param name="Prompt">The same material, framed for a model that is asked to write it up.</param>
public sealed record ReviewResult(int Findings, int Warnings, string Report, string Prompt);

/// <summary>Whatever reviews this application.</summary>
/// <remarks>
/// <para>
/// <strong>This interface exists because <c>FLOWX1003</c> was right.</strong> The first draft had
/// <see cref="ReviewApplication"/> take <c>FlowX.Ai.ManifestReview</c> directly and the build
/// refused it: a capability may reference <c>FlowX</c>, <c>FlowX.Runtime</c> and
/// <c>FlowX.Generated</c> and nothing else under <c>FlowX.</c>, because everything else there is
/// infrastructure. <c>FlowX.Ai</c> is not a transport, and the rule's conclusion still holds for
/// it — a reviewer is a library this application chose, exactly as <c>ITicketDesk</c> stands in
/// front of a store it chose. Swapping the reviewer changes <see cref="ManifestReviewer"/> and
/// nothing else.
/// </para>
/// <para>
/// It takes no arguments on purpose. There is one thing to review — this build's own manifest —
/// and a parameter would be the seam through which a caller pointed the reviewer at a document it
/// supplied.
/// </para>
/// </remarks>
public interface IApplicationReviewer
{
    /// <summary>Reviews this application's own build manifest.</summary>
    ReviewResult Review();
}

/// <summary>The tickets this desk holds.</summary>
public interface ITicketDesk
{
    /// <summary>The ticket of that id, or <c>null</c>.</summary>
    ValueTask<Ticket?> FindAsync(string ticketId, CancellationToken ct);

    /// <summary>The ids of every ticket matching a query.</summary>
    ValueTask<IReadOnlyList<string>> SearchAsync(string query, CancellationToken ct);
}

/// <summary>The payment provider.</summary>
public interface IPaymentGateway
{
    /// <summary>Reverses a charge, returning a reference or <c>null</c> when refused.</summary>
    ValueTask<string?> RefundAsync(
        string ticketId, decimal amount, string idempotencyKey, CancellationToken ct);
}
