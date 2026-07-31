using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using FlowX.Compiler.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FlowX.Compiler.Analysis;

/// <summary>
/// Checks that a flow's deadline can contain the step timeouts declared under it:
/// FLOWX1019.
/// </summary>
/// <remarks>
/// <para>
/// <c>[FlowDeadline]</c> is absolute and is never reset by a retry
/// (<c>10-Policy-Framework.md §5</c>). A step's <c>Timeout</c> policy bounds one attempt
/// and its <c>Retry</c> policy multiplies the attempts, so a flow's worst case is at
/// least the sum of those products. When that already exceeds the deadline, the later
/// steps of the flow cannot run at all on a bad day, and what the operator sees is a
/// cancelled flow several steps away from the policy that spent the budget —
/// <c>14-Performance.md §7</c>'s "arithmetically incoherent" case, and the one
/// <c>FlowDeadlineAttribute</c> promises this id for.
/// </para>
/// <para>
/// <strong>The number it reports is a floor, and everything it cannot read makes the
/// real figure larger, never smaller.</strong> That is the whole design: a rule about
/// arithmetic that fires on a flow which actually fits would be suppressed, and a
/// suppressed rule protects nothing. So it counts only what it can read and still
/// reports, which keeps every miss a false negative:
/// </para>
/// <list type="bullet">
/// <item>
/// Only the <strong>top-level</strong> chain is summed. Steps inside a <c>When</c>,
/// <c>Switch</c>, <c>ForEach</c>, <c>Parallel</c> or <c>SubFlow</c> block are ignored
/// entirely — they add time on some paths and, in a <c>Parallel</c>, overlap rather
/// than add, and no useful bound comes out of guessing which. Top-level steps are
/// unconditional, so summing them is sound.
/// </item>
/// <item>
/// Only a step carrying <c>.WithPolicy(set)</c> where the set resolves to a field or
/// property initialiser in source contributes. A set built at run time, or one reaching
/// this compilation as metadata, contributes zero rather than a guess.
/// </item>
/// <item>
/// Only a <c>Timeout(TimeSpan.From…(constant))</c> is read. There is no platform default
/// step timeout to fall back on in this release — <c>CapabilityAttribute</c> carries no
/// <c>Timeout</c> member — so a step without the policy is genuinely unbounded and is
/// counted as zero rather than as infinity.
/// </item>
/// <item>
/// <c>Retry(attempts)</c> is read as the <em>total</em> number of attempts, which is the
/// smaller of the two readings the parameter name allows. If it turns out to mean
/// retries-after-the-first, every figure here is short by one timeout and still a floor.
/// </item>
/// <item>
/// Backoff delays between attempts are not counted, and they are real elapsed time
/// against the same budget.
/// </item>
/// <item>
/// Where one step carries two <c>.WithPolicy</c> calls, only the first is counted:
/// which set wins is a resolution question this rule has no answer to, and counting both
/// would be the one way to overstate the total.
/// </item>
/// </list>
/// <para>
/// What it therefore <strong>cannot</strong> prove is the converse — silence is not a
/// statement that a flow fits. A flow of twenty un-timed steps behind a one-second
/// deadline says nothing here.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DeadlineCoherenceAnalyzer : DiagnosticAnalyzer
{
    private const string FlowAttributeMetadataName = "FlowAttribute";
    private const string FlowDeadlineAttributeMetadataName = "FlowDeadlineAttribute";
    private const string FlowXNamespace = "FlowX";
    private const string DefineMethodName = "Define";
    private const string TimeSpanTypeName = "System.TimeSpan";

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(FlowXDiagnostics.DeadlineCannotFitSteps);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            return;
        }

        // The generated partial carries no Define and no attributes of its own.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ClassDeclaration);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        if (context.Node is not ClassDeclarationSyntax declaration ||
            context.SemanticModel.GetDeclaredSymbol(declaration) is not INamedTypeSymbol flowType ||
            !CarriesAttribute(flowType, FlowAttributeMetadataName))
        {
            return;
        }

        // Everything below costs something, and nearly every flow exits here: the rule has
        // no subject at all without a declared deadline to compare against.
        var deadline = DeclaredDeadline(flowType);

        if (deadline is null)
        {
            return;
        }

        var define = declaration.Members
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(static m => m.Identifier.ValueText == DefineMethodName);

        if (define is null)
        {
            return;
        }

        var links = FlowChainWalker.Walk(
            (SyntaxNode?)define.ExpressionBody ?? define.Body,
            define.ParameterList.Parameters.Count > 0
                ? define.ParameterList.Parameters[0].Identifier.ValueText
                : null);

        var (floor, breakdown) = Floor(links, context.SemanticModel);

        if (breakdown.Count == 0 || floor <= deadline.Value.Budget)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            FlowXDiagnostics.DeadlineCannotFitSteps,
            deadline.Value.Location,
            flowType.Name,
            Format(deadline.Value.Budget),
            Format(floor),
            string.Join(" + ", breakdown)));
    }

    /// <summary>
    /// Sums the worst case of every top-level step whose timeout the compiler can read.
    /// </summary>
    private static (TimeSpan Floor, IReadOnlyList<string> Breakdown) Floor(
        IReadOnlyList<ChainLink> links,
        SemanticModel semanticModel)
    {
        var floor = TimeSpan.Zero;
        var breakdown = new List<string>();
        string? step = null;

        foreach (var link in links)
        {
            switch (link.MethodName)
            {
                case "Step":
                    step = link.TypeArguments.Count > 0 ? link.TypeArguments[0].ToString() : "step";
                    break;

                case "WithPolicy" when step is not null:
                    var budget = Budget(link, semanticModel);

                    // Consumed either way: a second WithPolicy on the same step is not
                    // added, and an unreadable set does not leave the step open for one.
                    var named = step;
                    step = null;

                    if (budget is null)
                    {
                        break;
                    }

                    var worst = TimeSpan.FromTicks(budget.Value.Timeout.Ticks * budget.Value.Attempts);
                    floor += worst;
                    breakdown.Add(FormattableString.Invariant(
                        $"{named} {budget.Value.Attempts}×{Format(budget.Value.Timeout)}"));
                    break;

                default:
                    // Blocks, compensations, emissions and anything the DSL grows later.
                    // None of them can make the total smaller, so the walk continues
                    // rather than abandoning the flow the way FLOWX1020 must.
                    break;
            }
        }

        return (floor, breakdown);
    }

    /// <summary>
    /// The timeout and attempt count the policy set named by a <c>.WithPolicy(...)</c>
    /// declares, or <c>null</c> when either cannot be read.
    /// </summary>
    /// <remarks>
    /// A set with no <c>Timeout</c> yields nothing rather than zero, because a step with a
    /// retry and no timeout is unbounded, not free, and this rule refuses to put a number
    /// on unbounded.
    /// </remarks>
    private static (TimeSpan Timeout, int Attempts)? Budget(ChainLink link, SemanticModel semanticModel)
    {
        var arguments = link.Invocation.ArgumentList.Arguments;

        if (arguments.Count != 1)
        {
            return null;
        }

        var initialiser = Initialiser(semanticModel.GetSymbolInfo(arguments[0].Expression).Symbol);

        if (initialiser is null)
        {
            return null;
        }

        TimeSpan? timeout = null;
        var attempts = 1;

        foreach (var policy in FlowChainWalker.Walk(initialiser))
        {
            switch (policy.MethodName)
            {
                case "Timeout":
                    timeout = LiteralDuration(Argument(policy, "duration", 0));
                    break;

                case "Retry":
                    attempts = Math.Max(1, LiteralCount(Argument(policy, "attempts", 0)) ?? 1);
                    break;

                default:
                    break;
            }
        }

        return timeout is null ? null : (timeout.Value, attempts);
    }

    /// <summary>The named argument if it was written that way, else the one in that position.</summary>
    private static ExpressionSyntax? Argument(ChainLink link, string name, int position)
    {
        var arguments = link.Invocation.ArgumentList.Arguments;

        foreach (var argument in arguments)
        {
            if (argument.NameColon?.Name.Identifier.ValueText == name)
            {
                return argument.Expression;
            }
        }

        return position < arguments.Count && arguments[position].NameColon is null
            ? arguments[position].Expression
            : null;
    }

    /// <summary>
    /// A literal <c>TimeSpan.From…(2)</c> as a value, or <c>null</c> for anything else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Read from syntax, not from a symbol.</strong> The policy set is nearly
    /// always declared in a different file from the flow, a <c>SemanticModel</c> belongs
    /// to one tree, and RS1030 forbids an analyzer from asking the compilation for another
    /// — for a good reason: binding a second tree per flow is how an analyzer becomes the
    /// slowest thing in the build. Nothing here needs a symbol. The argument's type is
    /// already fixed by <c>PolicySet.Timeout(TimeSpan)</c>, so any expression standing
    /// there is a duration; all this has to do is recognise the ones whose value is
    /// written down.
    /// </para>
    /// <para>
    /// The factory methods with a literal argument, and nothing else:
    /// <c>new TimeSpan(0, 0, 2)</c>, a <c>const</c> holding one, <c>TimeSpan.Parse</c> and
    /// an arithmetic expression are all read as unknown. Each missing spelling costs a
    /// false negative, never a wrong number.
    /// </para>
    /// </remarks>
    private static TimeSpan? LiteralDuration(ExpressionSyntax? expression)
    {
        if (expression is not InvocationExpressionSyntax invocation ||
            invocation.Expression is not MemberAccessExpressionSyntax access ||
            !IsTimeSpan(access.Expression) ||
            invocation.ArgumentList.Arguments.Count != 1 ||
            invocation.ArgumentList.Arguments[0].Expression is not LiteralExpressionSyntax literal)
        {
            return null;
        }

        var amount = literal.Token.Value switch
        {
            int value => value,
            long value => value,
            double value => value,
            float value => value,
            _ => (double?)null,
        };

        // A zero, negative or absurd figure is not arithmetic this rule should reason
        // from, and the From… factories throw on the last of those.
        if (amount is not > 0 || double.IsInfinity(amount.Value) || amount.Value > 365_000_000d)
        {
            return null;
        }

        return access.Name.Identifier.ValueText switch
        {
            "FromDays" => TimeSpan.FromDays(amount.Value),
            "FromHours" => TimeSpan.FromHours(amount.Value),
            "FromMinutes" => TimeSpan.FromMinutes(amount.Value),
            "FromSeconds" => TimeSpan.FromSeconds(amount.Value),
            "FromMilliseconds" => TimeSpan.FromMilliseconds(amount.Value),
            _ => null,
        };
    }

    /// <summary>Whether this receiver is written as the <c>TimeSpan</c> type.</summary>
    private static bool IsTimeSpan(ExpressionSyntax receiver)
    {
        var written = receiver.ToString();

        return written is "TimeSpan" or TimeSpanTypeName or "global::" + TimeSpanTypeName;
    }

    /// <summary>A literal <c>int</c> argument, or <c>null</c>.</summary>
    private static int? LiteralCount(ExpressionSyntax? expression) =>
        expression is LiteralExpressionSyntax literal && literal.Token.Value is int count ? count : null;

    /// <summary>
    /// The initialiser of the field or property the expression names.
    /// </summary>
    /// <remarks>
    /// The same restriction <see cref="PolicySetReader"/> works under, and for the same
    /// reason: a set assembled at run time has no compile-time contents, and returning
    /// nothing is the only honest answer.
    /// </remarks>
    private static ExpressionSyntax? Initialiser(ISymbol? symbol)
    {
        if (symbol is not IFieldSymbol and not IPropertySymbol)
        {
            return null;
        }

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

                default:
                    break;
            }
        }

        return null;
    }

    /// <summary>
    /// The flow's declared deadline and somewhere to report it, or <c>null</c>.
    /// </summary>
    /// <remarks>
    /// The diagnostic lands on the <c>[FlowDeadline]</c> attribute rather than on a step,
    /// because the deadline is the one figure a reader has to see next to the sum, and
    /// because there is usually more than one step to blame — the message names them.
    /// </remarks>
    private static (TimeSpan Budget, Location Location)? DeclaredDeadline(INamedTypeSymbol flowType)
    {
        foreach (var attribute in flowType.GetAttributes())
        {
            if (attribute.AttributeClass is not { ContainingType: null } attributeClass ||
                attributeClass.MetadataName != FlowDeadlineAttributeMetadataName ||
                !IsFlowXNamespace(attributeClass.ContainingNamespace) ||
                attribute.ConstructorArguments.Length != 1 ||
                attribute.ConstructorArguments[0].Value is not string duration)
            {
                continue;
            }

            var budget = Iso8601Duration(duration);

            if (budget is null)
            {
                // An unparseable duration is FlowDescriptor's complaint at start-up, not
                // this rule's; there is nothing to compare against and nothing to say.
                return null;
            }

            var syntax = attribute.ApplicationSyntaxReference?.GetSyntax();

            return (budget.Value, syntax?.GetLocation() ?? flowType.Locations.FirstOrDefault() ?? Location.None);
        }

        return null;
    }

    /// <summary>
    /// Parses the ISO-8601 duration <c>[FlowDeadline]</c> carries.
    /// </summary>
    /// <remarks>
    /// <c>System.Xml.XmlConvert.ToTimeSpan</c>, which is the function the emitted plan
    /// calls on the same string at start-up. Reimplementing the grammar here would be a
    /// second parser to keep in agreement with the first, and the two disagreeing is a
    /// diagnostic that reports arithmetic the runtime does not perform.
    /// </remarks>
    private static TimeSpan? Iso8601Duration(string duration)
    {
        try
        {
            return System.Xml.XmlConvert.ToTimeSpan(duration);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    /// <summary>How a duration is written in the message.</summary>
    /// <remarks>
    /// Seconds when it is a round number of them, because the units a developer wrote are
    /// what they will look for; <c>TimeSpan</c>'s own rendering, <c>00:00:02</c>, makes a
    /// reader count colons to compare two figures.
    /// </remarks>
    private static string Format(TimeSpan value) =>
        value.TotalSeconds >= 1 && value.Ticks % TimeSpan.TicksPerSecond == 0
            ? FormattableString.Invariant($"{value.TotalSeconds:0.##}s")
            : FormattableString.Invariant($"{value.TotalMilliseconds.ToString("0.##", CultureInfo.InvariantCulture)}ms");

    private static bool CarriesAttribute(INamedTypeSymbol type, string metadataName)
    {
        foreach (var attribute in type.GetAttributes())
        {
            if (attribute.AttributeClass is { ContainingType: null } attributeClass &&
                attributeClass.MetadataName == metadataName &&
                IsFlowXNamespace(attributeClass.ContainingNamespace))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether this is the top-level <c>FlowX</c> namespace.</summary>
    private static bool IsFlowXNamespace(INamespaceSymbol? candidate) =>
        candidate is { Name: FlowXNamespace } && candidate.ContainingNamespace is { IsGlobalNamespace: true };
}
