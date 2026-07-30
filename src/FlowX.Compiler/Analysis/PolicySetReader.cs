using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FlowX.Compiler.Analysis;

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
/// this returns nothing rather than guessing.
/// </para>
/// </remarks>
public static class PolicySetReader
{
    /// <summary>
    /// The policy kinds the referenced set declares, ordinally sorted, or an empty list
    /// when the set cannot be resolved.
    /// </summary>
    /// <param name="expression">The argument to <c>.WithPolicy(...)</c>.</param>
    /// <param name="semanticModel">Resolves the reference to its declaration.</param>
    public static IReadOnlyList<string> Read(ExpressionSyntax? expression, SemanticModel? semanticModel)
    {
        if (expression is null || semanticModel is null)
        {
            return new List<string>();
        }

        var symbol = semanticModel.GetSymbolInfo(expression).Symbol;

        var initialiser = symbol switch
        {
            IFieldSymbol field => Initialiser(field),
            IPropertySymbol property => Initialiser(property),
            _ => null,
        };

        if (initialiser is null)
        {
            return new List<string>();
        }

        // Every link in the chain except the PolicySet.Named(...) that starts it is a
        // policy. Named is the constructor, not a policy, and Empty is not a call at all.
        var kinds = FlowChainWalker.Walk(initialiser)
            .Select(link => link.MethodName)
            .Where(name => name != "Named" && name != "Empty")
            .Distinct()
            .ToList();

        kinds.Sort(System.StringComparer.Ordinal);
        return kinds;
    }

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
