using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// A service desk, and the clock it is actually measured on.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The promise is the feature; the case is a row with a status.</strong> Almost every
/// home-grown service desk computes its targets in calendar time, and so reports a Friday
/// afternoon case as breached by the Friday evening — when the desk was shut and nobody had a
/// chance to meet it. The arithmetic itself is proved in <c>BusinessCalendarTests</c> without a
/// database; what is proved here is that the promise a case carries came from that arithmetic and
/// was stamped on once.
/// </para>
/// <para>
/// <strong>Only a public reply from somebody other than the raiser stops the response
/// clock.</strong> An internal note does not, or every desk would report a first response time of
/// nine seconds and stop reading the number.
/// </para>
/// </remarks>
public sealed class ServiceConsoleApiTests
{
    private const string Hours = "/api/v1/crm/service/hours";
    private const string Policies = "/api/v1/crm/service/policies";
    private const string Cases = "/api/v1/crm/service/cases";
    private const string Comments = "/api/v1/crm/service/comments";
    private const string Worklist = "/api/v1/crm/service/queue";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>The week is said back in minutes, because a typo in it is invisible.</summary>
    /// <remarks>
    /// A week meant to be forty hours and written as four is a mistake somebody would otherwise
    /// find in a breach report a month later.
    /// </remarks>
    [Fact]
    public async Task TheWeekIsSaidBackInMinutes()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var set = await WeekAsync(app, OfficeWeek());

        set.Days.ShouldBe(5);
        set.MinutesPerWeek.ShouldBe(5 * 8 * 60);
    }

    /// <summary>A day that shuts before it opens is refused.</summary>
    /// <remarks>
    /// A window that never elapses is one a minute budget would walk for ever. Refused at the
    /// edge rather than defended against in the walk.
    /// </remarks>
    [Fact]
    public async Task ADayThatShutsBeforeItOpensIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var response = await app.PostAsync(
            Hours,
            new SetBusinessHours([new OpeningHoursOfDay(DayOfWeek.Monday, "17:00", "09:00")]),
            CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.service_day_inverted");
    }

    /// <summary>The same day twice is refused rather than silently reduced to one.</summary>
    [Fact]
    public async Task ADayGivenTwiceIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var response = await app.PostAsync(
            Hours,
            new SetBusinessHours(
            [
                new OpeningHoursOfDay(DayOfWeek.Monday, "09:00", "12:00"),
                new OpeningHoursOfDay(DayOfWeek.Monday, "13:00", "17:00"),
            ]),
            CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.service_day_repeated");
    }

    /// <summary>A promise to fix a thing sooner than to acknowledge it is refused.</summary>
    [Fact]
    public async Task AResolutionSoonerThanTheResponseIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var response = await app.PostAsync(
            Policies,
            new DefineSlaPolicy("inside_out", "Inside out", CasePriority.High, 240, 60, false),
            CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.service_resolution_before_response");
    }

    /// <summary>A second promise for a priority retires the first, and says it did.</summary>
    /// <remarks>
    /// A desk with two live promises for "urgent" has neither: which one a report showed would
    /// depend on which row a query read first.
    /// </remarks>
    [Fact]
    public async Task ASecondPromiseForAPriorityRetiresTheFirst()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var first = await PolicyAsync(
            app, new DefineSlaPolicy("urgent_v1", "Urgent", CasePriority.Urgent, 60, 240, false));

        first.Replaced.ShouldBeFalse("nothing governed Urgent before this.");

        var second = await PolicyAsync(
            app, new DefineSlaPolicy("urgent_v2", "Urgent", CasePriority.Urgent, 30, 120, false));

        second.Replaced.ShouldBeTrue();
        second.PolicyId.ShouldNotBe(first.PolicyId);
    }

    /// <summary>A promise measured around the clock is plain addition.</summary>
    [Fact]
    public async Task APromiseMeasuredAroundTheClockIsPlainAddition()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var account = await AccountAsync(app);

        await PolicyAsync(
            app, new DefineSlaPolicy("always", "Always", CasePriority.Urgent, 60, 240, false));

        var before = DateTimeOffset.UtcNow;
        var opened = await OpenAsync(app, account, CasePriority.Urgent);
        var after = DateTimeOffset.UtcNow;

        opened.Policy.ShouldBe("always");
        opened.FirstResponseDueAt.ShouldNotBeNull();
        opened.FirstResponseDueAt!.Value.ShouldBeInRange(
            before.AddMinutes(60), after.AddMinutes(60));
        opened.ResolutionDueAt!.Value.ShouldBeInRange(
            before.AddMinutes(240), after.AddMinutes(240));
    }

    /// <summary>
    /// A promise measured in open hours does not fall due on a day the desk is shut.
    /// </summary>
    /// <remarks>
    /// <strong>The test this suite exists for.</strong> The desk is open on exactly one day of the
    /// week, chosen relative to the day the test runs so that it is neither today nor tomorrow. A
    /// due date computed in calendar time would land within the hour; one computed in open hours
    /// cannot land before that day.
    /// </remarks>
    [Fact]
    public async Task APromiseInOpenHoursCannotFallDueOnADayTheDeskIsShut()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var account = await AccountAsync(app);

        // The only day the desk opens, two days from now. Picked relative to the run rather than
        // hard-coded, because a fixed day would make this test pass or fail by the calendar.
        var openDay = DateTimeOffset.UtcNow.AddDays(2);

        await WeekAsync(
            app, [new OpeningHoursOfDay(openDay.DayOfWeek, "09:00", "17:00")]);

        await PolicyAsync(
            app, new DefineSlaPolicy("weekly", "Weekly", CasePriority.High, 60, 120, true));

        var opened = await OpenAsync(app, account, CasePriority.High);

        opened.FirstResponseDueAt.ShouldNotBeNull();
        opened.FirstResponseDueAt!.Value.DayOfWeek.ShouldBe(
            openDay.DayOfWeek,
            "a promise measured in open hours can only fall due while the desk is open.");

        opened.FirstResponseDueAt.Value.ShouldBeGreaterThan(
            DateTimeOffset.UtcNow.AddDays(1),
            "an hour of calendar time would have put this within the hour.");
    }

    /// <summary>A priority nothing promises about carries no due date, and says so.</summary>
    /// <remarks>
    /// A legitimate state rather than a missing row: a desk may promise nothing at all about its
    /// low-priority queue, and inventing a target for it would put every one of them in a breach
    /// report nobody agreed to.
    /// </remarks>
    [Fact]
    public async Task APriorityNothingPromisesAboutCarriesNoDueDate()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var account = await AccountAsync(app);

        await PolicyAsync(
            app, new DefineSlaPolicy("urgent_only", "Urgent", CasePriority.Urgent, 60, 240, false));

        var opened = await OpenAsync(app, account, CasePriority.Low);

        opened.Policy.ShouldBeNull();
        opened.FirstResponseDueAt.ShouldBeNull();
        opened.ResolutionDueAt.ShouldBeNull();
    }

    /// <summary>
    /// A promise measured in open hours on a desk with no hours is refused, not defaulted.
    /// </summary>
    [Fact]
    public async Task APromiseInOpenHoursOnADeskWithNoHoursIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var account = await AccountAsync(app);

        await PolicyAsync(
            app, new DefineSlaPolicy("office", "Office", CasePriority.High, 60, 240, true));

        var response = await app.PostAsync(
            Cases,
            new OpenCase(account, null, "Nothing works", "At all.", CasePriority.High, CaseOrigin.Web),
            CrmTokens.Northwind,
            idempotencyKey: Guid.NewGuid().ToString("N"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.service_no_business_hours");
    }

    /// <summary>An internal note does not stop the response clock.</summary>
    /// <remarks>
    /// The quietest way to make a first-response number meaningless: count the moment somebody
    /// opened the case and typed "looking at this" to themselves.
    /// </remarks>
    [Fact]
    public async Task AnInternalNoteDoesNotStopTheResponseClock()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var account = await AccountAsync(app);

        await PolicyAsync(
            app, new DefineSlaPolicy("always", "Always", CasePriority.Urgent, 60, 240, false));

        var opened = await OpenAsync(app, account, CasePriority.Urgent);

        var commented = await CommentAsync(
            app,
            new CommentOnCase(opened.CaseId, "Looking at this.", IsPublic: false, CaseStatus.Working),
            CrmTokens.NorthwindManager);

        commented.StoppedTheResponseClock.ShouldBeFalse();
        commented.FirstResponseMinutes.ShouldBeNull();
    }

    /// <summary>The raiser replying to themselves does not stop the response clock.</summary>
    [Fact]
    public async Task TheRaiserRepliesToThemselvesAndTheClockKeepsRunning()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var account = await AccountAsync(app);

        await PolicyAsync(
            app, new DefineSlaPolicy("always", "Always", CasePriority.Urgent, 60, 240, false));

        var opened = await OpenAsync(app, account, CasePriority.Urgent);

        var commented = await CommentAsync(
            app,
            new CommentOnCase(opened.CaseId, "Also this.", IsPublic: true, Status: null),
            CrmTokens.Northwind);

        commented.StoppedTheResponseClock.ShouldBeFalse(
            "the person who raised the case cannot answer it.");
    }

    /// <summary>A public reply from somebody else stops the clock, and stops it once.</summary>
    [Fact]
    public async Task APublicReplyFromSomebodyElseStopsTheClockOnce()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var account = await AccountAsync(app);

        await PolicyAsync(
            app, new DefineSlaPolicy("always", "Always", CasePriority.Urgent, 60, 240, false));

        var opened = await OpenAsync(app, account, CasePriority.Urgent);

        var first = await CommentAsync(
            app,
            new CommentOnCase(opened.CaseId, "We are on it.", IsPublic: true, CaseStatus.Working),
            CrmTokens.NorthwindManager);

        first.StoppedTheResponseClock.ShouldBeTrue();
        first.FirstResponseMinutes.ShouldNotBeNull();
        first.BreachedFirstResponse.ShouldBeFalse("an hour was promised and seconds were taken.");
        first.Status.ShouldBe("Working");

        var second = await CommentAsync(
            app,
            new CommentOnCase(opened.CaseId, "Still on it.", IsPublic: true, Status: null),
            CrmTokens.NorthwindManager);

        second.StoppedTheResponseClock.ShouldBeFalse("a clock stops once.");
        second.Ordinal.ShouldBe(first.Ordinal + 1);
    }

    /// <summary>Two replies written at once stop the clock once and lose neither.</summary>
    /// <remarks>
    /// <strong>The sequential path cannot show this.</strong> A second reply arriving after the
    /// first has already been recorded is turned away by a read, and a build with no defence at all
    /// against the concurrent case passes every other test in this file. Both halves are load
    /// bearing: the clock stops at one instant rather than at whichever connection the pool ran
    /// last, and both comments survive — a thread that silently dropped what somebody said to a
    /// customer is the one thing it cannot do.
    /// </remarks>
    [Fact]
    public async Task TwoRepliesAtOnceStopTheClockOnceAndLoseNeither()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var account = await AccountAsync(app);

        await PolicyAsync(
            app, new DefineSlaPolicy("always", "Always", CasePriority.Urgent, 60, 240, false));

        var opened = await OpenAsync(app, account, CasePriority.Urgent);

        // Driven at the store rather than over HTTP. A durable flow's own overhead is long enough
        // that the first request finishes before the second reads, so the surface cannot reproduce
        // the race at all — and a build with no defence against it passes every other test here.
        var both = await Task.WhenAll(
            WriteAsync(app, opened.CaseId, "First reply.", "agent-one"),
            WriteAsync(app, opened.CaseId, "Second reply.", "agent-two"));

        both.Count(row => row.Stopped)
            .ShouldBe(1, "a clock stops once, whichever connection got there first.");

        both.Select(row => row.Ordinal).Distinct().Count()
            .ShouldBe(2, "both replies are in the thread, in different places.");

        var written = await app.Crm.ScalarAsTenantAsync<long>(
            CrmTokens.NorthwindTenant,
            "SELECT count(*) FROM case_comment WHERE case_id = @id",
            Cancellation,
            ("id", (object?)opened.CaseId));

        written.ShouldBe(2, "nothing said to a customer is dropped by a race.");
    }

    /// <summary>Nothing is added to a case that is finished.</summary>
    [Fact]
    public async Task AcommentOnAClosedCaseIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var account = await AccountAsync(app);

        var opened = await OpenAsync(app, account, CasePriority.Low);

        await CommentAsync(
            app,
            new CommentOnCase(opened.CaseId, "Sorted.", IsPublic: true, CaseStatus.Closed),
            CrmTokens.NorthwindManager);

        var response = await app.PostAsync(
            Comments,
            new CommentOnCase(opened.CaseId, "One more thing.", IsPublic: true, Status: null),
            CrmTokens.NorthwindManager,
            idempotencyKey: Guid.NewGuid().ToString("N"));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.service_case_closed");
    }

    /// <summary>The worklist says what is late and what nobody has answered.</summary>
    /// <remarks>
    /// Breach is computed as of the read rather than stored. A case does not become late by
    /// anybody doing anything to it — it becomes late by the clock passing an instant, and a flag
    /// a job had to sweep up would be wrong for however long the job's period is.
    /// </remarks>
    [Fact]
    public async Task TheWorklistSaysWhatIsLateAndWhatNobodyHasAnswered()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var account = await AccountAsync(app);

        await PolicyAsync(
            app, new DefineSlaPolicy("always", "Always", CasePriority.Urgent, 60, 240, false));

        var late = await OpenAsync(app, account, CasePriority.Urgent);
        var fresh = await OpenAsync(app, account, CasePriority.Urgent);

        // Wound back rather than waited for. The alternative is a test that takes an hour.
        await app.Crm.AsTenantAsync(
            CrmTokens.NorthwindTenant,
            "UPDATE support_case SET first_response_due_at = @due, resolution_due_at = @due " +
            "WHERE case_id = @id",
            Cancellation,
            ("due", (object?)DateTimeOffset.UtcNow.AddHours(-1)),
            ("id", late.CaseId));

        var all = await WorklistAsync(app, new ReadCaseWorklist(false, null, false));

        all.AwaitingFirstResponse.ShouldBe(2);
        all.Breached.ShouldBe(1);
        all.Cases[0].CaseId.ShouldBe(late.CaseId, "the tightest promise sorts first.");

        // A case nobody has answered is late the moment the promise passes. Asserted separately
        // from the resolution promise, because a build that only counted a breach once somebody
        // had replied would report the queue nobody has touched as perfectly healthy — which is
        // exactly the queue a desk manager needs to be shown.
        all.Cases[0].AwaitingFirstResponse.ShouldBeTrue();
        all.Cases[0].ResponseBreached.ShouldBeTrue();
        all.Cases[0].ResolutionBreached.ShouldBeTrue();
        all.Cases[1].ResponseBreached.ShouldBeFalse("the fresh one is inside its promise.");
        all.Cases[0].MinutesToResolutionDue.ShouldNotBeNull()
            .ShouldBeLessThan(0, "late is a negative, not a zero.");

        var breached = await WorklistAsync(app, new ReadCaseWorklist(false, null, true));

        breached.Cases.Count.ShouldBe(1);
        breached.Cases[0].CaseId.ShouldBe(late.CaseId);
        breached.Cases.ShouldNotContain(row => row.CaseId == fresh.CaseId);
    }

    /// <summary>Case numbers run per tenant, so no tenant learns another's volume.</summary>
    /// <remarks>
    /// A sequence every tenant drew from would let a client who opens two cases a week and sees
    /// them numbered 4,102 and 4,181 work out exactly how busy somebody else is.
    /// </remarks>
    [Fact]
    public async Task CaseNumbersRunPerTenant()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var northwind = await AccountAsync(app);
        var contoso = await AccountAsync(app, CrmTokens.ContosoTenant);

        (await OpenAsync(app, northwind, CasePriority.Low)).Number.ShouldBe(1);
        (await OpenAsync(app, northwind, CasePriority.Low)).Number.ShouldBe(2);

        (await OpenAsync(app, contoso, CasePriority.Low, CrmTokens.Contoso)).Number
            .ShouldBe(1, "the other tenant's cases are not in this tenant's numbering.");
    }

    /// <summary>Declaring the promise is administrative.</summary>
    [Fact]
    public async Task DeclaringThePromiseIsAdministrative()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        (await app.PostAsync(
            Policies,
            new DefineSlaPolicy("rep_wrote_this", "Mine", CasePriority.Low, 60, 120, false),
            CrmTokens.Northwind))
            .StatusCode.ShouldBe(
                HttpStatusCode.Forbidden, "a representative does not write what the desk promises.");
    }

    /// <summary>A case against an account of another tenant is not found.</summary>
    [Fact]
    public async Task ACaseAgainstAnotherTenantsAccountIsNotFound()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var contoso = await AccountAsync(app, CrmTokens.ContosoTenant);

        var response = await app.PostAsync(
            Cases,
            new OpenCase(contoso, null, "Theirs", "Not mine.", CasePriority.Low, CaseOrigin.Web),
            CrmTokens.Northwind,
            idempotencyKey: Guid.NewGuid().ToString("N"));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.service_account_not_found");
    }

    // ------------------------------------------------------------------------------- fixtures

    private static IReadOnlyList<OpeningHoursOfDay> OfficeWeek() =>
    [
        new(DayOfWeek.Monday, "09:00", "17:00"),
        new(DayOfWeek.Tuesday, "09:00", "17:00"),
        new(DayOfWeek.Wednesday, "09:00", "17:00"),
        new(DayOfWeek.Thursday, "09:00", "17:00"),
        new(DayOfWeek.Friday, "09:00", "17:00"),
    ];

    private static async Task<BusinessHoursSet> WeekAsync(
        CrmApplication app, IReadOnlyList<OpeningHoursOfDay> week)
    {
        var response = await app.PostAsync(
            Hours, new SetBusinessHours(week), CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<BusinessHoursSet>(response);
    }

    private static async Task<SlaPolicyDefined> PolicyAsync(
        CrmApplication app, DefineSlaPolicy policy)
    {
        var response = await app.PostAsync(Policies, policy, CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "declaring '" + policy.Name + "' failed.");

        return await CrmApplication.ReadAsync<SlaPolicyDefined>(response);
    }

    private static async Task<Guid> AccountAsync(
        CrmApplication app, string tenant = CrmTokens.NorthwindTenant) =>
        await app.Crm.AccountAsync(tenant, Lifecycle.Customer, Cancellation);

    private static async Task<CaseOpened> OpenAsync(
        CrmApplication app,
        Guid account,
        CasePriority priority,
        string token = CrmTokens.Northwind)
    {
        var response = await app.PostAsync(
            Cases,
            new OpenCase(
                account, null, "The thing is broken", "It stopped.", priority, CaseOrigin.Phone),
            token,
            idempotencyKey: Guid.NewGuid().ToString("N"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<CaseOpened>(response);
    }

    private static async Task<CaseCommented> CommentAsync(
        CrmApplication app, CommentOnCase comment, string token)
    {
        var response = await app.PostAsync(
            Comments, comment, token, idempotencyKey: Guid.NewGuid().ToString("N"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<CaseCommented>(response);
    }

    private static async Task<(int Ordinal, bool Stopped)> WriteAsync(
        CrmApplication app, Guid id, string body, string author) =>
        await app.Service.CommentAsync(
            CrmTokens.NorthwindTenant,
            id,
            author,
            new CommentOnCase(id, body, IsPublic: true, Status: null),
            stopsTheClock: true,
            DateTimeOffset.UtcNow,
            Cancellation);

    private static async Task<CaseWorklist> WorklistAsync(
        CrmApplication app, ReadCaseWorklist ask)
    {
        var response = await app.PostAsync(Worklist, ask, CrmTokens.Northwind, idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<CaseWorklist>(response);
    }

    private static async Task<JsonElement> Problem(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Cancellation);

        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
