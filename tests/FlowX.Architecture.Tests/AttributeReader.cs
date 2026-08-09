using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FlowX.Architecture.Tests;

/// <summary>
/// Reads attribute arguments out of parsed C#, in the several shapes an author may write them.
/// </summary>
/// <remarks>
/// Shared by <see cref="SourceSurvey"/> and <see cref="FlowTriggerSurvey"/> because both ask
/// the same awkward questions of an attribute list — is this <c>[Capability]</c> or
/// <c>[CapabilityAttribute]</c>, is that <c>ExecutionProfile.Durable</c> or a
/// <c>using static</c>'d <c>Durable</c> — and a second copy of the answers is a second chance
/// for one survey to start disagreeing with the other about what an attribute says.
/// </remarks>
internal static class AttributeReader
{
    /// <summary>Matches <c>[Capability]</c> and <c>[CapabilityAttribute]</c>, qualified or not.</summary>
    public static bool IsNamed(AttributeSyntax attribute, string name)
    {
        var simple = SimpleName(attribute.Name);

        return simple == name || simple == name + "Attribute";
    }

    /// <summary>The right-most identifier of a possibly qualified, possibly generic name.</summary>
    public static string SimpleName(TypeSyntax type) => type switch
    {
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        QualifiedNameSyntax qualified => SimpleName(qualified.Right),
        AliasQualifiedNameSyntax aliased => SimpleName(aliased.Name),
        _ => type.ToString(),
    };

    /// <summary>The <paramref name="index"/>th argument written without a name.</summary>
    public static string? PositionalArgument(AttributeSyntax attribute, int index)
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

    /// <summary>The argument written as <c>Name = value</c>, as written.</summary>
    public static string? NamedArgument(AttributeSyntax attribute, string name) =>
        attribute.ArgumentList?.Arguments
            .FirstOrDefault(a => a.NameEquals?.Name.Identifier.ValueText == name)
            ?.Expression.ToString();

    /// <summary>Whether a named boolean argument is written and written <c>true</c>.</summary>
    public static bool NamedFlagIsTrue(AttributeSyntax attribute, string name) =>
        string.Equals(NamedArgument(attribute, name), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>Authorization.Public</c> and <c>Public</c> both read as <c>Public</c>.</summary>
    public static string? EnumMember(string? expression)
    {
        if (expression is null)
        {
            return null;
        }

        var lastDot = expression.LastIndexOf('.');

        return lastDot < 0 ? expression : expression[(lastDot + 1)..];
    }
}
