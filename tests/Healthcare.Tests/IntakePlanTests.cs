using FlowX;
using Shouldly;
using Xunit;

namespace Healthcare.Tests;

/// <summary>
/// What the compiler produced from <c>PatientIntakeFlow</c>, asserted against the artifact
/// rather than against the source.
/// </summary>
/// <remarks>
/// These need no database: what they read is the compiled plan and the generated members. They
/// are here rather than in <c>FlowX.Compiler.Tests</c> because the subject of each is this
/// sample's own declaration, and a rule about the generator belongs beside the generator.
/// </remarks>
public sealed class IntakePlanTests
{
    /// <summary>The flow is durable, which is what gives erasure an instance row to key on.</summary>
    [Fact]
    public void TheFlowIsDurable()
    {
        PatientIntakeFlow.Plan.Flow.Profile.ShouldBe(
            ExecutionProfile.Durable,
            "a flow that journals nothing has nowhere to record the subject's handle, which " +
            "is one of the four shapes FLOWX1047 refuses.");
    }

    /// <summary>The generated partial names the member the subject's handle is derived from.</summary>
    /// <remarks>
    /// <strong>This is the line the whole erasure story rests on.</strong> It is emitted from
    /// the <c>[Subject]</c> marker on <see cref="PatientIntake"/> and passed to
    /// <c>JournalPayload.Of</c> by the generated <c>DescribeInput</c>. Unmark the contract
    /// member and this is <c>null</c>, the column is null on every row, and
    /// <c>SubjectErasureTests</c> goes red — which is the failure arriving at build-adjacent
    /// speed rather than when somebody exercises the right.
    /// </remarks>
    [Fact]
    public void TheGeneratedFlowNamesTheDataSubject()
    {
        PatientIntakeFlow.SubjectMember.ShouldBe(nameof(PatientIntake.NationalId));
    }

    /// <summary>Both marked members reach the redaction array, and nothing else does.</summary>
    /// <remarks>
    /// Ordinally sorted, and read off both contracts. <c>ConsentReference</c> is deliberately
    /// absent: a journal with nothing legible in it is one no operator can resolve an incident
    /// from, and marking everything is the failure mode on the other side of marking nothing.
    /// </remarks>
    [Fact]
    public void TheGeneratedFlowNamesExactlyTheSensitiveMembers()
    {
        PatientIntakeFlow.SensitiveMembers.ShouldBe(
            [nameof(PatientIntake.FullName), nameof(PatientIntake.NationalId)]);
    }

    /// <summary>The subject is also sensitive, which is the arrangement this sample exists for.</summary>
    /// <remarks>
    /// Asserted as a relationship rather than as two facts, because it is the relationship that
    /// is interesting: the member that must never be written down is the one the rows have to
    /// be found by, and the digest is computed before the pass that removes it.
    /// </remarks>
    [Fact]
    public void TheDataSubjectIsItselfASensitiveMember()
    {
        PatientIntakeFlow.SensitiveMembers.ShouldContain(PatientIntakeFlow.SubjectMember!);
    }

    /// <summary>The record write is the one compensable step.</summary>
    [Fact]
    public void TheRecordWriteIsCompensated()
    {
        var compensable = PatientIntakeFlow.Plan.Graph.Steps
            .Where(static step => step.Compensation is not null)
            .Select(static step => step.Capability!.Id)
            .ToList();

        compensable.ShouldBe(["records.store"]);
    }

    /// <summary>Consent is verified before anything about the patient is written.</summary>
    /// <remarks>
    /// An ordering assertion rather than a presence one. A flow that verified consent after
    /// resolving the patient id would have written a row about a person before establishing
    /// that it may — and the step would still be present, so a test that only counted steps
    /// would not notice.
    /// </remarks>
    [Fact]
    public void ConsentIsVerifiedBeforeThePatientIsResolved()
    {
        var order = PatientIntakeFlow.Plan.Graph.Steps
            .Where(static step => step.Capability is not null)
            .Select(static step => step.Capability!.Id)
            .ToList();

        order.IndexOf("consent.verify").ShouldBeLessThan(order.IndexOf("patient.deduplicate"));
        order.IndexOf("patient.deduplicate").ShouldBeLessThan(order.IndexOf("records.store"));
    }
}
