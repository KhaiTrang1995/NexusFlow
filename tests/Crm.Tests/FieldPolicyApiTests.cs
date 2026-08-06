using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// The four things an administrator can say about a field without a deployment: what a record may
/// not be, who may write it, that it must be unique, and that its changes are kept.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Each reuses a vocabulary that is already closed somewhere else</strong>, which is what
/// keeps them checkable. A validation rule's operators are <c>GuardOperator</c>, evaluated by the
/// same <c>ProcessRules.Holds</c> a transition guard uses; the field's permission is a scope
/// string the capability stance already understands. Neither is a new language.
/// </para>
/// <para>
/// <strong>A rule refuses when it holds.</strong> An administrator writes down what is wrong, not
/// the negation of everything that is right — and the message they wrote is what the caller
/// reads, which is the whole reason a rule beats a <c>CHECK</c> constraint.
/// </para>
/// </remarks>
public sealed class FieldPolicyApiTests
{
    private const string Objects = "/api/v1/crm/custom/objects";
    private const string Fields = "/api/v1/crm/custom/fields";
    private const string Records = "/api/v1/crm/custom/records";
    private const string Rules = "/api/v1/crm/custom/validation-rules";
    private const string EntityFields = "/api/v1/crm/custom/entity-fields";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A rule refuses the record that trips it, in the administrator's own words.</summary>
    [Fact]
    public async Task ARuleRefusesTheRecordThatTripsItWithTheMessageItWasGiven()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var site = await ObjectAsync(app, "site");
        await FieldAsync(app, new DefineField(
            null, site, "floor_area", "Floor area", CustomFieldType.Number, IsRequired: false));

        (await app.PostAsync(
            Rules,
            new DefineValidationRule(
                null, site, "too_large", "floor_area", GuardOperator.GreaterThan, "10000",
                "A site over 10,000 m² needs a survey before it can be recorded."),
            CrmTokens.NorthwindManager))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var refused = await app.PostAsync(
            Records,
            new CreateRecord(site, new Dictionary<string, string?> { ["floor_area"] = "12000" }),
            CrmTokens.Northwind);

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var problem = await Problem(refused);

        problem.GetProperty("code").GetString().ShouldBe("crm.custom_validation_failed");
        problem.GetProperty("detail").GetString().ShouldBe(
            "A site over 10,000 m² needs a survey before it can be recorded.",
            "the caller was shown this build's words instead of the administrator's, which is " +
            "the whole reason the rule carries a message.");
        problem.GetProperty("rule").GetString().ShouldBe("too_large");

        // And the rule is a rule rather than a wall: what does not trip it goes through.
        (await app.PostAsync(
            Records,
            new CreateRecord(site, new Dictionary<string, string?> { ["floor_area"] = "900" }),
            CrmTokens.Northwind))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>A rule naming a field nobody declared is refused when it is declared.</summary>
    /// <remarks>
    /// The property that makes rules worth having at all. A rule that silently never fires is
    /// worse than one that never existed, because somebody believes it is protecting them.
    /// </remarks>
    [Fact]
    public async Task ARuleNamingAnUndeclaredFieldIsRefusedAtDeclaration()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var site = await ObjectAsync(app, "site");

        var response = await app.PostAsync(
            Rules,
            new DefineValidationRule(
                null, site, "typo", "floor_arae", GuardOperator.GreaterThan, "1", "nope"),
            CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.custom_rule_field_not_declared");
    }

    /// <summary>An ordering rule with a bound that is not a number is refused at declaration.</summary>
    /// <remarks>
    /// It would compare 0 to 0 and never hold — the same fault
    /// <c>ProcessPublishing.Validate</c> refuses for a guard, refused at the same moment.
    /// </remarks>
    [Fact]
    public async Task AnOrderingRuleWithATextBoundIsRefusedAtDeclaration()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var site = await ObjectAsync(app, "site");
        await FieldAsync(app, new DefineField(
            null, site, "floor_area", "Floor area", CustomFieldType.Number, IsRequired: false));

        var response = await app.PostAsync(
            Rules,
            new DefineValidationRule(
                null, site, "bad_bound", "floor_area", GuardOperator.GreaterThan, "a lot", "nope"),
            CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.custom_rule_bound_not_numeric");
    }

    /// <summary>
    /// A rule over a field an update did not mention reads what the entity already holds.
    /// </summary>
    /// <remarks>
    /// Otherwise setting one field would be refused by a rule about another, which no
    /// administrator writing the rule intends.
    /// </remarks>
    [Fact]
    public async Task ARuleOverAnUnmentionedFieldReadsWhatIsAlreadyThere()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        await EntityFieldAsync(app, "risk", CustomFieldType.Text);
        await EntityFieldAsync(app, "segment", CustomFieldType.Text);

        (await app.PostAsync(
            Rules,
            new DefineValidationRule(
                EntityKind.Lead, null, "no_high_risk", "risk", GuardOperator.Equals, "high",
                "A high-risk lead cannot be edited."),
            CrmTokens.NorthwindManager))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var lead = await app.Crm.LeadAsync(CrmSchemaHarness.Northwind, Cancellation);

        // Setting an unrelated field is fine while risk is unset.
        (await app.PostAsync(
            EntityFields,
            new SetCustomFields(
                EntityKind.Lead, lead, new Dictionary<string, string?> { ["segment"] = "enterprise" }),
            CrmTokens.Northwind))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // Once risk is high, the same unrelated update is refused by the rule about risk.
        await app.Crm.AsTenantAsync(
            CrmSchemaHarness.Northwind,
            "UPDATE lead SET custom_fields = custom_fields || '{\"risk\":\"high\"}'::jsonb WHERE lead_id = @id",
            Cancellation,
            ("id", lead));

        var refused = await app.PostAsync(
            EntityFields,
            new SetCustomFields(
                EntityKind.Lead, lead, new Dictionary<string, string?> { ["segment"] = "smb" }),
            CrmTokens.Northwind);

        refused.StatusCode.ShouldBe(
            HttpStatusCode.BadRequest,
            "the rule read only what the update mentioned, so a rule about a field nobody was " +
            "editing stopped applying the moment somebody edited something else.");
    }

    /// <summary>A field requiring a grant is refused to a caller who does not hold it.</summary>
    [Fact]
    public async Task AFieldRequiringAGrantIsRefusedToACallerWithoutIt()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        await EntityFieldAsync(
            app, "board_notes", CustomFieldType.Text, requiredPermission: "crm.admin");

        var lead = await app.Crm.LeadAsync(CrmSchemaHarness.Northwind, Cancellation);

        var refused = await app.PostAsync(
            EntityFields,
            new SetCustomFields(
                EntityKind.Lead, lead, new Dictionary<string, string?> { ["board_notes"] = "x" }),
            CrmTokens.Northwind);

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var problem = await Problem(refused);

        problem.GetProperty("code").GetString().ShouldBe("crm.custom_field_forbidden");
        problem.GetProperty("permission").GetString().ShouldBe(
            "crm.admin", "the refusal must name the grant, or asking for it is guesswork.");

        // The manager holds crm.admin, so the same write goes through.
        (await app.PostAsync(
            EntityFields,
            new SetCustomFields(
                EntityKind.Lead, lead, new Dictionary<string, string?> { ["board_notes"] = "x" }),
            CrmTokens.NorthwindManager))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>A unique field refuses a second record holding the same value.</summary>
    [Fact]
    public async Task AUniqueFieldRefusesASecondRecordWithTheSameValue()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var site = await ObjectAsync(app, "site");
        await FieldAsync(app, new DefineField(
            null, site, "code", "Code", CustomFieldType.Text, IsRequired: false, IsUnique: true));

        (await app.PostAsync(
            Records,
            new CreateRecord(site, new Dictionary<string, string?> { ["code"] = "ldn_01" }),
            CrmTokens.Northwind))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var refused = await app.PostAsync(
            Records,
            new CreateRecord(site, new Dictionary<string, string?> { ["code"] = "ldn_01" }),
            CrmTokens.Northwind);

        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Problem(refused)).GetProperty("code").GetString()
            .ShouldBe("crm.custom_value_not_unique");

        (await app.Crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Northwind,
            "SELECT count(*) FROM custom_record WHERE object_id = @id",
            Cancellation,
            ("id", site)))
            .ShouldBe(1, "the second record was refused and written anyway.");

        // A different value is free, so uniqueness is a constraint rather than a wall.
        (await app.PostAsync(
            Records,
            new CreateRecord(site, new Dictionary<string, string?> { ["code"] = "ldn_02" }),
            CrmTokens.Northwind))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>Changing a custom value leaves a history row saying what it was.</summary>
    [Fact]
    public async Task AChangedValueLeavesAHistoryRowAndAnUnchangedOneDoesNot()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        await EntityFieldAsync(app, "segment", CustomFieldType.Text);

        var lead = await app.Crm.LeadAsync(CrmSchemaHarness.Northwind, Cancellation);

        await SetAsync(app, lead, "enterprise");
        await SetAsync(app, lead, "smb");

        // Twice, and the second is the same value: a merge that rewrote every field on every call
        // would make this a log of writes rather than a record of changes.
        await SetAsync(app, lead, "smb");

        (await app.Crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Northwind,
            "SELECT count(*) FROM custom_field_history WHERE entity_id = @id",
            Cancellation,
            ("id", lead)))
            .ShouldBe(2, "an unchanged value was recorded as a change.");

        (await app.Crm.ScalarAsTenantAsync<string>(
            CrmSchemaHarness.Northwind,
            """
            SELECT old_value FROM custom_field_history
            WHERE entity_id = @id ORDER BY changed_at DESC, old_value DESC LIMIT 1
            """,
            Cancellation,
            ("id", lead)))
            .ShouldBe("enterprise", "the history does not say what it used to be.");

        (await app.Crm.ScalarAsTenantAsync<Guid>(
            CrmSchemaHarness.Northwind,
            "SELECT changed_by FROM custom_field_history WHERE entity_id = @id LIMIT 1",
            Cancellation,
            ("id", lead)))
            .ShouldNotBe(
                Guid.Empty,
                "nobody was recorded as having made the change, so the history answers 'what' " +
                "and not 'who'.");
    }

    /// <summary>The history is append-only, and the grant is what makes that true.</summary>
    /// <remarks>
    /// A history a tenant can rewrite answers no question anybody asks it. Asserted against the
    /// grant rather than against the absence of a <c>DELETE</c> statement, because the statement
    /// somebody adds next year is exactly what this has to survive.
    /// </remarks>
    [Fact]
    public async Task TheHistoryCannotBeRewrittenByTheRoleThatWritesIt()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        await EntityFieldAsync(app, "segment", CustomFieldType.Text);

        var lead = await app.Crm.LeadAsync(CrmSchemaHarness.Northwind, Cancellation);

        await SetAsync(app, lead, "enterprise");

        var refusal = await app.Crm.RefusalAsync(
            CrmSchemaHarness.Northwind,
            "DELETE FROM custom_field_history WHERE entity_id = @id",
            Cancellation,
            ("id", lead));

        refusal.ShouldNotBeNull("the role that writes the history can erase it.");
        refusal.SqlState.ShouldBe("42501", "insufficient_privilege.");
    }

    private static async Task SetAsync(CrmApplication app, Guid lead, string segment)
    {
        var response = await app.PostAsync(
            EntityFields,
            new SetCustomFields(
                EntityKind.Lead, lead, new Dictionary<string, string?> { ["segment"] = segment }),
            CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<Guid> ObjectAsync(CrmApplication app, string name)
    {
        var response = await app.PostAsync(
            Objects, new DefineObject(name, name), CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "declaring '" + name + "' failed.");

        return (await CrmApplication.ReadAsync<ObjectDefined>(response)).ObjectId;
    }

    private static async Task FieldAsync(CrmApplication app, DefineField field)
    {
        var response = await app.PostAsync(Fields, field, CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "declaring '" + field.Name + "' failed.");
    }

    private static Task EntityFieldAsync(
        CrmApplication app,
        string name,
        CustomFieldType type,
        string? requiredPermission = null) =>
        FieldAsync(app, new DefineField(
            EntityKind.Lead, null, name, name, type, IsRequired: false,
            RequiredPermission: requiredPermission));

    private static async Task<JsonElement> Problem(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Cancellation);

        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
