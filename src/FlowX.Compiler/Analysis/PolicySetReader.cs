using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FlowX.Compiler.Analysis;

/// <summary>
/// What a named <c>PolicySet</c> declares, and whether the compiler could tell at all.
/// </summary>
/// <remarks>
/// <para>
/// The two answers are different and used to be spelled the same way. An empty kind list
/// meant both "this set declares nothing" and "this set could not be read", and every caller
/// treated the second as the first — which is how a whole declared set came to reach no plan,
/// no manifest and none of the five rules that ask what is in one, with nothing saying so.
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1036.md">FLOWX1036</a>
/// is that report, and <see cref="IsReadable"/> is what it is raised from.
/// </para>
/// </remarks>
public sealed class PolicySetContents
{
    private PolicySetContents(bool isReadable, IReadOnlyList<string> kinds, ExpressionSyntax? initialiser)
    {
        IsReadable = isReadable;
        Kinds = kinds;
        Initialiser = initialiser;
    }

    /// <summary>The answer for a set whose contents the compiler cannot see.</summary>
    public static PolicySetContents Unreadable { get; } =
        new PolicySetContents(false, new List<string>(), null);

    /// <summary>Whether the compiler could establish what is in the set.</summary>
    public bool IsReadable { get; }

    /// <summary>
    /// The policy kinds the set declares, ordinally sorted; empty when
    /// <see cref="IsReadable"/> is <see langword="false"/>.
    /// </summary>
    public IReadOnlyList<string> Kinds { get; }

    /// <summary>
    /// The fluent chain that built the set, when it was read out of source.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> for a well-known set resolved from metadata: there is no syntax
    /// to hand back, only the composition. A rule that needs a policy's <em>arguments</em> —
    /// FLOWX1035 reads the attempt count — is therefore silent on those, which is right for a
    /// set whose arguments FlowX itself fixed.
    /// </remarks>
    public ExpressionSyntax? Initialiser { get; }

    internal static PolicySetContents ReadFrom(ExpressionSyntax initialiser)
    {
        // Every link in the chain except the PolicySet.Named(...) that starts it is a
        // policy. Named is the constructor, not a policy, and Empty is not a call at all.
        var kinds = FlowChainWalker.Walk(initialiser)
            .Select(link => link.MethodName)
            .Where(name => name != "Named" && name != "Empty")
            .Distinct()
            .ToList();

        kinds.Sort(System.StringComparer.Ordinal);
        return new PolicySetContents(true, kinds, initialiser);
    }

    internal static PolicySetContents WellKnown(IReadOnlyList<string> kinds) =>
        new PolicySetContents(true, kinds, null);
}

/// <summary>
/// Reads which policies a named <c>PolicySet</c> declares.
/// </summary>
/// <remarks>
/// <para>
/// <c>.WithPolicy(Policies.PaymentGateway)</c> names a set; it does not say what is in
/// it. Until this existed the compiler stored the argument's source text and nothing
/// more, so <strong>FLOWX1014 and FLOWX1018 could never fire</strong> — both ask a
/// question about the set's contents. Both were documented as compile errors anyway,
/// FLOWX1014 as the thing that prevents a duplicate charge.
/// </para>
/// <para>
/// A policy set is declared as a fluent chain — <c>PolicySet.Named("x").Retry(3)</c> —
/// so reading it is the same problem as reading a <c>Define</c> body, and uses the same
/// <see cref="FlowChainWalker"/>. The set must be a field or property with an
/// initialiser in source; one built at run time cannot be inspected at compile time, and
/// this returns <see cref="PolicySetContents.Unreadable"/> rather than guessing.
/// </para>
/// <para>
/// <strong>With one exception, and it is the set the documents recommend.</strong>
/// <c>PolicySet.CompensationDefault</c> is declared in <c>FlowX.Abstractions</c>, so in every
/// consuming compilation its symbol arrives as metadata and has no
/// <c>DeclaringSyntaxReferences</c> — there is no initialiser to walk, because it was compiled
/// to IL in another build and Roslyn does not read IL. For two releases that meant
/// <c>.WithPolicy(PolicySet.CompensationDefault)</c> resolved to nothing, the emitter wrote no
/// chain, and the step got no policies at all: <c>docs/06-Execution-Engine.md</c> §7 rule 2
/// recommended it for compensation and FLOWX1033 offered it as a repair, and it was the one
/// way to retry an undo that could not work.
/// </para>
/// <para>
/// <strong>What the compiler can know here.</strong> Not the initialiser — but it does not
/// need the initialiser for a set FlowX itself declares. The composition of
/// <c>PolicySet</c>'s own static sets is part of FlowX's published surface, fixed at the same
/// version as this compiler, so <see cref="WellKnownSets"/> carries it directly. That is the
/// arrangement <c>ManifestWriter.KnownPolicyStages</c> already uses for the stage table and
/// <c>FlowEmitter.CompensationRetryKind</c> for the kind name, both under the same constraint:
/// this assembly targets netstandard2.0 and cannot link against <c>FlowX.Abstractions</c> to
/// read the real thing. Like both of those, the copy is pinned —
/// <c>PolicySetContentsAreThePinnedOnes</c> reads the real sets by reflection and fails the
/// build if the table drifts, or if <c>PolicySet</c> grows a well-known set this has not been
/// told about.
/// </para>
/// <para>
/// <strong>It stops there, and deliberately.</strong> A policy set in <em>anyone else's</em>
/// referenced assembly is genuinely unreadable: nothing pins its composition to the compiler
/// that would have to guess it, and a guess would put kinds in the manifest that are not in
/// the set. Those get FLOWX1036 instead of silence.
/// </para>
/// </remarks>
public static class PolicySetReader
{
    /// <summary>
    /// The composition of every well-known set <c>PolicySet</c> declares, by member name,
    /// with each set's kinds ordinally sorted.
    /// </summary>
    /// <remarks>
    /// Public so <c>PolicyStageFitnessTests</c> can compare it with the real sets. An unpinned
    /// copy of a published composition drifts silently, and this one drifts into emitting the
    /// wrong chain rather than into a wrong message.
    /// </remarks>
    public static IReadOnlyDictionary<string, string[]> WellKnownSets { get; } =
        new Dictionary<string, string[]>(System.StringComparer.Ordinal)
        {
            // docs/06-Execution-Engine.md §7 rule 2 — five attempts, full-jitter exponential
            // backoff, and nothing else. The attempt count is not carried: the emitter copies
            // the author's expression verbatim into the plan, so the runtime reads the real
            // set and this only has to know which kinds are in it.
            ["CompensationDefault"] = new[] { "CompensationRetry" },

            // Declares nothing, which is a readable answer and not an unreadable one. A
            // .WithPolicy(PolicySet.Empty) carries no policy and wants no diagnostic.
            ["Empty"] = System.Array.Empty<string>(),
        };

    /// <summary>
    /// The policy kinds the referenced set declares, ordinally sorted, or an empty list
    /// when the set cannot be resolved.
    /// </summary>
    /// <param name="expression">The argument to <c>.WithPolicy(...)</c>.</param>
    /// <param name="semanticModel">Resolves the reference to its declaration.</param>
    public static IReadOnlyList<string> Read(ExpressionSyntax? expression, SemanticModel? semanticModel) =>
        Resolve(expression, semanticModel).Kinds;

    /// <summary>
    /// What the referenced set declares, and whether the compiler could read it at all.
    /// </summary>
    /// <param name="expression">The argument to <c>.WithPolicy(...)</c>.</param>
    /// <param name="semanticModel">Resolves the reference to its declaration.</param>
    public static PolicySetContents Resolve(ExpressionSyntax? expression, SemanticModel? semanticModel)
    {
        if (expression is null || semanticModel is null)
        {
            return PolicySetContents.Unreadable;
        }

        var symbol = semanticModel.GetSymbolInfo(expression).Symbol;

        var initialiser = symbol switch
        {
            IFieldSymbol field => Initialiser(field),
            IPropertySymbol property => Initialiser(property),
            _ => null,
        };

        if (initialiser is not null)
        {
            return PolicySetContents.ReadFrom(initialiser);
        }

        return WellKnownKinds(symbol) is { } kinds
            ? PolicySetContents.WellKnown(kinds)
            : PolicySetContents.Unreadable;
    }

    /// <summary>
    /// The kinds a static member of <c>FlowX.PolicySet</c> declares, or <see langword="null"/>
    /// when the symbol is not one.
    /// </summary>
    /// <remarks>
    /// The source path is tried first, so this only ever answers for a set arriving as
    /// metadata — and it answers for <c>PolicySet</c>'s members and nothing else. A user type
    /// named <c>PolicySet</c> in another namespace is a different type and gets nothing.
    /// </remarks>
    private static string[]? WellKnownKinds(ISymbol? symbol)
    {
        if (symbol is not (IFieldSymbol or IPropertySymbol) ||
            !symbol.IsStatic ||
            symbol.ContainingType?.MetadataName != PolicySetTypeName ||
            symbol.ContainingType.ContainingNamespace?.ToDisplayString() != AbstractionsNamespace)
        {
            return null;
        }

        return WellKnownSets.TryGetValue(symbol.Name, out var kinds) ? kinds : null;
    }

    /// <summary><c>FlowX.PolicySet</c>, as metadata names it.</summary>
    private const string PolicySetTypeName = "PolicySet";

    /// <summary>The namespace <see cref="PolicySetTypeName"/> lives in.</summary>
    private const string AbstractionsNamespace = "FlowX";

    /// <summary>The initialiser expression of a field or property declared in source.</summary>
    private static ExpressionSyntax? Initialiser(ISymbol symbol)
    {
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            switch (reference.GetSyntax())
            {
                case VariableDeclaratorSyntax variable when variable.Initializer is not null:
                    return variable.Initializer.Value;

                case PropertyDeclarationSyntax property when property.Initializer is not null:
                    return property.Initializer.Value;

                case PropertyDeclarationSyntax property when property.ExpressionBody is not null:
                    return property.ExpressionBody.Expression;
            }
        }

        return null;
    }
}
