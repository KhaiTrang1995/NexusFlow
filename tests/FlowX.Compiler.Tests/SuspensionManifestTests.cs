using System.Linq;
using System.Text.Json;
using FlowX.Compiler.Emit;
using FlowX.Compiler.Model;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// What an <c>AwaitSignal</c> step publishes:
/// <a href="../../../docs/adr/ADR-0021-manifest-publishes-the-wait.md">ADR-0021</a>'s two
/// fields, and the cases in which the second is withheld.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Until ADR-0021 the whole entry was <c>{ "id": 1, "kind": "AwaitSignal" }</c>.</strong>
/// Two flows waiting for different things produced byte-identical steps, so a manifest
/// consumer could see that a flow stopped and not what would restart it — and
/// <c>flowx diff</c> had no field to compare, which is why renaming a signal contract was a
/// change nothing in the build could report.
/// </para>
/// <para>
/// The <c>timeout</c> assertions are the ones worth reading twice. It is the folded
/// duration, never the author's expression: the plan is C# and <c>Waits.Countersignature</c>
/// <em>is</em> the duration there, while the manifest is JSON read by tools that have never
/// seen the assembly, and a symbol name in that field would make
/// <c>FLOWX-DIFF-206</c> fire on a rename and stay silent on a change. Where the compiler
/// cannot evaluate the declaration the field is omitted, which is <c>merge</c>'s precedent:
/// an absent field is a consumer asking, a guessed one is a consumer misled.
/// </para>
/// </remarks>
public sealed class SuspensionManifestTests
{
    private static JsonElement Wait(string manifest, out JsonDocument document)
    {
        document = JsonDocument.Parse(manifest);

        return document.RootElement
            .GetProperty("flows")[0]
            .GetProperty("steps")
            .EnumerateArray()
            .Single(step => step.GetProperty("kind").GetString() == "AwaitSignal");
    }

    [Fact]
    public void TheStepNamesTheSignalItWaitsFor()
    {
        var wait = Wait(ManifestWriter.Write("Sample.App", "1.0.0", [Models.Waiting()]), out var document);

        using (document)
        {
            wait.GetProperty("signal").GetString().ShouldBe(
                "offer.countersigned",
                "A wait is an inbound address, like a trigger. Without the identity, a " +
                "consumer cannot tell two waiting flows apart and no sender can be told " +
                "where to deliver.");
        }
    }

    [Fact]
    public void TheStepCarriesTheDeclaredWaitAsADuration()
    {
        var wait = Wait(ManifestWriter.Write("Sample.App", "1.0.0", [Models.Waiting()]), out var document);

        using (document)
        {
            wait.GetProperty("timeout").GetString().ShouldBe("P7D");
        }
    }

    /// <summary>
    /// A declaration the compiler could not evaluate publishes no <c>timeout</c> at all.
    /// </summary>
    /// <remarks>
    /// Not an empty string and not the expression's source text. The first would read as a
    /// wait of no length; the second would publish a symbol into a document whose readers
    /// have no symbols.
    /// </remarks>
    [Fact]
    public void AWaitWhoseDurationCouldNotBeFoldedPublishesNoTimeout()
    {
        var wait = Wait(
            ManifestWriter.Write("Sample.App", "1.0.0", [Models.Waiting(timeout: null)]), out var document);

        using (document)
        {
            wait.TryGetProperty("timeout", out _).ShouldBeFalse();
            wait.GetProperty("signal").GetString().ShouldBe("offer.countersigned");
        }
    }

    /// <summary>Neither field appears on a step that is not a wait.</summary>
    [Fact]
    public void NoOtherKindOfStepCarriesEitherField()
    {
        using var document = JsonDocument.Parse(
            ManifestWriter.Write("Sample.App", "1.0.0", [Models.PlaceOrder()]));

        foreach (var step in document.RootElement.GetProperty("flows")[0].GetProperty("steps").EnumerateArray())
        {
            step.TryGetProperty("signal", out _).ShouldBeFalse();
            step.TryGetProperty("timeout", out _).ShouldBeFalse();
        }
    }

    /// <summary>
    /// The whole document still validates against the committed schema.
    /// </summary>
    /// <remarks>
    /// The step object is <c>additionalProperties: false</c>, so this is the assertion that
    /// says the schema change is real rather than something the writer emits into a
    /// document nobody validates — before it, the manifest above was invalid, which is the
    /// right way round.
    /// </remarks>
    [Fact]
    public void AFlowThatWaitsValidatesAgainstTheCommittedSchema()
        => ManifestSchemaTests.Validate(ManifestWriter.Write("Sample.App", "1.0.0", [Models.Waiting()]));

    [Fact]
    public void AFlowWhoseWaitCouldNotBeFoldedAlsoValidates()
        => ManifestSchemaTests.Validate(
            ManifestWriter.Write("Sample.App", "1.0.0", [Models.Waiting(timeout: null)]));

    /// <summary>
    /// The model refuses a duration that is not ISO-8601, at the point it is built.
    /// </summary>
    /// <remarks>
    /// The schema's <c>duration</c> pattern would catch it eventually, but only for whoever
    /// runs the schema tests — and the field is written by a generator that no application
    /// build validates. Refusing here means a folder that produced <c>7.00:00:00</c> fails
    /// in the compiler's own tests rather than in a consumer's parser.
    /// </remarks>
    [Fact]
    public void TheModelRefusesADurationThatIsNotIso8601()
        => Should.Throw<System.ArgumentException>(() => StepModel.AwaitSignal(
            0, "offer.countersigned", timeoutExpression: "x", timeout: "7.00:00:00"));
}
