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
    };
}
