using System;
using System.Globalization;
using System.Linq;
using FlowX.Compiler.Emit;
using FlowX.Compiler.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// The emitter, tested as a pure function from model to text.
/// </summary>
/// <remarks>
/// No Roslyn host, no generator driver, no compilation of a sample project — those
/// make a generator suite slow enough that people stop running it. The one place a
/// compiler appears is <see cref="EmittedSourceParsesAsValidCSharp"/>, because
/// "the text looks right" and "the text compiles" are different claims and only the
/// second one matters.
/// </remarks>
public sealed class FlowEmitterTests
{
    /// <summary>The flat layout <see cref="Models.Conditional"/> compiles to, in order.</summary>
    private static readonly string[] ConditionalNodeOrder =
    [
        "StepNode.ForCapability(0",
        "StepNode.ForBranch(1",
        "StepNode.ForCapability(2",
        "StepNode.ForJump(3",
        "StepNode.ForCapability(4",
        "StepNode.ForEmit(5",
    ];

    /// <summary>The flat layout <see cref="Models.Switching"/> compiles to, in order.</summary>
    private static readonly string[] SwitchNodeOrder =
    [
        "StepNode.ForCapability(0",
        "StepNode.ForSwitch(1",
        "StepNode.ForCapability(2",
        "StepNode.ForJump(3",
        "StepNode.ForCapability(4",
        "StepNode.ForJump(5",
        "StepNode.ForCapability(6",
        "StepNode.ForEmit(7",
    ];

    /// <summary>The emitted text between two markers, so one switch's cases cannot answer for another's.</summary>
    private static string Section(string source, string from, string to)
    {
        var start = source.IndexOf(from, StringComparison.Ordinal);
        var end = source.IndexOf(to, start + from.Length, StringComparison.Ordinal);

        start.ShouldBeGreaterThan(-1, $"'{from}' is not in the emitted source.");
        end.ShouldBeGreaterThan(-1, $"'{to}' is not in the emitted source.");

        return source[start..end];
    }

    [Fact]
    public void EmitsAPartialClassInTheFlowsOwnNamespace()
    {
        var source = FlowEmitter.Emit(Models.PlaceOrder());

        source.ShouldContain("namespace Sample.Flows");
        source.ShouldContain("partial class PlaceOrderFlow");
    }

    [Fact]
    public void EmitsOneDescriptorPerCapabilityStepAndPerCompensation()
    {
        var source = FlowEmitter.Emit(Models.PlaceOrder());

        source.ShouldContain("Step0 = CapabilityDescriptor.Create(\"order.validate\", \"1.0.0\", true)");
        source.ShouldContain("Step1 = CapabilityDescriptor.Create(\"inventory.reserve\", \"1.0.0\", true, \"inventory-ledger\")");
        source.ShouldContain("Step1Compensation = CapabilityDescriptor.Create(\"inventory.release\"");
        source.ShouldContain("Step2 = CapabilityDescriptor.Create(\"payment.capture\", \"2.1.0\", false, \"payment-gateway\", \"ledger\")");
    }

    [Fact]
    public void EmitsAnExecutionPlanMatchingTheDeclaredSteps()
    {
        var source = FlowEmitter.Emit(Models.PlaceOrder());

        source.ShouldContain("StepNode.ForCapability(0, Descriptors.Step0)");
        source.ShouldContain("StepNode.ForCapability(1, Descriptors.Step1, Descriptors.Step1Compensation)");
        source.ShouldContain("StepNode.ForCapability(2, Descriptors.Step2)");
        source.ShouldContain("StepNode.ForEmit(3, \"order.placed\")");
    }

    [Fact]
    public void TheDispatcherSwitchesOnStepIndexRatherThanLookingAnythingUp()
    {
        var source = FlowEmitter.Emit(Models.PlaceOrder());

        source.ShouldContain("switch (stepIndex)");
        source.ShouldContain("case 0:");
        source.ShouldContain("case 3:");
        source.Contains("typeof(", StringComparison.Ordinal).ShouldBeFalse(
            "A typeof in the dispatcher means something is resolved at run time. " +
            "ADR-0002's whole claim is that nothing is.");
        source.Contains("GetType()", StringComparison.Ordinal).ShouldBeFalse();
        source.Contains("Activator", StringComparison.Ordinal).ShouldBeFalse();
    }

    [Fact]
    public void CapabilitiesAreInjectedThroughTheConstructor()
    {
        var source = FlowEmitter.Emit(Models.PlaceOrder());

        source.ShouldContain("public Dispatcher(");
        source.ShouldContain("Sample.Capabilities.ValidateOrder validateOrder");
        source.ShouldContain("_validateOrder = validateOrder;");
    }

    [Fact]
    public void CompensationsAreDispatchedOnlyForStepsThatDeclareOne()
    {
        var source = FlowEmitter.Emit(Models.PlaceOrder());
        var compensate = source[source.IndexOf("CompensateAsync", System.StringComparison.Ordinal)..];

        compensate.Contains("case 1:", StringComparison.Ordinal).ShouldBeTrue(
            "Step 1 declared a compensation.");
        compensate.Contains("case 0:", StringComparison.Ordinal).ShouldBeFalse(
            "Step 0 declared none, so it has no case.");
        compensate.Contains("case 2:", StringComparison.Ordinal).ShouldBeFalse();
    }

    [Fact]
    public void FlattensAConditionalIntoBranchThenJumpOtherwise()
    {
        var source = FlowEmitter.Emit(Models.Conditional());

        source.ShouldContain("StepNode.ForCapability(0, Descriptors.Step0)");
        source.ShouldContain("StepNode.ForBranch(1, 4)");
        source.ShouldContain("StepNode.ForCapability(2, Descriptors.Step2, Descriptors.Step2Compensation)");
        source.ShouldContain("StepNode.ForJump(3, 5)");
        source.ShouldContain("StepNode.ForCapability(4, Descriptors.Step4)");
        source.ShouldContain("StepNode.ForEmit(5, \"order.reviewed\")");
    }

    [Fact]
    public void TheStepArrayIsEmittedInFlatIndexOrder()
    {
        // StepGraph.Create sorts by index, so an array in the wrong order would still
        // produce a correct plan — and a reader of the generated file would be looking at
        // a listing that does not match what runs. The file the header promises is
        // debuggable has to read in execution order.
        var source = FlowEmitter.Emit(Models.Conditional());

        var positions = ConditionalNodeOrder
            .Select(expression => source.IndexOf(expression, StringComparison.Ordinal))
            .ToList();

        positions.ShouldAllBe(p => p > 0);
        positions.ShouldBe(positions.OrderBy(p => p).ToList());
    }

    [Fact]
    public void EmitsThePredicateAsAStaticFieldRatherThanALambdaPerCall()
    {
        // Budget B2 is a hard zero. A lambda built at the branch would allocate a delegate
        // per execution, so the predicate is a field initialised once — the same shape,
        // and the same reason, as Projection.
        var source = FlowEmitter.Emit(Models.Conditional());

        source.ShouldContain("private static class Conditions");
        source.ShouldContain(
            "public static readonly Func<FlowContext, bool> Step1 = ctx => ctx.Get<RiskScore>().Value > 80;");
        source.ShouldContain("#line 12 \"/src/Flows/Review.cs\"");
    }

    [Fact]
    public void TheDispatcherEvaluatesEachBranchByStepIndex()
    {
        var source = FlowEmitter.Emit(Models.Conditional());
        var evaluate = source[source.IndexOf("bool Evaluate(", StringComparison.Ordinal)..];

        evaluate.ShouldContain("case 1:");
        evaluate.ShouldContain("return Conditions.Step1(ctx);");
    }

    [Fact]
    public void AFlowWithNoConditionalEmitsNoPredicatesAndNoSwitchToEvaluate()
    {
        var source = FlowEmitter.Emit(Models.PlaceOrder());

        source.Contains("class Conditions", StringComparison.Ordinal).ShouldBeFalse(
            "An empty holder class is noise in a file whose header promises readability.");

        // Bounded at Select, whose own message mentions the word "switch" — an unbounded
        // slice would read the next method's prose as this method's code.
        var evaluate = Section(source, "bool Evaluate(", "int Select(");

        evaluate.Contains("switch (stepIndex)", StringComparison.Ordinal).ShouldBeFalse(
            "A switch with only a default reads like an oversight. The flow declares no " +
            "conditional, so the method says exactly that and throws.");
    }

    [Fact]
    public void StepsInsideABranchGetTheirOwnDispatcherCaseAndDescriptor()
    {
        var source = FlowEmitter.Emit(Models.Conditional());

        source.ShouldContain("Step4 = CapabilityDescriptor.Create(\"payment.capture\"");
        source.ShouldContain("Sample.Capabilities.CapturePayment capturePayment");

        // Bounded at CompensateAsync, because the cases of the *other* two switches would
        // otherwise answer for this one — and the branch does have a case in Evaluate.
        var execute = Section(source, "ExecuteAsync(int stepIndex", "CompensateAsync");

        execute.ShouldContain("case 4:");
        execute.Contains("case 1:", StringComparison.Ordinal).ShouldBeFalse(
            "Step 1 is the branch. The engine reaches it through Evaluate, never through " +
            "ExecuteAsync, so a case for it would be dead code.");
        execute.Contains("case 3:", StringComparison.Ordinal).ShouldBeFalse("Step 3 is the jump.");
    }

    [Fact]
    public void ACompensationDeclaredInsideABranchIsStillDispatched()
    {
        var source = FlowEmitter.Emit(Models.Conditional());
        var compensate = Section(source, "CompensateAsync", "bool Evaluate(");

        // The reserve step is inside the `then` block. Reading only the top-level steps
        // would have lost its compensation, and a failure after a taken branch would then
        // leave the reservation dangling.
        compensate.ShouldContain("case 2:");
    }

    [Fact]
    public void FlattensASwitchIntoOneNodeWithATargetPerCase()
    {
        var source = FlowEmitter.Emit(Models.Switching());

        source.ShouldContain("StepNode.ForCapability(0, Descriptors.Step0)");
        source.ShouldContain("StepNode.ForSwitch(1, new[] { 2, 4 }, defaultTarget: 6)");
        source.ShouldContain("StepNode.ForCapability(2, Descriptors.Step2, Descriptors.Step2Compensation)");
        source.ShouldContain("StepNode.ForJump(3, 7)");
        source.ShouldContain("StepNode.ForCapability(4, Descriptors.Step4)");
        source.ShouldContain("StepNode.ForJump(5, 7)");
        source.ShouldContainText("StepNode.ForCapability(6, Descriptors.Step6)", "The `Default` block.");
        source.ShouldContain("StepNode.ForEmit(7, \"order.priced\")");
        source.Contains("StepNode.ForJump(7", StringComparison.Ordinal).ShouldBeFalse(
            "The default block is laid out last, so nothing follows it to skip.");
    }

    [Fact]
    public void TheSwitchStepArrayIsEmittedInFlatIndexOrder()
    {
        var source = FlowEmitter.Emit(Models.Switching());

        var positions = SwitchNodeOrder
            .Select(expression => source.IndexOf(expression, StringComparison.Ordinal))
            .ToList();

        positions.ShouldAllBe(p => p > 0);
        positions.ShouldBe(positions.OrderBy(p => p).ToList());
    }

    [Fact]
    public void EmitsTheSelectorAsATypedStaticFieldRatherThanALambdaPerCall()
    {
        // Same shape and same reason as a predicate: budget B2 is a hard zero, so the
        // selector is a field initialised once. Typed at the value it produces, because a
        // Func<FlowContext, object> would box an enum on every switch the flow takes.
        var source = FlowEmitter.Emit(Models.Switching());

        source.ShouldContain("private static class Selectors");
        source.ShouldContain(
            "public static readonly Func<FlowContext, Sample.Contracts.Channel> Step1 = " +
            "ctx => ctx.Get<ValidatedOrder>().Channel;");
        source.ShouldContain("#line 13 \"/src/Flows/Price.cs\"");
        source.ShouldContainText("#line 14 \"/src/Flows/Price.cs\"", "The case value points at its own line too.");
    }

    [Fact]
    public void TheDispatcherSelectsACaseByStepIndexAndEvaluatesTheSelectorOnce()
    {
        var source = FlowEmitter.Emit(Models.Switching());
        var select = source[source.IndexOf("int Select(", StringComparison.Ordinal)..];

        select.ShouldContain("case 1:");
        select.ShouldContain("var value = Selectors.Step1(ctx);");

        select.ShouldContain(
            "if (System.Collections.Generic.EqualityComparer<Sample.Contracts.Channel>.Default" +
            ".Equals(value, Sample.Contracts.Channel.Retail))");
        select.ShouldContain("return 0;");
        select.ShouldContain(
            "if (System.Collections.Generic.EqualityComparer<Sample.Contracts.Channel>.Default" +
            ".Equals(value, Sample.Contracts.Channel.Wholesale))");
        select.ShouldContain("return 1;");
        select.ShouldContainText("return -1;", "No case matched, so the engine takes the default target.");

        select.Split("Selectors.Step1(ctx)").Length.ShouldBe(2,
            "The selector runs once and the arms are tested against what it produced. " +
            "Reading it once per arm would be wrong for a reader and wasteful for a machine.");
    }

    [Fact]
    public void AFlowWithNoSwitchEmitsNoSelectorsAndNoCasesToSelect()
    {
        var source = FlowEmitter.Emit(Models.PlaceOrder());

        source.Contains("class Selectors", StringComparison.Ordinal).ShouldBeFalse(
            "An empty holder class is noise in a file whose header promises readability.");

        var select = source[source.IndexOf("int Select(", StringComparison.Ordinal)..];

        select.Contains("switch (stepIndex)", StringComparison.Ordinal).ShouldBeFalse(
            "The flow declares no switch, so the method says exactly that and throws.");
    }

    [Fact]
    public void StepsInsideACaseGetTheirOwnDispatcherCaseAndDescriptor()
    {
        var source = FlowEmitter.Emit(Models.Switching());

        source.ShouldContain("Step4 = CapabilityDescriptor.Create(\"payment.capture\"");
        source.ShouldContain("Sample.Capabilities.CapturePayment capturePayment");

        var execute = Section(source, "ExecuteAsync(int stepIndex", "CompensateAsync");

        execute.ShouldContain("case 4:");
        execute.ShouldContainText("case 6:", "The `Default` block runs like any other step.");
        execute.Contains("case 1:", StringComparison.Ordinal).ShouldBeFalse(
            "Step 1 is the switch. The engine reaches it through Select, never through " +
            "ExecuteAsync, so a case for it would be dead code.");
        execute.Contains("case 3:", StringComparison.Ordinal).ShouldBeFalse("Step 3 is a jump.");
        execute.Contains("case 5:", StringComparison.Ordinal).ShouldBeFalse("Step 5 is a jump.");
    }

    [Fact]
    public void ACompensationDeclaredInsideACaseIsStillDispatched()
    {
        var source = FlowEmitter.Emit(Models.Switching());
        var compensate = Section(source, "CompensateAsync", "bool Evaluate(");

        compensate.ShouldContain("case 2:");
    }

    /// <summary>
    /// The whole emitted shape of a switch, pinned character for character.
    /// </summary>
    /// <remarks>
    /// The substring assertions above each say one true thing about the output; this says
    /// what the output <em>is</em>. It is the test that notices the things nobody thought
    /// to assert — a stray blank line, an indent that drifted, a comment that stopped
    /// matching the code under it — in a file whose header promises a developer it is
    /// theirs to read and debug. When it fails for a deliberate change, the fix is to
    /// paste the new text in after reading it.
    /// </remarks>
    [Fact]
    public void TheEmittedSwitchIsPinnedExactly()
    {
        var source = FlowEmitter.Emit(Models.Switching());

        Section(source, "        /// <summary>Switch selectors", "        /// <summary>Contract members")
            .ShouldBe(
                """
                        /// <summary>Switch selectors, built once at type initialisation.</summary>
                        /// <remarks>
                        /// Each is your <c>.Switch(...)</c> expression, copied verbatim. They obey the same
                        /// determinism rule as a condition — context, flow input and prior step results
                        /// only — so that a replay selects the case it selected before.
                        /// </remarks>
                        private static class Selectors
                        {
                            #line 13 "/src/Flows/Price.cs"
                            public static readonly Func<FlowContext, Sample.Contracts.Channel> Step1 = ctx => ctx.Get<ValidatedOrder>().Channel;
                            #line default
                        }

                        /// <summary>The compiled execution plan. Built once, shared by every invocation.</summary>
                        public static ExecutionPlan Plan { get; } = ExecutionPlan.Create(
                            FlowDescriptor.Create(
                                "order.price",
                                "1.0.0",
                                ExecutionProfile.Ephemeral,
                                TimeSpan.FromSeconds(30)),
                            StepGraph.Create(new StepNode[]
                            {
                                StepNode.ForCapability(0, Descriptors.Step0),
                                StepNode.ForSwitch(1, new[] { 2, 4 }, defaultTarget: 6),
                                StepNode.ForCapability(2, Descriptors.Step2, Descriptors.Step2Compensation),
                                StepNode.ForJump(3, 7),
                                StepNode.ForCapability(4, Descriptors.Step4),
                                StepNode.ForJump(5, 7),
                                StepNode.ForCapability(6, Descriptors.Step6),
                                StepNode.ForEmit(7, "order.priced"),
                            }));


                """.ReplaceLineEndings("\n"));

        Section(source, "            /// <inheritdoc />\n            public int Select(", "        }\n    }\n}")
            .ShouldBe(
                """
                            /// <inheritdoc />
                            public int Select(int stepIndex, FlowContext ctx)
                            {
                                switch (stepIndex)
                                {
                                    case 1:
                                    {
                                        var value = Selectors.Step1(ctx);

                                        #line 14 "/src/Flows/Price.cs"
                                        if (System.Collections.Generic.EqualityComparer<Sample.Contracts.Channel>.Default.Equals(value, Sample.Contracts.Channel.Retail))
                                        {
                                            return 0;
                                        }
                                        #line default

                                        if (System.Collections.Generic.EqualityComparer<Sample.Contracts.Channel>.Default.Equals(value, Sample.Contracts.Channel.Wholesale))
                                        {
                                            return 1;
                                        }

                                        // No case matched. The engine takes the switch's default target,
                                        // which is the `Default` block when there is one and the join when
                                        // there is not — a value nothing matched simply continues.
                                        return -1;
                                    }
                                    default:
                                    {
                                        throw new ArgumentOutOfRangeException(
                                            nameof(stepIndex),
                                            stepIndex,
                                            "Step index does not name a switch in the compiled plan. The plan and " +
                                            "this dispatcher are generated together, so this means they came from " +
                                            "different builds.");
                                    }
                                }
                            }

                """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void EmitsLineDirectivesBackToTheDeclaringSource()
    {
        var withLocation = new FlowModel(
            "order.place", "1.0.0", "Ephemeral", null, "Sample", "F", "In", "Out",
            [StepModel.Capability(0, "C", "a.b", "1.0.0", true, location: "/src/Flows/Order.cs:42")]);

        var source = FlowEmitter.Emit(withLocation);

        source.ShouldContain("#line 42 \"/src/Flows/Order.cs\"");
        source.ShouldContain("#line default");
        // Risk R1: generated code must be debuggable. Without these a breakpoint lands
        // in a file the developer never wrote and a stack trace names nothing useful.
    }

    [Fact]
    public void OmitsLineDirectivesWhenThereIsNoLocation()
    {
        FlowEmitter.Emit(Models.Minimal()).Contains("#line", StringComparison.Ordinal).ShouldBeFalse();
    }

    [Fact]
    public void HandlesAFlowDeclaredWithoutANamespace()
    {
        var source = FlowEmitter.Emit(Models.Minimal());

        source.Contains("namespace ", StringComparison.Ordinal).ShouldBeFalse();
        source.ShouldContain("partial class PingFlow");
    }

    [Fact]
    public void DefaultsAnUndeclaredDeadlineToThirtySeconds()
    {
        FlowEmitter.Emit(Models.Minimal()).ShouldContain("TimeSpan.FromSeconds(30)");
        FlowEmitter.Emit(Models.PlaceOrder()).ShouldContain("XmlConvert.ToTimeSpan(\"PT30S\")");
    }

    [Fact]
    public void EmissionIsDeterministic()
    {
        var first = FlowEmitter.Emit(Models.PlaceOrder());
        var second = FlowEmitter.Emit(Models.PlaceOrder());

        second.ShouldBe(first,
            "Two builds of identical source must emit identical bytes, or every build " +
            "shows a diff and incremental compilation stops helping.");
    }

    [Fact]
    public void UsesUnixLineEndingsSoSnapshotsMatchOnEveryPlatform()
        => FlowEmitter.Emit(Models.PlaceOrder()).Contains('\r', StringComparison.Ordinal).ShouldBeFalse();

    [Fact]
    public void CarriesAHeaderThatTellsTheReaderWhatToDoWithTheFile()
    {
        var source = FlowEmitter.Emit(Models.PlaceOrder());

        source.ShouldStartWith("// <auto-generated>");
        source.Contains("Set a breakpoint in it", StringComparison.Ordinal).ShouldBeTrue(
            "The header exists to tell a developer this file is theirs to debug, not " +
            "an opaque artefact to be scrolled past.");
    }

    [Fact]
    public void FileNameIsDerivedFromTheFullTypeName()
        => FlowEmitter.FileNameFor(Models.PlaceOrder())
            .ShouldBe("Sample.Flows.PlaceOrderFlow.Flow.g.cs");

    [Fact]
    public void RejectsANullModel()
    {
        Should.Throw<System.ArgumentNullException>(() => FlowEmitter.Emit(null!));
        Should.Throw<System.ArgumentNullException>(() => FlowEmitter.FileNameFor(null!));
    }

    /// <summary>
    /// The only test here that runs a compiler: emitted text must actually parse.
    /// </summary>
    /// <remarks>
    /// Parsing, not full binding — binding would need the whole FlowX reference set and
    /// would turn a millisecond test into a slow one. A syntax error is the failure mode
    /// string emission actually has; a missing reference is the generator wiring's
    /// problem and is caught by the sample building at WP-10.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllModels))]
    public void EmittedSourceParsesAsValidCSharp(string name, FlowModel model)
    {
        var source = FlowEmitter.Emit(model);
        var tree = CSharpSyntaxTree.ParseText(
            source, cancellationToken: TestContext.Current.CancellationToken);

        var errors = tree.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => FormattableString.Invariant(
                $"{d.Id} at {d.Location.GetLineSpan().StartLinePosition}: {d.GetMessage(CultureInfo.InvariantCulture)}"))
            .ToArray();

        errors.ShouldBeEmpty($"Emitted source for '{name}' does not parse:\n{source}");
    }

    public static TheoryData<string, FlowModel> AllModels() => new()
    {
        { "place-order", Models.PlaceOrder() },
        { "minimal", Models.Minimal() },
        { "conditional", Models.Conditional() },
        { "switching", Models.Switching() },
    };
}
