using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// Approvals an administrator configures, and the controls that make them mean anything.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The test this suite exists for is the self-approval one.</strong> A submitter approving
/// their own request is the first thing an auditor asks about and almost the last thing a
/// home-grown approval system implements — because the happy path works perfectly without the
/// check, and nothing fails until somebody notices they can sign off their own discount. It is one
/// line of production code and one test whose only job is to fail when the line goes.
/// </para>
/// <para>
/// <strong>This does not replace <c>crm.discount.approve</c>.</strong> That grant answers whether
/// a person may approve discounts at all; this answers which particular people, in what order, for
/// this particular quote.
/// </para>
/// </remarks>
public sealed class ApprovalApiTests
{
    private const string Processes = "/api/v1/crm/approvals/processes";
    private const string Requests = "/api/v1/crm/approvals/requests";
    private const string Decisions = "/api/v1/crm/approvals/decisions";
    private const string Inbox = "/api/v1/crm/approvals/inbox";
    private const string Members = "/api/v1/crm/org/members";

    private const string Rep = "rep-northwind-1";
    private const string Manager = "manager-northwind-1";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A submitter cannot approve their own request.</summary>
    /// <remarks>
    /// The control the whole mechanism exists for. Everything else here is machinery; this is the
    /// point.
    /// </remarks>
    [Fact]
    public async Task ASubmitterCannotApproveTheirOwnRequest()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var quote = await WorldAsync(app, discount: 300m);

        await ProcessAsync(app, OverTwoHundred([Step("Manager", ApproverKind.SubmittersManager)]));

        var submitted = await SubmitAsync(app, quote, CrmTokens.Northwind);

        submitted.Required.ShouldBeTrue();

        var response = await app.PostAsync(
            Decisions,
            new DecideApproval(
                submitted.RequestId!.Value, ApprovalDecision.Approved, "Looks fine to me."),
            CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Problem(response)).GetProperty("code").GetString().ShouldBe("crm.approval_self");
    }

    /// <summary>Nothing needs approval unless a process says so, and that is said out loud.</summary>
    /// <remarks>
    /// Most quotes are under every threshold anybody configured. Returning nothing would make the
    /// common case indistinguishable from a failed call.
    /// </remarks>
    [Fact]
    public async Task NoProcessMeansNoApprovalAndTheAnswerSaysSo()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var quote = await WorldAsync(app, discount: 10m);

        await ProcessAsync(app, OverTwoHundred([Step("Manager", ApproverKind.SubmittersManager)]));

        var submitted = await SubmitAsync(app, quote, CrmTokens.Northwind);

        submitted.Required.ShouldBeFalse("a ten-unit discount is under the threshold.");
        submitted.RequestId.ShouldBeNull();
        submitted.Process.ShouldBeNull();
    }

    /// <summary>Two steps are asked in order, and the request completes on the last.</summary>
    [Fact]
    public async Task TwoStepsAreAskedInOrderAndTheLastCompletesIt()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var quote = await WorldAsync(app, discount: 300m);

        await ProcessAsync(app, OverTwoHundred(
        [
            Step("Manager", ApproverKind.SubmittersManager),
            Step("Finance", ApproverKind.Named, Manager),
        ]));

        var submitted = await SubmitAsync(app, quote, CrmTokens.Northwind);

        submitted.AwaitingStep.ShouldBe(0);
        submitted.AwaitingLabel.ShouldBe("Manager");

        var first = await DecideAsync(
            app, submitted.RequestId!.Value, ApprovalDecision.Approved, CrmTokens.NorthwindManager);

        first.Status.ShouldBe("Pending", "one of two is not an approval.");
        first.AwaitingStep.ShouldBe(1);
        first.AwaitingLabel.ShouldBe("Finance");

        var second = await DecideAsync(
            app, submitted.RequestId!.Value, ApprovalDecision.Approved, CrmTokens.NorthwindManager);

        second.Status.ShouldBe("Approved");
        second.AwaitingLabel.ShouldBeNull();
    }

    /// <summary>A rejection ends the request; no later step is asked.</summary>
    /// <remarks>
    /// A later approver being asked anyway would turn "no" into "not yet", which is a different
    /// answer and one nobody gave.
    /// </remarks>
    [Fact]
    public async Task ARejectionEndsTheRequest()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var quote = await WorldAsync(app, discount: 300m);

        await ProcessAsync(app, OverTwoHundred(
        [
            Step("Manager", ApproverKind.SubmittersManager),
            Step("Finance", ApproverKind.Named, Manager),
        ]));

        var submitted = await SubmitAsync(app, quote, CrmTokens.Northwind);

        var decided = await DecideAsync(
            app, submitted.RequestId!.Value, ApprovalDecision.Rejected, CrmTokens.NorthwindManager);

        decided.Status.ShouldBe("Rejected");

        var again = await app.PostAsync(
            Decisions,
            new DecideApproval(submitted.RequestId!.Value, ApprovalDecision.Approved, "Second go."),
            CrmTokens.NorthwindManager);

        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Problem(again)).GetProperty("code").GetString()
            .ShouldBe("crm.approval_already_decided");
    }

    /// <summary>Somebody who is not the step's approver is refused.</summary>
    [Fact]
    public async Task SomebodyWhoIsNotTheApproverIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var quote = await WorldAsync(app, discount: 300m);

        // A step naming somebody who is not the manager, submitted by the representative.
        await ProcessAsync(app, OverTwoHundred([Step("Finance", ApproverKind.Named, "cfo-1")]));

        await MemberAsync(app, new SetOrgMember("cfo-1", "Cy Okoro", OrgRole.Director, null));

        var submitted = await SubmitAsync(app, quote, CrmTokens.Northwind);

        var response = await app.PostAsync(
            Decisions,
            new DecideApproval(submitted.RequestId!.Value, ApprovalDecision.Approved, "Fine."),
            CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.approval_not_the_approver");
    }

    /// <summary>A step that resolves to nobody is refused when the request is submitted.</summary>
    /// <remarks>
    /// Refused later, the request would sit in a queue for a fortnight before anybody worked out
    /// why it never moved — and by then the deal it held up has gone.
    /// </remarks>
    [Fact]
    public async Task AStepThatResolvesToNobodyIsRefusedAtSubmission()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var quote = await WorldAsync(app, discount: 300m);

        // Nobody has been placed above the representative, so their manager is nobody.
        await MemberAsync(app, new SetOrgMember(Rep, "Ada Rowe", OrgRole.Representative, null));

        await ProcessAsync(app, OverTwoHundred([Step("Manager", ApproverKind.SubmittersManager)]));

        var response = await app.PostAsync(
            Requests, new SubmitForApproval(ApprovalSubject.Quote, quote), CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.approval_approver_unresolved");
    }

    /// <summary>One thing cannot have two approvals waiting on it.</summary>
    [Fact]
    public async Task OneThingCannotHaveTwoApprovalsWaiting()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var quote = await WorldAsync(app, discount: 300m);

        await ProcessAsync(app, OverTwoHundred([Step("Manager", ApproverKind.SubmittersManager)]));

        await SubmitAsync(app, quote, CrmTokens.Northwind);

        var response = await app.PostAsync(
            Requests, new SubmitForApproval(ApprovalSubject.Quote, quote), CrmTokens.Northwind,
            idempotencyKey: "second-submission");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.approval_already_pending");
    }

    /// <summary>The narrower process wins, by priority.</summary>
    [Fact]
    public async Task ThePriorityDecidesWhichProcessGoverns()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var quote = await WorldAsync(app, discount: 600m);

        await ProcessAsync(app, new DefineApprovalProcess(
            "over_five_hundred", "Over 500", ApprovalSubject.Quote, 10,
            [new ApprovalCriterion("discount", GuardOperator.GreaterThan, "500")],
            [Step("Director", ApproverKind.Named, Manager)]));

        await ProcessAsync(app, OverTwoHundred([Step("Manager", ApproverKind.SubmittersManager)]));

        (await SubmitAsync(app, quote, CrmTokens.Northwind)).Process.ShouldBe(
            "over_five_hundred", "both match; the lower priority governs.");
    }

    /// <summary>The inbox shows what is waiting on the caller, and nothing else.</summary>
    [Fact]
    public async Task TheInboxShowsWhatIsWaitingOnTheCaller()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var quote = await WorldAsync(app, discount: 300m);

        await ProcessAsync(app, OverTwoHundred([Step("Manager", ApproverKind.SubmittersManager)]));

        var submitted = await SubmitAsync(app, quote, CrmTokens.Northwind);

        var mine = await InboxAsync(app, CrmTokens.NorthwindManager);

        mine.Waiting.ShouldHaveSingleItem().RequestId.ShouldBe(submitted.RequestId!.Value);
        mine.Waiting[0].StepLabel.ShouldBe("Manager");
        mine.Waiting[0].SubmittedBy.ShouldBe(Rep);

        (await InboxAsync(app, CrmTokens.Northwind)).Waiting.ShouldBeEmpty(
            "an inbox full of things you are forbidden to decide is one people stop opening.");
    }

    /// <summary>A criterion on an attribute the subject does not have is refused when saved.</summary>
    [Fact]
    public async Task ACriterionOnAnUnknownAttributeIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        await WorldAsync(app, discount: 10m);

        var response = await app.PostAsync(
            Processes,
            new DefineApprovalProcess(
                "bad", "Bad", ApprovalSubject.Quote, 10,
                [new ApprovalCriterion("margin", GuardOperator.GreaterThan, "200")],
                [Step("Manager", ApproverKind.SubmittersManager)]),
            CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.approval_attribute_unknown");
    }

    /// <summary>A process with no steps is refused.</summary>
    [Fact]
    public async Task AProcessWithNoStepsIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        await WorldAsync(app, discount: 10m);

        var response = await app.PostAsync(
            Processes,
            new DefineApprovalProcess(
                "empty", "Empty", ApprovalSubject.Quote, 10,
                [new ApprovalCriterion("discount", GuardOperator.GreaterThan, "200")],
                []),
            CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.approval_process_without_steps");
    }

    /// <summary>Configuring a process is administrative; submitting one is not.</summary>
    [Fact]
    public async Task ConfiguringAProcessIsAdministrative()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        await WorldAsync(app, discount: 10m);

        (await app.PostAsync(
            Processes,
            OverTwoHundred([Step("Manager", ApproverKind.SubmittersManager)]),
            CrmTokens.Northwind))
            .StatusCode.ShouldBe(
                HttpStatusCode.Forbidden, "a representative does not write the approval rules.");
    }

    // ------------------------------------------------------------------------------- fixtures

    private static ApprovalStepDefinition Step(
        string label, ApproverKind kind, string? approver = null) =>
        new(label, kind, approver);

    private static DefineApprovalProcess OverTwoHundred(
        IReadOnlyList<ApprovalStepDefinition> steps) =>
        new(
            "over_two_hundred", "Over 200", ApprovalSubject.Quote, 50,
            [new ApprovalCriterion("discount", GuardOperator.GreaterThan, "200")],
            steps);

    private static async Task ProcessAsync(CrmApplication app, DefineApprovalProcess process)
    {
        (await app.PostAsync(Processes, process, CrmTokens.NorthwindManager))
            .StatusCode.ShouldBe(HttpStatusCode.OK, "declaring '" + process.Name + "' failed.");
    }

    private static async Task<ApprovalSubmitted> SubmitAsync(
        CrmApplication app, Guid quote, string token)
    {
        var response = await app.PostAsync(
            Requests, new SubmitForApproval(ApprovalSubject.Quote, quote), token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<ApprovalSubmitted>(response);
    }

    private static async Task<ApprovalDecided> DecideAsync(
        CrmApplication app, Guid request, ApprovalDecision decision, string token)
    {
        var response = await app.PostAsync(
            Decisions,
            new DecideApproval(request, decision, "Recorded."),
            token,
            idempotencyKey: Guid.NewGuid().ToString("N"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<ApprovalDecided>(response);
    }

    private static async Task<ApprovalInbox> InboxAsync(CrmApplication app, string token)
    {
        var response = await app.PostAsync(
            Inbox, new ReadApprovalInbox(), token, idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<ApprovalInbox>(response);
    }

    private static async Task MemberAsync(CrmApplication app, SetOrgMember member)
    {
        (await app.PostAsync(Members, member, CrmTokens.NorthwindManager))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<Guid> WorldAsync(CrmApplication app, decimal discount)
    {
        await MemberAsync(app, new SetOrgMember(Manager, "Bea Vance", OrgRole.Director, null));
        await MemberAsync(app, new SetOrgMember(Rep, "Ada Rowe", OrgRole.Representative, Manager));

        var account = await app.Crm.AccountAsync(
            CrmTokens.NorthwindTenant, Lifecycle.Customer, Cancellation);

        var contact = await app.Crm.ContactAsync(CrmTokens.NorthwindTenant, account, Cancellation);
        var (_, stage) = await app.Crm.ProcessAsync(CrmTokens.NorthwindTenant, 1, true, Cancellation);

        var opportunity = await app.Crm.OpportunityAsync(
            CrmTokens.NorthwindTenant, account, contact, stage, Cancellation);

        var (quote, _) = await app.Crm.QuoteAsync(
            CrmTokens.NorthwindTenant, opportunity, Cancellation);

        await app.Crm.AsTenantAsync(
            CrmTokens.NorthwindTenant,
            "UPDATE quote SET discount = @discount WHERE quote_id = @id",
            Cancellation,
            ("discount", (object?)discount),
            ("id", quote));

        return quote;
    }

    private static async Task<JsonElement> Problem(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Cancellation);

        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
