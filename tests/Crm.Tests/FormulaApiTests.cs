using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// Formula fields, and the ordering the query surface said it did not have.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A formula reads other fields of the same record; a roll-up reads other records.</strong>
/// Both write into <c>values</c> and both make their field read-only, so what differs is when
/// they are recomputed — a formula on every write of its own row, a roll-up when a child changes.
/// </para>
/// <para>
/// <strong>A formula may not read another formula</strong>, which is what keeps evaluation a
/// single pass with no dependency graph and no possible cycle. The cost is that
/// <c>(a + b) * c</c> is two declarations; the benefit is that the intermediate has a name
/// somebody can query, which a parenthesis does not.
/// </para>
/// </remarks>
public sealed class FormulaApiTests
{
    private const string Objects = "/api/v1/crm/custom/objects";
    private const string Fields = "/api/v1/crm/custom/fields";
    private const string Records = "/api/v1/crm/custom/records";
    private const string Formulas = "/api/v1/crm/custom/formulas";
    private const string Views = "/api/v1/crm/custom/list-views";
    private const string Queries = "/api/v1/crm/custom/queries";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A formula over two fields is computed and stored as a number.</summary>
    [Fact]
    public async Task AFormulaOverTwoFieldsIsComputedOnWrite()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        await FormulaAsync(app, new DefineFormula(
            world.Total, FormulaOperation.Multiply, "unit_price", "quantity", null));

        var record = await RecordAsync(app, world.Object, new()
        {
            ["label"] = "ldn",
            ["unit_price"] = "12.5",
            ["quantity"] = "4",
        });

        (await ValueAsync(app, record, "total")).ShouldBe("50.0");

        (await app.Crm.ScalarAsTenantAsync<string>(
            CrmSchemaHarness.Northwind,
            "SELECT jsonb_typeof(values->'total') FROM custom_record WHERE record_id = @id",
            Cancellation,
            ("id", record)))
            .ShouldBe("number", "a computed total stored as text orders '9' after '42'.");
    }

    /// <summary>A formula against a constant is computed too.</summary>
    [Fact]
    public async Task AFormulaAgainstAConstantIsComputed()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        await FormulaAsync(app, new DefineFormula(
            world.Total, FormulaOperation.Multiply, "unit_price", null, "2"));

        var record = await RecordAsync(app, world.Object, new()
        {
            ["label"] = "ldn",
            ["unit_price"] = "21",
        });

        (await ValueAsync(app, record, "total")).ShouldBe("42");
    }

    /// <summary>A formula whose operand is unset produces nothing rather than failing the write.</summary>
    /// <remarks>
    /// The whole reason every arm of the evaluator answers for a missing operand: one blank cell
    /// must not fail a write, and a silent zero would make an empty column read as a real number.
    /// </remarks>
    [Fact]
    public async Task AFormulaWithAMissingOperandLeavesTheFieldUnset()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        await FormulaAsync(app, new DefineFormula(
            world.Total, FormulaOperation.Multiply, "unit_price", "quantity", null));

        var record = await RecordAsync(app, world.Object, new()
        {
            ["label"] = "ldn",
            ["unit_price"] = "12.5",
        });

        (await ValueAsync(app, record, "total")).ShouldBeNull(
            "a missing operand produced a number, so an empty column reads as a real one.");
    }

    /// <summary>Dividing by zero produces nothing rather than throwing.</summary>
    [Fact]
    public async Task DividingByZeroLeavesTheFieldUnset()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        await FormulaAsync(app, new DefineFormula(
            world.Total, FormulaOperation.Divide, "unit_price", "quantity", null));

        var record = await RecordAsync(app, world.Object, new()
        {
            ["label"] = "ldn",
            ["unit_price"] = "12.5",
            ["quantity"] = "0",
        });

        (await ValueAsync(app, record, "total")).ShouldBeNull(
            "a formula must not be able to fail a write.");
    }

    /// <summary>A validation rule sees what the formula produced.</summary>
    /// <remarks>
    /// Which is why formulas are computed before rules are evaluated: an administrator who guards
    /// on a total wants the total that will be stored, not the one before it existed.
    /// </remarks>
    [Fact]
    public async Task AValidationRuleSeesWhatTheFormulaProduced()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        await FormulaAsync(app, new DefineFormula(
            world.Total, FormulaOperation.Multiply, "unit_price", "quantity", null));

        (await app.PostAsync(
            "/api/v1/crm/custom/validation-rules",
            new DefineValidationRule(
                null, world.Object, "too_large", "total", GuardOperator.GreaterThan, "1000",
                "An order over 1000 needs approval."),
            CrmTokens.NorthwindManager))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var refused = await app.PostAsync(
            Records,
            new CreateRecord(world.Object, new Dictionary<string, string?>
            {
                ["label"] = "ldn",
                ["unit_price"] = "500",
                ["quantity"] = "4",
            }),
            CrmTokens.Northwind);

        refused.StatusCode.ShouldBe(
            HttpStatusCode.BadRequest,
            "the rule was evaluated before the formula ran, so a guard on a computed field " +
            "never fires.");

        (await Problem(refused)).GetProperty("detail").GetString()
            .ShouldBe("An order over 1000 needs approval.");
    }

    /// <summary>A formula's field cannot be written by a caller.</summary>
    [Fact]
    public async Task AFormulaFieldCannotBeWrittenByACaller()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        await FormulaAsync(app, new DefineFormula(
            world.Total, FormulaOperation.Multiply, "unit_price", "quantity", null));

        var response = await app.PostAsync(
            Records,
            new CreateRecord(world.Object, new Dictionary<string, string?> { ["total"] = "99" }),
            CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.custom_field_is_computed");
    }

    /// <summary>A formula may not read another formula.</summary>
    [Fact]
    public async Task AFormulaCannotReadAnotherFormula()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        await FormulaAsync(app, new DefineFormula(
            world.Total, FormulaOperation.Multiply, "unit_price", "quantity", null));

        var response = await app.PostAsync(
            Formulas,
            new DefineFormula(world.Doubled, FormulaOperation.Multiply, "total", null, "2"),
            CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(
            HttpStatusCode.BadRequest,
            "a formula over a formula needs an evaluation order, which is a graph that can hold " +
            "a cycle.");

        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.formula_operand_is_computed");
    }

    /// <summary>A formula over a field nobody declared is refused at declaration.</summary>
    [Fact]
    public async Task AFormulaOverAnUndeclaredFieldIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        var response = await app.PostAsync(
            Formulas,
            new DefineFormula(world.Total, FormulaOperation.Multiply, "unit_prlce", "quantity", null),
            CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.formula_operand_not_declared");
    }

    /// <summary>An arithmetic formula against a non-numeric constant is refused.</summary>
    [Fact]
    public async Task AnArithmeticFormulaAgainstATextConstantIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        var response = await app.PostAsync(
            Formulas,
            new DefineFormula(world.Total, FormulaOperation.Add, "unit_price", null, "a lot"),
            CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.formula_literal_not_numeric");
    }

    /// <summary>A right operand given as both a field and a constant is refused.</summary>
    [Fact]
    public async Task AnAmbiguousRightOperandIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        var response = await app.PostAsync(
            Formulas,
            new DefineFormula(world.Total, FormulaOperation.Add, "unit_price", "quantity", "2"),
            CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.formula_operand_ambiguous");
    }

    // ------------------------------------------------------------------------------- ordering

    /// <summary>A numeric ordering sorts as numbers, and a textual one as text.</summary>
    /// <remarks>
    /// The limit <c>QueryStore</c> wrote down when the query surface was built: <c>-&gt;&gt;</c>
    /// gives text, so <c>"9"</c> sorts after <c>"42"</c>. Both orderings are asserted, because a
    /// numeric sort that was silently textual would pass a test that only checked one.
    /// </remarks>
    [Fact]
    public async Task ANumericOrderingSortsAsNumbersAndATextualOneAsText()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        foreach (var (label, price) in new[] { ("a", "9"), ("b", "42"), ("c", "100") })
        {
            await RecordAsync(app, world.Object, new()
            {
                ["label"] = label,
                ["unit_price"] = price,
            });
        }

        (await OrderedAsync(app, world.Object, new RecordOrder("unit_price", false, Numeric: true)))
            .ShouldBe(["a", "b", "c"], "the numeric ordering sorted as text.");

        (await OrderedAsync(app, world.Object, new RecordOrder("unit_price", false, Numeric: false)))
            .ShouldBe(["c", "b", "a"], "the textual ordering stopped being textual.");
    }

    /// <summary>A descending ordering reverses it.</summary>
    [Fact]
    public async Task ADescendingOrderingReversesIt()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        foreach (var (label, price) in new[] { ("a", "9"), ("b", "42"), ("c", "100") })
        {
            await RecordAsync(app, world.Object, new()
            {
                ["label"] = label,
                ["unit_price"] = price,
            });
        }

        (await OrderedAsync(app, world.Object, new RecordOrder("unit_price", true, Numeric: true)))
            .ShouldBe(["c", "b", "a"]);
    }

    /// <summary>A row whose value will not parse sorts last rather than failing the page.</summary>
    /// <remarks>
    /// The reason the numeric orderings cast defensively: a single unparseable value would
    /// otherwise throw and the whole list would fail to load.
    /// </remarks>
    [Fact]
    public async Task ARowThatWillNotParseSortsLastRatherThanFailingThePage()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        await RecordAsync(app, world.Object, new() { ["label"] = "a", ["unit_price"] = "9" });

        // Written straight into the column, because the API would refuse a Number field holding
        // text — and a repair script or a future capability would not.
        var broken = Guid.NewGuid();

        await app.Crm.AsTenantAsync(
            CrmSchemaHarness.Northwind,
            """
            INSERT INTO custom_record (record_id, tenant_id, object_id, values, created_at)
            VALUES (@id, @tenant, @object, '{"label": "z", "unit_price": "n/a"}'::jsonb, now())
            """,
            Cancellation,
            ("id", broken),
            ("tenant", CrmSchemaHarness.Northwind),
            ("object", world.Object));

        (await OrderedAsync(app, world.Object, new RecordOrder("unit_price", false, Numeric: true)))
            .ShouldBe(["a", "z"], "an unparseable value failed the whole page instead of sorting last.");
    }

    // ------------------------------------------------------------------------------- fixtures

    private sealed record World(Guid Object, Guid Total, Guid Doubled);

    private static async Task<World> WorldAsync(CrmApplication app)
    {
        var response = await app.PostAsync(
            Objects, new DefineObject("order_line", "Order line"), CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var target = (await CrmApplication.ReadAsync<ObjectDefined>(response)).ObjectId;

        await FieldAsync(app, target, "label", CustomFieldType.Text);
        await FieldAsync(app, target, "unit_price", CustomFieldType.Number);
        await FieldAsync(app, target, "quantity", CustomFieldType.Number);

        var total = await FieldAsync(app, target, "total", CustomFieldType.Number);
        var doubled = await FieldAsync(app, target, "doubled", CustomFieldType.Number);

        return new World(target, total, doubled);
    }

    private static async Task<Guid> FieldAsync(
        CrmApplication app, Guid target, string name, CustomFieldType type)
    {
        var response = await app.PostAsync(
            Fields,
            new DefineField(null, target, name, name, type, IsRequired: false),
            CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "declaring '" + name + "' failed.");

        return (await CrmApplication.ReadAsync<FieldDefined>(response)).FieldId;
    }

    private static async Task FormulaAsync(CrmApplication app, DefineFormula formula)
    {
        var response = await app.PostAsync(Formulas, formula, CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "declaring the formula failed.");
    }

    private static async Task<Guid> RecordAsync(
        CrmApplication app, Guid target, Dictionary<string, string?> values)
    {
        var response = await app.PostAsync(
            Records, new CreateRecord(target, values), CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return (await CrmApplication.ReadAsync<RecordCreated>(response)).RecordId;
    }

    private static ValueTask<string?> ValueAsync(CrmApplication app, Guid record, string field) =>
        app.Crm.ScalarAsTenantAsync<string>(
            CrmSchemaHarness.Northwind,
            "SELECT values->>'" + field + "' FROM custom_record WHERE record_id = @id",
            Cancellation,
            ("id", record));

    private static async Task<IReadOnlyList<string?>> OrderedAsync(
        CrmApplication app, Guid target, RecordOrder order)
    {
        var name = "view_" + Guid.NewGuid().ToString("n")[..8];

        (await app.PostAsync(
            Views,
            new DefineListView(target, name, name, null, order, Limit: 50),
            CrmTokens.NorthwindManager))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await app.PostAsync(
            Queries, new QueryRecords(null, name, null, 50), CrmTokens.Northwind,
            idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var page = await CrmApplication.ReadAsync<RecordPage>(response);

        return [.. page.Records.Select(record => record.Values["label"])];
    }

    private static async Task<JsonElement> Problem(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Cancellation);

        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
