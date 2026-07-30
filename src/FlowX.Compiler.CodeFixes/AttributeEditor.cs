using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Simplification;

namespace FlowX.Compiler.CodeFixes;

/// <summary>
/// Sets a named argument on an attribute, preserving everything else about it.
/// </summary>
/// <remarks>
/// <para>
/// Two of the three fixes are the same edit against different attributes — FLOWX1010
/// adds <c>Authorization</c> to <c>[Capability]</c>, FLOWX1017 sets <c>Profile</c> on
/// <c>[Flow]</c>. Sharing the edit is not only economy: the trivia handling is the part
/// that is easy to get subtly wrong, and a fix that reformats the line it touches is a
/// fix reviewers learn to undo.
/// </para>
/// <para>
/// Trivia is written out rather than left to the formatter. The formatter only visits
/// nodes carrying <c>Formatter.Annotation</c>, and annotating the attribute would let it
/// reflow argument lists the author had wrapped by hand.
/// </para>
/// </remarks>
internal static class AttributeEditor
{
    /// <summary>
    /// Returns <paramref name="attribute"/> with <paramref name="name"/> set to
    /// <paramref name="value"/>, replacing the argument if it is already present.
    /// </summary>
    internal static AttributeSyntax WithNamedArgument(AttributeSyntax attribute, string name, ExpressionSyntax value)
    {
        var arguments = attribute.ArgumentList;

        if (arguments is null)
        {
            return attribute.WithArgumentList(
                SyntaxFactory.AttributeArgumentList(SyntaxFactory.SingletonSeparatedList(NamedArgument(name, value))));
        }

        for (var i = 0; i < arguments.Arguments.Count; i++)
        {
            var existing = arguments.Arguments[i];

            if (existing.NameEquals?.Name.Identifier.ValueText != name)
            {
                continue;
            }

            // Replacing the expression rather than the whole argument keeps the author's
            // spacing around '=' intact, including the multi-line case where the argument
            // list was wrapped deliberately.
            return attribute.WithArgumentList(
                arguments.WithArguments(arguments.Arguments.Replace(existing, existing.WithExpression(value))));
        }

        return attribute.WithArgumentList(
            arguments.WithArguments(
                arguments.Arguments.Add(NamedArgument(name, value).WithLeadingTrivia(SyntaxFactory.Space))));
    }

    /// <summary>
    /// A member access written fully qualified and marked for simplification.
    /// </summary>
    /// <remarks>
    /// Emitting <c>global::FlowX.Authorization.Authenticated</c> and letting the reducer
    /// shorten it is the only spelling that is correct in every file. A file with
    /// <c>using FlowX;</c> gets <c>Authorization.Authenticated</c>; a file without one
    /// keeps the qualified form and still compiles. Emitting the short form directly
    /// would produce code that does not build in the second case — and the second case
    /// is exactly the file where someone forgot something.
    /// </remarks>
    internal static ExpressionSyntax QualifiedMemberAccess(string fullyQualifiedName) =>
        SyntaxFactory.ParseExpression("global::" + fullyQualifiedName)
            .WithAdditionalAnnotations(Simplifier.Annotation);

    private static AttributeArgumentSyntax NamedArgument(string name, ExpressionSyntax value) =>
        SyntaxFactory.AttributeArgument(
            SyntaxFactory.NameEquals(
                SyntaxFactory.IdentifierName(name),
                SyntaxFactory.Token(SyntaxKind.EqualsToken)
                    .WithLeadingTrivia(SyntaxFactory.Space)
                    .WithTrailingTrivia(SyntaxFactory.Space)),
            nameColon: null,
            expression: value);
}
