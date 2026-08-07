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
    private readonly TimeProvider _clock;

    /// <summary>Creates the applier.</summary>
    /// <param name="seeds">Writes the built-in rows and the process.</param>
    /// <param name="schema">Writes the custom objects, fields, relationships and records.</param>
    /// <param name="clock">Supplies the instant every written row is stamped with.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public SeedApplier(SeedStore seeds, CustomSchemaStore schema, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(seeds);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(clock);

        _seeds = seeds;
        _schema = schema;
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
            outcome = outcome.And(await _seeds.WriteAccountAsync(
                tenant, SeedIds.For(tenant, "account", account.Alias), account, ct).ConfigureAwait(false));
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
}
