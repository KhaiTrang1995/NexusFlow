using System.Collections.Generic;
using System.Linq;
using FlowX.Compiler.Diagnostics;
using FlowX.Compiler.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FlowX.Compiler.Analysis;

/// <summary>A flow model, or the diagnostics explaining why there is not one.</summary>
public sealed class AnalysisResult
{
    private AnalysisResult(FlowModel? model, IReadOnlyList<Diagnostic> diagnostics)
    {
        Model = model;
        Diagnostics = diagnostics;
    }

    /// <summary>The analysed flow, or <c>null</c> when analysis failed.</summary>
    public FlowModel? Model { get; }

    /// <summary>Everything to report, whether or not a model was produced.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>True when a model was produced and nothing blocking was found.</summary>
    public bool IsSuccess => Model is not null && !Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    internal static AnalysisResult Success(FlowModel model, IReadOnlyList<Diagnostic> diagnostics) =>
        new AnalysisResult(model, diagnostics);

    internal static AnalysisResult Failure(IReadOnlyList<Diagnostic> diagnostics) =>
        new AnalysisResult(null, diagnostics);
}

/// <summary>
/// Turns a flow declaration into a <see cref="FlowModel"/>, reporting diagnostics for
/// anything it cannot.
/// </summary>
/// <remarks>
/// <para>
/// The <strong>only</strong> place where Roslyn symbols and the model meet. Everything
/// above this reads syntax; everything below it reads plain objects. That boundary is
/// the R1 mitigation, and <c>ModelLayerHasNoRoslynDependency</c> keeps it honest.
/// </para>
/// <para>
/// Analysis is deliberately forgiving about what it does not understand: an unknown
/// chain method is skipped rather than reported. The DSL grows across phases, and a
/// generator that errors on every method it has not learned yet would block P1 work on
/// P0 code.
/// </para>
/// </remarks>
public static class FlowAnalyzer
{
    private const string FlowAttribute = "FlowX.FlowAttribute";
    private const string FlowDeadlineAttribute = "FlowX.FlowDeadlineAttribute";

    /// <summary>Analyses one flow type.</summary>
    /// <param name="flowType">The class carrying <c>[Flow]</c>.</param>
    /// <param name="declaration">Its syntax, used for locations and for the Define body.</param>
    /// <param name="semanticModel">The model that resolves the chain's type arguments.</param>
    public static AnalysisResult Analyze(
        INamedTypeSymbol flowType,
        ClassDeclarationSyntax declaration,
        SemanticModel semanticModel)
    {
        var diagnostics = new List<Diagnostic>();

        if (flowType is null || declaration is null || semanticModel is null)
        {
            return AnalysisResult.Failure(diagnostics);
        }

        var flowAttribute = flowType.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == FlowAttribute);

        if (flowAttribute is null || flowAttribute.ConstructorArguments.Length == 0)
        {
            return AnalysisResult.Failure(diagnostics);
        }

        // FLOWX1001 — the generated plan is emitted as a second part of this class.
        if (!declaration.Modifiers.Any(m => m.ValueText == "partial"))
        {
            diagnostics.Add(Diagnostic.Create(
                FlowXDiagnostics.FlowMustBePartial,
                declaration.Identifier.GetLocation(),
                flowType.Name));

            return AnalysisResult.Failure(diagnostics);
        }

        // FLOWX1005 — inheritance hides control flow from the compiled graph.
        var baseFlow = flowType.BaseType;

        if (baseFlow is not null && baseFlow.GetAttributes()
                .Any(a => a.AttributeClass?.ToDisplayString() == FlowAttribute))
        {
            diagnostics.Add(Diagnostic.Create(
                FlowXDiagnostics.FlowInheritsFlow,
                declaration.Identifier.GetLocation(),
                flowType.Name,
                baseFlow.Name));

            return AnalysisResult.Failure(diagnostics);
        }

        var defineBody = FindDefineBody(declaration);
        var links = FlowChainWalker.Walk(defineBody);
        var steps = BuildSteps(links, semanticModel, diagnostics);

        // FLOWX1023 — an empty flow has no observable behaviour.
        if (steps.Count == 0)
        {
            diagnostics.Add(Diagnostic.Create(
                FlowXDiagnostics.FlowHasNoSteps,
                declaration.Identifier.GetLocation(),
                flowType.Name));

            return AnalysisResult.Failure(diagnostics);
        }

        var profile = ReadProfile(flowAttribute);

        // FLOWX1017 — an in-memory wait does not survive a deployment.
        if (profile != "Durable" && steps.Any(s => s.Kind == StepKindModel.AwaitSignal))
        {
            diagnostics.Add(Diagnostic.Create(
                FlowXDiagnostics.AwaitSignalRequiresDurable,
                declaration.Identifier.GetLocation(),
                flowType.Name,
                profile));

            return AnalysisResult.Failure(diagnostics);
        }

        var contracts = ReadFlowContracts(flowType);

        var model = new FlowModel(
            flowId: flowAttribute.ConstructorArguments[0].Value as string ?? flowType.Name,
            version: ReadNamedString(flowAttribute, "Version") ?? "1.0.0",
            profile: profile,
            deadline: ReadDeadline(flowType),
            containingNamespace: flowType.ContainingNamespace.IsGlobalNamespace
                ? string.Empty
                : flowType.ContainingNamespace.ToDisplayString(),
            typeName: flowType.Name,
            inputTypeName: contracts.Input,
            outputTypeName: contracts.Output,
            steps: steps,
            declarationLocation: FormatLocation(declaration.Identifier.GetLocation()));

        return AnalysisResult.Success(model, diagnostics);
    }

    private static List<StepModel> BuildSteps(
        IReadOnlyList<ChainLink> links,
        SemanticModel semanticModel,
        List<Diagnostic> diagnostics)
    {
        var steps = new List<StepModel>();

        foreach (var link in links)
        {
            switch (link.MethodName)
            {
                case "Step":
                    AddCapabilityStep(link, semanticModel, diagnostics, steps);
                    break;

                case "Emit":
                case "EmitOnFailure":
                    AddEventStep(link, semanticModel, steps);
                    break;

                case "AwaitSignal":
                    AddSignalStep(link, semanticModel, steps);
                    break;

                case "CompensateWith":
                    AttachCompensation(link, semanticModel, steps);
                    break;

                case "WithPolicy":
                    AttachPolicy(link, steps);
                    break;

                default:
                    // Return, Otherwise, and anything the DSL grows in a later phase.
                    // Skipped rather than reported — see the class remarks on why a
                    // generator must not error on methods it has not learned yet.
                    break;
            }
        }

        return steps;
    }

    private static void AddCapabilityStep(
        ChainLink link,
        SemanticModel semanticModel,
        List<Diagnostic> diagnostics,
        List<StepModel> steps)
    {
        if (link.TypeArguments.Count == 0)
        {
            return;
        }

        var symbol = ResolveType(link.TypeArguments[0], semanticModel);

        if (symbol is null)
        {
            return;
        }

        // FLOWX1002 — a step invokes a capability, and this type is not one.
        if (!CapabilityReader.IsCapability(symbol))
        {
            diagnostics.Add(Diagnostic.Create(
                FlowXDiagnostics.StepIsNotACapability,
                link.TypeArguments[0].GetLocation(),
                symbol.Name));

            return;
        }

        // FLOWX1015 — a capability has exactly one input and one output type.
        var contracts = CapabilityReader.CountCapabilityContracts(symbol);

        if (contracts > 1)
        {
            diagnostics.Add(Diagnostic.Create(
                FlowXDiagnostics.CapabilityHasMultipleContracts,
                link.TypeArguments[0].GetLocation(),
                symbol.Name,
                contracts));

            return;
        }

        var info = CapabilityReader.Read(symbol);

        if (info is null)
        {
            return;
        }

        // FLOWX1010 — there is no permissive default.
        if (!info.DeclaresAuthorization)
        {
            diagnostics.Add(Diagnostic.Create(
                FlowXDiagnostics.CapabilityMissingAuthorization,
                link.TypeArguments[0].GetLocation(),
                info.Id));
        }

        steps.Add(StepModel.Capability(
            steps.Count,
            info.TypeName,
            info.Id,
            info.Version,
            info.IsIdempotent,
            info.SideEffects,
            FormatLocation(link.Invocation.GetLocation())));
    }

    private static void AddEventStep(ChainLink link, SemanticModel semanticModel, List<StepModel> steps)
    {
        if (link.TypeArguments.Count == 0)
        {
            return;
        }

        var symbol = ResolveType(link.TypeArguments[0], semanticModel);

        if (symbol is null)
        {
            return;
        }

        steps.Add(StepModel.Emit(
            steps.Count,
            ToEventIdentity(symbol.Name),
            FormatLocation(link.Invocation.GetLocation())));
    }

    private static void AddSignalStep(ChainLink link, SemanticModel semanticModel, List<StepModel> steps)
    {
        if (link.TypeArguments.Count == 0)
        {
            return;
        }

        var symbol = ResolveType(link.TypeArguments[0], semanticModel);

        if (symbol is null)
        {
            return;
        }

        steps.Add(StepModel.AwaitSignal(
            steps.Count,
            ToEventIdentity(symbol.Name),
            FormatLocation(link.Invocation.GetLocation())));
    }

    private static void AttachCompensation(ChainLink link, SemanticModel semanticModel, List<StepModel> steps)
    {
        if (steps.Count == 0 || link.TypeArguments.Count == 0)
        {
            return;
        }

        var info = CapabilityReader.Read(ResolveType(link.TypeArguments[0], semanticModel));

        if (info is null)
        {
            return;
        }

        // Attaches to the step already built — .CompensateWith follows the .Step it undoes.
        var last = steps.Count - 1;
        steps[last] = steps[last].WithCompensation(info.TypeName, info.Id, info.Version);
    }

    private static void AttachPolicy(ChainLink link, List<StepModel> steps)
    {
        if (steps.Count == 0 || link.Invocation.ArgumentList.Arguments.Count == 0)
        {
            return;
        }

        var last = steps.Count - 1;
        steps[last] = steps[last].WithPolicy(link.Invocation.ArgumentList.Arguments[0].ToString());
    }

    private static ArrowExpressionClauseSyntax? FindArrow(MethodDeclarationSyntax method) =>
        method.ExpressionBody;

    private static SyntaxNode? FindDefineBody(ClassDeclarationSyntax declaration)
    {
        var define = declaration.Members
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.ValueText == "Define");

        if (define is null)
        {
            return null;
        }

        return (SyntaxNode?)FindArrow(define) ?? define.Body;
    }

    private static ITypeSymbol? ResolveType(TypeSyntax syntax, SemanticModel semanticModel) =>
        semanticModel.GetSymbolInfo(syntax).Symbol as ITypeSymbol
        ?? semanticModel.GetTypeInfo(syntax).Type;

    private static (string Input, string Output) ReadFlowContracts(INamedTypeSymbol flowType)
    {
        for (var current = flowType.BaseType; current is not null; current = current.BaseType)
        {
            if (current.MetadataName == "Flow`2" && current.TypeArguments.Length == 2)
            {
                return (
                    Display(current.TypeArguments[0]),
                    Display(current.TypeArguments[1]));
            }
        }

        return ("object", "object");
    }

    private static string Display(ITypeSymbol symbol) =>
        symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty);

    private static string ReadProfile(AttributeData flowAttribute)
    {
        var value = flowAttribute.NamedArguments
            .FirstOrDefault(a => a.Key == "Profile").Value.Value;

        // The enum arrives as its underlying int. Ephemeral is zero, which is the whole
        // point of ADR-0003: a flow that says nothing gets the cheap profile.
        return value switch
        {
            1 => "Durable",
            2 => "Streaming",
            _ => "Ephemeral",
        };
    }

    private static string? ReadNamedString(AttributeData attribute, string name) =>
        attribute.NamedArguments.FirstOrDefault(a => a.Key == name).Value.Value as string;

    private static string? ReadDeadline(INamedTypeSymbol flowType)
    {
        var attribute = flowType.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == FlowDeadlineAttribute);

        return attribute is null || attribute.ConstructorArguments.Length == 0
            ? null
            : attribute.ConstructorArguments[0].Value as string;
    }

    /// <summary>Converts a contract type name into an event identity: <c>OrderPlaced</c> → <c>order.placed</c>.</summary>
    /// <remarks>
    /// A convention, and a temporary one. The identity belongs on the event contract as
    /// an attribute so it can be versioned independently of the CLR type name; deriving
    /// it here means renaming a class silently renames a published event. Tracked for
    /// P1 alongside the event catalogue.
    /// </remarks>
    private static string ToEventIdentity(string typeName)
    {
        var builder = new System.Text.StringBuilder();

        for (var i = 0; i < typeName.Length; i++)
        {
            var c = typeName[i];

            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    builder.Append('.');
                }

                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        var identity = builder.ToString();

        // An identity needs at least one separator to be well formed.
        return identity.Contains(".") ? identity : "event." + identity;
    }

    private static string? FormatLocation(Location location)
    {
        if (location is null || !location.IsInSource)
        {
            return null;
        }

        var span = location.GetLineSpan();

        return span.Path + ":" + (span.StartLinePosition.Line + 1).ToString(
            System.Globalization.CultureInfo.InvariantCulture);
    }
}
