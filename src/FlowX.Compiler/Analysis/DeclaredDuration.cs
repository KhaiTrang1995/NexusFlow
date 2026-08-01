using System;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FlowX.Compiler.Analysis;

/// <summary>
/// Turns a declared <see cref="TimeSpan"/> expression into the ISO-8601 duration the
/// manifest publishes, or into nothing.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This exists because the plan and the manifest need different things from the
/// same expression</strong>
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0021-manifest-publishes-the-wait.md">ADR-0021 §2.2</a>).
/// The plan is C#: <c>Waits.Countersignature</c> copied into generated source <em>is</em> the
/// duration, evaluated by the compilation that declared it, which is why
/// <c>StepModel.SignalTimeout</c> is verbatim and this class is not used for it. The manifest
/// is JSON, read by tools that have never seen the assembly — <c>"timeout":
/// "Waits.Countersignature"</c> publishes a symbol, and a <c>flowx diff</c> rule over a symbol
/// fires when somebody renames a constant and stays silent when somebody changes its value.
/// </para>
/// <para>
/// <strong>It refuses far more than it accepts, and that is the design.</strong> Five factory
/// methods with a constant argument, and <c>TimeSpan.Zero</c>. Anything else — a method call,
/// a conditional, a parse, a variable — returns <c>null</c>, and the field is then omitted
/// rather than guessed at. That is <c>merge</c>'s precedent: an absent field is a consumer
/// asking, a guessed one is a consumer misled. A folder with a plausible fallback would put
/// the fabricated <c>TimeSpan.FromHours(1)</c> that <c>FLOWX1031</c> was raised over back into
/// the artifact, one layer further out.
/// </para>
/// <para>
/// Syntactic, with no semantic model, so it is a pure function of a string and testable as
/// one. Following a member reference to its declaration needs a symbol and happens one level
/// up, in <c>FlowAnalyzer</c>, which hands the initialiser's text back here.
/// </para>
/// </remarks>
public static class DeclaredDuration
{
    /// <summary>Every factory this understands, and what one of its units is worth in ticks.</summary>
    /// <remarks>
    /// <c>FromTicks</c> is deliberately absent. It is the one factory whose argument is not a
    /// human unit, and a wait expressed in ticks is not a wait anybody declared on purpose.
    /// </remarks>
    private static readonly System.Collections.Generic.Dictionary<string, double> Units =
        new System.Collections.Generic.Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["FromDays"] = TimeSpan.TicksPerDay,
            ["FromHours"] = TimeSpan.TicksPerHour,
            ["FromMinutes"] = TimeSpan.TicksPerMinute,
            ["FromSeconds"] = TimeSpan.TicksPerSecond,
            ["FromMilliseconds"] = TimeSpan.TicksPerMillisecond,
        };

    /// <summary>Folds one declared duration, or answers <c>null</c>.</summary>
    /// <param name="expression">
    /// The expression's source text, exactly as the author wrote it at the call site.
    /// </param>
    /// <returns>
    /// An ISO-8601 duration matching the manifest schema's <c>duration</c> pattern, or
    /// <c>null</c> when this expression is not one this compiler can evaluate.
    /// </returns>
    public static string? Fold(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return null;
        }

        var syntax = SyntaxFactory.ParseExpression(expression!);

        // A half-typed buffer, or an expression this parser could not make sense of. Either
        // way there is nothing to evaluate, and diagnostics for it belong to C#, not here.
        if (syntax.ContainsDiagnostics)
        {
            return null;
        }

        var ticks = Evaluate(syntax);

        // Negative is refused rather than rendered: the schema's ISO-8601 grammar has no
        // sign, so there is nothing correct to write, and omitting the field is what tells a
        // reader nothing was published for this wait.
        return ticks is { } value && value >= 0 ? Render(TimeSpan.FromTicks((long)value)) : null;
    }

    private static double? Evaluate(ExpressionSyntax syntax)
    {
        if (syntax is MemberAccessExpressionSyntax zero &&
            zero.Name.Identifier.ValueText == "Zero" &&
            IsTimeSpan(zero.Expression))
        {
            return 0;
        }

        if (syntax is not InvocationExpressionSyntax invocation ||
            invocation.Expression is not MemberAccessExpressionSyntax factory ||
            !IsTimeSpan(factory.Expression) ||
            !Units.TryGetValue(factory.Name.Identifier.ValueText, out var unit) ||
            invocation.ArgumentList.Arguments.Count != 1)
        {
            return null;
        }

        return Number(invocation.ArgumentList.Arguments[0].Expression) is { } amount
            ? amount * unit
            : (double?)null;
    }

    /// <summary>
    /// Whether the receiver names <c>TimeSpan</c>, qualified or not.
    /// </summary>
    /// <remarks>
    /// By name rather than by symbol, because this class has no semantic model. The cost is
    /// that somebody's own <c>TimeSpan.FromDays</c> would be folded as though it were the
    /// framework's; the benefit is that the folder stays a pure function of a string. A type
    /// named <c>TimeSpan</c> that is not <c>System.TimeSpan</c> and whose <c>FromDays</c>
    /// means something other than days is a shape the DSL would not accept anyway — the
    /// parameter is typed <see cref="TimeSpan"/>.
    /// </remarks>
    private static bool IsTimeSpan(ExpressionSyntax receiver) => receiver switch
    {
        IdentifierNameSyntax name => name.Identifier.ValueText == "TimeSpan",
        MemberAccessExpressionSyntax qualified => qualified.Name.Identifier.ValueText == "TimeSpan",
        _ => false,
    };

    /// <summary>A numeric literal, or the negation of one. Nothing else is a constant here.</summary>
    private static double? Number(ExpressionSyntax syntax)
    {
        if (syntax is PrefixUnaryExpressionSyntax unary &&
            unary.IsKind(SyntaxKind.UnaryMinusExpression))
        {
            return Number(unary.Operand) is { } negated ? -negated : (double?)null;
        }

        return syntax is LiteralExpressionSyntax literal &&
            literal.IsKind(SyntaxKind.NumericLiteralExpression) &&
            double.TryParse(
                literal.Token.ValueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : (double?)null;
    }

    /// <summary>
    /// Renders a non-negative <see cref="TimeSpan"/> in the shape the schema's
    /// <c>duration</c> pattern accepts.
    /// </summary>
    /// <remarks>
    /// Hand-written rather than <c>XmlConvert.ToString</c>, for two reasons. This assembly is
    /// a Roslyn analyzer and every package it drags along is a package it has to ship. And
    /// the manifest promises byte-identical output for identical source, so the exact
    /// rendering is this writer's decision and not a framework method's — <c>P7D</c> and
    /// <c>P7DT0H0M0S</c> are both correct ISO-8601 and only one of them is stable to reason
    /// about in a diff.
    /// </remarks>
    private static string Render(TimeSpan span)
    {
        if (span == TimeSpan.Zero)
        {
            return "PT0S";
        }

        var builder = new StringBuilder("P");

        if (span.Days > 0)
        {
            builder.Append(span.Days.ToString(CultureInfo.InvariantCulture)).Append('D');
        }

        var subSecond = (span.Ticks % TimeSpan.TicksPerSecond) / (double)TimeSpan.TicksPerSecond;

        if (span.Hours == 0 && span.Minutes == 0 && span.Seconds == 0 && subSecond == 0)
        {
            return builder.ToString();
        }

        builder.Append('T');

        if (span.Hours > 0)
        {
            builder.Append(span.Hours.ToString(CultureInfo.InvariantCulture)).Append('H');
        }

        if (span.Minutes > 0)
        {
            builder.Append(span.Minutes.ToString(CultureInfo.InvariantCulture)).Append('M');
        }

        if (span.Seconds > 0 || subSecond > 0)
        {
            builder
                .Append((span.Seconds + subSecond).ToString("0.#######", CultureInfo.InvariantCulture))
                .Append('S');
        }

        return builder.ToString();
    }
}
