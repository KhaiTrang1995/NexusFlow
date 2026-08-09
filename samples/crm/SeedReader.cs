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
                Reports = metadata.Reports ?? [],
                ValidationRules = metadata.ValidationRules ?? [],
                ListViews = metadata.ListViews ?? [],
                RollUps = metadata.RollUps ?? [],
                Formulas = metadata.Formulas ?? [],
                Dashboards = metadata.Dashboards ?? [],
                Connectors = metadata.Connectors ?? [],
                Labels = metadata.Labels ?? [],
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
                // Each plan's three collections too, for the reason a quote's lines are done
                // here: a plan written without them reaches Validate with nulls, and the checks
                // below would be a null-reference exception rather than a sentence about a line.
                Plans =
                [
                    .. (data.Plans ?? []).Select(plan => plan with
                    {
                        Objectives = plan.Objectives ?? [],
                        Steps = plan.Steps ?? [],
                        Risks = plan.Risks ?? [],
                    }),
                ],
                Links = data.Links ?? [],
            },
        };
    }

    /// <summary>A document saying nothing, which every omitted section becomes.</summary>
    private static class Empty
    {
        public static readonly SeedMetadata Metadata =
            new([], [], [], [], [], [], [], [], [], [], [], [], [], [], [], [], [], [], [], [], [], []);

        public static readonly SeedData Data = new([], [], [], [], [], [], [], [], [], []);
    }

    /// <summary>Checks a document against the limits and against itself.</summary>
    /// <param name="document">What was read.</param>
    /// <returns>The same document, or the first thing wrong with it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    public static Result<SeedDocument> Validate(SeedDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (string.IsNullOrWhiteSpace(document.Tenant))
        {
            return Result.Fail<SeedDocument>(
                SeedErrors.BadAlias("document", document.Tenant ?? string.Empty, "a tenant is required."));
        }

        var metadata = document.Metadata;
        var data = document.Data;
        var aliases = new SeedAliases();

        // ORDER IS THE CONTRACT. Every check below returns the first thing wrong and `??` stops
        // there, so a document with two mistakes reports the same one it has always reported.
        // Several of these also fill the sets a later one reads — `Collected` gathers the
        // aliases, `Stages` and `ReportingLine` add what only they can see — so this is a
        // sequence, not a set of independent rules that happen to be written in an order.
        var error =
            Collected(metadata, data, aliases)
            ?? Stages(metadata, aliases)
            ?? Objects(metadata)
            ?? Fields(metadata, aliases)
            ?? Relationships(metadata, aliases)
            ?? Contacts(data, aliases)
            ?? Opportunities(data, aliases)
            ?? Leads(data)
            ?? ReportingLine(metadata, aliases)
            ?? Periods(metadata, aliases)
            ?? Strategy(metadata, aliases)
            ?? Quotas(metadata, aliases)
            ?? SimpleNames(metadata)
            ?? SlaPolicies(metadata)
            ?? BusinessHours(metadata)
            ?? Campaigns(metadata)
            ?? Activities(data, aliases)
            ?? Quotes(data, aliases)
            ?? Orders(data, aliases)
            ?? ApprovalProcesses(metadata)
            ?? Reports(metadata)
            ?? Declarations(metadata, aliases.Objects, aliases.Fields, aliases.Relationships, aliases.Reports)
            ?? Plans(data, aliases)
            ?? PlanTree(data.Plans)
            ?? Records(data, aliases)
            ?? Links(data, aliases);

        return error is null ? Result.Ok(document) : Result.Fail<SeedDocument>(error);
    }

    /// <summary>Every alias in the document, each unique within its collection.</summary>
    /// <param name="metadata">What the tenant is configured to have.</param>
    /// <param name="data">What the tenant holds.</param>
    /// <param name="aliases">Filled with what was collected.</param>
    /// <returns>The first thing wrong, or null.</returns>
    private static Error? Collected(SeedMetadata metadata, SeedData data, SeedAliases aliases) =>
        Collect("objects", metadata.Objects, item => item.Alias, aliases.Objects)
        ?? Collect("processes", metadata.Processes, item => item.Alias, [])
        ?? Collect("accounts", data.Accounts, item => item.Alias, aliases.Accounts)
        ?? Collect("contacts", data.Contacts, item => item.Alias, aliases.Contacts)
        ?? Collect("periods", metadata.Periods, item => item.Alias, aliases.Periods)
        ?? Collect("fields", metadata.Fields, item => item.Alias, aliases.Fields)
        ?? Collect("relationships", metadata.Relationships, item => item.Alias, aliases.Relationships)
        ?? Collect("opportunities", data.Opportunities, item => item.Alias, aliases.Deals)
        ?? Collect("leads", data.Leads, item => item.Alias, aliases.Leads)
        ?? Collect("records", data.Records, item => item.Alias, aliases.Records)
        ?? Collect("kpis", metadata.Kpis, item => item.Alias, [])
        ?? Collect("territories", metadata.Territories, item => item.Alias, [])
        ?? Collect("slaPolicies", metadata.SlaPolicies, item => item.Alias, [])
        ?? Collect("campaigns", metadata.Campaigns, item => item.Alias, [])
        ?? Collect("approvalProcesses", metadata.ApprovalProcesses, item => item.Alias, [])
        ?? Collect("reports", metadata.Reports, item => item.Alias, aliases.Reports)
        ?? Collect("activities", data.Activities, item => item.Alias, [])
        ?? Collect("quotes", data.Quotes, item => item.Alias, aliases.Quotes)
        ?? Collect("orders", data.Orders, item => item.Alias, [])
        ?? Collect("plans", data.Plans, item => item.Alias, aliases.Plans)
        ?? Collect("validationRules", metadata.ValidationRules, item => item.Alias, [])
        ?? Collect("listViews", metadata.ListViews, item => item.Alias, [])
        ?? Collect("rollUps", metadata.RollUps, item => item.Alias, [])
        ?? Collect("formulas", metadata.Formulas, item => item.Alias, [])
        ?? Collect("dashboards", metadata.Dashboards, item => item.Alias, [])
        ?? Collect("connectors", metadata.Connectors, item => item.Alias, [])
        ?? Collect("links", data.Links, item => item.Alias, []);

    /// <summary>Each process has stages, named once, and transitions between two of them.</summary>
    /// <param name="metadata">What the tenant is configured to have.</param>
    /// <param name="aliases">Filled with <c>process:stage</c> for every stage declared.</param>
    /// <returns>The first thing wrong, or null.</returns>
    private static Error? Stages(SeedMetadata metadata, SeedAliases aliases)
    {
        foreach (var process in metadata.Processes)
        {
            if (OneProcess(process, aliases) is { } bad)
            {
                return bad;
            }
        }

        return null;
    }

    /// <summary>One process: at least one stage, each named once, then its transitions.</summary>
    /// <param name="process">The process.</param>
    /// <param name="aliases">Filled with <c>process:stage</c> for every stage it declares.</param>
    /// <returns>The first thing wrong, or null.</returns>
    private static Error? OneProcess(SeedProcess process, SeedAliases aliases)
    {
        if (process.Stages.Count == 0)
        {
            return SeedErrors.OutOfRange(process.Alias, "stages", "at least one");
        }

        var named = new HashSet<string>(StringComparer.Ordinal);

        foreach (var stage in process.Stages)
        {
            if (!named.Add(stage.Name))
            {
                return SeedErrors.BadAlias("stages", stage.Name, "it is declared twice.");
            }

            aliases.Stages.Add(process.Alias + ":" + stage.Name);
        }

        return Transitions(process, named);
    }

    /// <summary>Every transition runs between two different stages the process declares.</summary>
    /// <param name="process">The process.</param>
    /// <param name="named">The stage names it declared, which the ends are looked up in.</param>
    /// <returns>The first thing wrong, or null.</returns>
    private static Error? Transitions(SeedProcess process, HashSet<string> named)
    {
        foreach (var transition in process.Transitions)
        {
            if (!named.Contains(transition.From))
            {
                return SeedErrors.UnknownReference(process.Alias + " transition", transition.From);
            }

            if (!named.Contains(transition.To))
            {
                return SeedErrors.UnknownReference(process.Alias + " transition", transition.To);
            }

            if (string.Equals(transition.From, transition.To, StringComparison.Ordinal))
            {
                return SeedErrors.OutOfRange(
                    process.Alias, "a transition", "between two different stages");
            }
        }

        return null;
    }

    /// <summary>Every custom object is named something a column can be called.</summary>
    /// <param name="metadata">What the tenant is configured to have.</param>
    /// <returns>The first thing wrong, or null.</returns>
    private static Error? Objects(SeedMetadata metadata)
    {
        foreach (var declared in metadata.Objects)
        {
            if (!CustomValues.IsUsableName(declared.Name))
            {
                return SeedErrors.NameIsNotUsable("object", declared.Name);
            }
        }

        return null;
    }

    /// <summary>Every custom field has one owner, a usable name and usable options.</summary>
    /// <param name="metadata">What the tenant is configured to have.</param>
    /// <param name="aliases">The objects a field may be targeted at.</param>
    /// <returns>The first thing wrong, or null.</returns>
    private static Error? Fields(SeedMetadata metadata, SeedAliases aliases)
    {
        foreach (var field in metadata.Fields)
        {
            // Exactly one owner. `DefineField` says the same thing and the capability refuses
            // the rest; saying it here is what makes the refusal name the file's line.
            if ((field.Entity is null) == (field.Target is null))
            {
                return SeedErrors.FieldOwnerAmbiguous(field.Alias);
            }

            if (!CustomValues.IsUsableName(field.Name))
            {
                return SeedErrors.NameIsNotUsable("field", field.Name);
            }

            // The same rule the capability applies, checked here so a file's mistake is a
            // sentence about the file rather than a check-constraint violation from migration
            // 0006 with the option's table name in it.
            foreach (var option in field.Options ?? [])
            {
                if (!CustomValues.IsUsableName(option.Value))
                {
                    return SeedErrors.NameIsNotUsable("option", option.Value);
                }
            }

            if (field.Target is { } owner && !aliases.Objects.Contains(owner))
            {
                return SeedErrors.UnknownReference("field " + field.Alias, owner);
            }
        }

        return null;
    }

    /// <summary>Every relationship is named usably and joins two declared objects.</summary>
    /// <param name="metadata">What the tenant is configured to have.</param>
    /// <param name="aliases">The objects an edge may join.</param>
    /// <returns>The first thing wrong, or null.</returns>
    private static Error? Relationships(SeedMetadata metadata, SeedAliases aliases)
    {
        foreach (var relationship in metadata.Relationships)
        {
            if (!CustomValues.IsUsableName(relationship.Name))
            {
                return SeedErrors.NameIsNotUsable("relationship", relationship.Name);
            }

            if (!aliases.Objects.Contains(relationship.From))
            {
                return SeedErrors.UnknownReference(
                    "relationship " + relationship.Alias, relationship.From);
            }

            if (!aliases.Objects.Contains(relationship.To))
            {
                return SeedErrors.UnknownReference(
                    "relationship " + relationship.Alias, relationship.To);
            }
        }

        return null;
    }

    /// <summary>Every contact belongs to an account the file declares.</summary>
    /// <param name="data">What the tenant holds.</param>
    /// <param name="aliases">The accounts declared.</param>
    /// <returns>The first thing wrong, or null.</returns>
    private static Error? Contacts(SeedData data, SeedAliases aliases)
    {
        foreach (var contact in data.Contacts)
        {
            if (!aliases.Accounts.Contains(contact.Account))
            {
                return SeedErrors.UnknownReference("contact " + contact.Alias, contact.Account);
            }
        }

        return null;
    }

    /// <summary>Every opportunity names a real account, contact and stage, and is in range.</summary>
    /// <param name="data">What the tenant holds.</param>
    /// <param name="aliases">The accounts, contacts and stages declared.</param>
    /// <returns>The first thing wrong, or null.</returns>
    private static Error? Opportunities(SeedData data, SeedAliases aliases)
    {
        foreach (var opportunity in data.Opportunities)
        {
            if (!aliases.Accounts.Contains(opportunity.Account))
            {
                return SeedErrors.UnknownReference(
                    "opportunity " + opportunity.Alias, opportunity.Account);
            }

            if (!aliases.Contacts.Contains(opportunity.PrimaryContact))
            {
                return SeedErrors.UnknownReference(
                    "opportunity " + opportunity.Alias, opportunity.PrimaryContact);
            }

            if (!aliases.Stages.Contains(opportunity.Stage))
            {
                return SeedErrors.UnknownReference(
                    "opportunity " + opportunity.Alias, opportunity.Stage);
            }

            if (opportunity.Probability is < 0 or > 100)
            {
                return SeedErrors.OutOfRange(opportunity.Alias, "probability", "between 0 and 100");
            }

            if (opportunity.Amount < 0)
            {
                return SeedErrors.OutOfRange(opportunity.Alias, "amount", "zero or more");
            }
        }

        return null;
    }

    /// <summary>Every lead scores in range and is not already converted.</summary>
    /// <param name="data">What the tenant holds.</param>
    /// <returns>The first thing wrong, or null.</returns>
    private static Error? Leads(SeedData data)
    {
        foreach (var lead in data.Leads)
        {
            if (lead.Score is < 0 or > 100)
            {
                return SeedErrors.OutOfRange(lead.Alias, "score", "between 0 and 100");
            }

            // A converted lead is four columns and a saga, and three of them are not in this
            // file's vocabulary. Accepting the status would write a row the schema's own CHECK
            // refuses — a message about a constraint rather than about the word.
            if (lead.Status is LeadStatus.Converted)
            {
                return SeedErrors.OutOfRange(
                    lead.Alias, "status", "anything but Converted; conversion is a flow, not a seed");
            }
        }

        return null;
    }

    /// <summary>Every org member is named once and reports to somebody else the file has.</summary>
    /// <param name="metadata">What the tenant is configured to have.</param>
    /// <param name="aliases">Filled with the user ids declared.</param>
    /// <returns>The first thing wrong, or null.</returns>
    /// <remarks>
    /// Two passes, because a quota and an activity both name a person and neither should be able
    /// to name one the tenant does not have: the whole line has to exist before any edge in it
    /// can be checked, or a manager declared below their report would read as unknown.
    /// </remarks>
    private static Error? ReportingLine(SeedMetadata metadata, SeedAliases aliases)
    {
        foreach (var member in metadata.OrgMembers)
        {
            if (string.IsNullOrWhiteSpace(member.UserId))
            {
                return SeedErrors.BadAlias("orgMembers", string.Empty, "a userId is required.");
            }

            if (!aliases.People.Add(member.UserId))
            {
                return SeedErrors.BadAlias("orgMembers", member.UserId, "it is declared twice.");
            }
        }

        foreach (var member in metadata.OrgMembers)
        {
            if (member.ReportsTo is { } manager && !aliases.People.Contains(manager))
            {
                return SeedErrors.UnknownReference("orgMember " + member.UserId, manager);
            }

            // A line that loops has no top, and every walk up it runs until something stops it.
            if (string.Equals(member.ReportsTo, member.UserId, StringComparison.Ordinal))
            {
                return SeedErrors.OutOfRange(member.UserId, "reportsTo", "somebody else");
            }
        }

        return null;
    }

    /// <summary>Every period is named usably, ends after it starts, and nests in a real one.</summary>
    /// <param name="metadata">What the tenant is configured to have.</param>
    /// <param name="aliases">The periods declared.</param>
    /// <returns>The first thing wrong, or null.</returns>
    private static Error? Periods(SeedMetadata metadata, SeedAliases aliases)
    {
        foreach (var period in metadata.Periods)
        {
            if (!CustomValues.IsUsableName(period.Name))
            {
                return SeedErrors.NameIsNotUsable("period", period.Name);
            }

            if (period.EndsOn < period.StartsOn)
            {
                return SeedErrors.OutOfRange(period.Alias, "endsOn", "on or after startsOn");
            }

            if (period.Parent is { } parent && !aliases.Periods.Contains(parent))
            {
                return SeedErrors.UnknownReference("period " + period.Alias, parent);
            }
        }

        return null;
    }

    /// <summary>Every strategy is written against a period the file declares.</summary>
    /// <param name="metadata">What the tenant is configured to have.</param>
    /// <param name="aliases">The periods declared.</param>
    /// <returns>The first thing wrong, or null.</returns>
    private static Error? Strategy(SeedMetadata metadata, SeedAliases aliases)
    {
        foreach (var strategy in metadata.Strategy)
        {
            if (!aliases.Periods.Contains(strategy.Period))
            {
                return SeedErrors.UnknownReference("strategy", strategy.Period);
            }
        }

        return null;
    }

    /// <summary>Every quota names a real period and person, and ramps by a real fraction.</summary>
    /// <param name="metadata">What the tenant is configured to have.</param>
    /// <param name="aliases">The periods and people declared.</param>
    /// <returns>The first thing wrong, or null.</returns>
    private static Error? Quotas(SeedMetadata metadata, SeedAliases aliases)
    {
        foreach (var quota in metadata.Quotas)
        {
            if (!aliases.Periods.Contains(quota.Period))
            {
                return SeedErrors.UnknownReference("quota for " + quota.UserId, quota.Period);
            }

            if (aliases.People.Count > 0 && !aliases.People.Contains(quota.UserId))
            {
                return SeedErrors.UnknownReference("quota", quota.UserId);
            }

            if (quota.RampFactor is <= 0 or > 1)
            {
                return SeedErrors.OutOfRange(
                    quota.UserId, "rampFactor", "above zero and at most one");
            }
        }

        return null;
    }

    /// <summary>The kinds whose only rule is that their name can be a column.</summary>
    /// <param name="metadata">What the tenant is configured to have.</param>
    /// <returns>The first thing wrong, or null.</returns>
    private static Error? SimpleNames(SeedMetadata metadata)
    {
        foreach (var named in metadata.Kpis)
        {
            if (!CustomValues.IsUsableName(named.Name))
            {
                return SeedErrors.NameIsNotUsable("kpi", named.Name);
            }
        }

        foreach (var territory in metadata.Territories)
        {
            if (!CustomValues.IsUsableName(territory.Name))
            {
                return SeedErrors.NameIsNotUsable("territory", territory.Name);
            }
        }

        return null;
    }

    /// <summary>Every SLA policy is named usably and both its clocks run forwards.</summary>
    /// <param name="metadata">What the tenant is configured to have.</param>
    /// <returns>The first thing wrong, or null.</returns>
    private static Error? SlaPolicies(SeedMetadata metadata)
    {
        foreach (var policy in metadata.SlaPolicies)
        {
            if (!CustomValues.IsUsableName(policy.Name))
            {
                return SeedErrors.NameIsNotUsable("slaPolicy", policy.Name);
            }

            if (policy.FirstResponseMinutes <= 0 || policy.ResolutionMinutes <= 0)
            {
                return SeedErrors.OutOfRange(policy.Alias, "its clocks", "above zero");
            }
        }

        return null;
    }

    /// <summary>Every business day is a real day and closes after it opens.</summary>
    /// <param name="metadata">What the tenant is configured to have.</param>
    /// <returns>The first thing wrong, or null.</returns>
    private static Error? BusinessHours(SeedMetadata metadata)
    {
        foreach (var hours in metadata.BusinessHours)
        {
            if (hours.DayOfWeek is < 0 or > 6)
            {
                return SeedErrors.OutOfRange("businessHours", "dayOfWeek", "between 0 and 6");
            }

            if (hours.ClosesAt <= hours.OpensAt)
            {
                return SeedErrors.OutOfRange("businessHours", "closesAt", "after opensAt");
            }
        }

        return null;
    }

    /// <summary>Every campaign is named usably and ends on or after it starts.</summary>
    /// <param name="metadata">What the tenant is configured to have.</param>
    /// <returns>The first thing wrong, or null.</returns>
    private static Error? Campaigns(SeedMetadata metadata)
    {
        foreach (var campaign in metadata.Campaigns)
        {
            if (!CustomValues.IsUsableName(campaign.Name))
            {
                return SeedErrors.NameIsNotUsable("campaign", campaign.Name);
            }

            if (campaign.EndsOn < campaign.StartsOn)
            {
                return SeedErrors.OutOfRange(campaign.Alias, "endsOn", "on or after startsOn");
            }
        }

        return null;
    }

    /// <summary>Every activity hangs off a record of the kind it names, and has not finished.</summary>
    /// <param name="data">What the tenant holds.</param>
    /// <param name="aliases">The accounts, contacts, leads and deals declared.</param>
    /// <returns>The first thing wrong, or null.</returns>
    /// <remarks>
    /// An activity's parent is an alias in whichever collection its kind names. Resolved here so
    /// a typo is a refusal rather than a foreign key the trigger of migration 0001 refuses with a
    /// message about polymorphic integrity.
    /// </remarks>
    private static Error? Activities(SeedData data, SeedAliases aliases)
    {
        foreach (var activity in data.Activities)
        {
            var known = activity.RelatesToKind switch
            {
                EntityKind.Account => aliases.Accounts,
                EntityKind.Contact => aliases.Contacts,
                EntityKind.Lead => aliases.Leads,
                _ => aliases.Deals,
            };

            if (!known.Contains(activity.RelatesTo))
            {
                return SeedErrors.UnknownReference("activity " + activity.Alias, activity.RelatesTo);
            }

            // Completed is completed_at, and the schema's CHECK ties the two together. A seed
            // that set the status without the instant would be refused by the constraint.
            if (activity.Status is ActivityStatus.Completed)
            {
                return SeedErrors.OutOfRange(
                    activity.Alias,
                    "status",
                    "anything but Completed; a seed starts work, it does not finish it");
            }
        }

        return null;
    }

    /// <summary>Every quote hangs off a deal, has lines that price, and discounts within them.</summary>
    /// <param name="data">What the tenant holds.</param>
    /// <param name="aliases">The deals declared.</param>
    /// <returns>The first thing wrong, or null.</returns>
    private static Error? Quotes(SeedData data, SeedAliases aliases)
    {
        foreach (var quote in data.Quotes)
        {
            if (!aliases.Deals.Contains(quote.Opportunity))
            {
                return SeedErrors.UnknownReference("quote " + quote.Alias, quote.Opportunity);
            }

            // A quote is its lines. One with none has a total nobody can reconcile and a builder
            // with nothing to draw, which is what every seeded quote was before they existed.
            if (quote.Lines.Count == 0)
            {
                return SeedErrors.OutOfRange(quote.Alias, "lines", "at least one");
            }

            foreach (var line in quote.Lines)
            {
                if (line.Quantity <= 0 || line.UnitPrice < 0)
                {
                    return SeedErrors.OutOfRange(
                        quote.Alias,
                        "line '" + line.Sku + "'",
                        "a positive quantity and a price that is not negative");
                }
            }

            // Against the sum, because that is what the subtotal will be. A discount larger than
            // it makes a negative total, which the schema takes without complaint.
            if (quote.Discount > quote.Lines.Sum(line => line.Quantity * line.UnitPrice))
            {
                return SeedErrors.OutOfRange(
                    quote.Alias, "discount", "no more than the lines add up to");
            }
        }

        return null;
    }

    /// <summary>Every order comes from a quote and belongs to an account.</summary>
    /// <param name="data">What the tenant holds.</param>
    /// <param name="aliases">The quotes and accounts declared.</param>
    /// <returns>The first thing wrong, or null.</returns>
    private static Error? Orders(SeedData data, SeedAliases aliases)
    {
        foreach (var order in data.Orders)
        {
            if (!aliases.Quotes.Contains(order.Quote))
            {
                return SeedErrors.UnknownReference("order " + order.Alias, order.Quote);
            }

            if (!aliases.Accounts.Contains(order.Account))
            {
                return SeedErrors.UnknownReference("order " + order.Alias, order.Account);
            }
        }

        return null;
    }

    /// <summary>Every approval process has steps, known criteria, and resolvable approvers.</summary>
    /// <param name="metadata">What the tenant is configured to have.</param>
    /// <returns>The first thing wrong, or null.</returns>
    private static Error? ApprovalProcesses(SeedMetadata metadata)
    {
        foreach (var process in metadata.ApprovalProcesses)
        {
            if (!CustomValues.IsUsableName(process.Name))
            {
                return SeedErrors.NameIsNotUsable("approvalProcess", process.Name);
            }

            if (process.Steps.Count == 0)
            {
                return SeedErrors.OutOfRange(process.Alias, "steps", "at least one");
            }

            if ((Criteria(process) ?? ApproverSteps(process)) is { } bad)
            {
                return bad;
            }
        }

        return null;
    }

    /// <summary>Every criterion names an attribute the subject actually has.</summary>
    /// <param name="process">The approval process.</param>
    /// <returns>The first thing wrong, or null.</returns>
    /// <remarks>
    /// The same closed list the capability checks. Saying it here makes a typo a sentence about
    /// the file rather than a refusal at start-up with the seed half applied.
    /// </remarks>
    private static Error? Criteria(SeedApprovalProcess process)
    {
        foreach (var criterion in process.Criteria)
        {
            if (!ApprovalAttributes.Of(process.Subject)
                    .Contains(criterion.Attribute, StringComparer.Ordinal))
            {
                return SeedErrors.UnknownReference(
                    $"approvalProcess {process.Alias} criterion", criterion.Attribute);
            }
        }

        return null;
    }

    /// <summary>Every step names an approver exactly when its kind needs one.</summary>
    /// <param name="process">The approval process.</param>
    /// <returns>The first thing wrong, or null.</returns>
    private static Error? ApproverSteps(SeedApprovalProcess process)
    {
        foreach (var step in process.Steps)
        {
            // A named approver with no name, or the submitter's manager with one, are both
            // a step nobody can resolve at the moment somebody needs it approved.
            var needsApprover = step.Kind is not ApproverKind.SubmittersManager;

            if (needsApprover != (step.Approver is { Length: > 0 }))
            {
                return SeedErrors.OutOfRange(
                    process.Alias,
                    $"step '{step.Label}'",
                    needsApprover ? "given an approver" : "given no approver");
            }
        }

        return null;
    }

    /// <summary>Every report names a dimension and a measure its source has.</summary>
    /// <param name="metadata">What the tenant is configured to have.</param>
    /// <returns>The first thing wrong, or null.</returns>
    /// <remarks>
    /// The same closed lists the capability checks. Reading them here makes a typo a sentence
    /// about the file rather than a report that is saved, runs, and groups everything under null
    /// — which reads as a data problem and is not.
    /// </remarks>
    private static Error? Reports(SeedMetadata metadata)
    {
        foreach (var report in metadata.Reports)
        {
            if (!CustomValues.IsUsableName(report.Name))
            {
                return SeedErrors.NameIsNotUsable("report", report.Name);
            }

            if (!ReportVocabulary.Dimensions(report.Source)
                    .Contains(report.Dimension, StringComparer.Ordinal))
            {
                return SeedErrors.UnknownReference(
                    $"report {report.Alias} dimension", report.Dimension);
            }

            // Count is of rows and takes no field; everything else needs one. Both halves fail at
            // the moment somebody runs it, which is the worst time to find out.
            if ((report.Measure == ReportMeasure.Count) != (report.MeasureOf is null))
            {
                return SeedErrors.OutOfRange(
                    report.Alias,
                    "measure " + report.Measure,
                    report.Measure == ReportMeasure.Count ? "given no field" : "given a field");
            }

            if (report.MeasureOf is { } field
                && !ReportVocabulary.Measures(report.Source).Contains(field, StringComparer.Ordinal))
            {
                return SeedErrors.UnknownReference($"report {report.Alias} measure", field);
            }
        }

        return null;
    }

    /// <summary>Every plan is named usably, sits in a period, and commits about something real.</summary>
    /// <param name="data">What the tenant holds.</param>
    /// <param name="aliases">The periods, accounts, deals and plans declared.</param>
    /// <returns>The first thing wrong, or null.</returns>
    private static Error? Plans(SeedData data, SeedAliases aliases)
    {
        foreach (var plan in data.Plans)
        {
            if (!CustomValues.IsUsableName(plan.Name))
            {
                return SeedErrors.NameIsNotUsable("plan", plan.Name);
            }

            if (!aliases.Periods.Contains(plan.Period))
            {
                return SeedErrors.UnknownReference("plan " + plan.Alias, plan.Period);
            }

            if (Commitment(plan) is { } badPlan)
            {
                return badPlan;
            }

            // The subject is looked for in the collection its kind names — the schema's own
            // CHECK ties the two together, and a plan about an account that is really an
            // opportunity is a foreign-key violation rather than a sentence about the file.
            // A demand plan is about a channel and has no subject at all, which Commitment has
            // already established by this point.
            if (plan.Subject is { } subject
                && !(plan.Kind is PlanKind.Account ? aliases.Accounts : aliases.Deals).Contains(subject))
            {
                return SeedErrors.UnknownReference("plan " + plan.Alias, subject);
            }

            if (plan.Parent is { } above && !aliases.Plans.Contains(above))
            {
                return SeedErrors.UnknownReference("plan " + plan.Alias, above);
            }
        }

        return null;
    }

    /// <summary>Every custom record is against an object the file declares.</summary>
    /// <param name="data">What the tenant holds.</param>
    /// <param name="aliases">The objects declared.</param>
    /// <returns>The first thing wrong, or null.</returns>
    private static Error? Records(SeedData data, SeedAliases aliases)
    {
        foreach (var record in data.Records)
        {
            if (!aliases.Objects.Contains(record.Target))
            {
                return SeedErrors.UnknownReference("record " + record.Alias, record.Target);
            }
        }

        return null;
    }

    /// <summary>Every link is of a declared relationship and joins two declared records.</summary>
    /// <param name="data">What the tenant holds.</param>
    /// <param name="aliases">The relationships and records declared.</param>
    /// <returns>The first thing wrong, or null.</returns>
    private static Error? Links(SeedData data, SeedAliases aliases)
    {
        foreach (var link in data.Links)
        {
            if (!aliases.Relationships.Contains(link.Relationship))
            {
                return SeedErrors.UnknownReference("link " + link.Alias, link.Relationship);
            }

            foreach (var end in (string[])[link.From, link.To])
            {
                if (!aliases.Records.Contains(end))
                {
                    return SeedErrors.UnknownReference("link " + link.Alias, end);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The seven configuration kinds this file declares by alias, checked against what it declares.
    /// </summary>
    /// <param name="metadata">What the tenant is configured to have.</param>
    /// <param name="objects">The custom objects the file declares.</param>
    /// <param name="fields">The custom fields it declares.</param>
    /// <param name="relationships">The edges it declares.</param>
    /// <param name="reports">The saved reports it declares.</param>
    /// <returns>The first thing wrong, or null.</returns>
    /// <remarks>
    /// <strong>Each check here is one the capability makes, restated so the refusal names a line
    /// of the file.</strong> The alternative is not "no check" — it is the same refusal arriving
    /// at start-up from a store, naming a column and a uuid the operator has never seen, with
    /// half the tenant already written.
    /// </remarks>
    private static Error? Declarations(
        SeedMetadata metadata,
        HashSet<string> objects,
        HashSet<string> fields,
        HashSet<string> relationships,
        HashSet<string> reports)
    {
        foreach (var rule in metadata.ValidationRules)
        {
            if (!CustomValues.IsUsableName(rule.Name))
            {
                return SeedErrors.NameIsNotUsable("validationRule", rule.Name);
            }

            // Exactly one owner, the same rule a field has and for the same reason: the row
            // carries applies_to and object_id, and one with both is a rule two readers claim.
            if ((rule.Entity is null) == (rule.Target is null))
            {
                return SeedErrors.FieldOwnerAmbiguous(rule.Alias);
            }

            if (rule.Target is { } owner && !objects.Contains(owner))
            {
                return SeedErrors.UnknownReference("validationRule " + rule.Alias, owner);
            }
        }

        foreach (var view in metadata.ListViews)
        {
            if (!CustomValues.IsUsableName(view.Name))
            {
                return SeedErrors.NameIsNotUsable("listView", view.Name);
            }

            if (!objects.Contains(view.Target))
            {
                return SeedErrors.UnknownReference("listView " + view.Alias, view.Target);
            }

            if (view.Limit is < 1 or > QueryLimits.Max)
            {
                return SeedErrors.OutOfRange(
                    view.Alias, "limit", $"between 1 and {QueryLimits.Max}");
            }

            if (view.Filter is { Criteria.Count: > QueryLimits.MaxCriteria } tooMany)
            {
                return SeedErrors.OutOfRange(
                    view.Alias,
                    $"filter of {tooMany.Criteria.Count} criteria",
                    $"no more than {QueryLimits.MaxCriteria}");
            }
        }

        foreach (var rollUp in metadata.RollUps)
        {
            // Count is of rows and reads no field; the other four need one. Both halves are an
            // aggregate that is silently always empty, which nobody investigates.
            if ((rollUp.Aggregate == RollupAggregate.Count) != (rollUp.SourceField is null))
            {
                return SeedErrors.OutOfRange(
                    rollUp.Alias,
                    "aggregate " + rollUp.Aggregate,
                    rollUp.Aggregate == RollupAggregate.Count
                        ? "given no sourceField"
                        : "given a sourceField");
            }

            if (!relationships.Contains(rollUp.Relationship))
            {
                return SeedErrors.UnknownReference("rollUp " + rollUp.Alias, rollUp.Relationship);
            }

            foreach (var named in (string?[])[rollUp.Field, rollUp.SourceField])
            {
                if (named is { } alias && !fields.Contains(alias))
                {
                    return SeedErrors.UnknownReference("rollUp " + rollUp.Alias, alias);
                }
            }
        }

        foreach (var formula in metadata.Formulas)
        {
            if ((formula.Right is null) == (formula.Literal is null))
            {
                return SeedErrors.OutOfRange(
                    formula.Alias, "right operand", "a field or a constant, and not both");
            }

            if (!fields.Contains(formula.Field))
            {
                return SeedErrors.UnknownReference("formula " + formula.Alias, formula.Field);
            }
        }

        foreach (var dashboard in metadata.Dashboards)
        {
            if (!CustomValues.IsUsableName(dashboard.Name))
            {
                return SeedErrors.NameIsNotUsable("dashboard", dashboard.Name);
            }

            // A dashboard of nothing is a screen with a heading on it, and the capability
            // refuses one for the same reason.
            if (dashboard.Reports.Count is 0)
            {
                return SeedErrors.OutOfRange(dashboard.Alias, "reports", "at least one");
            }

            if (dashboard.Reports.Count > ReportLimits.MaxTiles)
            {
                return SeedErrors.OutOfRange(
                    dashboard.Alias, "reports", $"no more than {ReportLimits.MaxTiles}");
            }

            foreach (var tile in dashboard.Reports)
            {
                if (!reports.Contains(tile))
                {
                    return SeedErrors.UnknownReference("dashboard " + dashboard.Alias, tile);
                }
            }
        }

        foreach (var connector in metadata.Connectors)
        {
            if (!CustomValues.IsUsableName(connector.Name))
            {
                return SeedErrors.NameIsNotUsable("connector", connector.Name);
            }

            // An absolute URL, because the endpoint is dialled rather than resolved against
            // anything. A relative one is a delivery that fails at the sweep, hours later.
            if (!Uri.TryCreate(connector.Endpoint, UriKind.Absolute, out _))
            {
                return SeedErrors.OutOfRange(connector.Alias, "endpoint", "an absolute URL");
            }
        }

        return Labels(metadata.Labels);
    }

    /// <summary>What a tenant renames, checked against the columns this build has.</summary>
    /// <param name="labels">What the file renames.</param>
    /// <returns>The first thing wrong, or null.</returns>
    private static Error? Labels(IReadOnlyList<SeedLabel> labels)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);

        foreach (var label in labels)
        {
            var what = label.Entity + (label.Column is { Length: > 0 } column ? "." + column : "");

            // The row is keyed by (kind, field), so two lines naming the same thing are one row
            // and the file's author only wrote one of the two names they think they set.
            if (!taken.Add(what))
            {
                return SeedErrors.BadAlias("labels", what, "it is renamed twice.");
            }

            if (label.Column is { Length: > 0 } named
                && !EntityColumns.Of(label.Entity).Contains(named, StringComparer.Ordinal))
            {
                return SeedErrors.UnknownReference("label " + what, named);
            }

            if (string.IsNullOrWhiteSpace(label.Label))
            {
                return SeedErrors.OutOfRange(what, "label", "something a person can read");
            }
        }

        return null;
    }

    /// <summary>What a plan of each kind must and must not carry.</summary>
    /// <param name="plan">The plan.</param>
    /// <returns>The first thing wrong, or null.</returns>
    /// <remarks>
    /// <strong>The same six agreements <c>CommitPlan</c> checks and the <c>plan</c> table's own
    /// <c>CHECK</c>s hold.</strong> Restated here because a seed that got one wrong would be a
    /// constraint violation at start-up naming <c>plan_check3</c>, and the operator would be
    /// looking at PostgreSQL's generated constraint name rather than at the line they wrote.
    /// </remarks>
    private static Error? Commitment(SeedPlan plan)
    {
        // Operation needs an activity kind and a count of it, and this file has a word for
        // neither. Writing one would be a row that is legal and that nothing can report against.
        // Portfolio is seedable now that a plan can name the one it rolls into.
        if (plan.Kind is PlanKind.Operation)
        {
            return SeedErrors.OutOfRange(
                plan.Alias,
                "kind " + plan.Kind,
                "Account, Opportunity, MarketingLead or Portfolio; a seed has no vocabulary for " +
                "the activity an Operation counts");
        }

        var demand = plan.Kind is PlanKind.MarketingLead;

        // The schema's own `(kind = 'Account') = (account_id IS NOT NULL)` and its opposite
        // number for an opportunity. A portfolio is about the plans underneath it rather than
        // about a row, so it has no subject either — and it is the second kind that has none,
        // which is why this cannot be written as "everything but demand".
        var about = plan.Kind is PlanKind.Account or PlanKind.Opportunity;

        if ((plan.Subject is not null) != about)
        {
            return SeedErrors.OutOfRange(
                plan.Alias,
                "kind " + plan.Kind,
                about ? "given a subject" : "given no subject");
        }

        foreach (var (value, what) in ((bool, string)[])
                 [(plan.Channel is not null, "channel"), (plan.TargetLeads is not null, "targetLeads")])
        {
            if (value != demand)
            {
                return SeedErrors.OutOfRange(
                    plan.Alias, "kind " + plan.Kind, (demand ? "given a " : "given no ") + what);
            }
        }

        foreach (var (value, what) in ((bool, string)[])
                 [(plan.TargetAmount is not null, "targetAmount"), (plan.Currency is not null, "currency")])
        {
            if (value == demand)
            {
                return SeedErrors.OutOfRange(
                    plan.Alias, "kind " + plan.Kind, (demand ? "given no " : "given a ") + what);
            }
        }

        // The five a lead can actually arrive from. A plan for a sixth is one no lead will ever
        // be counted against, so it reports nought for ever and reads as a marketing failure.
        if (plan.Channel is { } channel
            && !PlanningLimits.Channels.Contains(channel, StringComparer.Ordinal))
        {
            return SeedErrors.UnknownReference("plan " + plan.Alias + " channel", channel);
        }

        return plan.TargetAmount < 0 || plan.TargetLeads < 0
            ? SeedErrors.OutOfRange(plan.Alias, "its target", "zero or more")
            : null;
    }

    /// <summary>Whether any plan rolls up into itself.</summary>
    /// <param name="plans">Every plan the file declares, their parents already known to exist.</param>
    /// <returns>The first loop found, or null.</returns>
    /// <remarks>
    /// Walked upwards from each plan rather than coloured in one pass: the answer wanted is which
    /// line to point the operator at, and the plan the walk comes back to is that line.
    /// </remarks>
    private static Error? PlanTree(IReadOnlyList<SeedPlan> plans)
    {
        var above = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var plan in plans)
        {
            above[plan.Alias] = plan.Parent;
        }

        foreach (var plan in plans)
        {
            var walked = new HashSet<string>(StringComparer.Ordinal) { plan.Alias };

            // Every parent is an alias this file declares, checked before this runs, so the
            // lookup cannot miss and the walk ends at a root or at a repeat.
            for (var at = plan.Parent; at is not null; at = above[at])
            {
                if (!walked.Add(at))
                {
                    return SeedErrors.PlanTreeLoops(plan.Alias);
                }
            }
        }

        return null;
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

/// <summary>
/// The aliases a document declares, in the order the checks need them.
/// </summary>
/// <remarks>
/// One object rather than thirteen parameters, because most of these sets are filled by one
/// check and read by another several hundred lines later — passing only what each needed
/// meant thirteen names threaded through a signature that changed whenever a collection was
/// added. A set that is collected but read by nobody is still here on purpose: two items
/// sharing an alias share a derived id, and the second would silently be the first.
/// </remarks>
internal sealed class SeedAliases
{
    public HashSet<string> Objects { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Accounts { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Contacts { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Stages { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Periods { get; } = new(StringComparer.Ordinal);

    public HashSet<string> People { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Quotes { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Fields { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Relationships { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Reports { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Records { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Plans { get; } = new(StringComparer.Ordinal);

    /// <summary>Opportunity aliases, which a quote and a plan both refer to.</summary>
    public HashSet<string> Deals { get; } = new(StringComparer.Ordinal);

    /// <summary>Lead aliases, which an activity refers to.</summary>
    public HashSet<string> Leads { get; } = new(StringComparer.Ordinal);
}
