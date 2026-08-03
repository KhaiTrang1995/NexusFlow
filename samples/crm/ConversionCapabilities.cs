using FlowX;

namespace Crm;

/// <summary>Reads the lead and refuses the conversions that cannot happen.</summary>
/// <remarks>
/// <para>
/// <strong>The only step that refuses, and it comes first on purpose.</strong> Everything after
/// it writes. A lead that is missing, disqualified or already converted is turned away before
/// any row exists, so the common refusals cost no unwind at all — the saga's compensations are
/// for the failures nobody can predict, not for the three this can.
/// </para>
/// <para>
/// <c>Authenticated</c> rather than a permission: converting a lead is what a sales
/// representative does all day, and a deployment that had to grant a permission for it would
/// grant it to everyone.
/// </para>
/// </remarks>
[Capability("crm.lead.read_for_conversion", Version = "1.0.0",
    Authorization = Authorization.Authenticated,
    Idempotent = true)]
public sealed class ReadLeadForConversion : ICapability<ConvertLead, LeadUnderConversion>
{
    private readonly ConversionStore _store;

    /// <summary>Creates the capability.</summary>
    /// <param name="store">The CRM writes and reads.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public ReadLeadForConversion(ConversionStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <inheritdoc />
    public async ValueTask<Result<LeadUnderConversion>> ExecuteAsync(
        ConvertLead input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var lead = await _store.ReadLeadAsync(ctx.TenantId, input.LeadId, ct).ConfigureAwait(false);

        if (lead.IsFailure)
        {
            return lead;
        }

        // Checked here rather than by a foreign key, because the foreign key would fire three
        // steps later — after the account and the contact exist — and turn a bad request into
        // an unwind.
        return await _store.StageIsUsableAsync(ctx.TenantId, input.Stage, ct).ConfigureAwait(false)
            ? lead
            : Result.Fail<LeadUnderConversion>(ConversionErrors.StageNotUsable(input.Stage));
    }
}

/// <summary>Creates the account a converted lead becomes.</summary>
/// <remarks>
/// <strong><c>Idempotent = true</c> is a promise this keeps by construction.</strong> The id is
/// derived from the lead, so a second insert is the same primary key, and PostgreSQL refuses
/// it rather than producing a second account.
/// </remarks>
[Capability("crm.account.create", Version = "1.0.0",
    Authorization = Authorization.Authenticated,
    Idempotent = true,
    SideEffects = ["crm.account.written"])]
public sealed class CreateAccount : ICapability<CreateAccountRequest, AccountWritten>
{
    private readonly ConversionStore _store;

    /// <summary>Creates the capability.</summary>
    /// <param name="store">The CRM writes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public CreateAccount(ConversionStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <inheritdoc />
    public async ValueTask<Result<AccountWritten>> ExecuteAsync(
        CreateAccountRequest input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        await _store.CreateAccountAsync(
            ctx.TenantId, input.AccountId, input.Name, input.Industry, input.Region, input.Owner, ct)
            .ConfigureAwait(false);

        return Result.Ok(new AccountWritten(input.AccountId));
    }
}

/// <summary>Removes the account a conversion created.</summary>
/// <remarks>
/// Takes the same request its step took, which is the whole reason
/// <see cref="CreateAccountRequest.AccountId"/> is on the request rather than in the result.
/// </remarks>
[Capability("crm.account.remove", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true,
    SideEffects = ["crm.account.written"])]
public sealed class RemoveAccount : ICapability<CreateAccountRequest, AccountWritten>
{
    private readonly ConversionStore _store;

    /// <summary>Creates the capability.</summary>
    /// <param name="store">The CRM writes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public RemoveAccount(ConversionStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <inheritdoc />
    public async ValueTask<Result<AccountWritten>> ExecuteAsync(
        CreateAccountRequest input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        await _store.UndoAsync(ctx.TenantId, ConversionRow.Account, input.AccountId, ct)
            .ConfigureAwait(false);

        return Result.Ok(new AccountWritten(input.AccountId));
    }
}

/// <summary>Creates the contact a converted lead becomes.</summary>
[Capability("crm.contact.create", Version = "1.0.0",
    Authorization = Authorization.Authenticated,
    Idempotent = true,
    SideEffects = ["crm.contact.written"])]
public sealed class CreateContact : ICapability<CreateContactRequest, ContactWritten>
{
    private readonly ConversionStore _store;

    /// <summary>Creates the capability.</summary>
    /// <param name="store">The CRM writes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public CreateContact(ConversionStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <inheritdoc />
    public async ValueTask<Result<ContactWritten>> ExecuteAsync(
        CreateContactRequest input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        await _store.CreateContactAsync(
            ctx.TenantId,
            input.ContactId,
            input.AccountId,
            input.FullName,
            input.Email,
            ct).ConfigureAwait(false);

        return Result.Ok(new ContactWritten(input.ContactId));
    }
}

/// <summary>Removes the contact a conversion created.</summary>
[Capability("crm.contact.remove", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true,
    SideEffects = ["crm.contact.written"])]
public sealed class RemoveContact : ICapability<CreateContactRequest, ContactWritten>
{
    private readonly ConversionStore _store;

    /// <summary>Creates the capability.</summary>
    /// <param name="store">The CRM writes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public RemoveContact(ConversionStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <inheritdoc />
    public async ValueTask<Result<ContactWritten>> ExecuteAsync(
        CreateContactRequest input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        await _store.UndoAsync(ctx.TenantId, ConversionRow.Contact, input.ContactId, ct)
            .ConfigureAwait(false);

        return Result.Ok(new ContactWritten(input.ContactId));
    }
}

/// <summary>Creates the opportunity a converted lead becomes.</summary>
/// <remarks>
/// <strong>The timestamp comes from <see cref="CapabilityContext.UtcNow"/>.</strong> Under a
/// durable profile, reading <c>DateTimeOffset.UtcNow</c> here would be <c>FLOWX1007</c>: a
/// resumed instance would stamp a different <c>stage_entered_at</c> than the one it committed,
/// and the nightly sweep in §8.4 reads that column.
/// </remarks>
[Capability("crm.opportunity.create", Version = "1.0.0",
    Authorization = Authorization.Authenticated,
    Idempotent = true,
    SideEffects = ["crm.opportunity.written"])]
public sealed class CreateOpportunity : ICapability<CreateOpportunityRequest, OpportunityWritten>
{
    private readonly ConversionStore _store;

    /// <summary>Creates the capability.</summary>
    /// <param name="store">The CRM writes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public CreateOpportunity(ConversionStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <inheritdoc />
    public async ValueTask<Result<OpportunityWritten>> ExecuteAsync(
        CreateOpportunityRequest input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        await _store.CreateOpportunityAsync(
            ctx.TenantId,
            input.OpportunityId,
            input.AccountId,
            input.ContactId,
            input.Request,
            ctx.UtcNow,
            ct).ConfigureAwait(false);

        return Result.Ok(new OpportunityWritten(input.OpportunityId));
    }
}

/// <summary>Removes the opportunity a conversion created.</summary>
[Capability("crm.opportunity.remove", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true,
    SideEffects = ["crm.opportunity.written"])]
public sealed class RemoveOpportunity : ICapability<CreateOpportunityRequest, OpportunityWritten>
{
    private readonly ConversionStore _store;

    /// <summary>Creates the capability.</summary>
    /// <param name="store">The CRM writes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public RemoveOpportunity(ConversionStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <inheritdoc />
    public async ValueTask<Result<OpportunityWritten>> ExecuteAsync(
        CreateOpportunityRequest input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        await _store.UndoAsync(ctx.TenantId, ConversionRow.Opportunity, input.OpportunityId, ct)
            .ConfigureAwait(false);

        return Result.Ok(new OpportunityWritten(input.OpportunityId));
    }
}

/// <summary>Marks the lead converted, naming all three rows at once.</summary>
/// <remarks>
/// <para>
/// <strong>Last, and not compensable, and those two facts are the same fact.</strong> The
/// <c>lead</c> table's check constraint holds that the four converted columns are all set or
/// all null, so this write is the moment the conversion becomes true — and nothing after it can
/// fail, because there is nothing after it. A step that could fail here would need an undo, and
/// an undo of "the lead is converted" is a lead that was converted and then was not, which no
/// caller downstream could make sense of.
/// </para>
/// </remarks>
[Capability("crm.lead.mark_converted", Version = "1.0.0",
    Authorization = Authorization.Authenticated,
    Idempotent = true,
    SideEffects = ["crm.lead.converted"])]
public sealed class MarkLeadConverted : ICapability<MarkLeadConvertedRequest, LeadConverted>
{
    private readonly ConversionStore _store;

    /// <summary>Creates the capability.</summary>
    /// <param name="store">The CRM writes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public MarkLeadConverted(ConversionStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <inheritdoc />
    public async ValueTask<Result<LeadConverted>> ExecuteAsync(
        MarkLeadConvertedRequest input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        await _store.MarkConvertedAsync(ctx.TenantId, input.LeadId, input.Result, ctx.UtcNow, ct)
            .ConfigureAwait(false);

        return Result.Ok(new LeadConverted(input.LeadId, input.Result));
    }
}
