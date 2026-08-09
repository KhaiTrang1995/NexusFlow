using FlowX;

namespace Crm;

// The CRM's domain model: docs/26-CRM-Sample.md §5, as records.
//
// Two conventions hold across every type in this file, and they are stated once here rather
// than repeated on thirteen records.
//
// NO RECORD CARRIES A TENANT, AND §5 DRAWS FOUR THAT DO. Lead, Account, Activity and
// ProcessDefinition are given `TenantId Tenant` in §5.1, §5.3 and §5.4; the schema gives
// tenant_id to those four and to four more. The records have it nowhere, and this is the one
// place this package argues with the design it implements.
//
// A tenant on a contract is a value a caller can set. ADR-0046 settles where a tenant comes
// from — validated claims, never a header, never the payload — and the whole isolation story
// of §6 rests on the runtime deriving it rather than reading it: `ClaimTenantResolver` refuses
// an invocation whose asserted tenant the claims do not support, and `flowx.tenant_id` is set
// from that derived value on every connection. A `Tenant` member here would be a second source
// for the same fact, sitting on the request body, and the first capability to trust it would
// reintroduce exactly the cross-tenant read the policies exist to refuse. At run time the
// value is on `CapabilityContext.TenantId` and in the row's tenant_id column, and nothing that
// needs it has to be handed it.
//
// A TYPE §5 DEFINES BECOMES A TYPE; A TYPE §5 ONLY NAMES BECOMES ITS COLUMN. §5 marks Money
// and RelatedRef `<<value object>>` and spells out ten enumerations — those are all here. It
// also writes `LeadId Id`, `UserId Owner`, `StageId Stage` and `Region Region` without ever
// saying what any of them contains, and §6 does: uuid, uuid, uuid, text. So they are Guid,
// Guid, Guid and string. Minting thirteen single-field id structs would be inventing a
// decision the design did not take, and would put a wrapper between every contract and the
// uuid column it is read out of.

// ------------------------------------------------------------------------ value objects

/// <summary>An amount and the currency it is denominated in. Never a bare decimal.</summary>
/// <remarks>
/// §5.2: "the <c>event-driven</c> sample already found what happens when a currency is
/// hard-coded: four transports agreed with each other and all four were wrong". Every monetary
/// member in this file is a <see cref="Money"/>, including <see cref="QuoteLine.UnitPrice"/>,
/// which is why <c>quote_line</c> has a currency column that §6 does not draw.
/// </remarks>
/// <param name="Amount">How much.</param>
/// <param name="Currency">ISO 4217 code.</param>
public readonly record struct Money(decimal Amount, string Currency);

/// <summary>
/// A reference to one of four entity kinds: the discriminator and the identifier, together.
/// </summary>
/// <remarks>
/// §5.3. This is the one relationship in the schema with no foreign key, because it has four
/// possible parents. Migration <c>0003</c> is the trigger that enforces it instead, and it
/// enforces the tenant as well as the row — a parent in another tenant is refused with
/// <c>23503</c>, the same SQLSTATE a foreign key would have raised.
/// </remarks>
/// <param name="Kind">Which of the four tables <paramref name="Id"/> is a key into.</param>
/// <param name="Id">The parent row.</param>
public readonly record struct RelatedRef(EntityKind Kind, Guid Id);

/// <summary>What a lead became, once it was converted.</summary>
/// <remarks>
/// Null on a lead that has not been converted, and whole on one that has: §8.1's saga writes
/// all four values in the step that succeeds and unwinds them together when a later step does
/// not, so <c>0001</c>'s check constraint refuses any row where some of them are set.
/// </remarks>
/// <param name="Account">The account created for it.</param>
/// <param name="Contact">The contact created for it.</param>
/// <param name="Opportunity">The opportunity created for it.</param>
/// <param name="At">When the conversion committed.</param>
public sealed record ConvertedTo(Guid Account, Guid Contact, Guid Opportunity, DateTimeOffset At);

// ------------------------------------------------------------------------ enumerations

/// <summary>Where a lead came from.</summary>
/// <remarks>
/// §5.1 types <c>Lead.Source</c> as <c>LeadSource</c> and never lists its members; §6 stores
/// <c>text source</c>. These five are this package's, and the check constraint in <c>0001</c>
/// is what keeps the column and the enum from disagreeing.
/// </remarks>
public enum LeadSource
{
    /// <summary>A form on the marketing site.</summary>
    Web = 0,

    /// <summary>Named by an existing customer.</summary>
    Referral = 1,

    /// <summary>Collected at a conference or a webinar.</summary>
    Event = 2,

    /// <summary>Approached by a representative first.</summary>
    Outbound = 3,

    /// <summary>Passed on by a reseller.</summary>
    Partner = 4,
}

/// <summary>Where a lead has got to. §5.1.</summary>
public enum LeadStatus
{
    /// <summary>Captured and not yet looked at.</summary>
    New = 0,

    /// <summary>A representative is working it.</summary>
    Working = 1,

    /// <summary>Worth converting.</summary>
    Qualified = 2,

    /// <summary>Not worth converting, and why is a note rather than a field.</summary>
    Disqualified = 3,

    /// <summary>Converted, and <see cref="Lead.Conversion"/> says into what.</summary>
    Converted = 4,
}

/// <summary>How far an account has come. §5.1.</summary>
/// <remarks>
/// <strong>This enum is why there is no <c>Customer</c> entity.</strong> A customer is an
/// account that has reached <see cref="Customer"/> — the same row, the same identity, one
/// field different — so becoming one is a transition rather than a move between tables. The
/// <c>customer_account</c> view in migration <c>0001</c> is what a report that wants a
/// customer table reads.
/// </remarks>
public enum Lifecycle
{
    /// <summary>Not yet bought anything.</summary>
    Prospect = 0,

    /// <summary>Has. The view selects exactly these.</summary>
    Customer = 1,

    /// <summary>Was a customer and has stopped being one.</summary>
    Churned = 2,
}

/// <summary>How an opportunity ended.</summary>
/// <remarks>
/// §5.2 types <c>Opportunity.Outcome</c> as <c>OpportunityOutcome?</c> and does not list its
/// members. Null while the opportunity is open, which is what the question mark carries.
/// </remarks>
public enum OpportunityOutcome
{
    /// <summary>Closed with an order.</summary>
    Won = 0,

    /// <summary>Closed without one.</summary>
    Lost = 1,

    /// <summary>Neither: nobody is working it and nobody closed it.</summary>
    Abandoned = 2,
}

/// <summary>Where a quote has got to.</summary>
/// <remarks>§5.2 types <c>Quote.Status</c> and does not list its members.</remarks>
public enum QuoteStatus
{
    /// <summary>Being priced. Not yet sent.</summary>
    Draft = 0,

    /// <summary>Sent, and the customer has not answered.</summary>
    Issued = 1,

    /// <summary>Accepted. <c>quote.accepted</c> is what <c>PlaceOrderFlow</c> reacts to.</summary>
    Accepted = 2,

    /// <summary>Refused.</summary>
    Rejected = 3,

    /// <summary>Nobody answered before <see cref="Quote.ValidUntil"/>.</summary>
    Expired = 4,

    /// <summary>
    /// Replaced by a re-priced quote, which carries <c>supersedes</c> back to this one.
    /// </summary>
    /// <remarks>
    /// <strong>Terminal, and that is the whole of the guarantee.</strong> Ordering reads
    /// <c>Issued</c> and approving reads <c>Draft</c>, so a quote in this state can be read and
    /// nothing else — which is what stops a customer's old price being ordered at after the
    /// revision went out.
    /// </remarks>
    Superseded = 5,
}

/// <summary>Where a sales order has got to.</summary>
/// <remarks>§5.2 types <c>SalesOrder.Status</c> and does not list its members.</remarks>
public enum OrderStatus
{
    /// <summary>Placed, and <c>order.placed</c> published to the billing system.</summary>
    Placed = 0,

    /// <summary>Billing has fulfilled it.</summary>
    Fulfilled = 1,

    /// <summary>Withdrawn before fulfilment.</summary>
    Cancelled = 2,
}

/// <summary>What kind of work an activity is. §5.3.</summary>
public enum ActivityKind
{
    /// <summary>Something to do, with a due date and an SLA.</summary>
    Task = 0,

    /// <summary>A telephone call, made or to make.</summary>
    Call = 1,

    /// <summary>A meeting.</summary>
    Meeting = 2,

    /// <summary>A record of something, with nothing to do.</summary>
    Note = 3,
}

/// <summary>Where an activity has got to. §5.3.</summary>
public enum ActivityStatus
{
    /// <summary>Outstanding. The only status the SLA timer considers.</summary>
    Open = 0,

    /// <summary>Done, and <see cref="Activity.CompletedAt"/> says when.</summary>
    Completed = 1,

    /// <summary>Dropped without being done.</summary>
    Cancelled = 2,

    /// <summary>Overdue and escalated at least once. §8.4.</summary>
    Escalated = 3,
}

/// <summary>The four things an activity can hang off, and the four a process can govern.</summary>
/// <remarks>
/// §5.3 and §5.4 draw this enum twice, for <see cref="RelatedRef.Kind"/> and for
/// <see cref="ProcessDefinition.AppliesTo"/>. It is one enum: the set of entities this CRM
/// treats as addressable, and both uses want exactly that set.
/// </remarks>
public enum EntityKind
{
    /// <summary>A lead.</summary>
    Lead = 0,

    /// <summary>An account.</summary>
    Account = 1,

    /// <summary>A contact.</summary>
    Contact = 2,

    /// <summary>An opportunity.</summary>
    Opportunity = 3,
}

/// <summary>The five side effects a configured transition may have. §5.4.</summary>
/// <remarks>
/// <strong>Closed, and §7 is the whole argument for that.</strong> The compiler sees all five
/// branches of the <c>Switch</c> that dispatches them, the manifest publishes all five, and
/// <c>flowx diff</c> can say when one changes. A sixth kind of side effect is a code change, a
/// build and a deployment — §7.2 — and that is the property the platform is bought for rather
/// than a limitation to route around.
/// </remarks>
public enum ActionKind
{
    /// <summary>Create an <see cref="Activity"/> of kind <see cref="ActivityKind.Task"/>.</summary>
    CreateTask = 0,

    /// <summary>Send a notification to a contact.</summary>
    SendNotification = 1,

    /// <summary>Set a whitelisted field on the entity the transition moved.</summary>
    SetField = 2,

    /// <summary>Require an approval before the transition is treated as complete.</summary>
    RequestApproval = 3,

    /// <summary>Stage an event in the outbox.</summary>
    EmitEvent = 4,
}

/// <summary>The five comparisons a guard may make. §5.4.</summary>
/// <remarks>
/// §7.4: five operators over a whitelisted field set is what can be <em>checked</em> — a guard
/// naming a field that does not exist is refused when the definition is published, not when an
/// opportunity happens to reach that stage at three in the morning. A general expression
/// language would be a second execution engine, untyped and invisible to the manifest.
/// </remarks>
public enum GuardOperator
{
    /// <summary>The field equals the value.</summary>
    Equals = 0,

    /// <summary>The field does not equal the value.</summary>
    NotEquals = 1,

    /// <summary>The field, read as a number, is greater than the value.</summary>
    GreaterThan = 2,

    /// <summary>The field, read as a number, is less than the value.</summary>
    LessThan = 3,

    /// <summary>The field has any value at all. <c>Value</c> is ignored.</summary>
    IsSet = 4,
}

// ------------------------------------------------------------------------ parties · §5.1

/// <summary>Somebody who might become a customer, and has not been qualified yet.</summary>
/// <remarks>
/// <see cref="Email"/> carries <c>[Sensitive]</c>. §5.1 asks for it on
/// <see cref="Contact.Email"/> and <see cref="Contact.Phone"/>; a lead's address is the same
/// personal datum before anybody decided the person was worth talking to, and marking one and
/// not the other would put a person's address in a journal row for exactly as long as they
/// were unqualified.
/// </remarks>
/// <param name="Id">The lead.</param>
/// <param name="Company">The organisation it came in under.</param>
/// <param name="ContactName">Who to speak to. A name, not a <see cref="Contact"/> — there is no contact until conversion.</param>
/// <param name="Email">How to reach them. <strong>Sensitive.</strong></param>
/// <param name="Source">Where it came from.</param>
/// <param name="Status">How far it has got.</param>
/// <param name="Score">0 to 100, written by <c>ScoreLeadFlow</c>.</param>
/// <param name="Owner">The representative working it, or null before assignment.</param>
/// <param name="CapturedAt">When it arrived.</param>
/// <param name="Conversion">What it became, or null.</param>
public sealed record Lead(
    Guid Id,
    string Company,
    string ContactName,
    [property: Sensitive] string? Email,
    LeadSource Source,
    LeadStatus Status,
    int Score,
    Guid? Owner,
    DateTimeOffset CapturedAt,
    ConvertedTo? Conversion);

/// <summary>An organisation this CRM does business with, or hopes to.</summary>
/// <remarks>
/// <strong>There is no <c>Customer</c> record and that is §5.1's decision, kept.</strong> A
/// customer is an account whose <see cref="Lifecycle"/> has reached
/// <see cref="Lifecycle.Customer"/>. A separate type would mean either duplicating every field
/// here or carrying a foreign key that is always one-to-one, and it would make the moment of
/// becoming a customer a move rather than a transition. <c>customer_account</c> in migration
/// <c>0001</c> is the view for the reports that want one.
/// </remarks>
/// <param name="Id">The account.</param>
/// <param name="Name">What it is called.</param>
/// <param name="Industry">What it does.</param>
/// <param name="Lifecycle">How far it has come.</param>
/// <param name="Region">Where it is. Text, because §5 names a <c>Region</c> type and never says what is in one.</param>
/// <param name="Owner">The representative who holds it.</param>
public sealed record Account(
    Guid Id,
    string Name,
    string Industry,
    Lifecycle Lifecycle,
    string Region,
    Guid Owner);

/// <summary>A person at an account.</summary>
/// <remarks>
/// <para>
/// <strong><see cref="Email"/> and <see cref="Phone"/> carry <c>[Sensitive]</c>, and §5.1 says
/// what that buys: they go behind <c>JournalPayload</c>'s redaction pass, which has one exit
/// and no accessor, so they are absent from journal rows, outbox rows, audit records and
/// RFC 7807 problem documents without any capability having to remember.</strong>
/// </para>
/// <para>
/// It is a claim about FlowX's artifacts and not about the <c>contact</c> table: what a
/// capability writes to a column, it writes. The marker is what keeps the value from being
/// copied into everything else the platform records on the way past.
/// </para>
/// </remarks>
/// <param name="Id">The contact.</param>
/// <param name="Account">The account they work at.</param>
/// <param name="FullName">Their name.</param>
/// <param name="Email">Their address. <strong>Sensitive.</strong></param>
/// <param name="Phone">Their number. <strong>Sensitive.</strong></param>
/// <param name="IsPrimary">Whether they are the account's main point of contact.</param>
public sealed record Contact(
    Guid Id,
    Guid Account,
    string FullName,
    [property: Sensitive] string? Email,
    [property: Sensitive] string? Phone,
    bool IsPrimary);

// ------------------------------------------------------------------------ pipeline · §5.2

/// <summary>A piece of business that might close.</summary>
/// <remarks>
/// <para>
/// <strong><see cref="Stage"/> is what pins an in-flight opportunity to a process
/// version.</strong> §6 asks that an administrator publishing a new definition leaves
/// opportunities already in flight on the version they started on, "exactly as
/// <c>flow_instance</c> pins <c>flow_version</c>". There is no version column here and none is
/// needed: a stage belongs to exactly one <see cref="ProcessDefinition"/>, and publishing
/// version n+1 creates new stage rows, so this foreign key <em>is</em> the pin.
/// </para>
/// </remarks>
/// <param name="Id">The opportunity.</param>
/// <param name="Account">Whose business it is.</param>
/// <param name="PrimaryContact">Who is being sold to.</param>
/// <param name="Name">What it is called.</param>
/// <param name="Amount">What it is worth, in its own currency.</param>
/// <param name="Stage">The stage of the process version it is pinned to.</param>
/// <param name="Probability">0 to 100.</param>
/// <param name="ExpectedClose">When the representative thinks it closes.</param>
/// <param name="Owner">The representative who holds it.</param>
/// <param name="Outcome">How it ended, or null while it is open.</param>
/// <param name="StageEnteredAt">When it entered <paramref name="Stage"/>. What the nightly sweep in §8.4 reads.</param>
public sealed record Opportunity(
    Guid Id,
    Guid Account,
    Guid PrimaryContact,
    string Name,
    Money Amount,
    Guid Stage,
    int Probability,
    DateOnly ExpectedClose,
    Guid Owner,
    OpportunityOutcome? Outcome,
    DateTimeOffset StageEnteredAt);

/// <summary>A priced offer against an opportunity.</summary>
/// <remarks>
/// <see cref="ApprovedBy"/> is null until a manager approves a discount over the threshold.
/// §5.2 makes that the sample's second authorisation stance —
/// <c>Permission("crm.discount.approve")</c>, which a representative does not hold — so it is
/// a stance on a capability rather than a constraint on this record.
/// </remarks>
/// <param name="Id">The quote.</param>
/// <param name="Opportunity">What it prices.</param>
/// <param name="Status">Where it has got to.</param>
/// <param name="Subtotal">The sum of its lines.</param>
/// <param name="Discount">What was taken off.</param>
/// <param name="Total">What is being asked for.</param>
/// <param name="ValidUntil">After which it expires.</param>
/// <param name="ApprovedBy">The manager who approved the discount, or null.</param>
/// <param name="Supersedes">
/// The quote this one was re-priced from, or null when it is the first offer. The pointer runs
/// this way because the revision is the row being written and the one it replaces is already
/// there; "what replaced this quote" is the same edge read the other way.
/// </param>
public sealed record Quote(
    Guid Id,
    Guid Opportunity,
    QuoteStatus Status,
    Money Subtotal,
    Money Discount,
    Money Total,
    DateTimeOffset ValidUntil,
    Guid? ApprovedBy,
    Guid? Supersedes);

/// <summary>One line of a quote.</summary>
/// <param name="Id">The line.</param>
/// <param name="Quote">The quote it belongs to.</param>
/// <param name="Sku">What is being sold.</param>
/// <param name="Quantity">How many.</param>
/// <param name="UnitPrice">What one costs, in its own currency.</param>
public sealed record QuoteLine(
    Guid Id,
    Guid Quote,
    string Sku,
    int Quantity,
    Money UnitPrice);

/// <summary>An accepted quote, placed as an order.</summary>
/// <param name="Id">The order.</param>
/// <param name="Quote">The quote that was accepted.</param>
/// <param name="Account">Whose order it is.</param>
/// <param name="Status">Where it has got to.</param>
/// <param name="Total">What was ordered, in its own currency.</param>
/// <param name="PlacedAt">When it was placed.</param>
public sealed record SalesOrder(
    Guid Id,
    Guid Quote,
    Guid Account,
    OrderStatus Status,
    Money Total,
    DateTimeOffset PlacedAt);

// ------------------------------------------------------------------------ work · §5.3

/// <summary>Something that happened, or something that has to.</summary>
/// <remarks>
/// <see cref="RelatesTo"/> is a discriminated reference rather than a foreign key, because an
/// activity may hang off any of four entity kinds. Migration <c>0003</c> is what makes that
/// reference honest.
/// </remarks>
/// <param name="Id">The activity.</param>
/// <param name="Kind">What kind of work it is.</param>
/// <param name="Subject">What it is about.</param>
/// <param name="RelatesTo">What it hangs off.</param>
/// <param name="Owner">Whose it is. Reassigned to a manager on escalation.</param>
/// <param name="DueAt">When it has to be done by, or null when nothing is due.</param>
/// <param name="Status">Where it has got to.</param>
/// <param name="CompletedAt">When it was done, or null.</param>
/// <param name="EscalationCount">How many SLA windows have passed with it still open.</param>
public sealed record Activity(
    Guid Id,
    ActivityKind Kind,
    string Subject,
    RelatedRef RelatesTo,
    Guid Owner,
    DateTimeOffset? DueAt,
    ActivityStatus Status,
    DateTimeOffset? CompletedAt,
    int EscalationCount);

// ------------------------------------------------------------------------ process · §5.4

/// <summary>One published version of one entity kind's sales process.</summary>
/// <remarks>
/// <para>
/// <strong>Versioned, and <see cref="IsActive"/> is partial-unique per tenant and entity
/// kind.</strong> An administrator publishes a new version rather than editing the one in use;
/// migration <c>0001</c>'s <c>process_definition_one_active_per_kind</c> index is what refuses
/// a second active definition for the same kind. Superseded versions stay — they are what
/// in-flight opportunities are still running on — which is why the index is partial rather
/// than a unique over <c>(tenant, kind, is_active)</c> that would allow exactly one of those
/// too.
/// </para>
/// <para>
/// §7 is the whole argument for what an administrator may change this way and what still needs
/// a build: stages, transitions, guards over a whitelisted field set, and the order and
/// parameters of five closed action kinds — but never a sixth kind, a sixth operator, or a
/// field the whitelist does not name.
/// </para>
/// </remarks>
/// <param name="Id">The definition.</param>
/// <param name="AppliesTo">Which entity kind it governs.</param>
/// <param name="Version">1 for the first, and one higher for each publication after it.</param>
/// <param name="IsActive">Whether new work starts on this version.</param>
/// <param name="PublishedAt">When it was published.</param>
public sealed record ProcessDefinition(
    Guid Id,
    EntityKind AppliesTo,
    int Version,
    bool IsActive,
    DateTimeOffset PublishedAt);

/// <summary>One stage of one process definition.</summary>
/// <param name="Id">The stage. What an opportunity's <see cref="Opportunity.Stage"/> points at.</param>
/// <param name="Process">The definition it belongs to.</param>
/// <param name="Name">What it is called.</param>
/// <param name="Ordinal">Where it sits in the order.</param>
/// <param name="IsTerminal">Whether the process ends here.</param>
public sealed record ProcessStage(
    Guid Id,
    Guid Process,
    string Name,
    int Ordinal,
    bool IsTerminal);

/// <summary>A legal move from one stage to another.</summary>
/// <param name="Id">The transition.</param>
/// <param name="From">The stage it leaves.</param>
/// <param name="To">The stage it arrives at.</param>
/// <param name="Trigger">The event type that offers it.</param>
/// <param name="Ordinal">Which transition out of <paramref name="From"/> is considered first.</param>
public sealed record ProcessTransition(
    Guid Id,
    Guid From,
    Guid To,
    string Trigger,
    int Ordinal);

/// <summary>A condition a transition is only allowed under.</summary>
/// <param name="Id">The guard.</param>
/// <param name="Transition">What it guards.</param>
/// <param name="Field">The field it reads. Refused at publish time if the whitelist does not name it.</param>
/// <param name="Operator">How it compares.</param>
/// <param name="Value">What it compares against. Ignored by <see cref="GuardOperator.IsSet"/>.</param>
public sealed record TransitionGuard(
    Guid Id,
    Guid Transition,
    string Field,
    GuardOperator Operator,
    string Value);

/// <summary>Something a transition does once it is applied.</summary>
/// <param name="Id">The action.</param>
/// <param name="Transition">The transition that runs it.</param>
/// <param name="Kind">Which of the five closed kinds it is.</param>
/// <param name="Parameters">
/// Its configuration, as JSON. §5.4 types this <c>string</c> and §6 stores <c>jsonb</c>; the
/// record carries the text and the column parses it, so a definition with malformed parameters
/// is refused by the database at publish time rather than by an action at three in the morning.
/// </param>
/// <param name="Ordinal">Where it sits among the transition's actions.</param>
public sealed record TransitionAction(
    Guid Id,
    Guid Transition,
    ActionKind Kind,
    string Parameters,
    int Ordinal);

// ------------------------------------------------------------------------ the smoke endpoint

/// <summary>Asks a deployment whether its CRM schema is there and reachable.</summary>
/// <remarks>
/// <para>
/// <strong>This exists so <c>samples/crm</c> is an application rather than a library</strong>,
/// and it is deliberately the only flow in the package. §10 gives packages 4 to 12 the flows
/// of §4 by name; a foundation that also shipped a lead-capture endpoint would be two packages
/// disagreeing about who owns <c>CaptureLeadFlow</c>.
/// </para>
/// <para>
/// It carries no members. A probe that took parameters would be answering a question somebody
/// asked; this one answers the only question this package can answer — "are the tables here,
/// and can this caller's tenant reach them?" — and the caller is already named by the token.
/// </para>
/// </remarks>
public sealed record CrmSchemaProbe();

/// <summary>What the probe found.</summary>
/// <param name="SchemaVersion">
/// The highest CRM migration this schema has applied. <c>0</c> would mean nothing has been
/// migrated; <see cref="CrmMigrator.TargetVersion"/> is what this build expects.
/// </param>
/// <param name="Tables">
/// One row per CRM table, counted <em>through the tenant's own policies</em>. A caller sees
/// their own rows and nobody else's, which is the whole of what package 3 delivers, observable
/// over HTTP.
/// </param>
public sealed record CrmSchemaReport(int SchemaVersion, IReadOnlyList<CrmTableRowCount> Tables);

/// <summary>How many rows of one table this tenant has.</summary>
/// <param name="Table">The table.</param>
/// <param name="Rows">Its row count, under row-level security.</param>
public sealed record CrmTableRowCount(string Table, long Rows);
