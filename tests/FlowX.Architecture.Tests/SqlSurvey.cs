using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FlowX.Architecture.Tests;

/// <summary>
/// Where a shipping assembly hands text to a database, and what that text was built from.
/// </summary>
/// <remarks>
/// <para>
/// This is what replaced semgrep's <c>csharp.lang.security.sqli</c>, excluded by id in
/// <c>.github/workflows/security.yml</c>. That rule fires on the <em>assignment</em> rather
/// than on the expression, so <c>command.CommandText = Acquire;</c> — a named <c>const</c> —
/// reads to it exactly as an interpolation would, and all twenty of its findings here were of
/// that shape. It also never saw <c>new NpgsqlCommand(Read, connection)</c>, which is the same
/// sink written differently.
/// </para>
/// <para>
/// The question asked here is the one that rule meant to ask: <em>is the text fixed when the
/// assembly is built</em>. A string literal is. A <c>const</c> is, and the compiler says so —
/// including one assembled with <c>+</c> from other <c>const</c>s, which is why the initialiser
/// is not re-checked here. An embedded <c>.sql</c> resource is. A run-time value is not,
/// however it arrives: interpolation, concatenation, <c>string.Format</c>, or a parameter whose
/// value some caller chose.
/// </para>
/// <para>
/// Syntactic, like <see cref="SourceSurvey"/> and for its reasons: milliseconds, no metadata
/// references, and it cannot be knocked over by an unrelated build error.
/// </para>
/// </remarks>
internal static class SqlSurvey
{
    /// <summary>Command types whose first constructor argument is SQL text.</summary>
    /// <remarks>
    /// Enumerated rather than matched by suffix, so a command type nobody thought about fails
    /// <c>EverySqlSinkIsOneTheSurveyKnows</c> instead of being scanned wrongly or not at all.
    /// </remarks>
    public static readonly string[] CommandTypes = ["NpgsqlCommand", "NpgsqlBatchCommand"];

    /// <summary>
    /// Methods returning SQL that is fixed at build time by something other than a constant.
    /// </summary>
    /// <remarks>
    /// One entry, <c>PostgresMigrator.ReadScript</c>, which returns the content of an
    /// <c>EmbeddedResource</c> <c>.sql</c> file compiled into the package. Those bytes are
    /// settled by the build exactly as a literal's are, and nothing at run time chooses among
    /// them but the migration list, itself a static array in that class.
    /// <c>TheEmbeddedScriptAllowanceStillReadsAnEmbeddedScript</c> is what stops this name
    /// quietly coming to mean something else.
    /// </remarks>
    public static readonly string[] BuildTimeScriptReaders = ["ReadScript"];

    /// <summary>How far a parameter may be chased back towards its call sites.</summary>
    /// <remarks>
    /// Two hops covers every helper here and bounds the walk on a cycle. Running out of depth
    /// is reported as a failure rather than waved through: an unproven value is precisely what
    /// this gate exists to reject.
    /// </remarks>
    private const int MaxHops = 2;

    private static readonly Lazy<IReadOnlyList<CompilationUnitSyntax>> RootsLazy = new(Parse);
    private static readonly Lazy<IReadOnlyList<SqlSink>> SinksLazy = new(FindSinks);
    private static readonly Lazy<HashSet<string>> ConstantsLazy = new(FindStringConstants);

    /// <summary>Every place a shipping tree hands text to a database command.</summary>
    public static IReadOnlyList<SqlSink> Sinks => SinksLazy.Value;

    /// <summary><c>"JournalSql.StartInstance"</c>, for every <c>const string</c> in the survey.</summary>
    private static HashSet<string> Constants => ConstantsLazy.Value;

    /// <summary>Every node of a kind, across every parsed shipping file.</summary>
    public static IEnumerable<T> Nodes<T>()
        where T : SyntaxNode =>
        RootsLazy.Value.SelectMany(static root => root.DescendantNodes().OfType<T>());

    /// <summary>Repository-relative <c>path:line</c> of a node, for a message worth reading.</summary>
    public static string Where(SyntaxNode node) =>
        $"{SourceSurvey.RelativePath(new FileInfo(node.SyntaxTree.FilePath))}:" +
        $"{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}";

    /// <summary>The right-most identifier of a possibly qualified, possibly generic name.</summary>
    public static string Simple(SyntaxNode node) => node switch
    {
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        QualifiedNameSyntax qualified => Simple(qualified.Right),
        AliasQualifiedNameSyntax aliased => Simple(aliased.Name),
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        _ => node.ToString(),
    };

    /// <summary>
    /// Why this expression is not fixed at build time, or <see langword="null"/> when it is.
    /// </summary>
    public static string? WhyNotFixed(ExpressionSyntax expression, TypeDeclarationSyntax owner, int hops = 0) =>
        expression switch
        {
            ParenthesizedExpressionSyntax parenthesized => WhyNotFixed(parenthesized.Expression, owner, hops),
            LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => null,
            ConditionalExpressionSyntax choice =>
                WhyNotFixed(choice.WhenTrue, owner, hops) ?? WhyNotFixed(choice.WhenFalse, owner, hops),
            InvocationExpressionSyntax call when IsBuildTimeScriptReader(call) => null,
            MemberAccessExpressionSyntax member => WhyNotAConstant(
                $"{Simple(member.Expression)}.{member.Name.Identifier.ValueText}", member.ToString()),
            IdentifierNameSyntax name => WhyNotAConstantOrAProvenParameter(name, owner, hops),
            _ => $"`{Shorten(expression)}` is {Describe(expression)}, which is a run-time value",
        };

    private static IReadOnlyList<CompilationUnitSyntax> Parse() =>
    [
        .. SourceSurvey.SourceFiles(SourceSurvey.ShippingTrees)
            .Select(static file => (CompilationUnitSyntax)CSharpSyntaxTree
                .ParseText(
                    File.ReadAllText(file.FullName),
                    new CSharpParseOptions(LanguageVersion.Preview),
                    path: file.FullName)
                .GetRoot()),
    ];

    private static List<SqlSink> FindSinks()
    {
        var sinks = new List<SqlSink>();

        foreach (var assignment in Nodes<AssignmentExpressionSyntax>())
        {
            if (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                && Simple(assignment.Left) == "CommandText")
            {
                sinks.Add(Sink(assignment, assignment.Right));
            }
        }

        foreach (var creation in Nodes<ObjectCreationExpressionSyntax>())
        {
            var first = creation.ArgumentList?.Arguments.FirstOrDefault()?.Expression;

            if (first is not null && CommandTypes.Contains(Simple(creation.Type), StringComparer.Ordinal))
            {
                sinks.Add(Sink(creation, first));
            }
        }

        return sinks;
    }

    private static SqlSink Sink(SyntaxNode at, ExpressionSyntax text) =>
        new(Where(at), text, at.Ancestors().OfType<TypeDeclarationSyntax>().First());

    private static HashSet<string> FindStringConstants()
    {
        var constants = new HashSet<string>(StringComparer.Ordinal);

        foreach (var field in Nodes<FieldDeclarationSyntax>())
        {
            if (!field.Modifiers.Any(SyntaxKind.ConstKeyword)
                || field.Parent is not TypeDeclarationSyntax owner)
            {
                continue;
            }

            foreach (var declared in field.Declaration.Variables)
            {
                constants.Add($"{owner.Identifier.ValueText}.{declared.Identifier.ValueText}");
            }
        }

        return constants;
    }

    private static string? WhyNotAConstant(string qualified, string asWritten) =>
        Constants.Contains(qualified)
            ? null
            : $"`{asWritten}` is not a `const string` declared in this repository";

    private static string? WhyNotAConstantOrAProvenParameter(
        IdentifierNameSyntax name,
        TypeDeclarationSyntax owner,
        int hops)
    {
        var identifier = name.Identifier.ValueText;

        if (Constants.Contains($"{owner.Identifier.ValueText}.{identifier}"))
        {
            return null;
        }

        var method = name.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        var index = method?.ParameterList.Parameters
            .IndexOf(parameter => parameter.Identifier.ValueText == identifier) ?? -1;

        return method is null || index < 0
            ? $"`{identifier}` is neither a `const string` of {owner.Identifier.ValueText} nor a parameter"
            : WhyTheCallSitesDoNotProveIt(method, index, identifier, hops);
    }

    /// <summary>
    /// Why the call sites of a SQL-carrying parameter do not settle it at build time.
    /// </summary>
    /// <remarks>
    /// The method must be <c>private</c>, and that is what makes the enumeration below a proof:
    /// an <c>internal</c> or <c>public</c> method taking SQL has call sites this survey cannot
    /// see, and "found none that were wrong" is a different claim from "there are none".
    /// </remarks>
    private static string? WhyTheCallSitesDoNotProveIt(
        MethodDeclarationSyntax method,
        int index,
        string identifier,
        int hops)
    {
        var name = method.Identifier.ValueText;

        if (!method.Modifiers.Any(SyntaxKind.PrivateKeyword))
        {
            return $"`{identifier}` is a parameter of {name}, which is not private — callers "
                + "this survey cannot see may pass anything. Make it private, or pass a constant.";
        }

        if (hops >= MaxHops)
        {
            return $"`{identifier}` is a parameter of {name} and the walk back to a constant ran "
                + $"past {MaxHops} hops. Hand the SQL in directly.";
        }

        var owner = method.Ancestors().OfType<TypeDeclarationSyntax>().First();
        var arguments = ArgumentsAt(owner.Identifier.ValueText, method, index).ToList();

        return arguments.Count == 0
            ? $"`{identifier}` is a parameter of {name}, which nothing in the survey calls, so no "
                + "constant was proven to reach it"
            : arguments
                .Select(argument => WhyNotFixed(argument, owner, hops + 1))
                .FirstOrDefault(static why => why is not null);
    }

    /// <summary>
    /// The argument every call site of <paramref name="method"/> puts in one parameter's place.
    /// </summary>
    /// <remarks>
    /// Call sites are matched on the declaring type's <em>name</em> rather than on its syntax
    /// node, so a <c>partial</c> class cannot hide half of its own. A named argument resolves by
    /// name; a positional one by index, after checking the call is long enough to have reached
    /// it.
    /// </remarks>
    private static IEnumerable<ExpressionSyntax> ArgumentsAt(
        string typeName,
        MethodDeclarationSyntax method,
        int index)
    {
        var methodName = method.Identifier.ValueText;
        var parameterName = method.ParameterList.Parameters[index].Identifier.ValueText;

        foreach (var call in Nodes<InvocationExpressionSyntax>())
        {
            if (Simple(call.Expression) != methodName || DeclaringTypeName(call) != typeName)
            {
                continue;
            }

            var named = call.ArgumentList.Arguments
                .FirstOrDefault(argument => argument.NameColon?.Name.Identifier.ValueText == parameterName);

            if (named is not null)
            {
                yield return named.Expression;
            }
            else if (call.ArgumentList.Arguments.Count > index)
            {
                yield return call.ArgumentList.Arguments[index].Expression;
            }
        }
    }

    private static string? DeclaringTypeName(SyntaxNode node) =>
        node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText;

    private static bool IsBuildTimeScriptReader(InvocationExpressionSyntax call) =>
        BuildTimeScriptReaders.Contains(Simple(call.Expression), StringComparer.Ordinal);

    private static string Describe(ExpressionSyntax expression) => expression switch
    {
        InterpolatedStringExpressionSyntax => "an interpolated string",
        BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression) => "a concatenation",
        InvocationExpressionSyntax call => $"a call to {Simple(call.Expression)}",
        _ => $"a {expression.Kind()}",
    };

    private static string Shorten(SyntaxNode node)
    {
        var text = string.Join(' ', node.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return text.Length <= 60 ? text : string.Concat(text.AsSpan(0, 57), "...");
    }
}

/// <summary>One place a shipping tree hands text to a database command.</summary>
/// <param name="Where">Repository-relative <c>path:line</c>, so a failure can be acted on.</param>
/// <param name="Text">The expression that becomes the statement.</param>
/// <param name="Owner">The type the sink sits in, which scopes constant and call-site lookup.</param>
internal sealed record SqlSink(string Where, ExpressionSyntax Text, TypeDeclarationSyntax Owner);
