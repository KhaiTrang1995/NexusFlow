using FlowX;
using Shouldly;
using Xunit;

namespace Healthcare.Tests;

/// <summary>
/// Data residency: a refusal at admission, and what it does and does not buy.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read <see cref="TenantResidency"/> before reading these.</strong> Nothing here
/// selects a store, forwards a request or moves a row — a runtime in one region cannot do any
/// of those. What it can do is decline to process a clinic's work in a region that clinic's
/// data may not be processed in, before a lease is taken and before a row exists, so that a
/// misrouted call fails loudly rather than succeeding quietly. That is one half of a residency
/// guarantee; the other half is the deployment topology, and no test can supply it.
/// </para>
/// <para>
/// It is therefore not L4, and it does not reopen
/// <c>ADR-0051</c>: a fleet per region with its own database is exactly the topology that
/// record says a dedicated deployment is, and this is the check that the caller reached the
/// fleet its clinic belongs to.
/// </para>
/// </remarks>
public sealed class ResidencyTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A clinic pinned elsewhere is refused, and nothing is written.</summary>
    /// <remarks>
    /// The second assertion is the one that matters. A refusal that had already opened an
    /// instance row would have written the patient's identifier — redacted, but written — into
    /// a database in the region the pin exists to keep it out of.
    /// </remarks>
    [Fact]
    public async Task AClinicPinnedToAnotherRegionIsRefusedBeforeAnythingIsWritten()
    {
        await using var clinic = await ClinicHarness.CreateAsync(Cancellation, "eu-central-1");

        var refused = await clinic.AdmitAsync(
            Intakes.Treatment(), ClinicTokens.DublinTenant, "res-1", Cancellation);

        refused.IsFailure.ShouldBeTrue(
            "clinic-dublin is pinned to eu-west-1 and this deployment is eu-central-1.");

        refused.Error!.Code.ShouldBe(TenantErrors.ResidencyRefusedCode);

        refused.Error!.Category.ShouldBe(
            ErrorCategory.Forbidden,
            "terminal, not transient: waiting does not move the pod, so a caller must not " +
            "retry into it.");

        var connection = await clinic.DataSource.OpenConnectionAsync(Cancellation);
        await using var closing = connection.ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM flow_instance";

        (await command.ExecuteScalarAsync(Cancellation)).ShouldBe(
            0L,
            "the refusal is at admission. An instance row here would mean the identifier had " +
            "already been written in the region the pin exists to keep it out of.");
    }

    /// <summary>The same clinic is admitted by the deployment in the region it is pinned to.</summary>
    /// <remarks>
    /// The positive half, and it is not a formality: a residency check that refused everybody
    /// would pass the test above and be an outage. Nothing about the flow, the contracts or the
    /// capabilities differs between the two runs — only the region the deployment declares.
    /// </remarks>
    [Fact]
    public async Task TheSameClinicIsAdmittedByTheDeploymentInItsOwnRegion()
    {
        await using var clinic = await ClinicHarness.CreateAsync(Cancellation, "eu-west-1");

        var admitted = await clinic.AdmitAsync(
            Intakes.Treatment(), ClinicTokens.DublinTenant, "res-2", Cancellation);

        admitted.IsSuccess.ShouldBeTrue(
            "the identical intake, the identical flow, a deployment in the region the clinic " +
            $"is pinned to. {(admitted.IsFailure ? admitted.Error!.ToString() : string.Empty)}");
    }

    /// <summary>A clinic that is pinned nowhere is served wherever it arrives.</summary>
    /// <remarks>
    /// Absence means unpinned rather than forbidden, which is the only default that lets an
    /// existing multi-region deployment adopt this one clinic at a time. A deployment wanting
    /// the opposite states every clinic.
    /// </remarks>
    [Fact]
    public async Task AnUnpinnedClinicIsNotRefusedAnywhere()
    {
        await using var clinic = await ClinicHarness.CreateAsync(Cancellation, "ap-southeast-2");

        var admitted = await clinic.AdmitAsync(
            Intakes.Treatment(), "clinic-unpinned", "res-3", Cancellation);

        admitted.IsSuccess.ShouldBeTrue(
            "no requirement names clinic-unpinned, so no pin could refuse it.");
    }

    /// <summary>A pinned clinic in the wrong region is refused even when it names itself.</summary>
    /// <remarks>
    /// The refusal is decided against the tenant resolution, not against anything the caller
    /// supplies — the tenant itself comes from validated claims (ADR-0046), so there is no
    /// spelling of the request that reaches a different answer.
    /// </remarks>
    [Fact]
    public async Task ResidencyIsDecidedForEveryPinnedClinicAndNotOnlyTheFirst()
    {
        await using var clinic = await ClinicHarness.CreateAsync(Cancellation, "eu-west-1");

        var berlin = await clinic.AdmitAsync(
            Intakes.Treatment(), ClinicTokens.BerlinTenant, "res-4", Cancellation);

        var munich = await clinic.AdmitAsync(
            Intakes.Treatment(), ClinicTokens.MunichTenant, "res-5", Cancellation);

        berlin.IsFailure.ShouldBeTrue("clinic-berlin is pinned to eu-central-1.");
        munich.IsFailure.ShouldBeTrue("and so is clinic-munich, asserted separately.");

        berlin.Error!.Code.ShouldBe(TenantErrors.ResidencyRefusedCode);
        munich.Error!.Code.ShouldBe(TenantErrors.ResidencyRefusedCode);
    }
}
