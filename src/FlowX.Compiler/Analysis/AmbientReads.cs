using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FlowX.Compiler.Analysis;

/// <summary>Why a read is not permitted. The wording reaches the message verbatim.</summary>
internal enum Impurity
{
    /// <summary>The wall clock, read from somewhere other than the context.</summary>
    Clock,

    /// <summary>A new identifier or a random number, taken from somewhere other than the context.</summary>
    Randomness,

    /// <summary>Ambient process or machine state.</summary>
    Environment,

    /// <summary>An external service, the file system or the console.</summary>
    InputOutput,

    /// <summary>A static field or property something else can assign between two runs.</summary>
    MutableStatic,

    /// <summary>A local or parameter declared outside the delegate being checked.</summary>
    CapturedVariable,

    /// <summary>A field, property or event of the enclosing type.</summary>
    FlowInstanceState,
}

/// <summary>
/// The half of the determinism analysis that is about <em>what a static access reads</em>,
/// shared by every rule that asks the question.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PredicatePurityAnalyzer"/> (FLOWX1011) asked it first, for the lambdas a flow
/// passes to its builder; <see cref="DeterminismAnalyzer"/> (FLOWX1007, FLOWX1008) asks the
/// same question of a capability body and of the parts of a flow class that are not
/// builder lambdas. Two analyzers, one catalogue: a clock added here is recognised by both
/// on the same day, which is the failure the first version of this rule already had once —
/// one hard-coded construct, and a second construct under the identical rule shipping
/// unchecked beside it.
/// </para>
/// <para>
/// <strong>What lives here is only the part that is shared.</strong> The scope rules — a
/// captured local, a field reached through <c>this</c> — belong to FLOWX1011 alone, because
/// they encode "a flow delegate may read only the flow's own state", and a capability
/// legitimately holds injected dependencies and reads its own input. What both rules share
/// is the narrower claim that a <em>static</em> access to a clock or to a source of
/// identity is ambient wherever it appears.
/// </para>
/// <para>
/// <strong>The catalogue is a list, not a proof.</strong> There is no general way to
/// recognise a clock, so this recognises the ones teams actually reach for — the same
/// stance, and the same admitted limit, as <see cref="CapabilityAnalyzer"/>'s transport
/// list. A clock that is not named here is not detected, and the silence of these rules is
/// never a statement that code is deterministic.
/// </para>
/// </remarks>
internal static class AmbientReads
{
    /// <summary>
    /// Types every member of which is impure.
    /// </summary>
    /// <remarks>
    /// It only ever applies to a <em>static</em> access, one whose chain is rooted at the
    /// type name. <c>ctx.Random.Next()</c> is rooted at the context parameter and is
    /// permitted, which is the distinction the whole rule is about.
    /// </remarks>
    public static readonly Dictionary<string, Impurity> ImpureTypes = new Dictionary<string, Impurity>(StringComparer.Ordinal)
    {
        ["System.Random"] = Impurity.Randomness,
        ["System.Security.Cryptography.RandomNumberGenerator"] = Impurity.Randomness,
        ["System.Environment"] = Impurity.Environment,
        ["System.AppContext"] = Impurity.Environment,
        ["System.AppDomain"] = Impurity.Environment,
        ["System.Diagnostics.Process"] = Impurity.Environment,
        ["System.Threading.Thread"] = Impurity.Environment,
        ["System.Diagnostics.Stopwatch"] = Impurity.Clock,
        ["System.TimeProvider"] = Impurity.Clock,
        ["System.Console"] = Impurity.InputOutput,
        ["System.IO.File"] = Impurity.InputOutput,
        ["System.IO.Directory"] = Impurity.InputOutput,
        ["System.IO.FileInfo"] = Impurity.InputOutput,
        ["System.IO.DirectoryInfo"] = Impurity.InputOutput,
        ["System.Net.Dns"] = Impurity.InputOutput,
        ["System.Net.Http.HttpClient"] = Impurity.InputOutput,
    };

    /// <summary>Individual members whose containing type is otherwise perfectly usable.</summary>
    /// <remarks>
    /// <para>
    /// <c>DateTime</c> is why this list exists separately from <see cref="ImpureTypes"/>:
    /// comparing two dates is the most ordinary thing a condition can do, and only the
    /// ambient entry points are the problem.
    /// </para>
    /// <para>
    /// <c>Environment.TickCount</c> is here rather than being covered by
    /// <c>Environment</c>'s entry above, and the duplication is the point: it is a clock,
    /// not machine identity, and a rule that only reports clocks has to be able to tell
    /// them apart. A member listed here wins over its containing type.
    /// </para>
    /// </remarks>
    public static readonly Dictionary<string, Impurity> ImpureMembers = new Dictionary<string, Impurity>(StringComparer.Ordinal)
    {
        ["System.DateTime.Now"] = Impurity.Clock,
        ["System.DateTime.UtcNow"] = Impurity.Clock,
        ["System.DateTime.Today"] = Impurity.Clock,
        ["System.DateTimeOffset.Now"] = Impurity.Clock,
        ["System.DateTimeOffset.UtcNow"] = Impurity.Clock,
        ["System.Environment.TickCount"] = Impurity.Clock,
        ["System.Environment.TickCount64"] = Impurity.Clock,
        ["System.Guid.NewGuid"] = Impurity.Randomness,
        ["System.Guid.CreateVersion7"] = Impurity.Randomness,
    };

    /// <summary>Namespace-qualified, without <c>global::</c> and without type arguments.</summary>
    private static readonly SymbolDisplayFormat CatalogueFormat = new SymbolDisplayFormat(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces);

    /// <summary>
    /// Whether this node begins an access chain rather than continuing one.
    /// </summary>
    /// <remarks>
    /// The distinction the rule rests on. In <c>ctx.Get&lt;Order&gt;().Total</c> only
    /// <c>ctx</c> is a root; <c>Get</c> and <c>Total</c> are members of a value that came
    /// from it, and classifying them independently would report every ordinary property
    /// read on a step result.
    /// </remarks>
    public static bool IsAccessRoot(SyntaxNode node)
    {
        if (node is ThisExpressionSyntax || node is BaseExpressionSyntax)
        {
            return true;
        }

        if (node is not SimpleNameSyntax)
        {
            return false;
        }

        switch (node.Parent)
        {
            case MemberAccessExpressionSyntax member:
                return member.Expression == node;
            case QualifiedNameSyntax qualified:
                return qualified.Left == node;
            case MemberBindingExpressionSyntax:
            case AliasQualifiedNameSyntax:
            case NameColonSyntax:
            case NameEqualsSyntax:
                return false;
            default:
                return true;
        }
    }

    /// <summary>
    /// Walks up a chain of namespace and type names to the first real member.
    /// </summary>
    /// <remarks>
    /// <c>System.DateTime.UtcNow</c> parses as two nested member accesses whose root
    /// identifier is a namespace, so the classification has to climb rather than look at
    /// one node. A chain that never reaches a member — a <c>typeof</c>, a type argument,
    /// a pattern's type — is permitted.
    /// </remarks>
    public static (SyntaxNode Node, Impurity Impurity)? StaticMemberOn(
        SyntaxNode root,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        var current = root;

        while (current.Parent is MemberAccessExpressionSyntax member && member.Expression == current)
        {
            var symbol = model.GetSymbolInfo(member.Name, cancellationToken).Symbol;

            if (symbol is null)
            {
                return null;
            }

            if (symbol is INamespaceOrTypeSymbol)
            {
                current = member;
                continue;
            }

            return StaticMember(member, symbol);
        }

        return null;
    }

    /// <summary>Decides a static member: catalogue first, then mutability.</summary>
    public static (SyntaxNode Node, Impurity Impurity)? StaticMember(SyntaxNode node, ISymbol symbol)
    {
        var container = symbol.ContainingType;

        if (container is null)
        {
            return null;
        }

        // A constant or an enum member is a literal wearing a name.
        if (symbol is IFieldSymbol { IsConst: true } || container.TypeKind == TypeKind.Enum)
        {
            return null;
        }

        var containerName = Name(container);
        var member = Lookup(ImpureMembers, containerName + "." + symbol.Name)
            ?? Lookup(ImpureTypes, containerName);

        if (member is not null)
        {
            return (node, member.Value);
        }

        // Not on the list, so the remaining question is whether the symbol is state that
        // something else can change between two runs of the same flow.
        switch (symbol)
        {
            case IFieldSymbol { IsReadOnly: false }:
            case IPropertySymbol { SetMethod: not null }:
                return (node, Impurity.MutableStatic);
            default:
                return null;
        }
    }

    /// <summary>Decides a <c>new T(...)</c>, for the types whose every member is impure.</summary>
    /// <remarks>
    /// <c>new Random()</c> is the case that matters: it reaches the same randomness through
    /// a constructor rather than through <c>Random.Shared</c>, and a rule that only looked
    /// at static access would miss the shorter spelling.
    /// </remarks>
    public static (SyntaxNode Node, Impurity Impurity)? Constructed(
        ObjectCreationExpressionSyntax creation,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        var constructed = model.GetSymbolInfo(creation, cancellationToken).Symbol is IMethodSymbol constructor
            ? Lookup(ImpureTypes, Name(constructor.ContainingType))
            : null;

        return constructed is null ? null : (creation, constructed.Value);
    }

    /// <summary>Whether the node sits inside a <c>nameof</c> within the body being walked.</summary>
    /// <remarks>
    /// <c>nameof(x)</c> reads the name and never the value — the one construct that provably
    /// touches nothing.
    /// </remarks>
    public static bool IsInsideNameOf(SyntaxNode node, SyntaxNode body)
    {
        for (var current = node; current is not null && current != body.Parent; current = current.Parent)
        {
            if (current is InvocationExpressionSyntax invocation &&
                invocation.Expression is IdentifierNameSyntax name &&
                name.Identifier.ValueText == "nameof")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>How the reason reads. Written to complete "…, which is {phrase}".</summary>
    /// <remarks>
    /// Only the capture case needs the construct's name, and it needs it badly: "captured
    /// from outside the condition" is simply false when the lambda is a <c>Return</c>
    /// projection, and pointing a developer at the wrong construct costs more than saying
    /// nothing.
    /// </remarks>
    public static string Phrase(Impurity impurity, string noun)
    {
        switch (impurity)
        {
            case Impurity.Clock:
                return "the system clock";
            case Impurity.Randomness:
                return "a source of randomness or identity";
            case Impurity.Environment:
                return "ambient process or environment state";
            case Impurity.InputOutput:
                return "an external service or I/O";
            case Impurity.MutableStatic:
                return "mutable static state";
            case Impurity.CapturedVariable:
                return "a variable captured from outside the " + noun;
            default:
                return "state held on the flow instance rather than in the context";
        }
    }

    /// <summary>The type's namespace-qualified name, as the catalogues spell it.</summary>
    public static string Name(ITypeSymbol type) =>
        type.OriginalDefinition.ToDisplayString(CatalogueFormat);

    private static Impurity? Lookup(Dictionary<string, Impurity> catalogue, string key) =>
        catalogue.TryGetValue(key, out var impurity) ? impurity : (Impurity?)null;
}
