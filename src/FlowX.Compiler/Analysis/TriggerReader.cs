using System.Collections.Generic;
using System.Linq;
using FlowX.Compiler.Model;
using Microsoft.CodeAnalysis;

namespace FlowX.Compiler.Analysis;

/// <summary>
/// Reads the trigger attributes on a flow's class
/// (<a href="../../../docs/adr/ADR-0004-universal-trigger-model.md">ADR-0004</a>).
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this can and cannot see, stated plainly.</strong> A trigger's
/// <c>Kind</c> property is not attribute data — it is an abstract property that each
/// attribute overrides with an expression (<c>public override TriggerKind Kind =&gt;
/// TriggerKind.Http;</c>). From metadata that is executable code, not a value. So the kind
/// is declared a second time, as data, with <c>[TriggerKind(TriggerKind.Http)]</c> on the
/// attribute class: an enum constructor argument <em>is</em> attribute data and is
/// readable across an assembly boundary. <see cref="KindOf"/> is that read, and it walks
/// the attribute's base chain so a plugin with its own intermediate base declares the kind
/// once.
/// </para>
/// <para>
/// This is what makes ADR-0004's "one trigger abstraction for every transport" true for
/// transports FlowX does not ship. A third-party <c>TriggerAttribute</c> subclass carrying
/// the marker reaches the manifest with its kind; one without it is skipped, and
/// <c>TriggerDeclarationAnalyzer</c> reports <c>FLOWX1025</c> on exactly the attributes
/// <see cref="KindOf"/> returns <c>null</c> for — which is why that read lives here, beside
/// the switch it must agree with, rather than as a second rule in the analyzer.
/// </para>
/// <para>
/// <strong>Kind is general; argument shape is not.</strong> Knowing an attribute is
/// <c>Bus</c> says nothing about what its constructor arguments mean, so the switch in
/// <see cref="Shape"/> still recognises only the five attributes <c>FlowX.Abstractions</c>
/// ships, whose shape is part of the platform contract. A plugin trigger publishes its
/// kind and nothing else — an honest partial record rather than an absence, and rather
/// than a guess at which positional argument is a topic. Projecting a plugin's arguments
/// generically is possible in principle (<c>AttributeConstructor.Parameters</c> carries
/// parameter names in metadata) but has nowhere to go: the manifest schema's
/// <c>trigger</c> object is <c>additionalProperties: false</c> over a closed property
/// list, which is an ADR-0005 question rather than a compiler one.
/// </para>
/// <para>
/// <strong>Two declarations that can disagree.</strong> <c>Kind =&gt;</c> is what the
/// runtime reads and <c>[TriggerKind]</c> is what the manifest publishes. For the five
/// built-ins a fitness function holds them equal. For an attribute that arrives as a
/// compiled reference nothing can: the property's value exists only once the getter runs,
/// and a generator does not run the code it compiles. The manifest therefore publishes the
/// declared marker, and says so.
/// </para>
/// <para>
/// <strong>A declared trigger is not the same as a bound one.</strong> Nothing yet turns
/// these attributes into endpoint registrations — the sample maps its route by hand in
/// <c>Program.cs</c> — so a flow may be reachable at an address it does not declare, and
/// may declare one nothing serves. The manifest publishes the declaration, which is the
/// authored intent; that the two can disagree is a property of the current build, not of
/// the model.
/// </para>
/// </remarks>
public static class TriggerReader
{
    private const string TriggerAttributeBase = "FlowX.TriggerAttribute";

    private const string TriggerKindMarker = "FlowX.TriggerKindAttribute";

    /// <summary>
    /// The trigger attributes whose constructor and named arguments this build knows how
    /// to project into the manifest, by full type name.
    /// </summary>
    /// <remarks>
    /// Membership here decides <em>detail</em>, never whether a trigger is published at
    /// all: that is <see cref="KindOf"/>'s answer, and an attribute outside this list still
    /// reaches the manifest with its declared kind.
    /// <c>EveryTriggerAttributeTheAbstractionShipsHasAKnownShape</c> reflects over
    /// <c>FlowX.Abstractions</c> and fails if a sixth attribute is added without being
    /// added here, which would otherwise publish a bare kind for an attribute whose shape
    /// is part of the platform contract.
    /// </remarks>
    private static readonly string[] KnownShapes =
    [
        "FlowX.AgentTriggerAttribute",
        "FlowX.CronTriggerAttribute",
        "FlowX.HttpTriggerAttribute",
        "FlowX.KafkaTriggerAttribute",
        "FlowX.StreamTriggerAttribute",
    ];

    /// <summary>The full type names of the trigger attributes whose arguments this build projects.</summary>
    public static IReadOnlyList<string> AttributesWithKnownShape => KnownShapes;

    /// <summary>Every trigger the type declares, in attribute order.</summary>
    /// <param name="flow">The flow's class symbol.</param>
    public static IReadOnlyList<TriggerModel> Read(INamedTypeSymbol? flow)
    {
        if (flow is null)
        {
            return System.Array.Empty<TriggerModel>();
        }

        var triggers = new List<TriggerModel>();

        foreach (var attribute in flow.GetAttributes())
        {
            if (!IsTrigger(attribute.AttributeClass))
            {
                continue;
            }

            var model = ReadOne(attribute);

            if (model is not null)
            {
                triggers.Add(model);
            }
        }

        return triggers;
    }

    /// <summary>Whether an attribute derives from <c>FlowX.TriggerAttribute</c>.</summary>
    /// <remarks>
    /// Public because <c>TriggerDeclarationAnalyzer</c> asks the same question and must
    /// get the same answer: it reports on what this reader skipped, so the two cannot be
    /// allowed to disagree about what a trigger is.
    /// </remarks>
    /// <param name="attributeClass">The applied attribute's type, or <c>null</c>.</param>
    public static bool IsTrigger(INamedTypeSymbol? attributeClass)
    {
        for (var type = attributeClass?.BaseType; type is not null; type = type.BaseType)
        {
            if (type.ToDisplayString() == TriggerAttributeBase)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The transport family the attribute declares through <c>[TriggerKind]</c>, spelled as
    /// the manifest schema's <c>kind</c> enum spells it — or <c>null</c> when it declares
    /// none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Walks the base chain, because <c>ISymbol.GetAttributes</c> returns only what is
    /// applied to that symbol: Roslyn does not apply <c>Inherited = true</c> to it. A plugin
    /// that factors shared members into <c>abstract class BusTriggerAttribute</c> therefore
    /// marks the base once and every attribute derived from it inherits the declaration —
    /// which is how the marker behaves at run time, and a surprising place for the two to
    /// differ.
    /// </para>
    /// <para>
    /// <c>null</c> for a value outside <c>TriggerKind</c> as well as for no marker at all.
    /// A cast integer is not a declared kind, and inventing a name for it would put a value
    /// in the manifest that the schema's closed <c>kind</c> enum rejects; FLOWX1025 covers
    /// it alongside the undeclared case, which is where an author looking for the cause
    /// will be told to look.
    /// </para>
    /// </remarks>
    /// <param name="attributeClass">The applied attribute's type, or <c>null</c>.</param>
    public static string? KindOf(INamedTypeSymbol? attributeClass)
    {
        for (var type = attributeClass; type is not null; type = type.BaseType)
        {
            foreach (var marker in type.GetAttributes())
            {
                if (marker.AttributeClass?.ToDisplayString() != TriggerKindMarker ||
                    marker.ConstructorArguments.Length != 1)
                {
                    continue;
                }

                var name = KindName(marker.ConstructorArguments[0].Value);

                if (name is not null)
                {
                    return name;
                }
            }
        }

        return null;
    }

    /// <summary>The manifest name of a <c>TriggerKind</c> value, or <c>null</c> if it has none.</summary>
    /// <remarks>
    /// Exposed for the fitness function that checks this map against the enum and against
    /// the schema. Neither is reachable from the generator — it targets netstandard2.0 and
    /// cannot reference <c>FlowX.Abstractions</c> — so the check lives in a test, and the
    /// test needs the map.
    /// </remarks>
    /// <param name="value">The enum's underlying value.</param>
    public static string? ManifestKindName(int value) => KindName(value);

    /// <summary>
    /// Maps <c>TriggerKind</c>'s underlying value back to its name.
    /// </summary>
    /// <remarks>
    /// Spelled out rather than derived from the enum symbol, for the reason
    /// <c>CapabilityReader</c> gives about <c>Authorization</c>, and one more specific to
    /// this enum: these names are also the manifest schema's closed <c>kind</c> enum.
    /// Deriving the name from metadata would let a new <c>TriggerKind</c> member reach the
    /// manifest as a value the schema rejects, discovered by whoever validated the
    /// document. Written out, adding a member fails
    /// <c>EveryTriggerKindHasAManifestNameTheSchemaAccepts</c> instead.
    /// </remarks>
    private static string? KindName(object? value) => value switch
    {
        0 => "Manual",
        1 => "Http",
        2 => "Bus",
        3 => "Schedule",
        4 => "Stream",
        5 => "Change",
        6 => "Agent",
        7 => "Cli",
        _ => null,
    };

    private static TriggerModel? ReadOne(AttributeData attribute)
    {
        var kind = KindOf(attribute.AttributeClass);

        // No readable declaration of the family: skipped rather than guessed at, and
        // reported — see TriggerDeclarationAnalyzer.
        return kind is null ? null : Shape(attribute, kind) ?? new TriggerModel(kind);
    }

    /// <summary>
    /// The declared address of one of the five attributes whose shape is part of the
    /// platform contract, or <c>null</c> for an attribute whose arguments this build cannot
    /// interpret.
    /// </summary>
    /// <remarks>
    /// The kind comes from the marker rather than from this switch even for the built-ins,
    /// so the reader has exactly one answer to "which family is this" and the two cannot
    /// drift apart inside it.
    /// </remarks>
    private static TriggerModel? Shape(AttributeData attribute, string kind) =>
        attribute.AttributeClass?.ToDisplayString() switch
        {
            "FlowX.HttpTriggerAttribute" => new TriggerModel(
                kind,
                method: Positional(attribute, 0),
                route: Positional(attribute, 1),
                idempotent: Flag(attribute, "Idempotent")),

            "FlowX.KafkaTriggerAttribute" => new TriggerModel(
                kind,
                transport: "kafka",
                topic: Positional(attribute, 0),
                group: Named(attribute, "Group")),

            "FlowX.CronTriggerAttribute" => new TriggerModel(
                kind,
                cron: Positional(attribute, 0),
                timeZone: Named(attribute, "TimeZone") ?? "UTC"),

            "FlowX.StreamTriggerAttribute" => new TriggerModel(
                kind,
                topic: Positional(attribute, 0)),

            "FlowX.AgentTriggerAttribute" => new TriggerModel(
                kind,
                description: Named(attribute, "Description"),
                confirmation: ConfirmationName(attribute)),

            // A trigger attribute this build has no shape for. Its kind still publishes.
            _ => null,
        };

    private static string? Positional(AttributeData attribute, int index) =>
        attribute.ConstructorArguments.Length > index
            ? attribute.ConstructorArguments[index].Value as string
            : null;

    private static string? Named(AttributeData attribute, string name) => attribute.NamedArguments
        .Where(pair => pair.Key == name)
        .Select(pair => pair.Value.Value as string)
        .FirstOrDefault();

    /// <summary>Reads a boolean named argument, defaulting to the attribute's own default.</summary>
    /// <remarks>
    /// Returns <c>false</c> rather than <c>null</c> when absent: <c>Idempotent</c> is a
    /// <c>bool</c> with a default of <c>false</c>, so "not written" and "written false"
    /// are the same declaration and must produce the same manifest.
    /// </remarks>
    private static bool Flag(AttributeData attribute, string name) => attribute.NamedArguments
        .Where(pair => pair.Key == name)
        .Select(pair => pair.Value.Value is bool flag && flag)
        .FirstOrDefault();

    /// <summary>
    /// Maps <c>ConfirmationMode</c>'s underlying value back to its name.
    /// </summary>
    /// <remarks>
    /// Spelled out rather than derived, for the reason <c>CapabilityReader</c> gives about
    /// <c>Authorization</c>: reordering the enum is a breaking change the compiler cannot
    /// see here, and it should surface as a failing test rather than as a silently wrong
    /// manifest. The default matches the attribute's own —
    /// <c>RequiredForSideEffects</c> — so omitting the argument and writing it produce the
    /// same document.
    /// </remarks>
    private static string ConfirmationName(AttributeData attribute)
    {
        var declared = attribute.NamedArguments
            .Where(pair => pair.Key == "Confirmation")
            .Select(pair => pair.Value.Value)
            .FirstOrDefault();

        return declared switch
        {
            0 => "Never",
            2 => "Always",
            _ => "RequiredForSideEffects",
        };
    }
}
