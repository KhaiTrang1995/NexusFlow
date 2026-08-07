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

        return Validate(document);
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

        // The four whose aliases nothing refers to. Still collected, because two items sharing
        // an alias share a derived id, and the second would silently be the first.
        var unreferenced =
            Collect("fields", metadata.Fields, item => item.Alias, [])
            ?? Collect("relationships", metadata.Relationships, item => item.Alias, [])
            ?? Collect("opportunities", data.Opportunities, item => item.Alias, [])
            ?? Collect("leads", data.Leads, item => item.Alias, [])
            ?? Collect("records", data.Records, item => item.Alias, []);

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
