using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FlowX.Architecture.Tests;

/// <summary>
/// Reads the repository's own C# source and reports what it declares.
/// </summary>
/// <remarks>
/// <para>
/// The security fitness functions ask questions about <em>declarations</em> — does this
/// capability state an authorisation stance, is this <c>Public</c> one reviewed — and the
/// honest place to ask them is the source, not a compiled assembly. An assembly scan only
/// sees what the test project happens to reference and what the current configuration
/// happened to build; a capability in a project nobody referenced would pass the gate by
/// being invisible to it. For a gate whose failure mode is "a capability ships without a
/// stance", a false negative is the expensive direction.
/// </para>
/// <para>
/// Parsing is syntactic, with no compilation and no metadata references, so it costs
/// milliseconds and cannot be broken by an unrelated build error. It is a full C# parser
/// rather than a regular expression on purpose: a regex over attribute lists misses the
/// declaration written in an unfamiliar style, and misses it silently.
/// </para>
/// </remarks>
internal static class SourceSurvey
{
    /// <summary>
    /// The trees whose capabilities ship: the platform, the transports, and the reference
    /// applications.
    /// </summary>
    /// <remarks>
    /// <c>tests/</c> is deliberately not here. A benchmark's <c>EchoCapability</c> exists so
    /// a dispatch measurement has something to dispatch to; it never reaches a manifest, a
    /// trigger or a deployment, and requiring it to declare a stance would teach people that
    /// the stance is a formality. <c>samples/</c> <em>is</em> here: the samples are published
    /// as the reference for how to build on FlowX, and they are the DAST target
    /// (docs/21-Quality-Gates.md §4.1), so a permissive declaration there is a permissive
    /// declaration people copy.
    /// </remarks>
    public static readonly string[] ShippingTrees = ["src", "plugins", "samples"];

    private static readonly Lazy<IReadOnlyList<CapabilityDeclaration>> CapabilitiesLazy =
        new(() => Survey(ShippingTrees));

    /// <summary>Every type in a shipping tree that implements <c>ICapability&lt;,&gt;</c>.</summary>
    public static IReadOnlyList<CapabilityDeclaration> Capabilities => CapabilitiesLazy.Value;

    /// <summary>Every <c>.cs</c> file under the given trees, excluding build output.</summary>
    public static IEnumerable<FileInfo> SourceFiles(params string[] trees)
    {
        foreach (var tree in trees)
        {
            var directory = new DirectoryInfo(Path.Combine(RepositoryLayout.Root.FullName, tree));

            if (!directory.Exists)
            {
                continue;
            }

            foreach (var file in directory.EnumerateFiles("*.cs", SearchOption.AllDirectories))
            {
                if (!IsBuildOutput(file))
                {
                    yield return file;
                }
            }
        }
    }

    /// <summary>The path of a file relative to the repository root, with forward slashes.</summary>
    public static string RelativePath(FileInfo file) =>
        Path.GetRelativePath(RepositoryLayout.Root.FullName, file.FullName).Replace('\\', '/');

    private static bool IsBuildOutput(FileInfo file) =>
        file.FullName.Replace('\\', '/')
            .Split('/')
            .Any(static segment => segment is "obj" or "bin");

    private static List<CapabilityDeclaration> Survey(string[] trees)
    {
        var found = new List<CapabilityDeclaration>();

        foreach (var file in SourceFiles(trees))
        {
            var tree = CSharpSyntaxTree.ParseText(
                File.ReadAllText(file.FullName),
                new CSharpParseOptions(LanguageVersion.Preview),
                path: file.FullName);

            foreach (var type in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (!ImplementsCapability(type))
                {
                    continue;
                }

                found.Add(Describe(type, file));
            }
        }

        return found;
    }

    private static bool ImplementsCapability(TypeDeclarationSyntax type) =>
        type.BaseList is not null
        && type.BaseList.Types.Any(static t => SimpleName(t.Type) == "ICapability");

    private static CapabilityDeclaration Describe(TypeDeclarationSyntax type, FileInfo file)
    {
        var attributes = type.AttributeLists.SelectMany(static list => list.Attributes).ToList();

        var capability = attributes.FirstOrDefault(static a => IsNamed(a, "Capability"));
        var approvedBy = attributes.FirstOrDefault(static a => IsNamed(a, "ApprovedBy"));

        return new CapabilityDeclaration(
            TypeName: type.Identifier.ValueText,
            File: RelativePath(file),
            Line: type.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Id: capability is null ? null : PositionalArgument(capability, 0),
            Authorization: capability is null ? null : EnumMember(NamedArgument(capability, "Authorization")),
            HasCapabilityAttribute: capability is not null,
            Reviewer: approvedBy is null ? null : PositionalArgument(approvedBy, 0),
            ReviewDate: approvedBy is null ? null : PositionalArgument(approvedBy, 1));
    }

    /// <summary>Matches <c>[Capability]</c> and <c>[CapabilityAttribute]</c>, qualified or not.</summary>
    private static bool IsNamed(AttributeSyntax attribute, string name)
    {
        var simple = SimpleName(attribute.Name);

        return simple == name || simple == name + "Attribute";
    }

    /// <summary>The right-most identifier of a possibly qualified, possibly generic name.</summary>
    private static string SimpleName(TypeSyntax type) => type switch
    {
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        QualifiedNameSyntax qualified => SimpleName(qualified.Right),
        AliasQualifiedNameSyntax aliased => SimpleName(aliased.Name),
        _ => type.ToString(),
    };

    private static string? PositionalArgument(AttributeSyntax attribute, int index)
    {
        var positional = attribute.ArgumentList?.Arguments
            .Where(static a => a.NameEquals is null)
            .ToList();

        if (positional is null || positional.Count <= index)
        {
            return null;
        }

        return positional[index].Expression is LiteralExpressionSyntax literal
            ? literal.Token.ValueText
            : positional[index].Expression.ToString();
    }

    private static string? NamedArgument(AttributeSyntax attribute, string name) =>
        attribute.ArgumentList?.Arguments
            .FirstOrDefault(a => a.NameEquals?.Name.Identifier.ValueText == name)
            ?.Expression.ToString();

    /// <summary><c>Authorization.Public</c> and <c>Public</c> both read as <c>Public</c>.</summary>
    private static string? EnumMember(string? expression)
    {
        if (expression is null)
        {
            return null;
        }

        var lastDot = expression.LastIndexOf('.');

        return lastDot < 0 ? expression : expression[(lastDot + 1)..];
    }
}

/// <summary>One type implementing <c>ICapability&lt;,&gt;</c>, as declared in source.</summary>
/// <param name="TypeName">The declaring type's name.</param>
/// <param name="File">Repository-relative path, for a failure message that can be acted on.</param>
/// <param name="Line">One-based line of the type declaration.</param>
/// <param name="Id">The capability id, or null when <c>[Capability]</c> is absent.</param>
/// <param name="Authorization">
/// The declared stance as written, e.g. <c>Internal</c>. Null when the attribute is absent
/// or names no stance.
/// </param>
/// <param name="HasCapabilityAttribute">Whether <c>[Capability]</c> is present at all.</param>
/// <param name="Reviewer">The reviewer from <c>[ApprovedBy]</c>, when present.</param>
/// <param name="ReviewDate">The review date from <c>[ApprovedBy]</c>, when present.</param>
internal sealed record CapabilityDeclaration(
    string TypeName,
    string File,
    int Line,
    string? Id,
    string? Authorization,
    bool HasCapabilityAttribute,
    string? Reviewer,
    string? ReviewDate)
{
    /// <summary>Where this declaration is, in a form a failure message can print.</summary>
    public string Where => $"{File}:{Line} ({TypeName})";
}
