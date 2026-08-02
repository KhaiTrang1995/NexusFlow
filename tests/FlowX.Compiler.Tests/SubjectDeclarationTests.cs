using System;
using System.Linq;
using FlowX.Compiler.Diagnostics;
using Microsoft.CodeAnalysis;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// FLOWX1047 — a <c>[Subject]</c> the runtime could not read or could not record.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this rule prevents is silence, which is why the quiet cases are asserted as
/// heavily as the noisy ones.</strong> Every shape it refuses produces a system that runs
/// perfectly and cannot answer an erasure request, discovered months of rows later when
/// somebody exercises the right — and by then the repair is not a code change, because the
/// identifiers the handles would have been computed from were redacted on the way in. A rule
/// against that has to fire on all four shapes and on nothing else: a false positive stops a
/// build over a working flow, and a false negative is the defect itself.
/// </para>
/// <para>
/// The last two tests are the ones that keep it honest in the other direction. A flow that
/// marks nothing is silent, because most flows have no data subject and a rule that asked
/// every <c>Durable</c> flow to name one would be inventing a requirement the regulation does
/// not make.
/// </para>
/// </remarks>
public sealed class SubjectDeclarationTests
{
    private const string Preamble = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using FlowX;

        namespace Sample;

        public sealed record Filed(string PatientId);

        [Capability("intake.file", Version = "1.0.0", Authorization = Authorization.Authenticated, Idempotent = true)]
        public sealed class FileIntake : ICapability<Intake, Filed>
        {
            public ValueTask<Result<Filed>> ExecuteAsync(Intake input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new Filed("p-1")));
        }
        """;

    /// <summary>A flow over the given contracts, under the given profile.</summary>
    private static string FlowOver(string input, string output, string profile = "Durable") =>
        Preamble + "\n\n" + $$"""
        {{input}}
        {{output}}

        [Flow("patient.intake", Version = "1.0.0", Profile = ExecutionProfile.{{profile}}, Owner = "clinical")]
        public sealed partial class PatientIntakeFlow : Flow<Intake, IntakeResult>
        {
            protected override void Define(IFlowBuilder<Intake, IntakeResult> flow) => flow
                .Step<FileIntake>()
                .Return(ctx => new IntakeResult("p-1"));
        }
        """;

    private const string MarkedInput =
        "public sealed record Intake([property: Subject] string NationalId, string FullName);";

    private const string TwiceMarkedInput =
        "public sealed record Intake([property: Subject] string NationalId, [property: Subject] string PatientNumber);";

    private const string NumericSubjectInput =
        "public sealed record Intake([property: Subject] int NationalId, string FullName);";

    private const string PlainInput =
        "public sealed record Intake(string NationalId, string FullName);";

    private const string PlainOutput = "public sealed record IntakeResult(string PatientId);";

    private const string MarkedOutput =
        "public sealed record IntakeResult([property: Subject] string PatientId);";

    private static GeneratorRun Compile(string source)
    {
        GeneratorHarness.CompileErrorsIn(source).ShouldBeEmpty();

        return GeneratorHarness.Run(source);
    }

    private static string[] Messages(string source) =>
        [.. Compile(source).Diagnostics
            .Where(static d => string.Equals(d.Id, "FLOWX1047", StringComparison.Ordinal))
            .Select(static d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture))];

    // ------------------------------------------------------------------------- it fires

    /// <summary>Two members of one contract both claiming to be the subject is refused.</summary>
    /// <remarks>
    /// There is no defensible tie-break. Whichever the serialiser wrote first would become the
    /// handle, so the same flow rebuilt with a reordered contract would erase by a different
    /// member — and every row written before the reorder would become unreachable.
    /// </remarks>
    [Fact]
    public void TwoSubjectsOnOneContractAreRefused()
    {
        var messages = Messages(FlowOver(TwiceMarkedInput, PlainOutput));

        messages.ShouldHaveSingleItem();
        messages[0].ShouldContain("PatientIntakeFlow");
        messages[0].ShouldContain("more than one member");
        messages[0].ShouldContain("NationalId");
        messages[0].ShouldContain("PatientNumber");
    }

    /// <summary>A subject on the output contract is refused.</summary>
    /// <remarks>
    /// The handle is written onto the instance row before the first step runs. An output does
    /// not exist at that moment, and by the time it does the row it would have identified has
    /// already been written — so the marker could only ever be ignored.
    /// </remarks>
    [Fact]
    public void ASubjectOnTheOutputContractIsRefused()
    {
        var messages = Messages(FlowOver(PlainInput, MarkedOutput));

        messages.ShouldHaveSingleItem();
        messages[0].ShouldContain("output contract");
    }

    /// <summary>A subject that is not a string is refused.</summary>
    /// <remarks>
    /// A digest is computed over text, and a numeric or structured identifier has more than one
    /// faithful rendering. Two renderings are two subjects, so the choice of the canonical one
    /// belongs to the application that knows which it is — a platform that picked would silently
    /// split one person in two.
    /// </remarks>
    [Fact]
    public void ASubjectThatIsNotAStringIsRefused()
    {
        var messages = Messages(FlowOver(NumericSubjectInput, PlainOutput));

        messages.ShouldHaveSingleItem();
        messages[0].ShouldContain("NationalId");
        messages[0].ShouldContain("int");
    }

    /// <summary>A subject on a flow that journals nothing is refused.</summary>
    /// <remarks>
    /// <strong>The "declared and inert" shape, and the reason this is an error rather than a
    /// warning.</strong> An <c>Ephemeral</c> flow opens no instance row, so there is nowhere for
    /// the handle to be written and nothing for an erasure to find — while the contract reads,
    /// to a reviewer, as a flow whose records can be erased on request.
    /// </remarks>
    [Fact]
    public void ASubjectOnAFlowThatKeepsNoJournalIsRefused()
    {
        var messages = Messages(FlowOver(MarkedInput, PlainOutput, profile: "Ephemeral"));

        messages.ShouldHaveSingleItem();
        messages[0].ShouldContain("Ephemeral");
    }

    /// <summary>It is an error.</summary>
    /// <remarks>
    /// Each of the four shapes has a one-line fix that produces a flow this runtime serves
    /// today, and none of them is a correct program written for a platform that does not have
    /// the feature yet — the platform has it. What a suppression buys is a marked contract and
    /// a null column.
    /// </remarks>
    [Fact]
    public void ItIsAnError() =>
        FlowXDiagnostics.SubjectCannotBeRecorded.DefaultSeverity.ShouldBe(
            DiagnosticSeverity.Error,
            "the alternative to this rule is not a feature that does less, it is a deployment " +
            "that cannot answer an erasure request and does not find out until it is asked.");

    // ------------------------------------------------------------------------- it is quiet

    /// <summary>One string member of a durable flow's input is the shape that works.</summary>
    /// <remarks>
    /// And it is asserted through the generated code rather than through the absence of a
    /// diagnostic alone: a rule that stayed quiet while the generator emitted <c>null</c> would
    /// be quiet about exactly the defect it exists to prevent.
    /// </remarks>
    [Fact]
    public void OneStringSubjectOnADurableFlowsInputIsAccepted()
    {
        var run = Compile(FlowOver(MarkedInput, PlainOutput));

        run.Ids.ShouldNotContain("FLOWX1047");

        run.Plan.ShouldContain(
            "public const string? SubjectMember = \"NationalId\";",
            Case.Sensitive,
            "the marker's whole effect is this line and the argument it is passed to. Where " +
            "it is passed — DescribeInput, which needs a JsonSerializerContext this fixture " +
            "deliberately does not declare — is asserted end to end by " +
            "Healthcare.Tests.RedactionAtRestTests against a real journal row.");
    }

    /// <summary>A flow that marks nothing is silent and emits a null subject.</summary>
    /// <remarks>
    /// Most flows have no data subject: a funds transfer's journal is about an account rather
    /// than about a person the platform can identify. A rule that asked every durable flow to
    /// name one would be inventing a requirement.
    /// </remarks>
    [Fact]
    public void AFlowThatMarksNothingIsSilent()
    {
        var run = Compile(FlowOver(PlainInput, PlainOutput));

        run.Ids.ShouldNotContain("FLOWX1047");

        run.Plan.ShouldContain(
            "public const string? SubjectMember = null;",
            Case.Sensitive,
            "emitted even when there is none, so the payload writer's shape does not depend " +
            "on the contract.");
    }

    /// <summary>An ephemeral flow that marks nothing is silent too.</summary>
    /// <remarks>
    /// The profile is only half of the fourth rule. A rule that fired on every ephemeral flow
    /// would report most of the samples in this repository.
    /// </remarks>
    [Fact]
    public void AnEphemeralFlowThatMarksNothingIsSilent() =>
        Compile(FlowOver(PlainInput, PlainOutput, profile: "Ephemeral"))
            .Ids.ShouldNotContain("FLOWX1047");
}
