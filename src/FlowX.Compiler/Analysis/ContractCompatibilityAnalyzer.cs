using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using FlowX.Compiler.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FlowX.Compiler.Analysis;

/// <summary>
/// Checks that a flow's steps can actually hand values to each other: FLOWX1022.
/// </summary>
/// <remarks>
/// <para>
/// Steps do not pass values positionally. Each step's output goes into the flow's state
/// bag under its own type, and the next step's input is read back out by type — the
/// emitted dispatcher writes <c>ctx.Get&lt;TIn&gt;()</c> for every capability step. If
/// nothing earlier put a <c>TIn</c> in the bag, that <c>Get</c> throws on the first
/// request the flow ever serves. The flow compiles, deploys, and then fails for
/// everybody, which is precisely the class of failure this platform exists to move to
/// build time (quality requirement QR3).
/// </para>
/// <para>
/// A <see cref="DiagnosticAnalyzer"/> rather than more work inside the generator,
/// matching <see cref="CapabilityAnalyzer"/>: the question is about a flow's shape and
/// is worth answering in the editor, on the keystroke that reorders two steps, rather
/// than only when the generator next runs.
/// </para>
/// <para>
/// <strong>Availability is exact type identity, not assignability.</strong> The state
/// bag is a <c>Dictionary&lt;Type, object&gt;</c> keyed on <c>typeof(T)</c>, and both
/// sides of that lookup use the <em>declared</em> contract type. So a step producing a
/// <c>Derived</c> does not satisfy a step consuming its <c>Base</c>: the lookup misses
/// and the flow throws. Treating a base type as satisfied would make this rule agree
/// with an intuition about C# and disagree with the runtime, which is the worse of the
/// two ways to be wrong — it would stay silent on a flow that is genuinely broken.
/// </para>
/// <para>
/// <strong>Compensations are deliberately not checked.</strong> A compensation does not
/// bind its own input from the bag; the dispatcher hands it the input of the step it
/// undoes, because that is the value it has to reverse. Its declared input therefore
/// never reaches a <c>ctx.Get</c>, and reporting on it would point a developer at a type
/// nothing looks up. A compensation whose contract disagrees with its step is a real
/// defect, but it surfaces as a type error in the generated call, not as a missing bag
/// entry, and it is a different rule from this one. For the same reason a compensation
/// produces nothing: it runs only on the failure path, after which no later step runs.
/// </para>
/// <para>
/// <strong>Where it stays silent, and why.</strong> A rule that fires on a valid flow
/// gets suppressed, and a suppressed rule protects nothing — so every case this analyzer
/// cannot decide is a case it says nothing about:
/// </para>
/// <list type="bullet">
/// <item>
/// A step written with the explicit-mapping overload,
/// <c>.Step&lt;TCapability, TStepIn&gt;(ctx =&gt; …)</c>, supplies its own input from a
/// lambda instead of from the bag. It is skipped as a consumer and still counted as a
/// producer.
/// </item>
/// <item>
/// The chain is read up to the first call whose effect on the bag is not known —
/// <c>When</c>, <c>Parallel</c>, <c>ForEach</c>, <c>SubFlow</c>, or anything the DSL
/// grows later. Steps <em>before</em> it are still checked, because nothing hidden can
/// have run before them; everything after it is abandoned, because a branch may have
/// produced the very type the next step wants.
/// </item>
/// <item>
/// Any step whose capability contract cannot be resolved — an unresolved symbol, a type
/// parameter, or more than one <c>ICapability&lt;,&gt;</c> (FLOWX1015) — abandons the
/// whole flow, since an unknown output makes every later step unknowable too.
/// </item>
/// <item>
/// <c>AwaitSignal&lt;TSignal&gt;</c> is counted as producing its signal, which is what
/// the suspension means even though nothing delivers a signal in this release. Counting
/// it the other way would report a flow whose durable machinery does not exist yet.
/// </item>
/// </list>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ContractCompatibilityAnalyzer : DiagnosticAnalyzer
{
    private const string FlowAttribute = "FlowX.FlowAttribute";
    private const string FlowBaseMetadataName = "Flow`2";
    private const string CapabilityMetadataName = "ICapability`2";
    private const string FlowXNamespace = "FlowX";
    private const string DefineMethodName = "Define";

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(FlowXDiagnostics.StepInputIsNeverProduced);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            return;
        }

        // The generated partial carries no Define, so it has nothing to say here — but
        // excluding it explicitly keeps the analyzer off code it did not write, the same
        // stance CapabilityAnalyzer takes.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        // A syntax action, not a symbol one: the chain is syntax, and this is the only
        // registration that arrives with the semantic model already built for the tree.
        // Asking a symbol action for one would rebuild it per flow.
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ClassDeclaration);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        if (context.Node is not ClassDeclarationSyntax declaration ||
            context.SemanticModel.GetDeclaredSymbol(declaration) is not INamedTypeSymbol flowType ||
            !flowType.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == FlowAttribute))
        {
            return;
        }

        // The seed. The engine puts the flow's own input into the bag before the first
        // step runs, which is what makes step 1 bindable at all.
        var flowInput = FlowInputContract(flowType);

        if (flowInput is null)
        {
            return;
        }

        var define = declaration.Members
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.ValueText == DefineMethodName);

        if (define is null)
        {
            return;
        }

        var links = FlowChainWalker.Walk(
            (SyntaxNode?)define.ExpressionBody ?? define.Body,
            define.ParameterList.Parameters.Count > 0
                ? define.ParameterList.Parameters[0].Identifier.ValueText
                : null);

        Check(context, flowType, flowInput, links);
    }

    /// <summary>Walks the chain in declaration order, reporting steps nothing can feed.</summary>
    private static void Check(
        SyntaxNodeAnalysisContext context,
        INamedTypeSymbol flowType,
        ITypeSymbol flowInput,
        IReadOnlyList<ChainLink> links)
    {
        // Two collections for one fact: the set answers "is it there", the list keeps
        // production order so the message can list what the flow *can* supply. Being told
        // only what is missing leaves the developer guessing at the alternative.
        var available = new HashSet<ISymbol>(SymbolEqualityComparer.Default) { flowInput };
        var produced = new List<ITypeSymbol> { flowInput };

        foreach (var link in links)
        {
            switch (link.MethodName)
            {
                case "Step":
                    if (!CheckStep(context, flowType, link, available, produced))
                    {
                        return;
                    }

                    break;

                case "AwaitSignal":
                    if (!Produce(ResolvedTypeArgument(link, context.SemanticModel), available, produced))
                    {
                        return;
                    }

                    break;

                case "CompensateWith":
                case "WithPolicy":
                case "Emit":
                case "EmitOnFailure":
                case "Delay":
                case "Fail":
                case "Return":
                    // None of these read or write a typed slot. Emit and EmitOnFailure
                    // build their event from a lambda and publish it; the event never
                    // enters the bag, so a later step cannot bind to one.
                    break;

                default:
                    // A branch, a loop, a sub-flow, or a DSL method written after this
                    // rule was. Any of them can put a type into the bag out of sight of
                    // this walk, so everything from here on is unknowable — see the
                    // class remarks.
                    return;
            }
        }
    }

    /// <summary>
    /// Checks one <c>.Step&lt;…&gt;()</c> and records its output. Returns <c>false</c>
    /// when the step could not be understood, which abandons the rest of the flow.
    /// </summary>
    private static bool CheckStep(
        SyntaxNodeAnalysisContext context,
        INamedTypeSymbol flowType,
        ChainLink link,
        HashSet<ISymbol> available,
        List<ITypeSymbol> produced)
    {
        if (link.TypeArguments.Count == 0)
        {
            return false;
        }

        var capability = ResolveType(link.TypeArguments[0], context.SemanticModel);
        var contract = CapabilityContract(capability);

        if (contract is null)
        {
            return false;
        }

        // Only the one-argument overload binds from the bag. The two-argument form names
        // its own input and maps it, which is the documented fix for this very diagnostic
        // — reporting it would fire on the remedy.
        if (link.TypeArguments.Count == 1 && !available.Contains(contract.Value.Input))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                FlowXDiagnostics.StepInputIsNeverProduced,
                link.TypeArguments[0].GetLocation(),
                Display(capability!),
                Display(contract.Value.Input),
                flowType.Name,
                string.Join(", ", produced.Select(Display))));
        }

        return Produce(contract.Value.Output, available, produced);
    }

    /// <summary>Records a type as available to every later step.</summary>
    /// <remarks>
    /// Later steps, not merely the next one: the bag is never cleared between steps, so
    /// a value survives until the flow ends. A rule that only looked at the immediately
    /// preceding step would reject the common shape where step 4 needs the flow's input.
    /// </remarks>
    private static bool Produce(ITypeSymbol? type, HashSet<ISymbol> available, List<ITypeSymbol> produced)
    {
        if (type is null)
        {
            return false;
        }

        if (available.Add(type))
        {
            produced.Add(type);
        }

        return true;
    }

    /// <summary>
    /// The <c>TIn</c>/<c>TOut</c> of the single <c>ICapability&lt;,&gt;</c> the type
    /// implements, or <c>null</c> when there is not exactly one that is fully resolved.
    /// </summary>
    /// <remarks>
    /// Zero is FLOWX1002's business and more than one is FLOWX1015's; in both cases this
    /// rule has no defensible answer and gives none.
    /// </remarks>
    private static (ITypeSymbol Input, ITypeSymbol Output)? CapabilityContract(ITypeSymbol? type)
    {
        if (type is null)
        {
            return null;
        }

        INamedTypeSymbol? contract = null;

        foreach (var candidate in type.AllInterfaces)
        {
            if (candidate.MetadataName != CapabilityMetadataName ||
                candidate.ContainingNamespace?.ToDisplayString() != FlowXNamespace ||
                candidate.TypeArguments.Length != 2)
            {
                continue;
            }

            if (contract is not null)
            {
                return null;
            }

            contract = candidate;
        }

        if (contract is null ||
            !IsResolved(contract.TypeArguments[0]) ||
            !IsResolved(contract.TypeArguments[1]))
        {
            return null;
        }

        return (contract.TypeArguments[0], contract.TypeArguments[1]);
    }

    /// <summary>The <c>TIn</c> of the <c>Flow&lt;TIn, TOut&gt;</c> this type derives from.</summary>
    private static ITypeSymbol? FlowInputContract(INamedTypeSymbol flowType)
    {
        for (var current = flowType.BaseType; current is not null; current = current.BaseType)
        {
            if (current.MetadataName != FlowBaseMetadataName ||
                current.ContainingNamespace?.ToDisplayString() != FlowXNamespace ||
                current.TypeArguments.Length != 2)
            {
                continue;
            }

            return IsResolved(current.TypeArguments[0]) ? current.TypeArguments[0] : null;
        }

        return null;
    }

    private static ITypeSymbol? ResolvedTypeArgument(ChainLink link, SemanticModel semanticModel)
    {
        if (link.TypeArguments.Count != 1)
        {
            return null;
        }

        var type = ResolveType(link.TypeArguments[0], semanticModel);

        return type is not null && IsResolved(type) ? type : null;
    }

    /// <summary>
    /// Whether the type is concrete enough to be a bag key.
    /// </summary>
    /// <remarks>
    /// An error type means the file does not compile and the developer already has a
    /// better message than this one. An open type parameter has no <c>typeof</c> until it
    /// is substituted, so nothing can be said about it here.
    /// </remarks>
    private static bool IsResolved(ITypeSymbol type) =>
        type.TypeKind != TypeKind.Error && type is not ITypeParameterSymbol;

    private static ITypeSymbol? ResolveType(TypeSyntax syntax, SemanticModel semanticModel) =>
        semanticModel.GetSymbolInfo(syntax).Symbol as ITypeSymbol
        ?? semanticModel.GetTypeInfo(syntax).Type;

    /// <summary>
    /// How a type is named in the message.
    /// </summary>
    /// <remarks>
    /// Minimally qualified, not fully: the developer is looking at a call site written
    /// <c>.Step&lt;CapturePayment&gt;()</c>, and a message that says
    /// <c>Ecommerce.Capabilities.CapturePayment</c> makes them read past the namespace to
    /// find the word they already typed.
    /// </remarks>
    private static string Display(ITypeSymbol symbol) =>
        symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
}
