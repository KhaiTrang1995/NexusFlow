using FlowX;

namespace Crm;

// -------------------------------------------------------------------------------- the document

/// <summary>
/// A tenant's starting state, read from one JSON file.
/// </summary>
/// <param name="Tenant">
/// Which tenant it is written for. Named in the file and checked against the tenants this
/// deployment serves — never inferred, because a seed applied to the wrong tenant is
/// indistinguishable from a cross-tenant write.
/// </param>
/// <param name="Metadata">What the tenant is configured to have. Applied first.</param>
/// <param name="Data">Rows of it. Applied second, because they refer to the first.</param>
/// <remarks>
/// <para>
/// <strong>Every item carries an alias, and the alias is the identity.</strong> An id in a seed
/// file is a value somebody has to keep unique across environments by hand; an alias is a name
/// local to the file, and the id is derived from it — see <see cref="SeedIds"/>. That is what
/// makes a contact able to say <c>"account": "northwind"</c>, and what makes applying the same
/// file twice write nothing the second time.
/// </para>
/// <para>
/// <strong>What this deliberately is not.</strong> It is not a migration format and it is not a
/// backup format. It adds; it never updates and never deletes. Changing a name in the file and
/// re-applying leaves the old row exactly where it was, because the alternative — a seed file
/// that can overwrite live rows — is a support incident waiting for somebody to edit the wrong
/// line.
/// </para>
/// </remarks>
public sealed record SeedDocument(
    string Tenant,
    SeedMetadata Metadata,
    SeedData Data);

/// <summary>What a tenant is configured to have.</summary>
/// <param name="Objects">Entities this build has never heard of.</param>
/// <param name="Fields">Columns on those, or on the built-in entities.</param>
/// <param name="Relationships">Named edges between custom objects.</param>
/// <param name="Processes">
/// The configured pipeline. Applied before any data, because an opportunity's stage is a
/// foreign key into it — a seed that wrote rows first would fail on the first opportunity, and
/// only for tenants whose file happened to contain one.
/// </param>
/// <param name="Periods">The fiscal calendar. Everything in the executive surface hangs off it.</param>
/// <param name="Strategy">The number for a period, and the sentence beside it.</param>
/// <param name="OrgMembers">The reporting line, which is what decides whose rows a manager sees.</param>
/// <param name="Kpis">What is measured, and against what.</param>
/// <param name="Territories">The map.</param>
/// <param name="Quotas">Somebody's number, per period and per measure.</param>
/// <param name="BusinessHours">When the desk is open. The SLA clock stops outside these.</param>
/// <param name="SlaPolicies">What a case of each priority is promised.</param>
/// <param name="Campaigns">What marketing is running.</param>
/// <param name="ApprovalProcesses">Who has to agree to what, and in what order.</param>
public sealed record SeedMetadata(
    IReadOnlyList<SeedObject> Objects,
    IReadOnlyList<SeedField> Fields,
    IReadOnlyList<SeedRelationship> Relationships,
    IReadOnlyList<SeedProcess> Processes,
    IReadOnlyList<SeedPeriod> Periods,
    IReadOnlyList<SeedStrategy> Strategy,
    IReadOnlyList<SeedOrgMember> OrgMembers,
    IReadOnlyList<SeedKpi> Kpis,
    IReadOnlyList<SeedTerritory> Territories,
    IReadOnlyList<SeedQuota> Quotas,
    IReadOnlyList<SeedBusinessHours> BusinessHours,
    IReadOnlyList<SeedSlaPolicy> SlaPolicies,
    IReadOnlyList<SeedCampaign> Campaigns,
    IReadOnlyList<SeedApprovalProcess> ApprovalProcesses);

/// <summary>Rows.</summary>
/// <param name="Accounts">Applied first: contacts and opportunities reference them.</param>
/// <param name="Contacts">Second.</param>
/// <param name="Opportunities">Third, because one needs both of the above and a stage.</param>
/// <param name="Leads">Independent of the three, and applied last so a failure above stops sooner.</param>
/// <param name="Records">Rows of a custom object declared in <see cref="SeedMetadata.Objects"/>.</param>
/// <param name="Activities">Tasks, calls, meetings and notes against the rows above.</param>
/// <param name="Quotes">Priced offers against an opportunity.</param>
/// <param name="Orders">What a quote became once somebody committed.</param>
/// <param name="Plans">Account and deal plans, with what is under them.</param>
public sealed record SeedData(
    IReadOnlyList<SeedAccount> Accounts,
    IReadOnlyList<SeedContact> Contacts,
    IReadOnlyList<SeedOpportunity> Opportunities,
    IReadOnlyList<SeedLead> Leads,
    IReadOnlyList<SeedRecord> Records,
    IReadOnlyList<SeedActivity> Activities,
    IReadOnlyList<SeedQuote> Quotes,
    IReadOnlyList<SeedOrder> Orders,
    IReadOnlyList<SeedPlan> Plans);

// -------------------------------------------------------------------------------- metadata items

/// <summary>Declares a custom object.</summary>
/// <param name="Alias">Its name in this file. Referred to by fields, relationships and records.</param>
/// <param name="Name">The identifier. Lower case, snake case.</param>
/// <param name="Label">What a person sees.</param>
public sealed record SeedObject(string Alias, string Name, string Label);

/// <summary>Declares a custom field.</summary>
/// <param name="Alias">Its name in this file.</param>
/// <param name="Entity">A built-in entity kind, or null when <paramref name="Target"/> is given.</param>
/// <param name="Target">The alias of a custom object, or null when <paramref name="Entity"/> is.</param>
/// <param name="Name">The identifier a guard and a filter use.</param>
/// <param name="Label">What a person sees.</param>
/// <param name="Type">What it holds.</param>
/// <param name="Required">Whether a record without it is refused.</param>
/// <param name="Options">The closed set, for a picklist. Ignored otherwise.</param>
public sealed record SeedField(
    string Alias,
    EntityKind? Entity,
    string? Target,
    string Name,
    string Label,
    CustomFieldType Type,
    bool Required,
    IReadOnlyList<SeedOption>? Options);

/// <summary>One allowed value of a picklist.</summary>
/// <param name="Value">What is stored.</param>
/// <param name="Label">What a person sees.</param>
public sealed record SeedOption(string Value, string Label);

/// <summary>Declares an edge between two custom objects.</summary>
/// <param name="Alias">Its name in this file.</param>
/// <param name="Name">The identifier.</param>
/// <param name="From">The alias of the object an edge starts at.</param>
/// <param name="To">The alias of the object it ends at.</param>
/// <param name="Cardinality">What the administrator promises about how many.</param>
public sealed record SeedRelationship(
    string Alias,
    string Name,
    string From,
    string To,
    CustomCardinality Cardinality);

/// <summary>Publishes a configured process.</summary>
/// <param name="Alias">Its name in this file. An opportunity names a stage as <c>alias:stage</c>.</param>
/// <param name="AppliesTo">Which entity kind it drives.</param>
/// <param name="Version">Which version this is. One active version per kind per tenant.</param>
/// <param name="Stages">Its stages, in the order they are entered.</param>
/// <param name="Transitions">What may follow what.</param>
/// <remarks>
/// <strong>The first thing in this sample that publishes a process outside a test.</strong>
/// Everything downstream of <c>process_definition</c> — the transition flow, the stage a
/// pipeline board reads, the probability a forecast weights by — assumed a definition somebody
/// had already inserted by hand.
/// </remarks>
public sealed record SeedProcess(
    string Alias,
    EntityKind AppliesTo,
    int Version,
    IReadOnlyList<SeedStage> Stages,
    IReadOnlyList<SeedTransition> Transitions);

/// <summary>One stage of a configured process.</summary>
/// <param name="Name">Its name, which is what a transition and an opportunity refer to.</param>
/// <param name="Terminal">Whether nothing follows it.</param>
public sealed record SeedStage(string Name, bool Terminal);

/// <summary>One move between two stages.</summary>
/// <param name="From">The stage left.</param>
/// <param name="To">The stage entered.</param>
/// <param name="Trigger">What causes it.</param>
public sealed record SeedTransition(string From, string To, string Trigger);

/// <summary>One period of the fiscal calendar.</summary>
/// <param name="Alias">Its name in this file.</param>
/// <param name="Name">The identifier the API takes. Lower case, snake case — <c>fy26_q3</c>.</param>
/// <param name="Label">What a person sees.</param>
/// <param name="StartsOn">The first day.</param>
/// <param name="EndsOn">The last day.</param>
/// <param name="Parent">The alias of the period this one sits inside, or null.</param>
/// <remarks>
/// <strong>Without one of these the executive surface is not empty — it is a 404.</strong> Every
/// roll-up, scorecard, forecast and quota report is asked for by period name, and a tenant with
/// no calendar answers <c>crm.period_not_found</c> to all of them. That is the difference between
/// a screen with nothing on it and a screen that looks broken.
/// </remarks>
public sealed record SeedPeriod(
    string Alias,
    string Name,
    string Label,
    DateOnly StartsOn,
    DateOnly EndsOn,
    string? Parent);

/// <summary>The number for a period, and the sentence beside it.</summary>
/// <param name="Period">The alias of the period.</param>
/// <param name="Vision">What the number is for, in the executive's own words.</param>
/// <param name="TargetAmount">The number.</param>
/// <param name="Currency">Its unit. Three letters.</param>
public sealed record SeedStrategy(
    string Period,
    string Vision,
    decimal TargetAmount,
    string Currency);

/// <summary>One person in the reporting line.</summary>
/// <param name="UserId">Their identifier, which is what a token's subject carries.</param>
/// <param name="DisplayName">What a person sees.</param>
/// <param name="Role">What they may see: their own rows, their reports', or everybody's.</param>
/// <param name="ReportsTo">Whose report they are, or null at the top.</param>
public sealed record SeedOrgMember(
    string UserId,
    string DisplayName,
    OrgRole Role,
    string? ReportsTo);

/// <summary>One measured number.</summary>
/// <param name="Alias">Its name in this file.</param>
/// <param name="Name">The identifier.</param>
/// <param name="Label">What a person sees.</param>
/// <param name="Source">Which of the five counts it reads. A closed set, not an expression.</param>
/// <param name="Target">What it is held to.</param>
/// <param name="Direction">Whether more is better.</param>
public sealed record SeedKpi(
    string Alias,
    string Name,
    string Label,
    KpiSource Source,
    decimal Target,
    KpiDirection Direction);

/// <summary>One territory.</summary>
/// <param name="Alias">Its name in this file.</param>
/// <param name="Name">The identifier.</param>
/// <param name="Label">What a person sees.</param>
/// <param name="Priority">Which wins when two would both claim a row. Lower is stronger.</param>
public sealed record SeedTerritory(string Alias, string Name, string Label, int Priority);

/// <summary>Somebody's number.</summary>
/// <param name="Period">The alias of the period.</param>
/// <param name="UserId">Whose.</param>
/// <param name="Measure">Of what.</param>
/// <param name="Target">How much.</param>
/// <param name="RampFactor">
/// What fraction of it counts, for somebody who started part-way through. Between zero and one.
/// </param>
public sealed record SeedQuota(
    string Period,
    string UserId,
    QuotaMeasure Measure,
    decimal Target,
    decimal RampFactor);

/// <summary>When the desk is open on one day of the week.</summary>
/// <param name="DayOfWeek">Nought is Sunday, as PostgreSQL counts.</param>
/// <param name="OpensAt">Local opening time, <c>HH:mm</c>.</param>
/// <param name="ClosesAt">Local closing time.</param>
public sealed record SeedBusinessHours(int DayOfWeek, TimeOnly OpensAt, TimeOnly ClosesAt);

/// <summary>What a case of one priority is promised.</summary>
/// <param name="Alias">Its name in this file.</param>
/// <param name="Name">The identifier.</param>
/// <param name="Label">What a person sees.</param>
/// <param name="Priority">Which cases it governs.</param>
/// <param name="FirstResponseMinutes">How long until somebody answers.</param>
/// <param name="ResolutionMinutes">How long until it is closed.</param>
/// <param name="BusinessHoursOnly">Whether the clock stops when the desk shuts.</param>
public sealed record SeedSlaPolicy(
    string Alias,
    string Name,
    string Label,
    CasePriority Priority,
    int FirstResponseMinutes,
    int ResolutionMinutes,
    bool BusinessHoursOnly);

/// <summary>One campaign.</summary>
/// <param name="Alias">Its name in this file.</param>
/// <param name="Name">The identifier.</param>
/// <param name="Label">What a person sees.</param>
/// <param name="Channel">How it reaches people.</param>
/// <param name="StartsOn">The first day.</param>
/// <param name="EndsOn">The last day.</param>
/// <param name="Budget">What it was given.</param>
public sealed record SeedCampaign(
    string Alias,
    string Name,
    string Label,
    CampaignChannel Channel,
    DateOnly StartsOn,
    DateOnly EndsOn,
    decimal Budget);

/// <summary>Declares an approval process.</summary>
/// <param name="Alias">Its name in this file.</param>
/// <param name="Name">The identifier.</param>
/// <param name="Label">What a person sees.</param>
/// <param name="Subject">What it governs.</param>
/// <param name="Priority">Which process wins when two would both claim a subject. Lower is stronger.</param>
/// <param name="Criteria">
/// When it applies, all of it. A process with no criteria governs every subject of its kind —
/// which is a decision, and a loud one, so it is written rather than defaulted.
/// </param>
/// <param name="Steps">Who has to agree, in order.</param>
/// <remarks>
/// <strong>Without one of these the approval feature reads as dead.</strong>
/// <c>/approvals/requests</c> answers <c>required: false</c> when nothing governs the subject, so
/// a tenant with no process has an inbox that is permanently empty and a setup screen with
/// nothing on it — indistinguishable from a feature that does not work.
/// </remarks>
public sealed record SeedApprovalProcess(
    string Alias,
    string Name,
    string Label,
    ApprovalSubject Subject,
    int Priority,
    IReadOnlyList<SeedApprovalCriterion> Criteria,
    IReadOnlyList<SeedApprovalStep> Steps);

/// <summary>When an approval process applies.</summary>
/// <param name="Attribute">Which fact about the subject.</param>
/// <param name="Operator">How it is compared.</param>
/// <param name="Value">What it is compared against.</param>
public sealed record SeedApprovalCriterion(string Attribute, GuardOperator Operator, string Value);

/// <summary>One step of an approval.</summary>
/// <param name="Label">What a person sees.</param>
/// <param name="Kind">Named, the submitter's manager, or whoever holds a role.</param>
/// <param name="Approver">
/// The user or role, or null for <c>SubmittersManager</c> — which needs no name because the
/// reporting line already knows who it is.
/// </param>
public sealed record SeedApprovalStep(string Label, ApproverKind Kind, string? Approver);

// -------------------------------------------------------------------------------- data items

/// <summary>A row of <c>account</c>.</summary>
/// <param name="Alias">Its name in this file.</param>
/// <param name="Name">The account's name.</param>
/// <param name="Industry">Free text; nothing compares it.</param>
/// <param name="Lifecycle">Where it has got to.</param>
/// <param name="Region">Free text.</param>
/// <param name="Owner">Who holds it. An id, because a user directory is not this sample's.</param>
public sealed record SeedAccount(
    string Alias,
    string Name,
    string Industry,
    Lifecycle Lifecycle,
    string Region,
    Guid Owner);

/// <summary>A row of <c>contact</c>.</summary>
/// <param name="Alias">Its name in this file.</param>
/// <param name="Account">The alias of the account it belongs to.</param>
/// <param name="FullName">The person's name.</param>
/// <param name="Email">Their address, or null.</param>
/// <param name="Phone">Their number, or null.</param>
/// <param name="Primary">Whether they are the account's first point of contact.</param>
public sealed record SeedContact(
    string Alias,
    string Account,
    string FullName,
    string? Email,
    string? Phone,
    bool Primary);

/// <summary>A row of <c>opportunity</c>.</summary>
/// <param name="Alias">Its name in this file.</param>
/// <param name="Account">The alias of the account.</param>
/// <param name="PrimaryContact">The alias of the contact.</param>
/// <param name="Name">What the deal is called.</param>
/// <param name="Amount">Its value. A decimal, written as a JSON number or string.</param>
/// <param name="Currency">Its unit. Three letters, and the amount means nothing without it.</param>
/// <param name="Stage">The stage it sits in, as <c>process-alias:stage-name</c>.</param>
/// <param name="Probability">Nought to a hundred.</param>
/// <param name="ExpectedClose">When it is expected to close.</param>
/// <param name="Owner">Who holds it.</param>
public sealed record SeedOpportunity(
    string Alias,
    string Account,
    string PrimaryContact,
    string Name,
    decimal Amount,
    string Currency,
    string Stage,
    int Probability,
    DateOnly ExpectedClose,
    Guid Owner);

/// <summary>A row of <c>lead</c>.</summary>
/// <param name="Alias">Its name in this file.</param>
/// <param name="Company">Where they work.</param>
/// <param name="ContactName">Who they are.</param>
/// <param name="Email">Their address, or null.</param>
/// <param name="Source">Where they came from.</param>
/// <param name="Status">Where they have got to. Never <c>Converted</c>; see the reader.</param>
/// <param name="Score">Nought to a hundred.</param>
/// <param name="Owner">Who holds it, or null when nobody does.</param>
public sealed record SeedLead(
    string Alias,
    string Company,
    string ContactName,
    string? Email,
    LeadSource Source,
    LeadStatus Status,
    int Score,
    Guid? Owner);

/// <summary>A task, call, meeting or note against one of the rows above.</summary>
/// <param name="Alias">Its name in this file.</param>
/// <param name="Kind">Which of the four.</param>
/// <param name="Subject">What it is about.</param>
/// <param name="RelatesToKind">Which entity it hangs off.</param>
/// <param name="RelatesTo">The alias of the row, in that entity's collection.</param>
/// <param name="Owner">Whose it is.</param>
/// <param name="DueInDays">
/// When it is due, counted from the moment the seed is applied. A date would be in the past by
/// the time anybody looked at the sample, and an overdue-task sweep would escalate every one.
/// </param>
/// <param name="Status">Where it has got to. Never <c>Completed</c>; see the reader.</param>
public sealed record SeedActivity(
    string Alias,
    ActivityKind Kind,
    string Subject,
    EntityKind RelatesToKind,
    string RelatesTo,
    Guid Owner,
    int DueInDays,
    ActivityStatus Status);

/// <summary>A priced offer against an opportunity.</summary>
/// <param name="Alias">Its name in this file.</param>
/// <param name="Opportunity">The alias of the opportunity it prices.</param>
/// <param name="Status">Where it has got to.</param>
/// <param name="Subtotal">Before the discount.</param>
/// <param name="Discount">What came off.</param>
/// <param name="Currency">The unit of both.</param>
/// <param name="ValidForDays">
/// How long it stands, counted from when the seed is applied — so a demo does not open on a
/// quote that expired before anybody looked at it.
/// </param>
public sealed record SeedQuote(
    string Alias,
    string Opportunity,
    QuoteStatus Status,
    decimal Subtotal,
    decimal Discount,
    string Currency,
    int ValidForDays);

/// <summary>What a quote became once somebody committed.</summary>
/// <param name="Alias">Its name in this file.</param>
/// <param name="Quote">The alias of the quote it was placed from.</param>
/// <param name="Account">The alias of the account it belongs to.</param>
/// <param name="Status">Where it has got to.</param>
public sealed record SeedOrder(
    string Alias,
    string Quote,
    string Account,
    OrderStatus Status);

/// <summary>A plan, and everything under it.</summary>
/// <param name="Alias">Its name in this file.</param>
/// <param name="Name">The identifier the API takes.</param>
/// <param name="Label">What a person sees.</param>
/// <param name="Kind">Account or opportunity. A demand plan needs neither and is not seeded.</param>
/// <param name="Period">The alias of the period it belongs to.</param>
/// <param name="Owner">Whose it is, as a user identifier.</param>
/// <param name="Subject">
/// The alias of the account or opportunity it is about. The schema requires one for each kind —
/// <c>CHECK ((kind = 'Account') = (account_id IS NOT NULL))</c> — which is what stops a plan
/// about nothing.
/// </param>
/// <param name="TargetAmount">What it commits.</param>
/// <param name="Currency">The unit of that.</param>
/// <param name="Objectives">What it is trying to achieve.</param>
/// <param name="Steps">The mutual action plan.</param>
/// <param name="Risks">What could stop it.</param>
public sealed record SeedPlan(
    string Alias,
    string Name,
    string Label,
    PlanKind Kind,
    string Period,
    string Owner,
    string Subject,
    decimal TargetAmount,
    string Currency,
    IReadOnlyList<SeedObjective> Objectives,
    IReadOnlyList<SeedPlanStep> Steps,
    IReadOnlyList<SeedPlanRisk> Risks);

/// <summary>One thing a plan is trying to achieve.</summary>
/// <param name="Description">What it is.</param>
/// <param name="Measure">What it is counted in.</param>
/// <param name="Target">How much.</param>
/// <param name="Status">Where it has got to.</param>
public sealed record SeedObjective(
    string Description,
    ObjectiveMeasure Measure,
    decimal Target,
    ObjectiveStatus Status);

/// <summary>One agreed step.</summary>
/// <param name="Description">What was agreed.</param>
/// <param name="Owner">Whose it is.</param>
/// <param name="DueInDays">
/// When, counted from the moment the seed is applied — so a demo does not open on a plan whose
/// every step went overdue before anybody looked at it. Negative for one that deliberately has.
/// </param>
public sealed record SeedPlanStep(string Description, string Owner, int DueInDays);

/// <summary>One risk.</summary>
/// <param name="Description">What could stop it.</param>
/// <param name="Severity">How bad it would be.</param>
/// <param name="Mitigation">What is being done.</param>
public sealed record SeedPlanRisk(string Description, RiskSeverity Severity, string Mitigation);

/// <summary>A row of a custom object.</summary>
/// <param name="Alias">Its name in this file.</param>
/// <param name="Target">The alias of the object it belongs to.</param>
/// <param name="Values">Its field values, by field name.</param>
public sealed record SeedRecord(
    string Alias,
    string Target,
    IReadOnlyDictionary<string, string?> Values);

// -------------------------------------------------------------------------------- what is applied

/// <summary>What one application of a seed did.</summary>
/// <param name="Written">Rows this run inserted.</param>
/// <param name="Present">Rows a previous run had already inserted.</param>
public readonly record struct SeedOutcome(int Written, int Present)
{
    /// <summary>Adds one item's result.</summary>
    /// <param name="inserted">Whether this item was written rather than found.</param>
    /// <returns>The running total.</returns>
    public SeedOutcome And(bool inserted) =>
        inserted ? this with { Written = Written + 1 } : this with { Present = Present + 1 };
}

// -------------------------------------------------------------------------------- what it accepts

/// <summary>What a seed file may contain at most.</summary>
/// <remarks>
/// <strong>Bounded because the file is input.</strong> It arrives from a mounted volume or a
/// config map rather than from a request, which makes it lower risk and not no risk: an
/// unbounded read is an unbounded allocation whoever writes the file gets to choose. Each limit
/// is the point past which a seed has stopped being a starting state and become an import,
/// which is a different tool with different guarantees.
/// </remarks>
public static class SeedLimits
{
    /// <summary>The largest file that is read at all, in bytes.</summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    /// <summary>The most items one collection may hold.</summary>
    public const int MaxItems = 2_000;

    /// <summary>The deepest the document may nest.</summary>
    public const int MaxDepth = 12;

    /// <summary>The longest an alias may be.</summary>
    public const int MaxAlias = 64;
}

// -------------------------------------------------------------------------------- what can go wrong

/// <summary>Refusals reading or applying a seed can produce.</summary>
public static class SeedErrors
{
    /// <summary>The file is larger than <see cref="SeedLimits.MaxBytes"/>.</summary>
    /// <param name="bytes">How large it is.</param>
    /// <returns>The refusal.</returns>
    public static Error TooLarge(long bytes) =>
        new(
            "crm.seed_too_large",
            $"The seed file is {bytes} bytes and the limit is {SeedLimits.MaxBytes}. A file this " +
            "size is an import rather than a starting state.",
            ErrorCategory.Validation);

    /// <summary>The bytes are not the document this build reads.</summary>
    /// <param name="detail">What the reader said, which is the only useful part.</param>
    /// <returns>The refusal.</returns>
    public static Error NotReadable(string detail) =>
        new("crm.seed_unreadable", $"The seed file could not be read: {detail}", ErrorCategory.Validation);

    /// <summary>A collection holds more than <see cref="SeedLimits.MaxItems"/>.</summary>
    /// <param name="collection">Which one.</param>
    /// <param name="count">How many it holds.</param>
    /// <returns>The refusal.</returns>
    public static Error TooMany(string collection, int count) =>
        new(
            "crm.seed_too_many",
            $"'{collection}' holds {count} items and the limit is {SeedLimits.MaxItems}.",
            ErrorCategory.Validation);

    /// <summary>The file names a tenant this deployment does not serve.</summary>
    /// <param name="tenant">What it named.</param>
    /// <param name="served">What this deployment serves.</param>
    /// <returns>The refusal.</returns>
    /// <remarks>
    /// <strong>The check this module exists to not get wrong.</strong> Row-level security stops
    /// one tenant reading another's rows; it does not stop a deployment writing a demo dataset
    /// into a tenant that has real customers in it, because both writes are legitimate as far as
    /// the database is concerned. The name in the file is the only statement of intent there is.
    /// </remarks>
    public static Error TenantNotServed(string tenant, IReadOnlyCollection<string> served)
    {
        ArgumentNullException.ThrowIfNull(served);

        return new(
            "crm.seed_tenant_unknown",
            $"The seed names tenant '{tenant}' and this deployment serves: " +
            (served.Count == 0 ? "none" : string.Join(", ", served)) + ".",
            ErrorCategory.Validation);
    }

    /// <summary>An alias is missing, too long, or used twice in one collection.</summary>
    /// <param name="collection">Which one.</param>
    /// <param name="alias">What was written.</param>
    /// <param name="why">What is wrong with it.</param>
    /// <returns>The refusal.</returns>
    public static Error BadAlias(string collection, string alias, string why) =>
        new("crm.seed_alias_invalid", $"'{collection}' alias '{alias}': {why}", ErrorCategory.Validation);

    /// <summary>An item refers to an alias the file does not declare.</summary>
    /// <param name="from">Which item refers to it.</param>
    /// <param name="alias">What it referred to.</param>
    /// <returns>The refusal.</returns>
    /// <remarks>
    /// Checked before anything is written. A reference resolved at write time would leave a
    /// half-applied tenant behind, and the operator would be looking at a foreign-key violation
    /// rather than at the typo that caused it.
    /// </remarks>
    public static Error UnknownReference(string from, string alias) =>
        new(
            "crm.seed_reference_unknown",
            $"'{from}' refers to '{alias}', which this file does not declare.",
            ErrorCategory.Validation);

    /// <summary>A field names neither a built-in entity nor a custom object, or both.</summary>
    /// <param name="alias">Which field.</param>
    /// <returns>The refusal.</returns>
    public static Error FieldOwnerAmbiguous(string alias) =>
        new(
            "crm.seed_field_owner",
            $"Field '{alias}' must name exactly one of 'entity' and 'target'.",
            ErrorCategory.Validation);

    /// <summary>A name is not one this schema accepts.</summary>
    /// <param name="what">An object, a field, a relationship or a picklist option.</param>
    /// <param name="name">What was written.</param>
    /// <returns>The refusal.</returns>
    /// <remarks>
    /// The same rule the declaring capability applies, checked over the file so the answer is a
    /// sentence about a line rather than a check-constraint violation naming a table the operator
    /// has never heard of.
    /// </remarks>
    public static Error NameIsNotUsable(string what, string name) =>
        new(
            "crm.seed_name_invalid",
            $"The {what} name '{name}' is not usable: lower case, starting with a letter, then " +
            "letters, digits or underscores.",
            ErrorCategory.Validation);

    /// <summary>A value is outside the range its column allows.</summary>
    /// <param name="item">Which item.</param>
    /// <param name="what">Which value.</param>
    /// <param name="allowed">What is allowed.</param>
    /// <returns>The refusal.</returns>
    public static Error OutOfRange(string item, string what, string allowed) =>
        new("crm.seed_out_of_range", $"'{item}' {what} must be {allowed}.", ErrorCategory.Validation);
}
