using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FlowX.Architecture.Tests;

/// <summary>
/// Reads what each flow declares about itself and about the HTTP addresses it answers on.
/// </summary>
/// <remarks>
/// Source rather than the built manifest, for <see cref="SourceSurvey"/>'s reason: the manifest
/// records the flows in whatever configuration last built, and a flow in a project the test
/// host never referenced would satisfy a gate by being invisible to it. The declaration is the
/// thing being reviewed, so the declaration is what is read.
/// </remarks>
internal static class FlowTriggerSurvey
{
    private static readonly Lazy<IReadOnlyList<FlowDeclaration>> FlowsLazy =
        new(() => Survey(SourceSurvey.ShippingTrees));

    /// <summary>Every type in a shipping tree carrying <c>[Flow]</c>.</summary>
    public static IReadOnlyList<FlowDeclaration> Flows => FlowsLazy.Value;

    /// <summary>
    /// HTTP methods that change something, and so must be safe to repeat.
    /// </summary>
    /// <remarks>
    /// <c>GET</c> and <c>HEAD</c> are absent because a caller may repeat them freely already.
    /// Enumerated rather than expressed as "not GET" so that a method nobody has thought about
    /// is not swept into a rule written before it existed.
    /// </remarks>
    public static readonly string[] MutatingMethods = ["POST", "PUT", "PATCH", "DELETE"];

    private static List<FlowDeclaration> Survey(string[] trees)
    {
        var found = new List<FlowDeclaration>();

        foreach (var file in SourceSurvey.SourceFiles(trees))
        {
            var tree = CSharpSyntaxTree.ParseText(
                File.ReadAllText(file.FullName),
                new CSharpParseOptions(LanguageVersion.Preview),
                path: file.FullName);

            foreach (var type in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var attributes = type.AttributeLists.SelectMany(static list => list.Attributes).ToList();
                var flow = attributes.FirstOrDefault(static a => AttributeReader.IsNamed(a, "Flow"));

                if (flow is null)
                {
                    continue;
                }

                found.Add(Describe(type, file, flow, attributes));
            }
        }

        return found;
    }

    private static FlowDeclaration Describe(
        TypeDeclarationSyntax type,
        FileInfo file,
        AttributeSyntax flow,
        List<AttributeSyntax> attributes)
    {
        var triggers = attributes
            .Where(static a => AttributeReader.IsNamed(a, "HttpTrigger"))
            .Select(static a => new HttpTriggerDeclaration(
                Method: AttributeReader.PositionalArgument(a, 0) ?? string.Empty,
                Route: AttributeReader.PositionalArgument(a, 1) ?? string.Empty,
                Idempotent: AttributeReader.NamedFlagIsTrue(a, "Idempotent")))
            .ToList();

        return new FlowDeclaration(
            TypeName: type.Identifier.ValueText,
            File: SourceSurvey.RelativePath(file),
            Line: type.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Id: AttributeReader.PositionalArgument(flow, 0),
            Profile: AttributeReader.EnumMember(AttributeReader.NamedArgument(flow, "Profile")),
            HttpTriggers: triggers);
    }
}

/// <summary>One type carrying <c>[Flow]</c>, as declared in source.</summary>
/// <param name="TypeName">The declaring type's name.</param>
/// <param name="File">Repository-relative path, for a failure message that can be acted on.</param>
/// <param name="Line">One-based line of the type declaration.</param>
/// <param name="Id">The flow id.</param>
/// <param name="Profile">
/// The declared execution profile as written, e.g. <c>Durable</c>. Null when the attribute
/// names none, which the compiler reads as <c>Ephemeral</c>.
/// </param>
/// <param name="HttpTriggers">Every <c>[HttpTrigger]</c> on the type; the attribute may repeat.</param>
internal sealed record FlowDeclaration(
    string TypeName,
    string File,
    int Line,
    string? Id,
    string? Profile,
    IReadOnlyList<HttpTriggerDeclaration> HttpTriggers)
{
    /// <summary>
    /// Whether this flow writes something a repeat would write twice.
    /// </summary>
    /// <remarks>
    /// <c>Durable</c> is the declaration that the flow keeps a journal, and a flow keeps a
    /// journal precisely because its steps have effects worth not repeating. An
    /// <c>Ephemeral</c> flow holds nothing across a retry and has nothing to deduplicate, which
    /// is why the reads in these samples declare it.
    /// </remarks>
    public bool Mutates => string.Equals(Profile, "Durable", StringComparison.Ordinal);

    /// <summary>Where this flow is, in a form a failure message can print.</summary>
    public string Where => $"{File}:{Line} ({TypeName})";
}

/// <summary>One <c>[HttpTrigger]</c> on a flow.</summary>
/// <param name="Method">The HTTP method, as written.</param>
/// <param name="Route">The route template, as written.</param>
/// <param name="Idempotent">Whether an <c>Idempotency-Key</c> header is demanded at admission.</param>
internal sealed record HttpTriggerDeclaration(string Method, string Route, bool Idempotent)
{
    /// <summary>Whether this address changes something.</summary>
    public bool IsMutating =>
        FlowTriggerSurvey.MutatingMethods.Contains(Method, StringComparer.OrdinalIgnoreCase);
}
