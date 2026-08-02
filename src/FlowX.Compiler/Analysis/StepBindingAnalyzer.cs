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
/// Checks that a flow's steps can actually hand values to each other: FLOWX1020.
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
/// <c>.Step&lt;TCapability, TStepIn&gt;(ctx =&gt; …)</c>, is skipped as a consumer and
/// still counted as a producer. This is not a gap the rule tolerates, it is what the
/// generated code does: the emitted dispatcher runs the mapping and passes its result to
/// the capability as an argument, so the step's declared input never reaches a
/// <c>ctx.Get</c> and its absence from the bag cannot fail anything. The step's
/// <em>output</em> is written to the bag exactly as any other step's is, which is why it
/// still produces.
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
/// <para>
/// <strong>Why the step's capability is resolved speculatively.</strong> The one thing
/// this rule needs from the semantic model is the type each <c>.Step&lt;T&gt;()</c> names.
/// Asking for it the obvious way — <c>GetSymbolInfo</c> on the type-argument node where it
/// sits — makes the model bind the whole enclosing <c>Define</c> body first, because that
/// node is inside a statement and a statement is the smallest thing Roslyn will bind. A
/// <c>Define</c> body is a fluent chain with overload resolution and generic inference at
/// every link and a lambda in most of them, and binding it is expensive: measured on the
/// 200-flow synthetic solution it was <em>96 %</em> of this analyzer's entire cost. The
/// bill falls on the first step of each flow and on no other — 4.13 ms for the first,
/// 0.06 ms for each of the 600 that followed — which is the shape of one body being bound
/// and then cached, not of a type lookup being slow. It is also duplicated work: the
/// compiler binds those same bodies again when it emits, and does not share the model's
/// copy.
/// </para>
/// <para>
/// So the name is bound where it costs nothing to bind: speculatively, against the binder
/// in scope just inside the flow class's opening brace. That binder sees exactly what a
/// type name written inside the class sees — the file's usings and aliases, the enclosing
/// namespaces, the class's own members and its type parameters — and a type name is the
/// only thing being asked about. What it does not see are a method's own type parameters
/// and its locals, and neither can name a type here: <c>Define</c> is an override with a
/// fixed, non-generic signature, and C# has no local types. The measurement is in
/// docs/benchmarks/B12-scale.md §5.1.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StepBindingAnalyzer : DiagnosticAnalyzer
{
    private const string FlowAttributeMetadataName = "FlowAttribute";
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
            context.SemanticModel.GetDeclaredSymbol(declaration, context.CancellationToken) is not INamedTypeSymbol flowType ||
            !CarriesFlowAttribute(flowType))
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

        // Where every type argument in this flow gets bound: inside the class, outside any
        // member body. See the class remarks — this is the whole optimisation.
        var scope = declaration.OpenBraceToken.Span.End;

        Check(context, flowType, flowInput, links, scope);
    }

    /// <summary>Walks the chain in declaration order, reporting steps nothing can feed.</summary>
    private static void Check(
        SyntaxNodeAnalysisContext context,
        INamedTypeSymbol flowType,
        ITypeSymbol flowInput,
        IReadOnlyList<ChainLink> links,
        int scope)
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

                // A poll invokes a capability, so it consumes that capability's input and puts
                // its output in the bag exactly as a step does — the difference is only how
                // many times, which this rule does not ask about. An `.OnTimeout(...)` after it
                // ends the walk at the default arm below, which is the conservative answer
                // every other block already gets.
                case "PollUntil":
                    if (!CheckStep(context, flowType, link, available, produced, scope))
                    {
                        return;
                    }

                    break;

                case "AwaitSignal":
                    if (!Produce(ResolvedTypeArgument(link, context.SemanticModel, scope), available, produced))
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
        List<ITypeSymbol> produced,
        int scope)
    {
        if (link.TypeArguments.Count == 0)
        {
            return false;
        }

        var capability = ResolveType(link.TypeArguments[0], context.SemanticModel, scope);
        var contract = CapabilityContract(capability);

        if (contract is null)
        {
            return false;
        }

        // Only the one-argument overload binds from the bag, so only it can be reported.
        // The two-argument form names its own input and maps it, and the generated
        // dispatcher passes the mapping's result to the capability as an argument — the
        // declared input never reaches a ctx.Get, so it being absent from the bag cannot
        // fail at run time and there is nothing here to report.
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
                !IsFlowXNamespace(candidate.ContainingNamespace) ||
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
                !IsFlowXNamespace(current.ContainingNamespace) ||
                current.TypeArguments.Length != 2)
            {
                continue;
            }

            return IsResolved(current.TypeArguments[0]) ? current.TypeArguments[0] : null;
        }

        return null;
    }

    private static ITypeSymbol? ResolvedTypeArgument(ChainLink link, SemanticModel semanticModel, int scope)
    {
        if (link.TypeArguments.Count != 1)
        {
            return null;
        }

        var type = ResolveType(link.TypeArguments[0], semanticModel, scope);

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

    /// <summary>
    /// The type a <c>.Step&lt;T&gt;()</c> type argument names, bound at <paramref name="scope"/>.
    /// </summary>
    /// <param name="syntax">The type argument as written.</param>
    /// <param name="semanticModel">The model for the tree it was written in.</param>
    /// <param name="scope">A position inside the flow class but outside any member body.</param>
    /// <remarks>
    /// <para>
    /// The name is re-parsed rather than passed in as it stands, because a speculative bind
    /// is defined over an expression that is <em>not</em> part of the tree being asked
    /// about. Handing back a node that <em>is</em> part of it asks the model for the
    /// meaning that node already has, which is the route through the enclosing method body
    /// this exists to avoid. Re-parsing a type name a few identifiers long is what that
    /// costs, and it is small: parse and bind together measured ~0.05 ms per step, against
    /// the 4.13 ms the direct call cost on the first step of every flow.
    /// </para>
    /// <para>
    /// The <c>GetSpeculativeTypeInfo</c> fallback mirrors the <c>GetTypeInfo</c> one it
    /// replaces, and reaches the same answers. A name that binds to nothing yields
    /// <c>null</c> from the first call and an error type from the second, and both are
    /// refused downstream — by <see cref="CapabilityContract"/>, which finds no interfaces
    /// on either, and by <see cref="IsResolved"/>, which rejects error types.
    /// </para>
    /// </remarks>
    private static ITypeSymbol? ResolveType(TypeSyntax syntax, SemanticModel semanticModel, int scope)
    {
        var speculative = SyntaxFactory.ParseTypeName(syntax.ToString());

        return semanticModel
                   .GetSpeculativeSymbolInfo(scope, speculative, SpeculativeBindingOption.BindAsTypeOrNamespace)
                   .Symbol as ITypeSymbol
               ?? semanticModel
                   .GetSpeculativeTypeInfo(scope, speculative, SpeculativeBindingOption.BindAsTypeOrNamespace)
                   .Type;
    }

    /// <summary>Whether the type carries <c>[Flow]</c>.</summary>
    /// <remarks>
    /// Metadata name and namespace rather than <c>ToDisplayString()</c>, which this ran on
    /// every attribute of every class in the compilation and which builds a string to throw
    /// away. The <c>ContainingType</c> test is what keeps the two spellings equal: a nested
    /// <c>FlowX.Something.FlowAttribute</c> displays as its full path and never matched.
    /// </remarks>
    private static bool CarriesFlowAttribute(INamedTypeSymbol flowType)
    {
        foreach (var attribute in flowType.GetAttributes())
        {
            if (attribute.AttributeClass is { ContainingType: null } attributeClass &&
                attributeClass.MetadataName == FlowAttributeMetadataName &&
                IsFlowXNamespace(attributeClass.ContainingNamespace))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether this is the top-level <c>FlowX</c> namespace.</summary>
    /// <remarks>
    /// Exactly what <c>ContainingNamespace?.ToDisplayString() == "FlowX"</c> asked, without
    /// the string: that display is the dotted path from the global namespace, so equality
    /// with a one-segment name says the segment is <c>FlowX</c> and its parent is global.
    /// </remarks>
    private static bool IsFlowXNamespace(INamespaceSymbol? candidate) =>
        candidate is { Name: FlowXNamespace } && candidate.ContainingNamespace is { IsGlobalNamespace: true };

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
