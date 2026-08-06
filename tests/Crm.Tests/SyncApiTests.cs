using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// What a client that was offline is told when it comes back.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two things a "changed since" filter over the records cannot do</strong>, and they are
/// what this suite is about. It cannot report a deletion — there is no row left to match — so a
/// client that saw a record once keeps it forever. And a timestamp is not a safe place to resume
/// from: two writers take their clock at the start of a transaction and can commit in the other
/// order, so a client that read up to the later stamp never sees the earlier row again.
/// </para>
/// <para>
/// <strong>The feed is a second projection of custom values, so it is a second place the read
/// policy has to hold.</strong> A feed that returned unmasked values would be a way around the
/// field permissions spelled differently, which is why one of these tests is the query suite's
/// redaction test asked of the feed instead.
/// </para>
/// </remarks>
public sealed class SyncApiTests
{
    private const string Objects = "/api/v1/crm/custom/objects";
    private const string Fields = "/api/v1/crm/custom/fields";
    private const string Relationships = "/api/v1/crm/custom/relationships";
    private const string Records = "/api/v1/crm/custom/records";
    private const string Links = "/api/v1/crm/custom/links";
    private const string Rollups = "/api/v1/crm/custom/rollups";
    private const string Changes = "/api/v1/crm/custom/changes";
    private const string Deletions = "/api/v1/crm/custom/record-deletions";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A first sync that finds nothing still comes back with somewhere to resume.</summary>
    /// <remarks>
    /// Without this a client that syncs before its first write has nothing to send next time, and
    /// starts again from the beginning of history on every launch.
    /// </remarks>
    [Fact]
    public async Task AFirstSyncCarriesACursorEvenWhenNothingHasHappened()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var page = await SyncAsync(app, null, null, CrmTokens.Northwind);

        page.Changes.ShouldBeEmpty();
        page.Cursor.ShouldNotBeNullOrEmpty("an empty page with no cursor restarts the sync.");
        page.HasMore.ShouldBeFalse();
    }

    /// <summary>The cursor an empty page carries means "now", not "the beginning".</summary>
    /// <remarks>
    /// A caught-up client polls an empty feed all day. If those empty pages handed back a cursor
    /// that pointed at the start of history, the next real change would arrive with every change
    /// that ever preceded it — and the symptom is a client that grows slower the longer the tenant
    /// has existed, which nobody reads as a sync bug.
    /// </remarks>
    [Fact]
    public async Task TheCursorOnAnEmptyPageMeansNowAndNotTheBeginning()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        await RecordAsync(app, site, "harbour");

        var drained = (await WaitAsync(app, null, null, CrmTokens.Northwind, 1)).Cursor;
        var empty = await SyncAsync(app, null, drained, CrmTokens.Northwind);

        empty.Changes.ShouldBeEmpty();

        var second = await RecordAsync(app, site, "quay");

        (await WaitAsync(app, null, empty.Cursor, CrmTokens.Northwind, 1))
            .Changes.ShouldHaveSingleItem().RecordId.ShouldBe(
                second, "the empty page rewound the client to the start of history.");
    }

    /// <summary>A write shows up after the cursor that was taken before it.</summary>
    [Fact]
    public async Task AWriteShowsUpAfterTheCursorTakenBeforeIt()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        var start = (await SyncAsync(app, null, null, CrmTokens.Northwind)).Cursor;
        var record = await RecordAsync(app, site, "harbour");

        var page = await WaitAsync(app, null, start, CrmTokens.Northwind, 1);

        var change = page.Changes.ShouldHaveSingleItem();

        change.RecordId.ShouldBe(record);
        change.ObjectId.ShouldBe(site);
        change.Kind.ShouldBe("Upserted");
        change.Values.ShouldNotBeNull()["label"].ShouldBe("harbour");
    }

    /// <summary>A delete is a tombstone with no values, not an absence.</summary>
    /// <remarks>
    /// The object id survives the delete on purpose: a client told only that some id is gone has
    /// to search every collection it holds to find out which one to remove it from.
    /// </remarks>
    [Fact]
    public async Task ADeleteIsATombstoneAndNotAnAbsence()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        var record = await RecordAsync(app, site, "harbour");

        // A sibling that outlives the delete, so that "the tombstone carries no values" is a claim
        // about the tombstone and not about there being nothing left in the table to carry.
        await RecordAsync(app, site, "quay");

        var afterWrite = (await WaitAsync(app, null, null, CrmTokens.Northwind, 2)).Cursor;

        (await DeleteAsync(app, record)).Deleted.ShouldBeTrue();

        var change = (await WaitAsync(app, null, afterWrite, CrmTokens.Northwind, 1))
            .Changes.ShouldHaveSingleItem();

        change.RecordId.ShouldBe(record);
        change.Kind.ShouldBe("Deleted");
        change.ObjectId.ShouldBe(site, "a client has to know which collection to remove it from.");
        change.Values.ShouldBeNull(
            "empty values would be written over the client's copy instead of removing it.");
    }

    /// <summary>Deleting what is already gone succeeds and says so.</summary>
    /// <remarks>
    /// A client retrying a delete whose response it lost would otherwise have to tell "already
    /// done" from "never existed", and from the client's side those are the same.
    /// </remarks>
    [Fact]
    public async Task DeletingWhatIsAlreadyGoneIsNotAnError()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        var record = await RecordAsync(app, site, "harbour");

        (await DeleteAsync(app, record)).Deleted.ShouldBeTrue();
        (await DeleteAsync(app, record)).Deleted.ShouldBeFalse();
    }

    /// <summary>Paging the feed serves every change once and stops.</summary>
    [Fact]
    public async Task DrainingTheFeedServesEveryChangeExactlyOnce()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        var written = new List<Guid>();

        for (var i = 0; i < 5; i++)
        {
            written.Add(await RecordAsync(app, site, "site-" + i));
        }

        await WaitAsync(app, null, null, CrmTokens.Northwind, written.Count);

        var seen = new List<Guid>();
        string? cursor = null;

        for (var page = 0; page < 10; page++)
        {
            var served = await SyncAsync(app, null, cursor, CrmTokens.Northwind, limit: 2);

            seen.AddRange(served.Changes.Select(change => change.RecordId));
            cursor = served.Cursor;

            if (!served.HasMore)
            {
                break;
            }
        }

        seen.ShouldBe(written, "the feed is in the order the writes committed, each one once.");

        (await SyncAsync(app, null, cursor, CrmTokens.Northwind)).Changes.ShouldBeEmpty(
            "a drained feed hands back a cursor that returns nothing until something changes.");
    }

    /// <summary>A recomputation nothing logged explicitly is still a change.</summary>
    /// <remarks>
    /// <strong>The reason the log is a trigger and not a line in the record capability.</strong>
    /// Nothing in the roll-up path writes a change, and nothing had to: it updates the record, and
    /// the trigger is on the record. A log the application appends to is correct until somebody
    /// adds the fourth write path — and this is the third.
    /// </remarks>
    [Fact]
    public async Task ARollupRecomputationReachesTheFeed()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var account = await ObjectAsync(app, "managed_account");
        var site = await ObjectAsync(app, "site");

        var siteCount = await FieldAsync(app, new DefineField(
            null, account, "site_count", "Sites", CustomFieldType.Number, IsRequired: false));

        await FieldAsync(app, new DefineField(
            null, site, "label", "Label", CustomFieldType.Text, IsRequired: false));

        var edge = (await CrmApplication.ReadAsync<RelationshipDefined>(
            await app.PostAsync(
                Relationships,
                new DefineRelationship("account_site", account, site, CustomCardinality.OneToMany),
                CrmTokens.NorthwindManager))).RelationshipId;

        (await app.PostAsync(
            Rollups,
            new DefineRollup(siteCount, edge, RollupAggregate.Count, null, null),
            CrmTokens.NorthwindManager))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var parent = await RecordAsync(app, account, null);
        var child = await RecordAsync(app, site, "harbour");

        var beforeLink = (await WaitAsync(app, null, null, CrmTokens.Northwind, 2)).Cursor;

        (await app.PostAsync(Links, new LinkRecords(edge, parent, child), CrmTokens.Northwind))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var change = (await WaitAsync(app, null, beforeLink, CrmTokens.Northwind, 1))
            .Changes.Single(served => served.RecordId == parent);

        change.Values.ShouldNotBeNull()["site_count"].ShouldBe(
            "1", "the roll-up wrote the parent and the feed never heard about it.");
    }

    /// <summary>Syncing one object carries nothing from another.</summary>
    [Fact]
    public async Task SyncingOneObjectCarriesNothingFromAnother()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);
        var other = await ObjectAsync(app, "depot");

        await FieldAsync(app, new DefineField(
            null, other, "label", "Label", CustomFieldType.Text, IsRequired: false));

        await RecordAsync(app, site, "harbour");
        var depot = await RecordAsync(app, other, "north");

        var page = await WaitAsync(app, other, null, CrmTokens.Northwind, 1);

        page.Changes.ShouldHaveSingleItem().RecordId.ShouldBe(depot);
    }

    /// <summary>One tenant's changes never reach another's client.</summary>
    [Fact]
    public async Task ATenantIsNeverToldWhatChangedInAnother()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        await RecordAsync(app, site, "harbour");

        await WaitAsync(app, null, null, CrmTokens.Northwind, 1);

        (await SyncAsync(app, null, null, CrmTokens.Contoso)).Changes.ShouldBeEmpty(
            "row-level security is what makes this true, and it is what this asserts.");
    }

    /// <summary>A field the caller may not read is redacted in the feed as it is in a query.</summary>
    [Fact]
    public async Task AFieldTheCallerMayNotReadIsRedactedInTheFeed()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await ObjectAsync(app, "site");

        await FieldAsync(app, new DefineField(
            null, site, "label", "Label", CustomFieldType.Text, IsRequired: false));
        await FieldAsync(app, new DefineField(
            null, site, "margin", "Margin", CustomFieldType.Text, IsRequired: false,
            ReadPermission: "crm.admin"));

        (await app.PostAsync(
            Records,
            new CreateRecord(site, new Dictionary<string, string?>
            {
                ["label"] = "harbour",
                ["margin"] = "0.42",
            }),
            CrmTokens.NorthwindManager))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var page = await WaitAsync(app, null, null, CrmTokens.Northwind, 1);
        var values = page.Changes.ShouldHaveSingleItem().Values.ShouldNotBeNull();

        values["margin"].ShouldBe(
            "[redacted]", "a feed that leaks is a read policy spelled differently.");
        values["label"].ShouldBe("harbour");
        page.Redacted.ShouldBe(["margin"]);
    }

    /// <summary>A cursor this API did not issue is refused rather than treated as a first sync.</summary>
    /// <remarks>
    /// Starting over silently is how a client whose stored cursor was corrupted re-downloads the
    /// whole tenant on every launch and nobody finds out.
    /// </remarks>
    [Fact]
    public async Task ACursorThisApiDidNotIssueIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var response = await app.PostAsync(
            Changes, new SyncChanges(null, "not-a-cursor", 100), CrmTokens.Northwind,
            idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.sync_cursor_unusable");
    }

    // ------------------------------------------------------------------------------- fixtures

    // THE FEED IS EVENTUAL, AND THESE TESTS HAVE TO SAY SO. A change is served only once its
    // transaction id is below `pg_snapshot_xmin` — the id under which nothing is still running —
    // so a write stays invisible while any older transaction is open anywhere in the cluster. That
    // is the price of never losing a row, and in a suite whose classes share one cluster it is
    // visible. Asserting immediately would make these tests fail for the reason the design works.
    private static async Task<ChangePage> WaitAsync(
        CrmApplication app, Guid? target, string? cursor, string token, int count, int limit = 100)
    {
        var page = await SyncAsync(app, target, cursor, token, limit);

        for (var attempt = 0; attempt < 100 && page.Changes.Count < count; attempt++)
        {
            await Task.Delay(100, Cancellation);

            page = await SyncAsync(app, target, cursor, token, limit);
        }

        page.Changes.Count.ShouldBeGreaterThanOrEqualTo(
            count, "the feed never served what was written.");

        return page;
    }

    private static async Task<ChangePage> SyncAsync(
        CrmApplication app, Guid? target, string? cursor, string token, int limit = 100)
    {
        var response = await app.PostAsync(
            Changes, new SyncChanges(target, cursor, limit), token, idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<ChangePage>(response);
    }

    private static async Task<RecordDeleted> DeleteAsync(CrmApplication app, Guid record)
    {
        var response = await app.PostAsync(
            Deletions, new DeleteRecord(record), CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<RecordDeleted>(response);
    }

    private static async Task<Guid> WorldAsync(CrmApplication app)
    {
        var site = await ObjectAsync(app, "site");

        await FieldAsync(app, new DefineField(
            null, site, "label", "Label", CustomFieldType.Text, IsRequired: false));

        return site;
    }

    private static async Task<Guid> ObjectAsync(CrmApplication app, string name)
    {
        var response = await app.PostAsync(
            Objects, new DefineObject(name, name), CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return (await CrmApplication.ReadAsync<ObjectDefined>(response)).ObjectId;
    }

    private static async Task<Guid> FieldAsync(CrmApplication app, DefineField field)
    {
        var response = await app.PostAsync(Fields, field, CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "declaring '" + field.Name + "' failed.");

        return (await CrmApplication.ReadAsync<FieldDefined>(response)).FieldId;
    }

    private static async Task<Guid> RecordAsync(CrmApplication app, Guid target, string? label)
    {
        var values = label is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?> { ["label"] = label };

        var response = await app.PostAsync(
            Records, new CreateRecord(target, values), CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return (await CrmApplication.ReadAsync<RecordCreated>(response)).RecordId;
    }

    private static async Task<JsonElement> Problem(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Cancellation);

        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
