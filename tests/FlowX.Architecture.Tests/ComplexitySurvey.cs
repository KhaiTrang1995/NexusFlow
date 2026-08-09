using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FlowX.Architecture.Tests;

/// <summary>
/// Measures the control-flow complexity of every method the repository ships.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why measure rather than review.</strong> "This method is too complicated" is an
/// opinion until it is a number, and an opinion loses to the author who wrote the method and
/// remembers why each branch is there. A number that the build computes the same way every
/// time turns the argument into an arithmetic one, which is the only kind a gate can settle.
/// </para>
/// <para>
/// <strong>Cognitive complexity, not just cyclomatic.</strong> Cyclomatic complexity counts
/// decision points, so a flat twelve-arm <c>switch</c> that reads like a table scores the same
/// as four nested loops with a break condition each. Cognitive complexity — Campbell's metric,
/// the one SonarQube reports — charges nesting for what it costs the reader: a branch three
/// levels deep costs four, the same branch at the top level costs one. Both are here because
/// they disagree in the informative direction. A method that is high cyclomatic and low
/// cognitive is usually a dispatch table and should be left alone; high cognitive is the one
/// that hurts.
/// </para>
/// <para>
/// Syntax only, in the manner of <see cref="SourceSurvey"/>: no compilation, no metadata
/// references, so the measurement costs milliseconds and cannot be broken by an unrelated
/// build error.
/// </para>
/// </remarks>
internal static class ComplexitySurvey
{
    private static readonly Lazy<IReadOnlyList<MethodComplexity>> MethodsLazy =
        new(() => Survey(SourceSurvey.ShippingTrees));

    /// <summary>Every method-like body in a shipping tree, with its complexity measured.</summary>
    public static IReadOnlyList<MethodComplexity> Methods => MethodsLazy.Value;

    private static List<MethodComplexity> Survey(string[] trees)
    {
        var measured = new List<MethodComplexity>();

        foreach (var file in SourceSurvey.SourceFiles(trees))
        {
            if (IsGenerated(file))
            {
                continue;
            }

            var tree = CSharpSyntaxTree.ParseText(
                File.ReadAllText(file.FullName),
                new CSharpParseOptions(LanguageVersion.Preview),
                path: file.FullName);

            foreach (var member in tree.GetRoot().DescendantNodes().OfType<MemberDeclarationSyntax>())
            {
                measured.AddRange(MeasureMember(member, file));
            }
        }

        return measured;
    }

    /// <summary>
    /// Source the repository did not write by hand is not the repository's to simplify.
    /// </summary>
    /// <remarks>
    /// FlowX's own generator emits flow plans and dispatchers, and an emitted dispatch switch
    /// is allowed to be as wide as the flow it came from. Holding generated output to a
    /// hand-written readability budget would either fail the build for a shape nobody can edit
    /// or push the budget up until it stops binding on the code people do edit.
    /// </remarks>
    private static bool IsGenerated(FileInfo file) =>
        file.Name.EndsWith(".g.cs", StringComparison.Ordinal)
        || file.Name.EndsWith(".Generated.cs", StringComparison.Ordinal)
        || file.Name.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Yields one measurement per body a reader has to hold in their head at once.
    /// </summary>
    /// <remarks>
    /// A property with a getter and a setter is two bodies, not one, because nobody reads them
    /// together. An expression-bodied member is one. A local function or lambda is <em>not</em>
    /// its own entry: its complexity is charged to the method that declares it, which is the
    /// method a reader has to understand to understand the closure.
    /// </remarks>
    private static IEnumerable<MethodComplexity> MeasureMember(MemberDeclarationSyntax member, FileInfo file)
    {
        switch (member)
        {
            case BaseMethodDeclarationSyntax method:
                var methodBody = (SyntaxNode?)method.Body ?? method.ExpressionBody;

                if (methodBody is not null)
                {
                    yield return Measure(NameOf(method), methodBody, member, file);
                }

                break;

            case PropertyDeclarationSyntax { ExpressionBody: { } getter } property:
                yield return Measure(property.Identifier.ValueText, getter, member, file);

                break;

            // Both expression-bodied indexers in the repository are one-liners today. The case
            // is here so that the first complicated one is not invisible to the gate.
            case IndexerDeclarationSyntax { ExpressionBody: { } indexer }:
                yield return Measure("this[]", indexer, member, file);

                break;

            case BasePropertyDeclarationSyntax { AccessorList: { } accessors } declaration:
                foreach (var accessor in accessors.Accessors)
                {
                    var body = (SyntaxNode?)accessor.Body ?? accessor.ExpressionBody;

                    if (body is not null)
                    {
                        yield return Measure(
                            $"{NameOf(declaration)}.{accessor.Keyword.ValueText}", body, member, file);
                    }
                }

                break;

            default:
                break;
        }
    }

    private static string NameOf(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax method => method.Identifier.ValueText,
        ConstructorDeclarationSyntax constructor => $"{constructor.Identifier.ValueText}.ctor",
        DestructorDeclarationSyntax destructor => $"~{destructor.Identifier.ValueText}",
        OperatorDeclarationSyntax op => $"operator {op.OperatorToken.ValueText}",
        ConversionOperatorDeclarationSyntax conversion => $"operator {conversion.Type}",
        PropertyDeclarationSyntax property => property.Identifier.ValueText,
        IndexerDeclarationSyntax => "this[]",
        EventDeclarationSyntax @event => @event.Identifier.ValueText,
        _ => "<member>",
    };

    private static MethodComplexity Measure(
        string name, SyntaxNode body, MemberDeclarationSyntax member, FileInfo file)
    {
        var cognitive = new CognitiveScorer();
        cognitive.Walk(body, 0);

        return new MethodComplexity(
            Name: $"{DeclaringTypeOf(member)}.{name}",
            File: SourceSurvey.RelativePath(file),
            Line: member.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Cognitive: cognitive.Score,
            Cyclomatic: Cyclomatic(body),
            MaxNesting: cognitive.MaxNesting);
    }

    private static string DeclaringTypeOf(SyntaxNode member) =>
        member.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText
        ?? "<global>";

    /// <summary>
    /// Decision points plus one — the classic McCabe count.
    /// </summary>
    /// <remarks>
    /// Counted over the whole body including lambdas, matching the cognitive score's subject so
    /// the two numbers describe the same text. <c>case</c> labels count, <c>default</c> does
    /// not, because <c>default</c> adds no edge that the labels did not already imply. Pattern
    /// combinators (<c>and</c>, <c>or</c>) count for the same reason <c>&amp;&amp;</c> does:
    /// each is a path the reader has to evaluate. <c>?.</c> is deliberately <em>not</em>
    /// counted — a null-conditional chain is one guard written compactly, and counting each
    /// link would rank fluent null-safe code above genuinely branchy code.
    /// </remarks>
    private static int Cyclomatic(SyntaxNode body) =>
        1 + body.DescendantNodesAndSelf().Count(static node => node switch
        {
            IfStatementSyntax or WhileStatementSyntax or DoStatementSyntax => true,
            ForStatementSyntax or ForEachStatementSyntax or ForEachVariableStatementSyntax => true,
            CaseSwitchLabelSyntax or CasePatternSwitchLabelSyntax or SwitchExpressionArmSyntax => true,
            CatchClauseSyntax or ConditionalExpressionSyntax => true,
            BinaryExpressionSyntax binary => binary.IsKind(SyntaxKind.LogicalAndExpression)
                || binary.IsKind(SyntaxKind.LogicalOrExpression)
                || binary.IsKind(SyntaxKind.CoalesceExpression),
            BinaryPatternSyntax => true,
            _ => false,
        });

    /// <summary>
    /// Campbell's cognitive complexity, walked by hand.
    /// </summary>
    /// <remarks>
    /// Written as an explicit recursive walk rather than a <see cref="CSharpSyntaxWalker"/>
    /// because the metric's whole content is <em>where the nesting level changes</em>, and a
    /// visitor that carries nesting in a field has to remember to restore it on every exit
    /// path. Passing the level as an argument makes that structural instead of remembered.
    /// </remarks>
    private sealed class CognitiveScorer
    {
        public int Score { get; private set; }

        public int MaxNesting { get; private set; }

        public void Walk(SyntaxNode node, int nesting)
        {
            foreach (var child in node.ChildNodes())
            {
                WalkNode(child, nesting);
            }
        }

        private void WalkNode(SyntaxNode node, int nesting)
        {
            switch (node)
            {
                case IfStatementSyntax ifStatement:
                    WalkIf(ifStatement, nesting, isElseIf: false);

                    return;

                case SwitchStatementSyntax or SwitchExpressionSyntax:
                case WhileStatementSyntax or DoStatementSyntax:
                case ForStatementSyntax or ForEachStatementSyntax or ForEachVariableStatementSyntax:
                case CatchClauseSyntax:
                    Nest(node, nesting);

                    return;

                case ConditionalExpressionSyntax ternary:
                    Nest(ternary, nesting);

                    return;

                // A closure is a new reading context: what is inside it nests, but the closure
                // itself is not a branch, so it costs nesting without costing a point.
                case AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax:
                    Descend(node, nesting + 1);

                    return;

                case BinaryExpressionSyntax binary when IsLogicalRoot(binary):
                    Score += LogicalRuns(binary);
                    Walk(binary, nesting);

                    return;

                case GotoStatementSyntax:
                    Score++;

                    return;

                default:
                    Walk(node, nesting);

                    return;
            }
        }

        /// <summary>
        /// <c>else if</c> is one branch to the reader, not a nested <c>if</c> inside an
        /// <c>else</c>, so it scores a flat point and does not deepen the level.
        /// </summary>
        private void WalkIf(IfStatementSyntax ifStatement, int nesting, bool isElseIf)
        {
            Score += isElseIf ? 1 : 1 + nesting;
            Observe(nesting);

            // WalkNode rather than Walk: the condition may itself be the logical chain that has
            // to be scored, and Walk would step straight past it into its operands.
            WalkNode(ifStatement.Condition, nesting);
            Descend(ifStatement.Statement, nesting + 1);

            if (ifStatement.Else is not { } elseClause)
            {
                return;
            }

            if (elseClause.Statement is IfStatementSyntax chained)
            {
                WalkIf(chained, nesting, isElseIf: true);

                return;
            }

            Score++;
            Descend(elseClause.Statement, nesting + 1);
        }

        private void Nest(SyntaxNode node, int nesting)
        {
            Score += 1 + nesting;
            Observe(nesting);
            Descend(node, nesting + 1);
        }

        private void Descend(SyntaxNode node, int nesting)
        {
            Observe(nesting - 1);
            Walk(node, nesting);
        }

        private void Observe(int nesting) => MaxNesting = Math.Max(MaxNesting, nesting + 1);

        /// <summary>
        /// Whether this node begins a chain of logical operators rather than continuing one.
        /// </summary>
        /// <remarks>
        /// Only the outermost node of a chain is scored, because <see cref="LogicalRuns"/>
        /// flattens the whole chain from there. Scoring every node would charge
        /// <c>a &amp;&amp; b &amp;&amp; c</c> twice for the same single thought.
        /// </remarks>
        private static bool IsLogicalRoot(BinaryExpressionSyntax binary) =>
            IsLogical(binary) && !IsLogical(binary.Parent);

        private static bool IsLogical(SyntaxNode? node) =>
            node is BinaryExpressionSyntax binary
            && (binary.IsKind(SyntaxKind.LogicalAndExpression) || binary.IsKind(SyntaxKind.LogicalOrExpression));

        /// <summary>
        /// A run of one operator costs one; every switch between <c>&amp;&amp;</c> and
        /// <c>||</c> costs another.
        /// </summary>
        /// <remarks>
        /// <c>a &amp;&amp; b &amp;&amp; c</c> is one thing to hold — "all of these". Mixing in
        /// an <c>||</c> is where the reader has to start tracking precedence, and that is what
        /// the metric charges for.
        /// </remarks>
        private static int LogicalRuns(BinaryExpressionSyntax root)
        {
            var operators = new List<SyntaxKind>();
            Flatten(root, operators);

            var runs = 0;

            for (var i = 0; i < operators.Count; i++)
            {
                if (i == 0 || operators[i] != operators[i - 1])
                {
                    runs++;
                }
            }

            return runs;
        }

        private static void Flatten(SyntaxNode node, List<SyntaxKind> operators)
        {
            if (node is not BinaryExpressionSyntax binary || !IsLogical(binary))
            {
                return;
            }

            Flatten(binary.Left, operators);
            operators.Add(binary.Kind());
            Flatten(binary.Right, operators);
        }
    }
}

/// <summary>One method body, measured.</summary>
/// <param name="Name">Declaring type and member, for a failure message that can be searched for.</param>
/// <param name="File">Repository-relative path.</param>
/// <param name="Line">One-based line of the declaration.</param>
/// <param name="Cognitive">Campbell's cognitive complexity — how hard the body is to read.</param>
/// <param name="Cyclomatic">McCabe's count — how many paths the body has.</param>
/// <param name="MaxNesting">Deepest control-flow nesting reached, one-based.</param>
internal sealed record MethodComplexity(
    string Name,
    string File,
    int Line,
    int Cognitive,
    int Cyclomatic,
    int MaxNesting)
{
    /// <summary>Where this method is, in a form a failure message can print.</summary>
    public string Where => $"{File}:{Line} ({Name})";
}
