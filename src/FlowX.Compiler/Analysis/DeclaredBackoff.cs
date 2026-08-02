using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FlowX.Compiler.Analysis;

/// <summary>
/// Reads the <em>first</em> gap out of a declared <c>Backoff</c> expression, or nothing.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The first gap and no more of the schedule, because that is the only part a
/// compile-time rule can act on.</strong> <c>FLOWX1043</c> asks one question — does the poll
/// wake for a second attempt before its own budget runs out — and the answer needs the base
/// delay alone. Folding the whole curve would mean deciding what the ceiling and the jitter
/// draw are worth in a rule that has no run time to observe them in.
/// </para>
/// <para>
/// <strong>It refuses far more than it accepts</strong>, for <see cref="DeclaredDuration"/>'s
/// reason and by its shape: the three factories the DSL publishes, with an argument this
/// compiler can evaluate. A variable, a helper, a schedule read from configuration — all
/// answer <c>null</c>, and the rule then says nothing rather than guessing at a flow that is
/// correct at run time.
/// </para>
/// <para>
/// Syntactic, with no semantic model, so it is a pure function of a string and testable as
/// one — again <see cref="DeclaredDuration"/>'s bargain, and it delegates to that class for
/// the <see cref="TimeSpan"/> forms rather than parsing them a second time.
/// </para>
/// </remarks>
public static class DeclaredBackoff
{
    /// <summary>Folds the gap before a poll's second attempt, or answers <c>null</c>.</summary>
    /// <param name="expression">
    /// The <c>interval:</c> argument's source text, exactly as the author wrote it.
    /// </param>
    /// <returns>
    /// An ISO-8601 duration matching the manifest schema's <c>duration</c> pattern, or
    /// <c>null</c> when this expression is not one this compiler can evaluate.
    /// </returns>
    public static string? FoldFirstGap(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return null;
        }

        var syntax = SyntaxFactory.ParseExpression(expression!);

        if (syntax.ContainsDiagnostics ||
            syntax is not InvocationExpressionSyntax invocation ||
            invocation.Expression is not MemberAccessExpressionSyntax factory ||
            !IsBackoff(factory.Expression) ||
            !IsSchedule(factory.Name.Identifier.ValueText) ||
            invocation.ArgumentList.Arguments.Count == 0)
        {
            return null;
        }

        var first = invocation.ArgumentList.Arguments[0].Expression;

        // Two forms, because the type publishes two: `Exponential("PT5S", "PT5M")`, which a
        // flow declaration reaches for, and `Exponential(TimeSpan.FromSeconds(5), …)`, which a
        // policy set does. Neither is folded by the other's reader, so both are read here.
        return first is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)
            ? Iso8601(literal.Token.ValueText)
            : DeclaredDuration.Fold(first.ToString());
    }

    /// <summary>Whether the receiver names <c>Backoff</c>, qualified or not.</summary>
    /// <remarks>
    /// By name rather than by symbol, for <see cref="DeclaredDuration"/>'s reason: the class
    /// stays a pure function of a string. The parameter is typed <c>Backoff</c> at the call
    /// site, so a type of that name which is not this one would not have compiled.
    /// </remarks>
    private static bool IsBackoff(ExpressionSyntax receiver) => receiver switch
    {
        IdentifierNameSyntax name => name.Identifier.ValueText == "Backoff",
        MemberAccessExpressionSyntax qualified => qualified.Name.Identifier.ValueText == "Backoff",
        _ => false,
    };

    /// <summary>The factories whose first argument is the gap before the second attempt.</summary>
    private static bool IsSchedule(string factory) =>
        factory is "Exponential" or "ExponentialJitter";

    /// <summary>
    /// Round-trips an author's ISO-8601 string through the renderer the manifest uses.
    /// </summary>
    /// <remarks>
    /// Not passed through unchanged, even though it is already ISO-8601: <c>PT300S</c> and
    /// <c>PT5M</c> are the same duration and only one of them is what this repository writes
    /// down. Re-rendering is what keeps a diagnostic's message and a manifest's field spelling
    /// one duration one way.
    /// </remarks>
    private static string? Iso8601(string value)
    {
        try
        {
            var parsed = System.Xml.XmlConvert.ToTimeSpan(value);

            return parsed < TimeSpan.Zero
                ? null
                : DeclaredDuration.Fold(
                    "TimeSpan.FromMilliseconds(" +
                    parsed.TotalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    ")");
        }
        catch (FormatException)
        {
            // Not a duration. The DSL's own factory throws on this at run time with a message
            // naming the value; a diagnostic here would be a second, worse copy of it.
            return null;
        }
    }
}
