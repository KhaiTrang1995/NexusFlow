using FlowX;

namespace FlowXStarter;

/// <summary>What a caller asks for. This record is the request body.</summary>
/// <param name="Subject">One line saying what is wrong.</param>
/// <param name="Reporter">Who is reporting it.</param>
/// <param name="ContactPhone">
/// A number to call back on. <c>[Sensitive]</c> is not documentation: the member is
/// redacted in logs, traces and problem responses, and it is recorded as sensitive in
/// the manifest, so the fact travels with the contract rather than with this comment.
/// </param>
public sealed record OpenTicket(string Subject, string Reporter, [property: Sensitive] string ContactPhone);

/// <summary>A ticket that passed validation. Passed between steps; never on the wire.</summary>
public sealed record ValidatedTicket(string Subject, string Reporter);

/// <summary>What the caller gets back. This record is the response body.</summary>
public sealed record TicketOpened(string TicketId, string Subject);
