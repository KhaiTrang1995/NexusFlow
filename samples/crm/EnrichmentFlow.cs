using System.Text.Json;
using FlowX;

namespace Crm;

/// <summary>How long the enrichment wait lasts and how often it looks.</summary>
public static class EnrichmentWaits
{
    /// <summary>How long between one look at the ticket and the next.</summary>
    /// <remarks>
    /// <strong>Exponential with jitter, because a thousand leads captured in one campaign would
    /// otherwise ask the provider at the same instant forever.</strong> The first attempt is
    /// seconds after the request and the last is minutes apart, which is the shape of a question
    /// whose answer becomes less likely to have changed the longer it has not.
    /// </remarks>
    public static Backoff Polling { get; } = Backoff.ExponentialJitter("PT10S", "PT2M");

    /// <summary>How long the wait may go on. An hour.</summary>
    /// <remarks>
    /// Measured from the first attempt's committed row rather than from any one invocation's
    /// clock, so a lead passed between nodes over an hour has one budget and not three.
    /// </remarks>
    public static TimeSpan Budget { get; } = TimeSpan.FromHours(1);
}

/// <summary>What the applying step is given, from whichever ending the wait had.</summary>
/// <param name="LeadId">The lead.</param>
/// <param name="Profile">What the provider said.</param>
public sealed record ApplyEnrichment(Guid LeadId, CompanyProfile Profile);

/// <summary>
/// Asks the provider about a lead's company and records the ticket.
/// </summary>
/// <remarks>
/// <strong>The ticket is derived from the lead, so asking twice asks about the same
/// request.</strong> <c>lead.created</c> is redelivered whenever a consumer does not acknowledge,
/// and a fresh ticket per delivery would leave the provider holding several requests of which
/// the webhook could only ever answer one.
/// </remarks>
[Capability("crm.lead.request_enrichment", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true,
    SideEffects = ["enrichment-provider", "crm.lead_enrichment.written"])]
public sealed class RequestLeadEnrichment : ICapability<BusMessage, EnrichmentRequested>
{
    private readonly EnrichmentStore _store;
    private readonly EnrichmentProvider _provider;

    /// <summary>Creates the capability.</summary>
    /// <param name="store">Records the request.</param>
    /// <param name="provider">Stands where the provider stands.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public RequestLeadEnrichment(EnrichmentStore store, EnrichmentProvider provider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(provider);

        _store = store;
        _provider = provider;
    }

    /// <inheritdoc />
    public async ValueTask<Result<EnrichmentRequested>> ExecuteAsync(
        BusMessage input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (Read(input) is not { } created)
        {
            return Result.Fail<EnrichmentRequested>(IntakeErrors.EventHasNoBody(input.EventId));
        }

        if (!await _store.LeadExistsAsync(ctx.TenantId, created.LeadId, ct).ConfigureAwait(false))
        {
            return Result.Fail<EnrichmentRequested>(EnrichmentErrors.LeadNotFound(created.LeadId));
        }

        var ticket = _provider.Request(created.LeadId);

        await _store.RequestAsync(ctx.TenantId, created.LeadId, ticket, ctx.UtcNow, ct)
            .ConfigureAwait(false);

        var answered = await _store.ReadProfileAsync(ctx.TenantId, created.LeadId, ct)
            .ConfigureAwait(false);

        return Result.Ok(new EnrichmentRequested(created.LeadId, ticket, answered is not null));
    }

    /// <summary>Reads the event's body.</summary>
    /// <param name="message">What the broker handed over.</param>
    /// <returns>The event, or null when the body was absent.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    public static LeadCreated? Read(BusMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return message.Payload is { Length: > 0 } body
            ? JsonSerializer.Deserialize(body, CrmJsonContext.Default.LeadCreated)
            : null;
    }
}

/// <summary>
/// One look at whether the provider has answered.
/// </summary>
/// <remarks>
/// <strong>It writes the answer down as soon as it sees one.</strong> The attempt that satisfies
/// the poll and the webhook that ends it early are two ways of learning the same fact, and both
/// put it in <c>lead_enrichment</c> — so the step after the wait reads one place and does not
/// have to know which ending happened.
/// </remarks>
[Capability("crm.lead.check_enrichment", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true,
    SideEffects = ["crm.lead_enrichment.written"])]
public sealed class CheckLeadEnrichment : ICapability<EnrichmentRequested, EnrichmentAttempt>
{
    private readonly EnrichmentStore _store;
    private readonly EnrichmentProvider _provider;

    /// <summary>Creates the capability.</summary>
    /// <param name="store">Reads and records the answer.</param>
    /// <param name="provider">Stands where the provider stands.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public CheckLeadEnrichment(EnrichmentStore store, EnrichmentProvider provider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(provider);

        _store = store;
        _provider = provider;
    }

    /// <inheritdoc />
    public async ValueTask<Result<EnrichmentAttempt>> ExecuteAsync(
        EnrichmentRequested input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (await _store.ReadProfileAsync(ctx.TenantId, input.LeadId, ct).ConfigureAwait(false)
            is { } recorded)
        {
            return Result.Ok(new EnrichmentAttempt(input.LeadId, input.Ticket, recorded));
        }

        if (_provider.Poll(input.Ticket) is not { } answer)
        {
            return Result.Ok(new EnrichmentAttempt(input.LeadId, input.Ticket, null));
        }

        await _store.AnswerAsync(ctx.TenantId, input.LeadId, answer, ctx.UtcNow, ct)
            .ConfigureAwait(false);

        return Result.Ok(new EnrichmentAttempt(input.LeadId, input.Ticket, answer));
    }
}

/// <summary>Writes what the provider said onto the lead's enrichment row.</summary>
/// <remarks>
/// <strong>Reached from both endings, which is why it takes the profile rather than reading
/// one.</strong> On the polled ending the attempt carries it; on the signalled ending the
/// delivery does. The flow is where those two are made into one input, because the flow is the
/// only place that knows a wait has two endings.
/// </remarks>
[Capability("crm.lead.apply_enrichment", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true,
    SideEffects = ["crm.lead_enrichment.written"])]
public sealed class ApplyLeadEnrichment : ICapability<ApplyEnrichment, LeadEnriched>
{
    private readonly EnrichmentStore _store;

    /// <summary>Creates the capability.</summary>
    /// <param name="store">Writes the answer.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public ApplyLeadEnrichment(EnrichmentStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <inheritdoc />
    public async ValueTask<Result<LeadEnriched>> ExecuteAsync(
        ApplyEnrichment input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        // False when the row is already Answered, which a redelivery makes ordinary. The
        // enrichment is the same either way, and the caller is told what the lead now says.
        await _store.AnswerAsync(ctx.TenantId, input.LeadId, input.Profile, ctx.UtcNow, ct)
            .ConfigureAwait(false);

        return Result.Ok(new LeadEnriched(
            input.LeadId, input.Profile.Industry, input.Profile.Employees, input.Profile.Region));
    }
}

/// <summary>Marks a request the provider never answered.</summary>
[Capability("crm.lead.abandon_enrichment", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true,
    SideEffects = ["crm.lead_enrichment.written"])]
public sealed class AbandonLeadEnrichment : ICapability<EnrichmentRequested, EnrichmentAttempt>
{
    private readonly EnrichmentStore _store;

    /// <summary>Creates the capability.</summary>
    /// <param name="store">Marks the row.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public AbandonLeadEnrichment(EnrichmentStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <inheritdoc />
    public async ValueTask<Result<EnrichmentAttempt>> ExecuteAsync(
        EnrichmentRequested input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        await _store.AbandonAsync(ctx.TenantId, input.LeadId, ct).ConfigureAwait(false);

        return Result.Ok(new EnrichmentAttempt(input.LeadId, input.Ticket, null));
    }
}

/// <summary>
/// Waits for an outside provider to say something about a lead's company.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One wait with two endings, and §8.2 is the sequence.</strong> The instance parks on
/// one row carrying one <c>wake_at</c>. A webhook arriving between attempts ends that same wait —
/// it does not start a second one and there is no race to reconcile, which is ADR-0058.
/// </para>
/// <para>
/// <strong>Between attempts the flow holds nothing.</strong> No thread, no lease, no pooled
/// connection: a hundred thousand leads waiting on a slow provider cost a hundred thousand rows.
/// That is the property this package exists to demonstrate, and it is why the wait is a node in
/// the compiled plan rather than a loop somebody wrote.
/// </para>
/// <para>
/// <strong>The applying step is given its input by the flow.</strong> Both endings know the
/// profile — the attempt carries it on one path and the delivery on the other — and the flow is
/// the only place that can see which happened.
/// </para>
/// </remarks>
[Flow("crm.lead.enrichment", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-sales")]
[FlowDeadline("PT2H")]
[BusTrigger("lead.created", Group = "enrichment")]
public sealed partial class EnrichLeadFlow : Flow<BusMessage, LeadEnriched>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<BusMessage, LeadEnriched> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<RequestLeadEnrichment>()

            .PollUntil<CheckLeadEnrichment>(
                until: ctx => ctx.Get<EnrichmentAttempt>().IsAnswered,
                interval: EnrichmentWaits.Polling,
                timeout: EnrichmentWaits.Budget)

                // The webhook. It ends the wait the poll is in, on the same row, and control
                // continues below with the delivered payload in the state bag.
                .OrSignal<EnrichmentWebhook>()

                .OnTimeout(f => f
                    .Step<AbandonLeadEnrichment>()
                    .Fail(EnrichmentErrors.NotEnrichedInTime(Guid.Empty)))

            .Step<ApplyLeadEnrichment, ApplyEnrichment>(ctx => new ApplyEnrichment(
                ctx.Get<EnrichmentRequested>().LeadId,
                ctx.Context.TryGet<EnrichmentWebhook>(out var webhook)
                    ? webhook.Profile
                    : ctx.Get<EnrichmentAttempt>().Profile!))

            .Emit<LeadEnriched>(ctx => ctx.Get<LeadEnriched>())

            .Return(ctx => ctx.Get<LeadEnriched>());
    }
}
