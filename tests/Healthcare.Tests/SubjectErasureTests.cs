using FlowX;
using Shouldly;
using Xunit;

namespace Healthcare.Tests;

/// <summary>
/// The right to erasure, against the rows an admission actually wrote.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What "erased" means here is asserted rather than assumed.</strong> The payloads go
/// and the skeleton stays: the instance row, its steps and its staged events survive with every
/// recorded value null and the handle cleared. Deleting the rows outright would destroy the
/// system's own record that a flow ran, at what version and with what outcome — which is
/// operational history rather than personal data — so the tests below check both halves, and a
/// change that started deleting rows would fail on the second.
/// </para>
/// </remarks>
public sealed class SubjectErasureTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>
    /// A dry run reports the counts the confirmation will produce, and changes nothing.
    /// </summary>
    /// <remarks>
    /// <strong>Both halves, and the first is why the dry run runs the updates.</strong> An
    /// operator deciding whether to destroy a patient's records is entitled to the row counts
    /// rather than to an estimate of them, so the same three statements run inside a
    /// transaction nothing commits — which is also what keeps the two modes from being two code
    /// paths that were meant to agree.
    /// </remarks>
    [Fact]
    public async Task ADryRunReportsWhatWouldGoAndRemovesNothing()
    {
        await using var clinic = await ClinicHarness.CreateAsync(Cancellation);

        var admitted = await clinic.AdmitAsync(
            Intakes.Treatment(), ClinicTokens.BerlinTenant, "era-1", Cancellation);

        admitted.IsSuccess.ShouldBeTrue();

        var proposed = await Erase(clinic, ErasureMode.DryRun);

        proposed.Matched.ShouldBe(1);
        proposed.Erased.ShouldBe(1);
        proposed.IsComplete.ShouldBeTrue();
        proposed.Mode.ShouldBe(ErasureMode.DryRun);
        proposed.StepsCleared.ShouldBeGreaterThan(0, "the counts are real, not estimated.");

        var instance = await clinic.InstanceAsync("era-1", Cancellation);

        (await clinic.ColumnAsync(instance, "input", Cancellation)).ShouldNotBeNull(
            "a dry run that had removed the input would be a confirmation with a misleading " +
            "name — the worst possible defect in a compliance tool.");

        (await clinic.ColumnAsync(instance, "subject_digest", Cancellation)).ShouldNotBeNull();

        var done = await Erase(clinic, ErasureMode.Confirm);

        done.StepsCleared.ShouldBe(
            proposed.StepsCleared,
            "the confirmation produces the numbers the dry run reported, because it is the same " +
            "three statements.");

        done.EventsCleared.ShouldBe(proposed.EventsCleared);
    }

    /// <summary>A confirmation clears every recorded payload and the handle itself.</summary>
    [Fact]
    public async Task AConfirmedErasureClearsEveryPayloadAndTheHandle()
    {
        await using var clinic = await ClinicHarness.CreateAsync(Cancellation);

        var admitted = await clinic.AdmitAsync(
            Intakes.Treatment(), ClinicTokens.BerlinTenant, "era-2", Cancellation);

        admitted.IsSuccess.ShouldBeTrue();

        var instance = await clinic.InstanceAsync("era-2", Cancellation);

        (await clinic.StepResultsAsync(instance, Cancellation))
            .Count(static result => result is not null)
            .ShouldBeGreaterThan(0, "there has to be something to clear for this to mean anything.");

        var receipt = await Erase(clinic, ErasureMode.Confirm);

        receipt.Erased.ShouldBe(1);
        receipt.StepsCleared.ShouldBeGreaterThan(0);
        receipt.IsComplete.ShouldBeTrue();

        (await clinic.ColumnAsync(instance, "input", Cancellation)).ShouldBeNull();
        (await clinic.ColumnAsync(instance, "state_bag", Cancellation)).ShouldBeNull();
        (await clinic.ColumnAsync(instance, "subject_digest", Cancellation)).ShouldBeNull();

        (await clinic.StepResultsAsync(instance, Cancellation))
            .ShouldAllBe(static result => result == null);

        (await clinic.EventBodiesAsync(instance, Cancellation))
            .ShouldAllBe(static body => body == null);
    }

    /// <summary>The row survives the erasure, carrying what is not about the patient.</summary>
    /// <remarks>
    /// A durable execution that vanished mid-recovery would be indistinguishable from one that
    /// never happened, and "which flow ran, at what version, ending how" is the system's record
    /// of itself. This is the assertion that a future change to <c>ErasureSql</c> from
    /// <c>UPDATE</c> to <c>DELETE</c> would fail.
    /// </remarks>
    [Fact]
    public async Task ErasureLeavesTheRecordThatAFlowRan()
    {
        await using var clinic = await ClinicHarness.CreateAsync(Cancellation);

        var admitted = await clinic.AdmitAsync(
            Intakes.Treatment(), ClinicTokens.BerlinTenant, "era-3", Cancellation);

        admitted.IsSuccess.ShouldBeTrue();

        var instance = await clinic.InstanceAsync("era-3", Cancellation);

        await Erase(clinic, ErasureMode.Confirm);

        (await clinic.ColumnAsync(instance, "flow_id", Cancellation)).ShouldBe("patient.intake");
        (await clinic.ColumnAsync(instance, "flow_version", Cancellation)).ShouldBe("1.0.0");
        (await clinic.ColumnAsync(instance, "state", Cancellation)).ShouldBe("Completed");
    }

    /// <summary>Erasing twice is not an error and finds nothing the second time.</summary>
    /// <remarks>
    /// The handle is cleared by the first pass, so the row is unreachable by the predicate for
    /// ever afterwards. That is what makes a repeated request — the ordinary shape of an
    /// operator following up — cheap and truthful rather than a second sweep of the table.
    /// </remarks>
    [Fact]
    public async Task ASecondErasureMatchesNothing()
    {
        await using var clinic = await ClinicHarness.CreateAsync(Cancellation);

        var admitted = await clinic.AdmitAsync(
            Intakes.Treatment(), ClinicTokens.BerlinTenant, "era-4", Cancellation);

        admitted.IsSuccess.ShouldBeTrue();

        await Erase(clinic, ErasureMode.Confirm);

        var second = await Erase(clinic, ErasureMode.Confirm);

        second.Matched.ShouldBe(0);
        second.IsComplete.ShouldBeTrue("nothing was withheld, because nothing was found.");
    }

    /// <summary>An instance that has not finished is withheld and says why.</summary>
    /// <remarks>
    /// <para>
    /// <strong>The refusal that keeps erasure from corrupting a running saga.</strong> Clearing
    /// the state bag of a flow that is still executing hands every step after the frontier the
    /// values of an execution nobody performed — a well-formed, absent document, which is the
    /// one shape <c>RestoreState</c> cannot detect.
    /// </para>
    /// <para>
    /// The receipt reports it rather than failing, because a partial erasure that named what it
    /// could not do is more useful to an operator than a refusal that did nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ARunningInstanceIsWithheldRatherThanErased()
    {
        await using var clinic = await ClinicHarness.CreateAsync(Cancellation);

        var admitted = await clinic.AdmitAsync(
            Intakes.Treatment(), ClinicTokens.BerlinTenant, "era-5", Cancellation);

        admitted.IsSuccess.ShouldBeTrue();

        var instance = await clinic.InstanceAsync("era-5", Cancellation);

        await clinic.ReopenAsync(instance, Cancellation);

        var receipt = await Erase(clinic, ErasureMode.Confirm);

        receipt.Matched.ShouldBe(1);
        receipt.Erased.ShouldBe(0);
        receipt.IsComplete.ShouldBeFalse("an erasure that reported success here would be lying.");

        receipt.Withheld.ShouldHaveSingleItem().Reason.ShouldContain(
            "Running",
            Case.Sensitive,
            "the reason has to name the condition that must clear, because the operator's " +
            "next action is to wait and run it again.");

        (await clinic.ColumnAsync(instance, "input", Cancellation)).ShouldNotBeNull(
            "and the payload is genuinely still there, not merely reported as withheld.");
    }

    /// <summary>An erasure reaches one patient's records and not another's.</summary>
    [Fact]
    public async Task ErasureReachesOnePatientAndLeavesTheOther()
    {
        await using var clinic = await ClinicHarness.CreateAsync(Cancellation);

        (await clinic.AdmitAsync(
            Intakes.Treatment(), ClinicTokens.BerlinTenant, "era-6a", Cancellation))
            .IsSuccess.ShouldBeTrue();

        (await clinic.AdmitAsync(
            Intakes.Treatment(Intakes.OtherPatientId),
            ClinicTokens.BerlinTenant,
            "era-6b",
            Cancellation))
            .IsSuccess.ShouldBeTrue();

        var receipt = await Erase(clinic, ErasureMode.Confirm);

        receipt.Matched.ShouldBe(1, "two patients, one handle each.");

        var other = await clinic.InstanceAsync("era-6b", Cancellation);

        (await clinic.ColumnAsync(other, "input", Cancellation)).ShouldNotBeNull(
            "the other patient's record is untouched. A handle that matched by name or by " +
            "date of birth would have taken it too.");
    }

    /// <summary>Two admissions of one patient are erased together.</summary>
    [Fact]
    public async Task EveryAdmissionOfOnePatientIsErasedTogether()
    {
        await using var clinic = await ClinicHarness.CreateAsync(Cancellation);

        (await clinic.AdmitAsync(
            Intakes.Treatment(), ClinicTokens.BerlinTenant, "era-7a", Cancellation))
            .IsSuccess.ShouldBeTrue();

        (await clinic.AdmitAsync(
            Intakes.Treatment(), ClinicTokens.BerlinTenant, "era-7b", Cancellation))
            .IsSuccess.ShouldBeTrue();

        var receipt = await Erase(clinic, ErasureMode.Confirm);

        receipt.Matched.ShouldBe(
            2,
            "'everything about this person' is the request, and a per-instance answer would " +
            "make the operator responsible for enumerating the admissions.");

        receipt.Erased.ShouldBe(2);
    }

    /// <summary>
    /// An instance holding an event a declared consumer has not taken is withheld.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The half of the design that is easy to get backwards.</strong> Emptying the body
    /// of an event a publisher has not drained keeps the promise's shape and discards its
    /// content: the consumer receives a message that does not carry what its type says it
    /// carries. So the instance is withheld — by the same predicate
    /// <c>PostgresRetention</c> refuses to purge one with, asked of the same rows, because two
    /// copies of it would eventually disagree.
    /// </para>
    /// <para>
    /// <strong>And it is the reason the consumer set has to be declared.</strong> This
    /// deployment stages an event and wires no publisher, so the default — one publisher —
    /// holds every instance for a consumer that does not exist. The harness says
    /// <c>Publisher = false</c>, which is a fact about the deployment that nothing in the
    /// database can discover: an undrained row looks identical whether the publisher is missing
    /// or merely down.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnInstanceOwingAnEventToADeclaredConsumerIsWithheld()
    {
        await using var clinic = await ClinicHarness.CreateAsync(Cancellation);

        var admitted = await clinic.AdmitAsync(
            Intakes.Treatment(), ClinicTokens.BerlinTenant, "era-8", Cancellation);

        admitted.IsSuccess.ShouldBeTrue();

        var receipt = await clinic.ErasureWithAPublisherDeclared.EraseAsync(
            new ErasureRequest
            {
                SubjectDigest = SubjectDigest.Of(Intakes.BerlinPatientId),
                TenantId = ClinicTokens.BerlinTenant,
                Mode = ErasureMode.Confirm,
            },
            Cancellation);

        receipt.IsSuccess.ShouldBeTrue();
        receipt.Value.Matched.ShouldBe(1);
        receipt.Value.Erased.ShouldBe(0);

        receipt.Value.Withheld.ShouldHaveSingleItem().Reason.ShouldContain(
            "owed to a declared consumer",
            Case.Sensitive,
            "the flow emits patient.admitted, nothing has published it, and this erasure was " +
            "told a publisher exists.");

        var instance = await clinic.InstanceAsync("era-8", Cancellation);

        (await clinic.EventBodiesAsync(instance, Cancellation))
            .ShouldAllBe(static body => body != null,
                "and the body is genuinely still there. A consumer that received an emptied " +
                "message would have no way to tell it apart from one the flow meant to send.");
    }

    private static async ValueTask<ErasureReceipt> Erase(ClinicHarness clinic, ErasureMode mode)
    {
        var receipt = await clinic.Erasure.EraseAsync(
            new ErasureRequest
            {
                SubjectDigest = SubjectDigest.Of(Intakes.BerlinPatientId),
                TenantId = ClinicTokens.BerlinTenant,
                Mode = mode,
            },
            Cancellation);

        receipt.IsSuccess.ShouldBeTrue(
            receipt.IsFailure ? receipt.Error.ToString() : string.Empty);

        return receipt.Value;
    }
}
