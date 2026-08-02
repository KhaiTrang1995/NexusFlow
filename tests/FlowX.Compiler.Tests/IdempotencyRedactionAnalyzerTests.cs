using System;
using System.Linq;
using FlowX.Compiler.Analysis;
using FlowX.Compiler.Diagnostics;
using Microsoft.CodeAnalysis;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// FLOWX1040 — an <c>Idempotency</c> window on a flow whose result cannot be recorded without
/// redaction.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The silent direction is where this rule earns its severity.</strong> It is an
/// <em>error</em>, so a false positive stops a build over a working flow. Every case below that
/// must stay quiet is asserted at least as heavily as the case that must fire: a flow that marks
/// nothing, a set with no window, a mark on an intermediate contract rather than on the flow's
/// own two, and a <c>.WithPolicy(...)</c> whose enclosing flow the walk cannot find.
/// </para>
/// <para>
/// <strong>The rule is flow-wide and that is deliberate</strong>, which is what
/// <see cref="AMarkOnTheOutputContractCountsToo"/> pins from one side and
/// <see cref="AMarkOnAnIntermediateContractIsNotTheFlowsOwn"/> from the other.
/// <c>SensitiveMembers</c> is read off the flow's input and output contracts and applied to
/// every document the flow writes, so those two are the whole of what makes a flow
/// unrecordable — and a rule that read a step's own contracts instead would be answering a
/// narrower question than the mechanism asks. ADR-0042 §1.4 rejects the narrower rule and says
/// why: the match is by name, at every depth, over a graph reaching referenced assemblies, and a
/// traversal wrong in the permissive direction ships a silently fabricated replay.
/// </para>
/// </remarks>
public sealed class IdempotencyRedactionAnalyzerTests
{
    private const string Preamble = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using FlowX;

        namespace Sample;

        public sealed record Validated(string DebtorIban, decimal Amount);

        [Capability("transfer.validate", Version = "1.0.0", Authorization = Authorization.Authenticated, Idempotent = true)]
        public sealed class ValidateTransfer : ICapability<Transfer, Validated>
        {
            public ValueTask<Result<Validated>> ExecuteAsync(Transfer input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new Validated(input.DebtorIban, input.Amount)));
        }
        """;

    /// <summary>A flow over the given contracts, declaring the given set on its one step.</summary>
    private static string FlowOver(string input, string output, string policies) =>
        Preamble + "\n\n" + $$"""
        {{input}}
        {{output}}

        public static class Policies
        {
            {{policies}}
        }

        [Flow("transfer.execute", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "payments")]
        public sealed partial class ExecuteTransferFlow : Flow<Transfer, TransferResult>
        {
            protected override void Define(IFlowBuilder<Transfer, TransferResult> flow) => flow
                .Step<ValidateTransfer>().WithPolicy(Policies.Admission)
                .Return(ctx => new TransferResult("id"));
        }
        """;

    private const string MarkedInput =
        "public sealed record Transfer([property: Sensitive] string DebtorIban, decimal Amount);";

    private const string PlainInput =
        "public sealed record Transfer(string DebtorIban, decimal Amount);";

    private const string PlainOutput = "public sealed record TransferResult(string Id);";

    private const string Window =
        """public static readonly PolicySet Admission = PolicySet.Named("a").Idempotency(TimeSpan.FromHours(24));""";

    private static string[] Analyze(string source)
    {
        GeneratorHarness.CompileErrorsIn(source).ShouldBeEmpty();

        return GeneratorHarness.Analyze(source, new DeclaredPolicyAnalyzer());
    }

    private static string[] Messages(string source)
    {
        GeneratorHarness.CompileErrorsIn(source).ShouldBeEmpty();

        return [.. GeneratorHarness
            .AnalyzeWithMessages(source, new DeclaredPolicyAnalyzer())
            .Where(static m => m.Contains("[redacted]", StringComparison.Ordinal))];
    }

    // ------------------------------------------------------------------------- it fires

    /// <summary>A marked input contract and a declared window is the reported case.</summary>
    [Fact]
    public void AWindowOnAFlowThatMarksItsInputIsReported() =>
        Analyze(FlowOver(MarkedInput, PlainOutput, Window)).ShouldContain("FLOWX1040");

    /// <summary>It is an error, not a warning.</summary>
    /// <remarks>
    /// The severity is the decision this rule turns on and the one a later reader is most likely
    /// to tidy into agreement with FLOWX1032 beside it. They are different: that rule reports a
    /// stage nothing implements and a program that becomes correct when it does, and this one
    /// reports a declaration the platform cannot serve for this flow — where the alternative to
    /// the error is not a policy that does less but a step that fails at run time on its first
    /// execution, after its capability has already been dispatched.
    /// </remarks>
    [Fact]
    public void ItIsAnError() =>
        FlowXDiagnostics.IdempotencyCannotRecordARedactedResult.DefaultSeverity.ShouldBe(
            DiagnosticSeverity.Error,
            "the alternative to this rule is not a policy that does less, it is a step that " +
            "fails at run time on its first execution after its capability has been dispatched.");

    /// <summary>The message names the set, the flow and the member that makes it unrecordable.</summary>
    /// <remarks>
    /// All three, because the author's next question after "which set" is "which member", and a
    /// message that named only the set would send them to read every contract the flow touches.
    /// </remarks>
    [Fact]
    public void TheMessageNamesTheSetTheFlowAndTheMarkedMember()
    {
        var messages = Messages(FlowOver(MarkedInput, PlainOutput, Window));

        messages.ShouldHaveSingleItem();
        messages[0].ShouldContain("Policies.Admission");
        messages[0].ShouldContain("ExecuteTransferFlow");
        messages[0].ShouldContain("DebtorIban");
        messages[0].ShouldContain("[redacted]");
    }

    /// <summary>A mark on the output contract counts, because the redaction set reads both.</summary>
    /// <remarks>
    /// The half a rule written from the obvious reading would miss. <c>SensitiveMembers</c> is
    /// the union of the marks on <c>Flow&lt;TIn, TOut&gt;</c>'s two arguments, so a flow whose
    /// <em>result</em> carries the marked member redacts every document it writes just as
    /// thoroughly as one whose input does.
    /// </remarks>
    [Fact]
    public void AMarkOnTheOutputContractCountsToo() =>
        Analyze(FlowOver(
            PlainInput,
            "public sealed record TransferResult([property: Sensitive] string Id);",
            Window))
            .ShouldContain("FLOWX1040");

    // ------------------------------------------------------------------------ it is silent

    /// <summary>A flow that marks nothing may declare a window.</summary>
    /// <remarks>
    /// The control, and the one that makes every assertion above mean something. A rule that
    /// fired on every <c>.Idempotency(...)</c> would be trivial to write and would delete stage 3
    /// from the DSL.
    /// </remarks>
    [Fact]
    public void AWindowOnAFlowThatMarksNothingIsSilent() =>
        Analyze(FlowOver(PlainInput, PlainOutput, Window)).ShouldNotContain("FLOWX1040");

    /// <summary>A marked flow with no window is silent.</summary>
    [Fact]
    public void AMarkedFlowWithNoWindowIsSilent() =>
        Analyze(FlowOver(
            MarkedInput,
            PlainOutput,
            """public static readonly PolicySet Admission = PolicySet.Named("a").RateLimit(permits: 5, TimeSpan.FromSeconds(1));"""))
            .ShouldNotContain(
                "FLOWX1040",
                "stage 1 records nothing, so redaction cannot reach it. A rule that fired here " +
                "would refuse a rate limit for a reason that belongs to a different stage.");

    /// <summary>
    /// A mark on a contract that is neither the flow's input nor its output is silent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The precision this rule needs, and the one that is easy to get wrong in the
    /// noisy direction.</strong> <c>Validated</c> below carries the attribute and the flow's own
    /// two contracts do not, so <c>SensitiveMembers</c> is empty, so nothing this flow writes is
    /// redacted and a window records exactly what it produced. A rule that walked every contract
    /// it could reach would refuse a working flow.
    /// </para>
    /// <para>
    /// The inverse case — an intermediate contract carrying a member <em>named</em> the same as
    /// a marked one, which <em>is</em> redacted — is exactly why the rule is flow-wide rather
    /// than per step: it is the flow's marks that decide, wherever the matching name turns up.
    /// <c>samples/banking</c> is the worked example, and its own test asserts it.
    /// </para>
    /// </remarks>
    [Fact]
    public void AMarkOnAnIntermediateContractIsNotTheFlowsOwn()
    {
        var source = FlowOver(PlainInput, PlainOutput, Window)
            .Replace(
                "public sealed record Validated(string DebtorIban, decimal Amount);",
                "public sealed record Validated([property: Sensitive] string DebtorIban, decimal Amount);",
                StringComparison.Ordinal);

        Analyze(source).ShouldNotContain(
            "FLOWX1040",
            "Validated is neither the flow's input nor its output, so it does not reach " +
            "SensitiveMembers and nothing this flow writes is redacted because of it.");
    }

    /// <summary>A set the compiler cannot read leaves this rule silent, and FLOWX1036 speaks.</summary>
    /// <remarks>
    /// <strong>This is why the run-time guard is not optional.</strong> The rule inherits
    /// <c>PolicySetReader</c>'s silence — a set in a referenced assembly has no initialiser to
    /// walk — so a marked flow can reach production with a window the analyzer never saw.
    /// <c>JournalPayload.TryToReplayableJson</c> is what refuses it there, and ADR-0042 §2.3 is
    /// why the two mechanisms are deliberately not the same one.
    /// </remarks>
    [Fact]
    public void AnUnreadableSetIsFLOWX1036AndNotThisRule()
    {
        var source = FlowOver(MarkedInput, PlainOutput, Window)
            .Replace(".WithPolicy(Policies.Admission)", ".WithPolicy(Build())", StringComparison.Ordinal)
            .Replace(
                "protected override void Define",
                """
                private static PolicySet Build() => PolicySet.Named("a").Idempotency(TimeSpan.FromHours(24));

                    protected override void Define
                """,
                StringComparison.Ordinal);

        var reported = Analyze(source);

        reported.ShouldNotContain(
            "FLOWX1040",
            "the compiler could not read the set, so it cannot say whether it holds a window — " +
            "and a rule that guessed would be an error raised on an inference.");

        reported.ShouldContain(
            "FLOWX1036",
            "the silence itself is reported, which is that rule's whole job.");
    }
}
