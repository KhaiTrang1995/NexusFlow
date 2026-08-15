using System.Linq;
using Microsoft.CodeAnalysis;

namespace FlowX.Compiler.Analysis;

/// <summary>
/// The one reading of <c>[EventSchema("…")]</c> on an event contract, and the one judgement
/// of whether the value is a version.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One reader, because the value is written twice.</strong> It reaches the
/// manifest's <c>event.schemaVersion</c> through <c>ManifestWriter</c> and the outbox row's
/// <c>schema_version</c> through <c>FlowEmitter</c>, and a document promising one version
/// over rows stamped another is the defect ADR-0017 F2 names. Both writers take
/// <c>StepModel.EventSchemaVersion</c>, which is filled here and nowhere else.
/// </para>
/// <para>
/// <strong><see cref="DeclaredOn"/> and <see cref="Malformed"/> are the same function read
/// two ways</strong> — the reader keeps only a value it would not report, and
/// <c>EventSchemaAnalyzer</c> reports only a value the reader would not keep. Splitting the
/// judgement would let a build publish a version the analyzer had already refused.
/// </para>
/// </remarks>
public static class EventSchemaReader
{
    /// <summary>The version an event carries when its contract declares none.</summary>
    /// <remarks>
    /// The value every event in every manifest FlowX produced carried before the attribute
    /// existed. Keeping it as the absence means an application that declares nothing emits
    /// the manifest it emitted yesterday, byte for byte.
    /// </remarks>
    public const string Default = "1.0.0";

    private const string AttributeName = "FlowX.EventSchemaAttribute";

    /// <summary>
    /// The version this contract declares, or <c>null</c> when it declares none or declares
    /// one that is not a version.
    /// </summary>
    /// <remarks>
    /// A malformed value is dropped rather than published: <c>FLOWX1055</c> is what tells the
    /// author, and emitting the rubble beside it would put a string in the manifest that
    /// <c>flowx diff</c>'s <c>id@major</c> key cannot read — F2's own objection to a value
    /// nobody wrote, arriving from the other direction.
    /// </remarks>
    public static string? DeclaredOn(ITypeSymbol? contract)
    {
        var declared = ValueOn(contract);

        return declared is not null && IsSemanticVersion(declared) ? declared : null;
    }

    /// <summary>
    /// The declared value when it is not a semantic version, or <c>null</c> when the contract
    /// declares nothing or declares something readable.
    /// </summary>
    public static string? Malformed(ITypeSymbol? contract)
    {
        var declared = ValueOn(contract);

        return declared is not null && !IsSemanticVersion(declared) ? declared : null;
    }

    /// <summary>Whether this type carries the attribute at all, readable or not.</summary>
    public static bool IsDeclaredOn(ITypeSymbol? contract) => ValueOn(contract) is not null;

    /// <summary>The attribute's first argument verbatim, or <c>null</c> when there is none.</summary>
    /// <remarks>
    /// An attribute whose argument is absent or is not a string cannot be written by an author
    /// whose code compiles — the constructor takes one <c>string</c> — so the null branch is
    /// reached only from a half-typed buffer, where C# is already saying something more useful.
    /// </remarks>
    private static string? ValueOn(ITypeSymbol? contract)
    {
        var attribute = contract?.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.ToDisplayString() == AttributeName);

        return attribute is { ConstructorArguments.Length: > 0 }
            ? attribute.ConstructorArguments[0].Value as string
            : null;
    }

    /// <summary>
    /// SemVer 2.0: three numeric identifiers, optional pre-release and build metadata.
    /// </summary>
    /// <remarks>
    /// Hand-walked rather than a regex, and the same grammar <c>Identifiers.VersionPattern</c>
    /// enforces at run time — this assembly is a netstandard2.0 analyzer and cannot reference
    /// the one that holds it. The two are held together by
    /// <c>EventSchemaAnalyzerTests.TheGrammarIsTheOneTheRuntimeEnforces</c>, which puts the
    /// same specimens through both.
    /// </remarks>
    private static bool IsSemanticVersion(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        var core = value;
        var build = core.IndexOf('+');

        if (build >= 0)
        {
            if (!IsDotSeparatedAlphanumeric(core.Substring(build + 1)))
            {
                return false;
            }

            core = core.Substring(0, build);
        }

        var pre = core.IndexOf('-');

        if (pre >= 0)
        {
            if (!IsDotSeparatedAlphanumeric(core.Substring(pre + 1)))
            {
                return false;
            }

            core = core.Substring(0, pre);
        }

        var parts = core.Split('.');

        return parts.Length == 3 && parts.All(IsNumericIdentifier);
    }

    /// <summary>Digits, and no leading zero unless the identifier is <c>0</c> itself.</summary>
    private static bool IsNumericIdentifier(string part) =>
        part.Length > 0 &&
        part.All(c => c >= '0' && c <= '9') &&
        (part.Length == 1 || part[0] != '0');

    /// <summary>
    /// One or more of <c>[0-9A-Za-z-.]</c>, which is the run-time pattern's own character
    /// class for a pre-release or a build tag — not SemVer's stricter identifier grammar.
    /// </summary>
    private static bool IsDotSeparatedAlphanumeric(string value) =>
        value.Length > 0 && value.All(c =>
            (c >= '0' && c <= '9') ||
            (c >= 'A' && c <= 'Z') ||
            (c >= 'a' && c <= 'z') ||
            c == '-' ||
            c == '.');
}
