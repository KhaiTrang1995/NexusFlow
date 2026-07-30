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
/// <c>Kind</c> is not attribute data — it is an abstract property that each attribute
/// overrides with an expression (<c>public override TriggerKind Kind =&gt;
/// TriggerKind.Http;</c>). From metadata that is executable code, not a value, so there
/// is no general way to ask an arbitrary <c>TriggerAttribute</c> subclass what family it
/// belongs to, nor what its constructor arguments mean.
/// </para>
/// <para>
/// This reader therefore recognises the five attributes <c>FlowX.Abstractions</c> ships,
/// whose shape is part of the platform contract, and <strong>skips any other
/// <c>TriggerAttribute</c> subclass rather than guessing</strong> — the same stance
/// <c>ManifestWriter.WritePolicies</c> takes toward a policy kind whose stage it does not
/// know. A transport plugin that declares its own trigger attribute is consequently
/// invisible to the manifest today. That is a real gap, recorded here rather than papered
/// over with an invented kind, and closing it needs a declaration the compiler can read
/// without running the plugin.
/// </para>
/// <para>
/// <strong>The skip is no longer silent.</strong> Declining to invent a kind is right;
/// doing it without saying so is not, because the result is an <em>absent</em>
/// <c>triggers</c> entry and <c>flowx diff</c> classifies a removed trigger as breaking —
/// so a contract gate loses its input and reports nothing. <c>TriggerDeclarationAnalyzer</c>
/// reports <c>FLOWX1025</c> on exactly the attributes <see cref="IsRecognised"/> rejects,
/// which is why that predicate lives here beside the switch it must agree with rather
/// than as a second list in the analyzer.
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

    /// <summary>
    /// The trigger attributes this build can read, by full type name.
    /// </summary>
    /// <remarks>
    /// Must stay in step with the switch in <c>ReadOne</c>; a name here that the switch
    /// does not handle would silence <c>FLOWX1025</c> for a trigger that still never
    /// reaches the manifest, which is the worst of both positions.
    /// <c>EveryTriggerAttributeTheAbstractionShipsIsRecognised</c> reflects over
    /// <c>FlowX.Abstractions</c> and fails if a sixth attribute is added without being
    /// added here.
    /// </remarks>
    private static readonly string[] Recognised =
    [
        "FlowX.AgentTriggerAttribute",
        "FlowX.CronTriggerAttribute",
        "FlowX.HttpTriggerAttribute",
        "FlowX.KafkaTriggerAttribute",
        "FlowX.StreamTriggerAttribute",
    ];

    /// <summary>The full type names of the trigger attributes this build can read.</summary>
    public static IReadOnlyList<string> RecognisedAttributes => Recognised;

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

    /// <summary>Whether this build can read the trigger the attribute declares.</summary>
    /// <remarks>
    /// <c>false</c> for every <c>TriggerAttribute</c> subclass the abstractions do not
    /// ship — see the type's remarks for why that cannot be decided from metadata.
    /// </remarks>
    /// <param name="attributeClass">The applied attribute's type, or <c>null</c>.</param>
    public static bool IsRecognised(INamedTypeSymbol? attributeClass) =>
        attributeClass is not null &&
        System.Array.IndexOf(Recognised, attributeClass.ToDisplayString()) >= 0;

    private static TriggerModel? ReadOne(AttributeData attribute) =>
        attribute.AttributeClass?.ToDisplayString() switch
        {
            "FlowX.HttpTriggerAttribute" => new TriggerModel(
                "Http",
                method: Positional(attribute, 0),
                route: Positional(attribute, 1),
                idempotent: Flag(attribute, "Idempotent")),

            "FlowX.KafkaTriggerAttribute" => new TriggerModel(
                "Bus",
                transport: "kafka",
                topic: Positional(attribute, 0),
                group: Named(attribute, "Group")),

            "FlowX.CronTriggerAttribute" => new TriggerModel(
                "Schedule",
                cron: Positional(attribute, 0),
                timeZone: Named(attribute, "TimeZone") ?? "UTC"),

            "FlowX.StreamTriggerAttribute" => new TriggerModel(
                "Stream",
                topic: Positional(attribute, 0)),

            "FlowX.AgentTriggerAttribute" => new TriggerModel(
                "Agent",
                description: Named(attribute, "Description"),
                confirmation: ConfirmationName(attribute)),

            // A trigger attribute this build does not recognise. See the type's remarks.
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
