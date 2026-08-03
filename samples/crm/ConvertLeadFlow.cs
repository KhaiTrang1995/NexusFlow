using FlowX;

namespace Crm;

/// <summary>
/// Turns a qualified lead into an account, a contact and an opportunity, or leaves nothing
/// behind.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The sample's saga, and §8.1's sequence.</strong> Three writes across three
/// aggregates, each naming its own inverse, and a fourth that records the conversion once the
/// first three have held. A failure at any of the writes unwinds the ones before it in strict
/// reverse order, so the tables are left as they were found.
/// </para>
/// <para>
/// <strong><c>Durable</c>, and it has to be.</strong> The unwind is rebuilt from the journal:
/// a node that dies mid-compensation resumes the compensation rather than restarting the flow,
/// and a node that dies between a write and its commit re-runs that write against an id
/// derived from the lead rather than a fresh one. On <c>Ephemeral</c> both of those become a
/// half-converted lead nobody can find — which is <c>FLOWX1012</c>, and it would refuse this
/// flow at build time rather than let it ship.
/// </para>
/// <para>
/// <strong><c>lead.converted</c> is staged by the last step and by nothing earlier.</strong>
/// An event naming an account that a later compensation removes is an event no consumer can
/// act on, and the outbox stages it in the same transaction as the write, so a failure after
/// it is impossible rather than merely unlikely.
/// </para>
/// <para>
/// <strong>What this flow does not do.</strong> It does not create the follow-up task, send a
/// notification or move the opportunity's stage. Those are the configured process's business —
/// §7 — and they run from <c>lead.converted</c> through the change feed. A conversion that also
/// did them would be a second place where the process is written, and the administrator's
/// definition would stop being the only one.
/// </para>
/// </remarks>
[Flow("crm.lead.convert", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-sales")]
[FlowDeadline("PT30S")]
[HttpTrigger("POST", "/api/v1/crm/lead-conversions", Idempotent = true)]
public sealed partial class ConvertLeadFlow : Flow<ConvertLead, ConversionResult>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<ConvertLead, ConversionResult> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            // Reads the lead and refuses what cannot be converted, before anything is written.
            .Step<ReadLeadForConversion>()

            // The account. Its name is the lead's company rather than anything the caller sent,
            // so the two cannot disagree.
            .Step<CreateAccount, CreateAccountRequest>(ctx => new CreateAccountRequest(
                ConversionIds.Account(ctx.Input.LeadId),
                ctx.Get<LeadUnderConversion>().Company,
                ctx.Input.Industry,
                ctx.Input.Region,
                ctx.Input.Owner))
                .CompensateWith<RemoveAccount>()

            // The contact, at that account. The address comes from the lead and carries
            // [Sensitive] the whole way, so it is in the contact row and in none of the
            // journal, outbox or audit rows this step writes on the way past.
            .Step<CreateContact, CreateContactRequest>(ctx => new CreateContactRequest(
                ConversionIds.Contact(ctx.Input.LeadId),
                ConversionIds.Account(ctx.Input.LeadId),
                ctx.Get<LeadUnderConversion>().ContactName,
                ctx.Get<LeadUnderConversion>().Email))
                .CompensateWith<RemoveContact>()

            // The opportunity. This is §8.1's failing step: a credit check, a stage that has
            // been retired between the read and the write, or the database simply refusing.
            // Whatever the reason, the contact and then the account are undone behind it.
            .Step<CreateOpportunity, CreateOpportunityRequest>(ctx => new CreateOpportunityRequest(
                ConversionIds.Opportunity(ctx.Input.LeadId),
                ConversionIds.Account(ctx.Input.LeadId),
                ConversionIds.Contact(ctx.Input.LeadId),
                ctx.Input))
                .CompensateWith<RemoveOpportunity>()

            // The lead. Not compensable, because nothing after it can fail.
            .Step<MarkLeadConverted, MarkLeadConvertedRequest>(ctx => new MarkLeadConvertedRequest(
                ctx.Input.LeadId,
                new ConversionResult(
                    ConversionIds.Account(ctx.Input.LeadId),
                    ConversionIds.Contact(ctx.Input.LeadId),
                    ConversionIds.Opportunity(ctx.Input.LeadId))))

            .Emit<LeadConverted>(ctx => new LeadConverted(
                ctx.Input.LeadId,
                ConversionIds.Account(ctx.Input.LeadId),
                ConversionIds.Contact(ctx.Input.LeadId),
                ConversionIds.Opportunity(ctx.Input.LeadId)))

            .Return(ctx => ctx.Get<LeadConversionRecorded>().Result);
    }
}

/// <summary>A lead became an account, a contact and an opportunity.</summary>
/// <remarks>
/// <para>
/// <strong>It names ids and nothing else.</strong> A consumer that wants the company or the
/// contact's address reads them from the tables under its own tenant and its own
/// authorisation; putting them on the event would put a contact's address in the outbox, where
/// <c>[Sensitive]</c> exists to keep it from going.
/// </para>
/// <para>
/// This is what the configured process listens for — §8.3 — and it reaches
/// <c>RunWorkflowTransitionFlow</c> through the change feed reading the outbox rather than
/// through a broker, so a broker outage cannot lose a configured action.
/// </para>
/// </remarks>
/// <param name="LeadId">The lead that was converted.</param>
/// <param name="AccountId">The account it became.</param>
/// <param name="ContactId">The contact it became.</param>
/// <param name="OpportunityId">The opportunity it became.</param>
public sealed record LeadConverted(
    Guid LeadId,
    Guid AccountId,
    Guid ContactId,
    Guid OpportunityId);
