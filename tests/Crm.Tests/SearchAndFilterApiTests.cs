using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// Filters with more than one criterion, and a search box over everything.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The compound filter is the case that makes people concatenate SQL, and it does not
/// have to be.</strong> The criteria are bound as three arrays and <c>unnest</c> turns them back
/// into rows inside a constant statement, so a filter of one and a filter of twelve are the same
/// statement with different values. That is what these tests are ultimately protecting.
/// </para>
/// <para>
/// <strong>The search index is a generated column</strong>, so there is no maintenance code
/// anywhere for it and none can drift: PostgreSQL computes it inside the same write. Two of the
/// tests below write rows by paths that know nothing about searching, and then find them.
/// </para>
/// </remarks>
public sealed class SearchAndFilterApiTests
{
    private const string Objects = "/api/v1/crm/custom/objects";
    private const string Fields = "/api/v1/crm/custom/fields";
    private const string Records = "/api/v1/crm/custom/records";
    private const string Views = "/api/v1/crm/custom/list-views";
    private const string Queries = "/api/v1/crm/custom/queries";
    private const string Search = "/api/v1/crm/search";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>Every criterion must hold when the match is All.</summary>
    [Fact]
    public async Task AllRequiresEveryCriterionToHold()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        await RecordAsync(app, site, "ldn", 100, "open");
        await RecordAsync(app, site, "par", 900, "open");
        await RecordAsync(app, site, "ber", 900, "closed");

        var page = await QueryAsync(app, site, new RecordFilter(FilterMatch.All,
        [
            new RollupFilter("floor_area", GuardOperator.GreaterThan, "200"),
            new RollupFilter("status", GuardOperator.Equals, "open"),
        ]));

        page.Records.ShouldHaveSingleItem().Values["label"].ShouldBe(
            "par", "All returned a record that satisfied only one of the two criteria.");
    }

    /// <summary>One criterion is enough when the match is Any.</summary>
    [Fact]
    public async Task AnyRequiresOnlyOneCriterionToHold()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        await RecordAsync(app, site, "ldn", 100, "open");
        await RecordAsync(app, site, "par", 900, "closed");
        await RecordAsync(app, site, "ber", 100, "closed");

        var page = await QueryAsync(app, site, new RecordFilter(FilterMatch.Any,
        [
            new RollupFilter("floor_area", GuardOperator.GreaterThan, "200"),
            new RollupFilter("status", GuardOperator.Equals, "open"),
        ]));

        page.Records.Select(r => r.Values["label"]).ShouldBe(
            ["ldn", "par"], ignoreOrder: true, customMessage: "Any behaved like All.");
    }

    /// <summary>Twelve criteria are evaluated; thirteen are refused.</summary>
    /// <remarks>
    /// Bounded because every criterion is a jsonb extraction per row, and an unbounded list is a
    /// caller's way of asking for an unbounded scan. The boundary is tested from both sides, so a
    /// change to the constant cannot silently move it.
    /// </remarks>
    [Fact]
    public async Task TheCriteriaLimitHoldsOnBothSides()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        await RecordAsync(app, site, "ldn", 100, "open");

        var twelve = Enumerable
            .Range(0, QueryLimits.MaxCriteria)
            .Select(static _ => new RollupFilter("status", GuardOperator.Equals, "open"))
            .ToList();

        (await QueryAsync(app, site, new RecordFilter(FilterMatch.All, twelve)))
            .Records.Count.ShouldBe(1, "twelve criteria is inside the limit.");

        var response = await app.PostAsync(
            Queries,
            new QueryRecords(
                site, null, new RecordFilter(FilterMatch.All, [.. twelve, twelve[0]]), 10),
            CrmTokens.Northwind,
            idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var problem = await Problem(response);

        problem.GetProperty("code").GetString().ShouldBe("crm.query_too_many_criteria");
        problem.GetProperty("max").GetInt32().ShouldBe(QueryLimits.MaxCriteria);
    }

    /// <summary>An empty criteria list returns everything rather than nothing.</summary>
    /// <remarks>
    /// <c>bool_and</c> over an empty set is NULL, not true, and a <c>WHERE</c> of NULL returns no
    /// rows — so a filter with no criteria is the case that silently empties a list view. The
    /// statement guards it with a cardinality check; this is what would notice if it stopped.
    /// </remarks>
    [Fact]
    public async Task AFilterWithNoCriteriaReturnsEverything()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        await RecordAsync(app, site, "ldn", 100, "open");
        await RecordAsync(app, site, "par", 900, "closed");

        (await QueryAsync(app, site, new RecordFilter(FilterMatch.All, [])))
            .Records.Count.ShouldBe(2, "an empty filter emptied the list.");
    }

    /// <summary>A saved view keeps its criteria and its match mode.</summary>
    [Fact]
    public async Task ASavedViewKeepsItsCriteriaAndItsMatchMode()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        await RecordAsync(app, site, "ldn", 100, "open");
        await RecordAsync(app, site, "par", 900, "closed");
        await RecordAsync(app, site, "ber", 50, "closed");

        (await app.PostAsync(
            Views,
            new DefineListView(
                site, "interesting", "Interesting",
                new RecordFilter(FilterMatch.Any,
                [
                    new RollupFilter("floor_area", GuardOperator.GreaterThan, "500"),
                    new RollupFilter("status", GuardOperator.Equals, "open"),
                ]),
                Order: new RecordOrder("label", Descending: false, Numeric: false),
                Limit: 50),
            CrmTokens.NorthwindManager))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var page = await CrmApplication.ReadAsync<RecordPage>(
            await app.PostAsync(
                Queries,
                new QueryRecords(null, "interesting", null, 10),
                CrmTokens.Northwind,
                idempotencyKey: null));

        page.Records.Select(r => r.Values["label"]).ShouldBe(
            ["ldn", "par"],
            "the saved view's match mode was not kept — Any read back as All.");
    }

    /// <summary>A view whose criterion names an undeclared field is refused when it is saved.</summary>
    [Fact]
    public async Task AViewWhoseCriterionNamesAnUndeclaredFieldIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        var response = await app.PostAsync(
            Views,
            new DefineListView(
                site, "typo", "Typo",
                new RecordFilter(FilterMatch.All,
                [
                    new RollupFilter("status", GuardOperator.Equals, "open"),
                    new RollupFilter("stauts", GuardOperator.Equals, "open"),
                ]),
                Order: null,
                Limit: 10),
            CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(
            HttpStatusCode.BadRequest,
            "only the first criterion was checked, so a typo in the second would never match.");

        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.custom_rule_field_not_declared");
    }

    // ------------------------------------------------------------------------------- search

    /// <summary>A search finds a lead written by a path that knows nothing about searching.</summary>
    /// <remarks>
    /// The whole argument for a generated column: <c>POST /leads</c> has no idea a search index
    /// exists, and the row is findable the moment it commits because PostgreSQL computed the
    /// document inside the same write.
    /// </remarks>
    [Fact]
    public async Task ASearchFindsALeadNothingIndexed()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        (await app.PostAsync(
            "/api/v1/crm/leads",
            new CaptureLead("Northwind Traders", "A Person", null, LeadSource.Web),
            CrmTokens.Northwind))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var hits = await SearchAsync(app, "Northwind", CrmTokens.Northwind);

        var hit = hits.Hits.ShouldHaveSingleItem();

        hit.Kind.ShouldBe("Lead");
        hit.Title.ShouldBe("Northwind Traders");
    }

    /// <summary>A search finds a custom record on a field an administrator invented.</summary>
    [Fact]
    public async Task ASearchFindsACustomRecordOnAnInventedField()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        await RecordAsync(app, site, "battersea", 100, "open");

        var hits = await SearchAsync(app, "battersea", CrmTokens.Northwind);

        var hit = hits.Hits.ShouldHaveSingleItem();

        hit.Kind.ShouldBe("CustomRecord");
        hit.Title.ShouldBe("site", "the title is the object's label, never a custom value.");
    }

    /// <summary>A search reaches every kind at once.</summary>
    [Fact]
    public async Task ASearchReachesEveryKindAtOnce()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        await app.PostAsync(
            "/api/v1/crm/leads",
            new CaptureLead("Battersea Holdings", "A Person", null, LeadSource.Web),
            CrmTokens.Northwind);

        await RecordAsync(app, site, "battersea", 100, "open");

        var hits = await SearchAsync(app, "battersea", CrmTokens.Northwind);

        hits.Hits.Select(h => h.Kind).ShouldBe(["Lead", "CustomRecord"], ignoreOrder: true);
    }

    /// <summary>A search does not reach another tenant's rows.</summary>
    /// <remarks>
    /// The tenant appears in none of the five predicates — row-level security scopes all of them
    /// at once, which is the argument for the scope being a connection setting. This is what
    /// would notice if that stopped being true for one of the five.
    /// </remarks>
    [Fact]
    public async Task ASearchDoesNotReachAnotherTenantsRows()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        await app.PostAsync(
            "/api/v1/crm/leads",
            new CaptureLead("Northwind Traders", "A Person", null, LeadSource.Web),
            CrmTokens.Northwind);

        (await SearchAsync(app, "Northwind", CrmTokens.Contoso)).Hits.ShouldBeEmpty(
            "Contoso found Northwind's lead through the search box.");

        (await SearchAsync(app, "Northwind", CrmTokens.Northwind)).Hits.Count.ShouldBe(
            1, "and the wall is a wall rather than an outage.");
    }

    /// <summary>
    /// Half a word finds the whole one, which is what a search box is for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The defect this replaced.</strong> <c>websearch_to_tsquery</c> matches whole
    /// lexemes, so typing "north" into the search box found nothing at all until the word
    /// "northwind" was finished — on the one surface in this application a person uses by typing.
    /// It looked like an empty database rather than an unfinished word.
    /// </para>
    /// <para>
    /// The prefix query is built from the lexemes <c>to_tsvector</c> produces rather than by
    /// appending <c>:*</c> to the caller's text, which is why the apostrophe below is a name and
    /// not a syntax error.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task APrefixFindsTheWholeWord()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        await app.PostAsync(
            "/api/v1/crm/leads",
            new CaptureLead("Northwind Traders", "A Person", null, LeadSource.Web),
            CrmTokens.Northwind);

        (await SearchAsync(app, "nor", CrmTokens.Northwind)).Hits.Count.ShouldBe(
            1, "three letters of a company's name did not find it.");

        (await SearchAsync(app, "nor tra", CrmTokens.Northwind)).Hits.Count.ShouldBe(
            1, "every word is a prefix, and all of them have to match.");

        (await SearchAsync(app, "nor zzz", CrmTokens.Northwind)).Hits.ShouldBeEmpty(
            "all of them, not any of them.");
    }

    /// <summary>An apostrophe is a letter, not a tsquery operator.</summary>
    /// <remarks>
    /// The reason the query is assembled from <c>quote_literal(lexeme)</c>. Appending <c>:*</c>
    /// to the caller's own text would make this a 500 rather than a search.
    /// </remarks>
    [Fact]
    public async Task PunctuationInThePhraseIsNotAnOperator()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        foreach (var phrase in (string[])["o'brien", "a & b", "! ? :*", "north | south"])
        {
            var response = await app.PostAsync(
                Search, new SearchEverything(phrase, 10), CrmTokens.Northwind, idempotencyKey: null);

            response.StatusCode.ShouldBe(
                HttpStatusCode.OK, $"'{phrase}' reached the parser as syntax.");
        }
    }

    /// <summary>A search with nothing to look for is refused.</summary>
    [Fact]
    public async Task ASearchWithNoPhraseIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var response = await app.PostAsync(
            Search, new SearchEverything("   ", 10), CrmTokens.Northwind, idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("code").GetString().ShouldBe("crm.search_phrase_empty");
    }

    /// <summary>An anonymous caller searches nothing.</summary>
    [Fact]
    public async Task AnAnonymousSearchIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var response = await app.PostAsync(
            Search, new SearchEverything("anything", 10), token: null, idempotencyKey: null);

        response.IsSuccessStatusCode.ShouldBeFalse();
        response.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    // ------------------------------------------------------------------------------- fixtures

    private static async Task<Guid> WorldAsync(CrmApplication app)
    {
        var response = await app.PostAsync(
            Objects, new DefineObject("site", "site"), CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var site = (await CrmApplication.ReadAsync<ObjectDefined>(response)).ObjectId;

        foreach (var field in new[]
                 {
                     new DefineField(null, site, "label", "Label", CustomFieldType.Text, false),
                     new DefineField(null, site, "status", "Status", CustomFieldType.Text, false),
                     new DefineField(null, site, "floor_area", "Area", CustomFieldType.Number, false),
                 })
        {
            (await app.PostAsync(Fields, field, CrmTokens.NorthwindManager))
                .StatusCode.ShouldBe(HttpStatusCode.OK, "declaring '" + field.Name + "' failed.");
        }

        return site;
    }

    private static async Task RecordAsync(
        CrmApplication app, Guid site, string label, int area, string status)
    {
        var response = await app.PostAsync(
            Records,
            new CreateRecord(site, new Dictionary<string, string?>
            {
                ["label"] = label,
                ["status"] = status,
                ["floor_area"] = area.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }),
            CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<RecordPage> QueryAsync(
        CrmApplication app, Guid site, RecordFilter filter)
    {
        var response = await app.PostAsync(
            Queries, new QueryRecords(site, null, filter, 50), CrmTokens.Northwind,
            idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<RecordPage>(response);
    }

    private static async Task<SearchResults> SearchAsync(
        CrmApplication app, string phrase, string token)
    {
        var response = await app.PostAsync(
            Search, new SearchEverything(phrase, 20), token, idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<SearchResults>(response);
    }

    private static async Task<JsonElement> Problem(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Cancellation);

        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
