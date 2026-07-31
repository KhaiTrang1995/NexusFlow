using FlowX;

namespace FlowXStarter;

/// <summary>Every failure this application can return.</summary>
/// <remarks>
/// Declared in one place so two capabilities cannot invent two spellings of the same
/// condition. Each code reaches the manifest and the RFC 7807 <c>type</c> URI a caller
/// sees, so it is part of the contract.
/// </remarks>
public static class TicketErrors
{
    /// <summary>The subject is missing or blank.</summary>
    public static Error SubjectRequired() =>
        new("ticket.subject_required", "A ticket needs a subject.", ErrorCategory.Validation);
}

/// <summary>Checks the request is well formed.</summary>
/// <remarks>
/// A read: no side effects, safe to repeat. It references no transport type and calls no
/// other capability, so it is tested by constructing it and calling the method.
/// </remarks>
[Capability("ticket.validate", Version = "1.0.0",
    Authorization = Authorization.Authenticated,
    Idempotent = true)]
public sealed class ValidateTicket : ICapability<OpenTicket, ValidatedTicket>
{
    /// <inheritdoc />
    public ValueTask<Result<ValidatedTicket>> ExecuteAsync(
        OpenTicket input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        // An expected failure is a value, not an exception. Throwing here would be a
        // defect alert rather than a 400, and the compiler says so (FLOWX1016).
        return ValueTask.FromResult(string.IsNullOrWhiteSpace(input.Subject)
            ? Result.Fail<ValidatedTicket>(TicketErrors.SubjectRequired())
            : Result.Ok(new ValidatedTicket(input.Subject.Trim(), input.Reporter)));
    }
}

/// <summary>Writes the ticket down.</summary>
/// <remarks>
/// Effectful, and it says which effect it has. <c>Idempotent = true</c> is a promise the
/// implementation keeps by keying the write on <c>ctx.IdempotencyKey</c>; declaring it
/// falsely is how a retry becomes a duplicate.
/// </remarks>
[Capability("ticket.record", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "ticket.write",
    Idempotent = true,
    SideEffects = ["ticket-store"])]
public sealed class RecordTicket : ICapability<ValidatedTicket, TicketOpened>
{
    private readonly ITicketStore _store;

    /// <summary>Creates the capability.</summary>
    public RecordTicket(ITicketStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public async ValueTask<Result<TicketOpened>> ExecuteAsync(
        ValidatedTicket input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        // The identity comes from the context. A new Guid or the clock would make the
        // same request produce a different ticket on every retry.
        await _store.SaveAsync(ctx.IdempotencyKey, input.Subject, ct).ConfigureAwait(false);

        return new TicketOpened(ctx.IdempotencyKey, input.Subject);
    }
}

/// <summary>Where tickets are kept. A port: the capability depends on this, not on a database.</summary>
public interface ITicketStore
{
    /// <summary>Stores a ticket under its id. Called again with the same id, it writes once.</summary>
    ValueTask SaveAsync(string ticketId, string subject, CancellationToken ct);
}
