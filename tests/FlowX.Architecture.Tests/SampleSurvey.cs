using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FlowX.Architecture.Tests;

/// <summary>
/// Reads a sample application as its author wrote it: which types each file declares, what a
/// flow's <c>Define</c> body says, and how many lines of code a declaration costs.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="FlowTriggerSurvey"/> on purpose. That one answers questions about
/// <em>declarations</em> — the flow id, the profile, the routes — over every shipping tree, and
/// the two gates that read this one ask about <em>bodies and files</em> in one named sample:
/// how many files a use case is spread over, and whether four flows write the same chain.
/// Widening the first survey to carry a fluent chain would make every gate that uses it pay for
/// a parse it does not need.
/// </para>
/// <para>
/// Syntax, not text. A chain compared as a list of parsed calls is a chain compared as the
/// compiler reads it: reformatting it, wrapping a line differently or renaming the builder
/// variable changes nothing, and inserting a step changes exactly one element. A regular
/// expression over the same source would report both as a difference and neither as a step.
/// </para>
/// </remarks>
internal static class SampleSurvey
{
    private static readonly ConcurrentDictionary<string, IReadOnlyList<TypeSite>> Surveyed =
        new(StringComparer.Ordinal);

    /// <summary>Every type declared under a tree, e.g. <c>samples/ecommerce</c>.</summary>
    public static IReadOnlyList<TypeSite> TypesIn(string tree) =>
        Surveyed.GetOrAdd(tree, static t => SourceSurvey.SourceFiles(t).SelectMany(Read).ToList());

    /// <summary>
    /// The files holding the tree's top-level statements — its composition roots.
    /// </summary>
    /// <remarks>
    /// Found by asking the parser rather than by matching <c>Program.cs</c>: the composition
    /// root is the file that runs, and renaming it must not be what makes a gate stop looking
    /// at it.
    /// </remarks>
    public static IReadOnlyList<FileInfo> CompositionRoots(string tree) =>
        SourceSurvey.SourceFiles(tree)
            .Where(static file => Parse(file).GetRoot().ChildNodes().OfType<GlobalStatementSyntax>().Any())
            .OrderBy(static file => file.FullName, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The lines of a file whose <em>code</em> names one of these types or writes one of these
    /// strings.
    /// </summary>
    /// <remarks>
    /// Tokens, so a comment naming the thing is not a use of it. That distinction is the whole
    /// question being asked of a composition root: <c>samples/ecommerce/Program.cs</c> says
    /// "nothing in this file mentions order.place" twice, in prose, and a textual search would
    /// read its own denial as the mention.
    /// </remarks>
    public static IReadOnlyList<string> LinesNaming(
        FileInfo file,
        IReadOnlyCollection<string> types,
        IReadOnlyCollection<string> literals)
    {
        var found = new SortedDictionary<int, string>();

        foreach (var token in Parse(file).GetRoot().DescendantTokens())
        {
            var names =
                (token.IsKind(SyntaxKind.IdentifierToken) && types.Contains(token.ValueText))
                || (token.IsKind(SyntaxKind.StringLiteralToken) && literals.Contains(token.ValueText));

            if (!names)
            {
                continue;
            }

            var line = token.GetLocation().GetLineSpan().StartLinePosition.Line;
            found[line] = $"{SourceSurvey.RelativePath(file)}:{line + 1} names {token.ValueText}";
        }

        return [.. found.Values];
    }

    /// <summary>
    /// Whether a line is code rather than blank space or commentary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rule a line budget needs, and the direction matters. Counting comment lines would
    /// make the budget payable by deleting documentation — a sample that explains itself would
    /// fail where the same sample stripped of its remarks would pass, which is precisely
    /// backwards for a repository whose samples <em>are</em> the explanation. Counting only
    /// code means prose is free and cannot be traded for headroom in either direction.
    /// </para>
    /// <para>
    /// A textual test, and it can be fooled by a line of a verbatim string literal that begins
    /// with <c>//</c>. That costs at most an undercount in a sample that does not contain one,
    /// and the alternative — classifying every line through the parser's trivia — buys nothing
    /// a budget can act on.
    /// </para>
    /// </remarks>
    public static bool IsCode(string line)
    {
        var trimmed = line.Trim();

        return trimmed.Length > 0
            && !trimmed.StartsWith("//", StringComparison.Ordinal)
            && !trimmed.StartsWith("/*", StringComparison.Ordinal)
            && !trimmed.StartsWith('*');
    }

    private static SyntaxTree Parse(FileInfo file) =>
        CSharpSyntaxTree.ParseText(
            File.ReadAllText(file.FullName),
            new CSharpParseOptions(LanguageVersion.Preview),
            path: file.FullName);

    private static IEnumerable<TypeSite> Read(FileInfo file)
    {
        var tree = Parse(file);
        var text = tree.GetText();

        foreach (var type in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var attributes = type.AttributeLists.SelectMany(static list => list.Attributes).ToList();
            var flow = attributes.FirstOrDefault(static a => AttributeReader.IsNamed(a, "Flow"));
            var define = type.Members.OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(static m => m.Identifier.ValueText == "Define");

            yield return new TypeSite(
                Name: type.Identifier.ValueText,
                File: SourceSurvey.RelativePath(file),
                Line: type.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                CodeLines: CodeLines(text, type),
                BaseNames: [.. BaseNames(type)],
                Attributes: [.. attributes.Select(static a => Normalise(a.ToString()))],
                FlowId: flow is null ? null : AttributeReader.PositionalArgument(flow, 0),
                Chain: [.. Chain(define)],
                Members: [.. type.Members.Select(Describe)],
                StatementsBesideTheChain: [.. StatementsBesideTheChain(define)]);
        }
    }

    /// <summary>Code lines from the declaration's first attribute or directive to its brace.</summary>
    private static int CodeLines(Microsoft.CodeAnalysis.Text.SourceText text, TypeDeclarationSyntax type)
    {
        // FullSpan rather than Span: it starts at the end of the previous declaration, so the
        // `#pragma warning disable` a suppression is written on is inside the count while the
        // documentation above it — trivia either way — falls out under IsCode.
        var span = text.Lines.GetLinePositionSpan(type.FullSpan);
        var lines = 0;

        for (var line = span.Start.Line; line <= span.End.Line; line++)
        {
            if (IsCode(text.Lines[line].ToString()))
            {
                lines++;
            }
        }

        return lines;
    }

    /// <summary>Every identifier in a base list: <c>Flow&lt;A, B&gt;</c> reads as Flow, A, B.</summary>
    private static IEnumerable<string> BaseNames(TypeDeclarationSyntax type) =>
        (type.BaseList?.Types ?? default)
        .SelectMany(static t => new[] { AttributeReader.SimpleName(t.Type) }.Concat(TypeArguments(t.Type)));

    private static IEnumerable<string> TypeArguments(TypeSyntax type) =>
        type is GenericNameSyntax generic
            ? generic.TypeArgumentList.Arguments.Select(AttributeReader.SimpleName)
            : [];

    /// <summary>The fluent chain of a <c>Define</c> body, in the order it is written.</summary>
    private static List<ChainCall> Chain(MethodDeclarationSyntax? define)
    {
        var statement = ChainStatement(define);

        if (statement is null)
        {
            return [];
        }

        var calls = new List<ChainCall>();

        for (var current = statement.Expression;
             current is InvocationExpressionSyntax invocation
                 && invocation.Expression is MemberAccessExpressionSyntax access;
             current = access.Expression)
        {
            calls.Add(new ChainCall(
                AttributeReader.SimpleName(access.Name),
                [.. TypeArguments(access.Name)],
                Normalise(invocation.ArgumentList.ToString())));
        }

        calls.Reverse();

        return calls;
    }

    /// <summary>Everything else <c>Define</c> does, as written.</summary>
    private static IEnumerable<string> StatementsBesideTheChain(MethodDeclarationSyntax? define)
    {
        var chain = ChainStatement(define);

        return (define?.Body?.Statements ?? default)
            .Where(statement => statement != chain)
            .Select(static statement => Normalise(statement.ToString()));
    }

    /// <summary>
    /// The statement that builds the flow: the one rooted at <c>Define</c>'s own parameter.
    /// </summary>
    /// <remarks>
    /// Rooted rather than "the longest" or "the last", so that a guard clause, a local and a
    /// second chain are each told apart from the builder by what they start from rather than by
    /// where they sit.
    /// </remarks>
    private static ExpressionStatementSyntax? ChainStatement(MethodDeclarationSyntax? define)
    {
        var builder = define?.ParameterList.Parameters.FirstOrDefault()?.Identifier.ValueText;

        return builder is null
            ? null
            : (define?.Body?.Statements ?? default)
                .OfType<ExpressionStatementSyntax>()
                .FirstOrDefault(statement => Root(statement.Expression) == builder);
    }

    private static string? Root(ExpressionSyntax expression)
    {
        var current = expression;

        while (current is InvocationExpressionSyntax invocation
            && invocation.Expression is MemberAccessExpressionSyntax access)
        {
            current = access.Expression;
        }

        return (current as IdentifierNameSyntax)?.Identifier.ValueText;
    }

    private static string Describe(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax method => "method " + method.Identifier.ValueText,
        ConstructorDeclarationSyntax constructor => "constructor " + constructor.Identifier.ValueText,
        PropertyDeclarationSyntax property => "property " + property.Identifier.ValueText,
        FieldDeclarationSyntax field => "field " + field.Declaration.Variables[0].Identifier.ValueText,
        TypeDeclarationSyntax type => "type " + type.Identifier.ValueText,
        _ => member.Kind().ToString(),
    };

    private static string Normalise(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>One type declaration in a sample, as written.</summary>
/// <param name="Name">The type's name.</param>
/// <param name="File">Repository-relative path, for a failure message that can be acted on.</param>
/// <param name="Line">One-based line of the declaration.</param>
/// <param name="CodeLines">
/// Lines of code the declaration costs — its attributes, its body, and any compiler directive
/// written inside it. Blank lines and commentary are not code; see <c>SampleSurvey.IsCode</c>.
/// </param>
/// <param name="BaseNames">Every identifier in the base list, generic arguments included.</param>
/// <param name="Attributes">Each attribute as written, with its arguments, whitespace collapsed.</param>
/// <param name="FlowId">The flow id when the type carries <c>[Flow]</c>; null otherwise.</param>
/// <param name="Chain">The fluent chain of <c>Define</c>, empty for a type that has none.</param>
/// <param name="Members">Each member as "kind name", so a gate can ask what else a type declares.</param>
/// <param name="StatementsBesideTheChain">Everything <c>Define</c> does apart from building the flow.</param>
internal sealed record TypeSite(
    string Name,
    string File,
    int Line,
    int CodeLines,
    IReadOnlyList<string> BaseNames,
    IReadOnlyList<string> Attributes,
    string? FlowId,
    IReadOnlyList<ChainCall> Chain,
    IReadOnlyList<string> Members,
    IReadOnlyList<string> StatementsBesideTheChain)
{
    /// <summary>Where this declaration is, in a form a failure message can print.</summary>
    public string Where => $"{File}:{Line} ({Name})";

    /// <summary>The capability types the chain names, in order, compensations included.</summary>
    public IEnumerable<string> Steps =>
        Chain.Where(static call => call.Name is "Step" or "CompensateWith")
            .SelectMany(static call => call.TypeArguments);
}

/// <summary>One call in a flow's fluent chain.</summary>
/// <param name="Name">The method, e.g. <c>Step</c>.</param>
/// <param name="TypeArguments">Its type arguments by simple name, e.g. <c>ValidateOrder</c>.</param>
/// <param name="Arguments">The argument list as written, whitespace collapsed.</param>
internal sealed record ChainCall(string Name, IReadOnlyList<string> TypeArguments, string Arguments)
{
    /// <summary>The call as one comparable string.</summary>
    public string Text =>
        TypeArguments.Count == 0
            ? Name + Arguments
            : $"{Name}<{string.Join(", ", TypeArguments)}>{Arguments}";
}
