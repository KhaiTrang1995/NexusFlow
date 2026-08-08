using System.Text.Json;
using FlowX;

namespace Crm;

/// <summary>
/// Turns bytes into a document, or says why they are not one.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Strict, because a seed file is written by hand.</strong> <c>UnmappedMemberHandling
/// .Disallow</c> means a misspelt property is a refusal naming the property rather than a field
/// that silently keeps its default — which is the failure mode of every lenient configuration
/// format there has ever been, and the one that costs an afternoon because the file looks right.
/// </para>
/// <para>
/// <strong>Everything is checked before anything is written.</strong> References are resolved
/// against the aliases the file declares, not against the database, so a typo is a refusal with
/// nothing applied. Resolving them at write time would leave a tenant half-configured and hand
/// the operator a foreign-key violation instead of the line they got wrong.
/// </para>
/// </remarks>
public static class SeedReader
{
    /// <summary>Reads a document from bytes.</summary>
    /// <param name="utf8">The file's contents.</param>
    /// <returns>The document, or why it is not one.</returns>
    public static Result<SeedDocument> Read(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > SeedLimits.MaxBytes)
        {
            return Result.Fail<SeedDocument>(SeedErrors.TooLarge(utf8.Length));
        }

        SeedDocument? document;

        try
        {
            document = JsonSerializer.Deserialize(utf8, SeedJsonContext.Bounded.SeedDocument);
        }
        catch (JsonException failure)
        {
            // The message names the path and the line, which is the whole value of the refusal.
            return Result.Fail<SeedDocument>(SeedErrors.NotReadable(failure.Message));
        }

        if (document is null)
        {
            return Result.Fail<SeedDocument>(SeedErrors.NotReadable("it is null."));
        }

        return Validate(Normalise(document));
    }

    /// <summary>Turns every omitted collection into an empty one.</summary>
    /// <param name="document">What was read.</param>
    /// <returns>The same document with no null collections.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    /// <remarks>
    /// <strong>So a file that only wants two accounts is two accounts long.</strong> Without this
    /// every seed would have to write eleven empty arrays to say nothing about eleven things it
    /// does not care about, and forgetting one would be a null-reference exception rather than a
    /// refusal — the least useful message this module could produce.
    /// </remarks>
    public static SeedDocument Normalise(SeedDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var metadata = document.Metadata ?? Empty.Metadata;
        var data = document.Data ?? Empty.Data;

        return document with
        {
            Metadata = metadata with
            {
                Objects = metadata.Objects ?? [],
                Fields = metadata.Fields ?? [],
                Relationships = metadata.Relationships ?? [],
                Processes = metadata.Processes ?? [],
                Periods = metadata.Periods ?? [],
                Strategy = metadata.Strategy ?? [],
                OrgMembers = metadata.OrgMembers ?? [],
                Kpis = metadata.Kpis ?? [],
                Territories = metadata.Territories ?? [],
                Quotas = metadata.Quotas ?? [],
                BusinessHours = metadata.BusinessHours ?? [],
                SlaPolicies = metadata.SlaPolicies ?? [],
                Campaigns = metadata.Campaigns ?? [],
                ApprovalProcesses = metadata.ApprovalProcesses ?? [],
            },
            Data = data with
            {
                Accounts = data.Accounts ?? [],
                Contacts = data.Contacts ?? [],
                Opportunities = data.Opportunities ?? [],
                Leads = data.Leads ?? [],
                Records = data.Records ?? [],
                Activities = data.Activities ?? [],
                // Each quote's lines too. A quote written without them reaches Validate with a
                // null collection, and "at least one line" would be a null-reference exception
                // rather than the sentence about the file it is meant to be.
                Quotes = [.. (data.Quotes ?? []).Select(quote => quote with { Lines = quote.Lines ?? [] })],
                Orders = data.Orders ?? [],
                Plans = data.Plans ?? [],
            },
        };
    }

    /// <summary>A document saying nothing, which every omitted section becomes.</summary>
    private static class Empty
    {
        public static readonly SeedMetadata Metadata =
            new([], [], [], [], [], [], [], [], [], [], [], [], [], []);

        public static readonly SeedData Data = new([], [], [], [], [], [], [], [], []);
    }

    /// <summary>Checks a document against the limits and against itself.</summary>
    /// <param name="document">What was read.</param>
    /// <returns>The same document, or the first thing wrong with it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    public static Result<SeedDocument> Validate(SeedDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var metadata = document.Metadata;
        var data = document.Data;

        if (string.IsNullOrWhiteSpace(document.Tenant))
        {
            return Result.Fail<SeedDocument>(
                SeedErrors.BadAlias("document", document.Tenant ?? string.Empty, "a tenant is required."));
        }

        var objects = new HashSet<string>(StringComparer.Ordinal);
        var processes = new HashSet<string>(StringComparer.Ordinal);
        var accounts = new HashSet<string>(StringComparer.Ordinal);
        var contacts = new HashSet<string>(StringComparer.Ordinal);
        var stages = new HashSet<string>(StringComparer.Ordinal);
        var periods = new HashSet<string>(StringComparer.Ordinal);
        var people = new HashSet<string>(StringComparer.Ordinal);
        var quotes = new HashSet<string>(StringComparer.Ordinal);

        if (Collect("objects", metadata.Objects, item => item.Alias, objects) is { } badObject)
        {
            return Result.Fail<SeedDocument>(badObject);
        }

        if (Collect("processes", metadata.Processes, item => item.Alias, processes) is { } badProcess)
        {
            return Result.Fail<SeedDocument>(badProcess);
        }

        if (Collect("accounts", data.Accounts, item => item.Alias, accounts) is { } badAccount)
        {
            return Result.Fail<SeedDocument>(badAccount);
        }

        if (Collect("contacts", data.Contacts, item => item.Alias, contacts) is { } badContact)
        {
            return Result.Fail<SeedDocument>(badContact);
        }

        if (Collect("periods", metadata.Periods, item => item.Alias, periods) is { } badPeriod)
        {
            return Result.Fail<SeedDocument>(badPeriod);
        }

        // The four whose aliases nothing refers to. Still collected, because two items sharing
        // an alias share a derived id, and the second would silently be the first.
        var unreferenced =
            Collect("fields", metadata.Fields, item => item.Alias, [])
            ?? Collect("relationships", metadata.Relationships, item => item.Alias, [])
            ?? Collect("opportunities", data.Opportunities, item => item.Alias, [])
            ?? Collect("leads", data.Leads, item => item.Alias, [])
            ?? Collect("records", data.Records, item => item.Alias, [])
            ?? Collect("kpis", metadata.Kpis, item => item.Alias, [])
            ?? Collect("territories", metadata.Territories, item => item.Alias, [])
            ?? Collect("slaPolicies", metadata.SlaPolicies, item => item.Alias, [])
            ?? Collect("campaigns", metadata.Campaigns, item => item.Alias, [])
            ?? Collect("approvalProcesses", metadata.ApprovalProcesses, item => item.Alias, [])
            ?? Collect("activities", data.Activities, item => item.Alias, [])
            ?? Collect("quotes", data.Quotes, item => item.Alias, quotes)
            ?? Collect("orders", data.Orders, item => item.Alias, [])
            ?? Collect("plans", data.Plans, item => item.Alias, []);

        if (unreferenced is { } bad)
        {
            return Result.Fail<SeedDocument>(bad);
        }

        foreach (var process in metadata.Processes)
        {
            if (process.Stages.Count == 0)
            {
                return Result.Fail<SeedDocument>(
                    SeedErrors.OutOfRange(process.Alias, "stages", "at least one"));
            }

            var named = new HashSet<string>(StringComparer.Ordinal);

            foreach (var stage in process.Stages)
            {
                if (!named.Add(stage.Name))
                {
                    return Result.Fail<SeedDocument>(
                        SeedErrors.BadAlias("stages", stage.Name, "it is declared twice."));
                }

                stages.Add(process.Alias + ":" + stage.Name);
            }

            foreach (var transition in process.Transitions)
            {
                if (!named.Contains(transition.From))
                {
                    return Result.Fail<SeedDocument>(
                        SeedErrors.UnknownReference(process.Alias + " transition", transition.From));
                }

                if (!named.Contains(transition.To))
                {
                    return Result.Fail<SeedDocument>(
                        SeedErrors.UnknownReference(process.Alias + " transition", transition.To));
                }

                if (string.Equals(transition.From, transition.To, StringComparison.Ordinal))
                {
                    return Result.Fail<SeedDocument>(
                        SeedErrors.OutOfRange(process.Alias, "a transition", "between two different stages"));
                }
            }
        }

        foreach (var declared in metadata.Objects)
        {
            if (!CustomValues.IsUsableName(declared.Name))
            {
                return Result.Fail<SeedDocument>(SeedErrors.NameIsNotUsable("object", declared.Name));
            }
        }

        foreach (var field in metadata.Fields)
        {
            // Exactly one owner. `DefineField` says the same thing and the capability refuses
            // the rest; saying it here is what makes the refusal name the file's line.
            if ((field.Entity is null) == (field.Target is null))
            {
                return Result.Fail<SeedDocument>(SeedErrors.FieldOwnerAmbiguous(field.Alias));
            }

            if (!CustomValues.IsUsableName(field.Name))
            {
                return Result.Fail<SeedDocument>(SeedErrors.NameIsNotUsable("field", field.Name));
            }

            // The same rule the capability applies, checked here so a file's mistake is a
            // sentence about the file rather than a check-constraint violation from migration
            // 0006 with the option's table name in it.
            foreach (var option in field.Options ?? [])
            {
                if (!CustomValues.IsUsableName(option.Value))
                {
                    return Result.Fail<SeedDocument>(SeedErrors.NameIsNotUsable("option", option.Value));
                }
            }

            if (field.Target is { } owner && !objects.Contains(owner))
            {
                return Result.Fail<SeedDocument>(SeedErrors.UnknownReference("field " + field.Alias, owner));
            }
        }

        foreach (var relationship in metadata.Relationships)
        {
            if (!CustomValues.IsUsableName(relationship.Name))
            {
                return Result.Fail<SeedDocument>(
                    SeedErrors.NameIsNotUsable("relationship", relationship.Name));
            }

            if (!objects.Contains(relationship.From))
            {
                return Result.Fail<SeedDocument>(
                    SeedErrors.UnknownReference("relationship " + relationship.Alias, relationship.From));
            }

            if (!objects.Contains(relationship.To))
            {
                return Result.Fail<SeedDocument>(
                    SeedErrors.UnknownReference("relationship " + relationship.Alias, relationship.To));
            }
        }

        foreach (var contact in data.Contacts)
        {
            if (!accounts.Contains(contact.Account))
            {
                return Result.Fail<SeedDocument>(
                    SeedErrors.UnknownReference("contact " + contact.Alias, contact.Account));
            }
        }

        foreach (var opportunity in data.Opportunities)
        {
            if (!accounts.Contains(opportunity.Account))
            {
                return Result.Fail<SeedDocument>(
                    SeedErrors.UnknownReference("opportunity " + opportunity.Alias, opportunity.Account));
            }

            if (!contacts.Contains(opportunity.PrimaryContact))
            {
                return Result.Fail<SeedDocument>(
                    SeedErrors.UnknownReference(
                        "opportunity " + opportunity.Alias, opportunity.PrimaryContact));
            }

            if (!stages.Contains(opportunity.Stage))
            {
                return Result.Fail<SeedDocument>(
                    SeedErrors.UnknownReference("opportunity " + opportunity.Alias, opportunity.Stage));
            }

            if (opportunity.Probability is < 0 or > 100)
            {
                return Result.Fail<SeedDocument>(
                    SeedErrors.OutOfRange(opportunity.Alias, "probability", "between 0 and 100"));
            }

            if (opportunity.Amount < 0)
            {
                return Result.Fail<SeedDocument>(
                    SeedErrors.OutOfRange(opportunity.Alias, "amount", "zero or more"));
            }
        }

        foreach (var lead in data.Leads)
        {
            if (lead.Score is < 0 or > 100)
            {
                return Result.Fail<SeedDocument>(
                    SeedErrors.OutOfRange(lead.Alias, "score", "between 0 and 100"));
            }

            // A converted lead is four columns and a saga, and three of them are not in this
            // file's vocabulary. Accepting the status would write a row the schema's own CHECK
            // refuses — a message about a constraint rather than about the word.
            if (lead.Status is LeadStatus.Converted)
            {
                return Result.Fail<SeedDocument>(
                    SeedErrors.OutOfRange(
                        lead.Alias, "status", "anything but Converted; conversion is a flow, not a seed"));
            }
        }

        // The reporting line. Collected first, because a quota and an activity both name a
        // person and neither should be able to name one the tenant does not have.
        foreach (var member in metadata.OrgMembers)
        {
            if (string.IsNullOrWhiteSpace(member.UserId))
            {
                return Result.Fail<SeedDocument>(
                    SeedErrors.BadAlias("orgMembers", string.Empty, "a userId is required."));
            }

            if (!people.Add(member.UserId))
            {
                return Result.Fail<SeedDocument>(
                    SeedErrors.BadAlias("orgMembers", member.UserId, "it is declared twice."));
            }
        }

        foreach (var member in metadata.OrgMembers)
        {
            if (member.ReportsTo is { } manager && !people.Contains(manager))
            {
                return Result.Fail<SeedDocument>(
                    SeedErrors.UnknownReference("orgMember " + member.UserId, manager));
            }

            // A line that loops has no top, and every walk up it runs until something stops it.
            if (string.Equals(member.ReportsTo, member.UserId, StringComparison.Ordinal))
            {
                return Result.Fail<SeedDocument>(
                    SeedErrors.OutOfRange(member.UserId, "reportsTo", "somebody else"));
            }
        }

        foreach (var period in metadata.Periods)
        {
            if (!CustomValues.IsUsableName(period.Name))
            {
                return Result.Fail<SeedDocument>(SeedErrors.NameIsNotUsable("period", period.Name));
            }

            if (period.EndsOn < period.StartsOn)
            {
                return Result.Fail<SeedDocument>(
                    SeedErrors.OutOfRange(period.Alias, "endsOn", "on or after startsOn"));
            }

            if (period.Parent is { } parent && !periods.Contains(parent))
            {
                return Result.Fail<SeedDocument>(
                    SeedErrors.UnknownReference("period " + period.Alias, parent));
            }
        }

        foreach (var strategy in metadata.Strategy)
        {
            if (!periods.Contains(strategy.Period))
            {
                return Result.Fail<SeedDocument>(
                    SeedErrors.UnknownReference("strategy", strategy.Period));
            }
        }

        foreach (var quota in metadata.Quotas)
        {
            if (!periods.Contains(quota.Period))
            {
                return Result.Fail<SeedDocument>(
                    SeedErrors.UnknownReference("quota for " + quota.UserId, quota.Period));
            }

            if (people.Count > 0 && !people.Contains(quota.UserId))
            {
                return Result.Fail<SeedDocument>(
                    SeedErrors.UnknownReference("quota", quota.UserId));
            }

            if (quota.RampFactor is <= 0 or > 1)
            {
                return Result.Fail<SeedDocument>(
                    SeedErrors.OutOfRange(quota.UserId, "rampFactor", "above zero and at most one"));
            }
        }

        foreach (var named in metadata.Kpis)
        {
            if (!CustomValues.IsUsableName(named.Name))
            {
                return Result.Fail<SeedDocument>(SeedErrors.NameIsNotUsable("kpi", named.Name));
            }
        }

        foreach (var territory in metadata.Territories)
        {
            if (!CustomValues.IsUsableName(territory.Name))
            {
                return Result.Fail<SeedDocument>(
                    SeedErrors.NameIsNotUsable("territory", territory.Name));
            }
        }

        foreach (var policy in metadata.SlaPolicies)
        {
            if (!CustomValues.IsUsableName(policy.Name))
            {
                return Result.Fail<SeedDocument>(SeedErrors.NameIsNotUsable("slaPolicy", policy.Name));
            }

            if (policy.FirstResponseMinutes <= 0 || policy.ResolutionMinutes <= 0)
            {
                return Result.Fail<SeedDocument>(
                    SeedErrors.OutOfRange(policy.Alias, "its clocks", "above zero"));
            }
        }

        foreach (var hours in metadata.BusinessHours)
        {
            if (hours.DayOfWeek is < 0 or > 6)
            {
                return Result.Fail<SeedDocument>(
                    SeedErrors.OutOfRange("businessHours", "dayOfWeek", "between 0 and 6"));
            }

            if (hours.ClosesAt <= hours.OpensAt)
            {
                return Result.Fail<SeedDocument>(
                    SeedErrors.OutOfRange("businessHours", "closesAt", "after opensAt"));
            }
        }

        foreach (var campaign in metadata.Campaigns)
        {
            if (!CustomValues.IsUsableName(campaign.Name))
            {
                return Result.Fail<SeedDocument>(SeedErrors.NameIsNotUsable("campaign", campaign.Name));
            }

            if (campaign.EndsOn < campaign.StartsOn)
            {
                return Result.Fail<SeedDocument>(
                    SeedErrors.OutOfRange(campaign.Alias, "endsOn", "on or after startsOn"));
            }
        }

        // An activity's parent is an alias in whichever collection its kind names. Resolved here
        // so a typo is a refusal rather than a foreign key the trigger of migration 0001 refuses
        // with a message about polymorphic integrity.
        foreach (var activity in data.Activities)
        {
            var known = activity.RelatesToKind switch
            {
                EntityKind.Account => accounts,
                EntityKind.Contact => contacts,
                EntityKind.Lead => new HashSet<string>(data.Leads.Select(lead => lead.Alias), StringComparer.Ordinal),
                _ => new HashSet<string>(
                    data.Opportunities.Select(opportunity => opportunity.Alias), StringComparer.Ordinal),
            };

            if (!known.Contains(activity.RelatesTo))
            {
                return Result.Fail<SeedDocument>(
                    SeedErrors.UnknownReference("activity " + activity.Alias, activity.RelatesTo));
            }

            // Completed is completed_at, and the schema's CHECK ties the two together. A seed
            // that set the status without the instant would be refused by the constraint.
            if (activity.Status is ActivityStatus.Completed)
            {
                return Result.Fail<SeedDocument>(SeedErrors.OutOfRange(
                    activity.Alias, "status", "anything but Completed; a seed starts work, it does not finish it"));
            }
        }

        var deals = new HashSet<string>(
            data.Opportunities.Select(opportunity => opportunity.Alias), StringComparer.Ordinal);

        foreach (var quote in data.Quotes)
        {
            if (!deals.Contains(quote.Opportunity))
            {
                return Result.Fail<SeedDocument>(
                    SeedErrors.UnknownReference("quote " + quote.Alias, quote.Opportunity));
            }

            // A quote is its lines. One with none has a total nobody can reconcile and a builder
            // with nothing to draw, which is what every seeded quote was before they existed.
            if (quote.Lines.Count == 0)
            {
                return Result.Fail<SeedDocument>(
                    SeedErrors.OutOfRange(quote.Alias, "lines", "at least one"));
            }

            foreach (var line in quote.Lines)
            {
                if (line.Quantity <= 0 || line.UnitPrice < 0)
                {
                    return Result.Fail<SeedDocument>(SeedErrors.OutOfRange(
                        quote.Alias,
                        "line '" + line.Sku + "'",
                        "a positive quantity and a price that is not negative"));
                }
            }

            // Against the sum, because that is what the subtotal will be. A discount larger than
            // it makes a negative total, which the schema takes without complaint.
            if (quote.Discount > quote.Lines.Sum(line => line.Quantity * line.UnitPrice))
            {
                return Result.Fail<SeedDocument>(
                    SeedErrors.OutOfRange(quote.Alias, "discount", "no more than the lines add up to"));
            }
        }

        foreach (var order in data.Orders)
        {
            if (!quotes.Contains(order.Quote))
            {
                return Result.Fail<SeedDocument>(
                    SeedErrors.UnknownReference("order " + order.Alias, order.Quote));
            }

            if (!accounts.Contains(order.Account))
            {
                return Result.Fail<SeedDocument>(
                    SeedErrors.UnknownReference("order " + order.Alias, order.Account));
            }
        }

        foreach (var process in metadata.ApprovalProcesses)
        {
            if (!CustomValues.IsUsableName(process.Name))
            {
                return Result.Fail<SeedDocument>(
                    SeedErrors.NameIsNotUsable("approvalProcess", process.Name));
            }

            if (process.Steps.Count == 0)
            {
                return Result.Fail<SeedDocument>(
                    SeedErrors.OutOfRange(process.Alias, "steps", "at least one"));
            }

            // The same closed list the capability checks. Saying it here makes a typo a sentence
            // about the file rather than a refusal at start-up with the seed half applied.
            foreach (var criterion in process.Criteria)
            {
                if (!ApprovalAttributes.Of(process.Subject)
                        .Contains(criterion.Attribute, StringComparer.Ordinal))
                {
                    return Result.Fail<SeedDocument>(SeedErrors.UnknownReference(
                        $"approvalProcess {process.Alias} criterion", criterion.Attribute));
                }
            }

            foreach (var step in process.Steps)
            {
                // A named approver with no name, or the submitter's manager with one, are both
                // a step nobody can resolve at the moment somebody needs it approved.
                var needsApprover = step.Kind is not ApproverKind.SubmittersManager;

                if (needsApprover != (step.Approver is { Length: > 0 }))
                {
                    return Result.Fail<SeedDocument>(SeedErrors.OutOfRange(
                        process.Alias,
                        $"step '{step.Label}'",
                        needsApprover ? "given an approver" : "given no approver"));
                }
            }
        }

        foreach (var plan in data.Plans)
        {
            if (!CustomValues.IsUsableName(plan.Name))
            {
                return Result.Fail<SeedDocument>(SeedErrors.NameIsNotUsable("plan", plan.Name));
            }

            if (!periods.Contains(plan.Period))
            {
                return Result.Fail<SeedDocument>(
                    SeedErrors.UnknownReference("plan " + plan.Alias, plan.Period));
            }

            // The subject is looked for in the collection its kind names — the schema's own
            // CHECK ties the two together, and a plan about an account that is really an
            // opportunity is a foreign-key violation rather than a sentence about the file.
            var subjects = plan.Kind is PlanKind.Account ? accounts : deals;

            if (!subjects.Contains(plan.Subject))
            {
                return Result.Fail<SeedDocument>(
                    SeedErrors.UnknownReference("plan " + plan.Alias, plan.Subject));
            }
        }

        foreach (var record in data.Records)
        {
            if (!objects.Contains(record.Target))
            {
                return Result.Fail<SeedDocument>(
                    SeedErrors.UnknownReference("record " + record.Alias, record.Target));
            }
        }

        return Result.Ok(document);
    }

    /// <summary>Checks one collection's size and its aliases, and collects them.</summary>
    private static Error? Collect<T>(
        string collection,
        IReadOnlyList<T> items,
        Func<T, string> alias,
        HashSet<string> into)
    {
        if (items.Count > SeedLimits.MaxItems)
        {
            return SeedErrors.TooMany(collection, items.Count);
        }

        foreach (var item in items)
        {
            var name = alias(item);

            if (string.IsNullOrWhiteSpace(name))
            {
                return SeedErrors.BadAlias(collection, string.Empty, "an alias is required.");
            }

            if (name.Length > SeedLimits.MaxAlias)
            {
                return SeedErrors.BadAlias(
                    collection, name, $"it is longer than {SeedLimits.MaxAlias} characters.");
            }

            if (!into.Add(name))
            {
                return SeedErrors.BadAlias(collection, name, "it is declared twice.");
            }
        }

        return null;
    }
}
