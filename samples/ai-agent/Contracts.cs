using FlowX;

namespace AiAgent;

/// <summary>What an agent asks for when it wants a refund issued.</summary>
/// <param name="TicketId">The support ticket the refund is against.</param>
/// <param name="Reason">Why, in the agent's own words. Reaches the ledger and the audit trail.</param>
/// <param name="CardholderReference">
/// The customer's reference at the payment provider.
/// </param>
/// <remarks>
/// <c>[Sensitive]</c> on the last member is one word with four consequences, and the fourth is
/// the one this sample is about: the value is redacted out of the journal payload, out of an
/// RFC 7807 body, out of a log record — and out of the confirmation prompt a human reads, which
/// <c>McpElicitation</c> builds from the <c>sensitive</c> list the manifest publishes. A prompt
/// is text a client transcribes, keeps and may hand back to a model.
/// </remarks>
public sealed record IssueRefund(
    string TicketId, string Reason, [property: Sensitive] string CardholderReference);

/// <summary>A ticket, as the desk holds it.</summary>
public sealed record Ticket(string TicketId, string Subject, decimal ChargedAmount);

/// <summary>What the payment provider did.</summary>
public sealed record Refund(string TicketId, decimal Amount, string RefundReference);

/// <summary>What the caller gets back once a refund is issued.</summary>
public sealed record RefundIssued(string TicketId, decimal Amount, string RefundReference);

/// <summary>What an agent asks for when it wants to find a ticket.</summary>
public sealed record SearchTickets(string Query);

/// <summary>What the search found.</summary>
/// <remarks>
/// A count and the ids rather than the tickets. The flow behind this declares no side effects
/// and needs no confirmation, and the smaller the answer the less of a customer's record ends up
/// in a model's context for a query that was only meant to locate one.
/// </remarks>
public sealed record TicketMatches(int Count, IReadOnlyList<string> TicketIds);

/// <summary>What an agent asks for when it wants this application reviewed.</summary>
/// <param name="Narrate">
/// Whether to ask the caller's own model to write the findings up in prose.
/// </param>
/// <remarks>
/// <para>
/// The argument is a request, not a promise. A caller with no model attached — an HTTP client, or
/// an MCP client that does not serve <c>sampling/createMessage</c> — gets the deterministic
/// rendering and is told so in <see cref="ApplicationReview.Narrated"/>, because a flow whose
/// answer silently changes shape depending on who called it is worse than one that says which
/// answer it gave.
/// </para>
/// <para>
/// There is deliberately no free-text member here. Whatever a model is asked to write about is a
/// pure function of <c>flowx.manifest.json</c> (<c>ManifestReview.ToPrompt</c>), and a
/// pass-through prompt would be the hole through which a caller put anything at all into a
/// completion request this application paid for and signed.
/// </para>
/// </remarks>
public sealed record ReviewApplicationRequest(bool Narrate);

/// <summary>What the review produced.</summary>
/// <param name="Findings">How many the manifest raised.</param>
/// <param name="Warnings">How many of them are defects rather than notes.</param>
/// <param name="Narrated">Whether a model wrote the report, or the reviewer did.</param>
/// <param name="Report">The report itself.</param>
public sealed record ApplicationReview(
    int Findings, int Warnings, bool Narrated, string Report);
