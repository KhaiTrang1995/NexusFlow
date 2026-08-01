using FlowX;
using FlowX.Testing;
using Shouldly;
using Xunit;

namespace Workflow.Tests;

/// <summary>
/// Quality goal Q2: a capability is testable by constructing it and calling it.
/// </summary>
/// <remarks>
/// <para>
/// There is no host in this file and no journal either, which is worth noticing in a project
/// where every flow-level test needs both. A capability does not know its flow's profile: it
/// is a class with a method, and whether the orchestration around it journals its result is
/// none of its business. That is why <see cref="TestCapabilityContext"/> is enough here while
/// <c>FlowTestHost</c> is not enough one level up.
/// </para>
/// <para>
/// These are the business rules. The flow tests assert order, condition and recovery; these
/// assert what each step actually decides.
/// </para>
/// </remarks>
public sealed class CapabilityTests
{
    private static readonly CapabilityContext Context = new TestCapabilityContext("key-1");

    [Fact]
    public async Task ValidateOfferAcceptsAWellFormedOffer()
    {
        var result = await new ValidateOffer()
            .ExecuteAsync(Offers.Permanent(), Context, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.CandidateId.ShouldBe("c-1");
        result.Value.Employment.ShouldBe(EmploymentType.Permanent);
        result.Value.Equipment.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ValidateOfferRejectsAnOfferThatNamesNobody(string candidateId)
    {
        var offer = Offers.Permanent() with { CandidateId = candidateId };

        var result = await new ValidateOffer()
            .ExecuteAsync(offer, Context, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("onboarding.missing_candidate");
        result.Error.Category.ShouldBe(ErrorCategory.Validation);
    }

    /// <summary>
    /// Reserving under the same idempotency key twice has the effect of doing it once.
    /// </summary>
    /// <remarks>
    /// This is what <c>Idempotent = true</c> on the capability promises, and it is what makes
    /// a journaled retry safe: a resumed instance that re-runs a step whose commit was lost
    /// must not open a second payroll record.
    /// </remarks>
    [Fact]
    public async Task OpeningAPayrollRecordTwiceUnderOneKeyOpensOne()
    {
        var people = new InMemoryPeopleDirectory();
        var capability = new OpenPayrollRecord(people);
        var offer = (await new ValidateOffer()
            .ExecuteAsync(Offers.Permanent(), Context, TestContext.Current.CancellationToken)).Value;

        var first = await capability.ExecuteAsync(offer, Context, TestContext.Current.CancellationToken);
        var second = await capability.ExecuteAsync(offer, Context, TestContext.Current.CancellationToken);

        first.Value.PayrollId.ShouldBe(second.Value.PayrollId);
        people.OpenPayrollRecords.ShouldBe(1);
    }

    /// <summary>A directory that will not answer is a business failure, not an exception.</summary>
    [Fact]
    public async Task CreateIdentityReportsAnUnavailableDirectory()
    {
        var result = await new CreateIdentity(new RefusingDirectory())
            .ExecuteAsync(
                new ValidatedOffer("c-1", EmploymentType.Permanent, "london", [], RequiresBackgroundCheck: false),
                Context,
                TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("identity.directory_unavailable");
        result.Error.Category.ShouldBe(ErrorCategory.Unavailable);
    }

    /// <summary>A full site is a conflict, and the error carries which site.</summary>
    [Fact]
    public async Task AllocateDeskReportsAFullSiteWithTheSiteInTheError()
    {
        var result = await new AllocateDesk(new FullSite())
            .ExecuteAsync(
                new ProvisionWorkspace("c-1", "london"),
                Context,
                TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("workspace.no_desk");
        result.Error.Category.ShouldBe(ErrorCategory.Conflict);
        result.Error.Data!["site"].ShouldBe("london");
    }

    /// <summary>An item that needs no approval is cleared by policy rather than by a person.</summary>
    [Fact]
    public async Task AutoClearRecordsWhoClearedTheItem()
    {
        var result = await new AutoClearEquipment()
            .ExecuteAsync(
                new EquipmentRequest("laptop-bag", NeedsApproval: false),
                Context,
                TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ClearedBy.ShouldBe("policy");
    }

    private sealed class RefusingDirectory : IPeopleDirectory
    {
        public ValueTask<string?> CreateAccountAsync(string candidateId, string idempotencyKey, CancellationToken ct)
            => ValueTask.FromResult<string?>(null);

        public ValueTask DisableAccountAsync(string idempotencyKey, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask<string> OpenPayrollAsync(string candidateId, string idempotencyKey, CancellationToken ct)
            => ValueTask.FromResult("unused");

        public ValueTask ClosePayrollAsync(string idempotencyKey, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask<string> SignAgreementAsync(string candidateId, string idempotencyKey, CancellationToken ct)
            => ValueTask.FromResult("unused");

        public ValueTask VoidAgreementAsync(string idempotencyKey, CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class FullSite : IFacilities
    {
        public ValueTask<string?> AllocateDeskAsync(string site, string idempotencyKey, CancellationToken ct)
            => ValueTask.FromResult<string?>(null);

        public ValueTask ReleaseDeskAsync(string idempotencyKey, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask<string?> IssuePassAsync(string deskId, string idempotencyKey, CancellationToken ct)
            => ValueTask.FromResult<string?>("unused");

        public ValueTask CancelPassAsync(string idempotencyKey, CancellationToken ct) => ValueTask.CompletedTask;
    }
}
