using System.Text.Json;
using FlowX;

namespace Crm;

/// <summary>
/// Applies a validated document, metadata first.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Order is a correctness property here, not tidiness.</strong> A process before an
/// opportunity because <c>opportunity.stage_id</c> is a foreign key into it; an account before a
/// contact for the same reason; an object before a field, and a field before a record, because
/// the record's values are validated against the fields. Getting this wrong does not produce a
/// wrong answer — it produces a foreign-key violation halfway through, which is worse only
/// because it leaves the tenant half-configured.
/// </para>
/// <para>
/// <strong>Custom metadata goes through <see cref="CustomSchemaStore"/> rather than through new
/// statements.</strong> Those inserts already exist, already carry <c>ON CONFLICT DO NOTHING</c>
/// and already write the picklist options beside the field. A second spelling of them here would
/// be a second thing to keep right.
/// </para>
/// </remarks>
public sealed class SeedApplier
{
    private readonly SeedStore _seeds;
    private readonly CustomSchemaStore _schema;
    private readonly ApprovalStore _approvals;
    private readonly ReportStore _reports;
    private readonly TimeProvider _clock;

    /// <summary>Creates the applier.</summary>
    /// <param name="seeds">Writes the built-in rows and the process.</param>
    /// <param name="schema">Writes the custom objects, fields, relationships and records.</param>
    /// <param name="approvals">Writes the approval processes, through the store that owns them.</param>
    /// <param name="reports">Writes the saved reports, through the store that owns them.</param>
    /// <param name="clock">Supplies the instant every written row is stamped with.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public SeedApplier(
        SeedStore seeds,
        CustomSchemaStore schema,
        ApprovalStore approvals,
        ReportStore reports,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(seeds);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(approvals);
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(clock);

        _seeds = seeds;
        _schema = schema;
        _approvals = approvals;
        _reports = reports;
        _clock = clock;
    }

    /// <summary>Applies a document.</summary>
    /// <param name="document">What to apply. Already validated by <see cref="SeedReader"/>.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>What was written, or the first refusal.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    public async ValueTask<Result<SeedOutcome>> ApplyAsync(SeedDocument document, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);

        var tenant = document.Tenant;
        var now = _clock.GetUtcNow();
        var outcome = default(SeedOutcome);

        // ------------------------------------------------------------------ metadata

        // The calendar first: a strategy, a quota and every executive screen name a period, and
        // a tenant without one answers `crm.period_not_found` rather than answering emptily.
        foreach (var period in document.Metadata.Periods)
        {
            outcome = outcome.And(
                await _seeds.WritePeriodAsync(tenant, period, now, ct).ConfigureAwait(false));
        }

        foreach (var strategy in document.Metadata.Strategy)
        {
            outcome = outcome.And(
                await _seeds.WriteStrategyAsync(tenant, strategy, now, ct).ConfigureAwait(false));
        }

        // Managers before their reports: `reports_to` is a foreign key into this same table, so
        // the order the file lists them in is the order they have to be written in.
        foreach (var member in InReportingOrder(document.Metadata.OrgMembers))
        {
            outcome = outcome.And(
                await _seeds.WriteOrgMemberAsync(tenant, member, ct).ConfigureAwait(false));
        }

        foreach (var kpi in document.Metadata.Kpis)
        {
            outcome = outcome.And(await _seeds.WriteKpiAsync(tenant, kpi, now, ct).ConfigureAwait(false));
        }

        foreach (var territory in document.Metadata.Territories)
        {
            outcome = outcome.And(
                await _seeds.WriteTerritoryAsync(tenant, territory, now, ct).ConfigureAwait(false));
        }

        foreach (var quota in document.Metadata.Quotas)
        {
            outcome = outcome.And(
                await _seeds.WriteQuotaAsync(tenant, quota, now, ct).ConfigureAwait(false));
        }

        foreach (var hours in document.Metadata.BusinessHours)
        {
            outcome = outcome.And(
                await _seeds.WriteBusinessHoursAsync(tenant, hours, ct).ConfigureAwait(false));
        }

        foreach (var policy in document.Metadata.SlaPolicies)
        {
            outcome = outcome.And(
                await _seeds.WriteSlaPolicyAsync(tenant, policy, ct).ConfigureAwait(false));
        }

        foreach (var campaign in document.Metadata.Campaigns)
        {
            outcome = outcome.And(
                await _seeds.WriteCampaignAsync(tenant, campaign, now, ct).ConfigureAwait(false));
        }

        // Through ApprovalStore rather than through a statement of this module's own: it already
        // writes the process, its criteria and its steps in one transaction, and a second
        // spelling of that would be a second thing to keep right.
        foreach (var approval in document.Metadata.ApprovalProcesses)
        {
            var written = await _approvals.SaveProcessAsync(
                tenant,
                SeedIds.For(tenant, "approval", approval.Alias),
                new DefineApprovalProcess(
                    approval.Name,
                    approval.Label,
                    approval.Subject,
                    approval.Priority,
                    [.. approval.Criteria.Select(criterion =>
                        new ApprovalCriterion(criterion.Attribute, criterion.Operator, criterion.Value))],
                    [.. approval.Steps.Select(step =>
                        new ApprovalStepDefinition(step.Label, step.Kind, step.Approver))]),
                now,
                ct).ConfigureAwait(false);

            outcome = outcome.And(written is not null);
        }

        // Through ReportStore for the same reason as the approvals: the insert, the name
        // collision and the summary already exist there once.
        foreach (var report in document.Metadata.Reports)
        {
            var written = await _reports.SaveReportAsync(
                tenant,
                SeedIds.For(tenant, "report", report.Alias),
                new DefineReport(
                    report.Name,
                    report.Label,
                    report.Source,

                    // Built-in sources only. A custom-object report names its object by id, and a
                    // file cannot know one before the object it refers to has been written.
                    Target: null,
                    report.Dimension,
                    report.Measure,
                    report.MeasureOf),
                now,
                ct).ConfigureAwait(false);

            outcome = outcome.And(written is not null);
        }

        foreach (var process in document.Metadata.Processes)
        {
            var wanted = SeedIds.For(tenant, "process", process.Alias);
            var active = await _seeds.ActiveProcessAsync(tenant, process.AppliesTo, ct).ConfigureAwait(false);

            if (active is { } running && running.Id != wanted)
            {
                // Publishing a second active definition is what the schema's partial unique
                // index refuses, and it would be right to. Saying so here names the process
                // rather than the index.
                return Result.Fail<SeedOutcome>(new Error(
                    "crm.seed_process_conflict",
                    $"'{process.Alias}' drives {process.AppliesTo} and version {running.Version} " +
                    "is already active for this tenant. A seed adds; it does not supersede.",
                    ErrorCategory.Conflict));
            }

            outcome = outcome.And(
                await _seeds.WriteProcessAsync(tenant, process, now, ct).ConfigureAwait(false));
        }

        foreach (var declared in document.Metadata.Objects)
        {
            var written = await _schema.DeclareObjectAsync(
                tenant,
                SeedIds.For(tenant, "object", declared.Alias),
                new DefineObject(declared.Name, declared.Label),
                now,
                ct).ConfigureAwait(false);

            outcome = outcome.And(written is not null);
        }

        foreach (var field in document.Metadata.Fields)
        {
            var written = await _schema.DeclareFieldAsync(
                tenant,
                SeedIds.For(tenant, "field", field.Alias),
                new DefineField(
                    field.Entity,
                    field.Target is { } owner ? SeedIds.For(tenant, "object", owner) : null,
                    field.Name,
                    field.Label,
                    field.Type,
                    field.Required,
                    field.Options?.Select(option => new CustomFieldOption(option.Value, option.Label)).ToList()),
                now,
                ct).ConfigureAwait(false);

            outcome = outcome.And(written is not null);
        }

        foreach (var relationship in document.Metadata.Relationships)
        {
            var written = await _schema.DeclareRelationshipAsync(
                tenant,
                SeedIds.For(tenant, "relationship", relationship.Alias),
                new DefineRelationship(
                    relationship.Name,
                    SeedIds.For(tenant, "object", relationship.From),
                    SeedIds.For(tenant, "object", relationship.To),
                    relationship.Cardinality),
                ct).ConfigureAwait(false);

            outcome = outcome.And(written is not null);
        }

        // ------------------------------------------------------------------ data

        foreach (var account in document.Data.Accounts)
        {
            var accountId = SeedIds.For(tenant, "account", account.Alias);

            outcome = outcome.And(
                await _seeds.WriteAccountAsync(tenant, accountId, account, ct).ConfigureAwait(false));

            // The declared fields it carries, through the same merge the HTTP path uses — so a
            // value the schema would refuse is refused here too, rather than written by a second
            // statement that does not know about picklists.
            if (account.Values is { Count: > 0 } values)
            {
                var declared = await _schema
                    .FieldsForAsync(tenant, EntityKind.Account, ct)
                    .ConfigureAwait(false);

                // Not requireComplete: a seed sets the fields it cares about, and a required one
                // it omits is the schema's business at the write it omits it on.
                var faults = CustomValues.Validate(declared, values, requireComplete: false);

                if (faults.Count > 0)
                {
                    return Result.Fail<SeedOutcome>(faults[0]!);
                }

                await _schema
                    .MergeCustomFieldsAsync(
                        tenant,
                        EntityKind.Account,
                        accountId,
                        CustomValues.ToJson(declared, values),
                        ct)
                    .ConfigureAwait(false);
            }
        }

        foreach (var contact in document.Data.Contacts)
        {
            outcome = outcome.And(await _seeds.WriteContactAsync(
                tenant,
                SeedIds.For(tenant, "contact", contact.Alias),
                SeedIds.For(tenant, "account", contact.Account),
                contact,
                ct).ConfigureAwait(false));
        }

        foreach (var opportunity in document.Data.Opportunities)
        {
            var stage = opportunity.Stage.Split(':', 2);

            outcome = outcome.And(await _seeds.WriteOpportunityAsync(
                tenant,
                SeedIds.For(tenant, "opportunity", opportunity.Alias),
                SeedIds.For(tenant, "account", opportunity.Account),
                SeedIds.For(tenant, "contact", opportunity.PrimaryContact),
                SeedStore.StageId(tenant, stage[0]!, stage.Length > 1 ? stage[1]! : string.Empty),
                opportunity,
                now,
                ct).ConfigureAwait(false));
        }

        foreach (var lead in document.Data.Leads)
        {
            outcome = outcome.And(await _seeds.WriteLeadAsync(
                tenant, SeedIds.For(tenant, "lead", lead.Alias), lead, now, ct).ConfigureAwait(false));
        }

        foreach (var quote in document.Data.Quotes)
        {
            outcome = outcome.And(
                await _seeds.WriteQuoteAsync(tenant, quote, now, ct).ConfigureAwait(false));
        }

        foreach (var order in document.Data.Orders)
        {
            outcome = outcome.And(
                await _seeds.WriteOrderAsync(tenant, order, now, ct).ConfigureAwait(false));
        }

        // After the accounts and opportunities it is about, and after the periods it belongs to.
        foreach (var plan in document.Data.Plans)
        {
            outcome = outcome.And(
                await _seeds.WritePlanAsync(tenant, plan, now, ct).ConfigureAwait(false));
        }

        foreach (var activity in document.Data.Activities)
        {
            var kind = activity.RelatesToKind switch
            {
                EntityKind.Account => "account",
                EntityKind.Contact => "contact",
                EntityKind.Lead => "lead",
                _ => "opportunity",
            };

            outcome = outcome.And(await _seeds.WriteActivityAsync(
                tenant,
                activity,
                SeedIds.For(tenant, kind, activity.RelatesTo),
                now,
                ct).ConfigureAwait(false));
        }

        foreach (var record in document.Data.Records)
        {
            var target = SeedIds.For(tenant, "object", record.Target);
            var declared = await _schema.FieldsForAsync(tenant, target, ct).ConfigureAwait(false);

            // The same validation a written record meets over HTTP: a required field with no
            // value, a picklist value outside its set, a number that is not one. A seed that
            // skipped it would be the one way into this database that does not.
            if (CustomValues.Validate(declared, record.Values, requireComplete: true) is { Count: > 0 } faults)
            {
                return Result.Fail<SeedOutcome>(faults[0]!);
            }

            outcome = outcome.And(await _schema.WriteRecordAsync(
                tenant,
                SeedIds.For(tenant, "record", record.Alias),
                target,
                CustomValues.ToJson(declared, record.Values),
                now,
                ct).ConfigureAwait(false));
        }

        return Result.Ok(outcome);
    }

    /// <summary>Managers before the people who report to them.</summary>
    /// <remarks>
    /// <c>org_member.reports_to</c> is a foreign key into <c>org_member</c>, so a file listing a
    /// representative above their manager would be refused by the database — and the operator
    /// would be told about a constraint rather than about an ordering they had no reason to know
    /// mattered. A depth-first walk from the people who report to nobody puts them in an order
    /// that always works. The reader has already refused a cycle, so this terminates.
    /// </remarks>
    private static IEnumerable<SeedOrgMember> InReportingOrder(IReadOnlyList<SeedOrgMember> members)
    {
        var byManager = members
            .GroupBy(member => member.ReportsTo ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        var queue = new Queue<SeedOrgMember>(
            byManager.TryGetValue(string.Empty, out var top) ? top : []);

        while (queue.Count > 0)
        {
            var member = queue.Dequeue();

            yield return member;

            if (byManager.TryGetValue(member.UserId, out var reports))
            {
                foreach (var report in reports)
                {
                    queue.Enqueue(report);
                }
            }
        }
    }
}
