using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace FlowX.Compiler.Analysis;

/// <summary>What a <c>[Capability]</c> declaration says about a type.</summary>
public sealed class CapabilityInfo
{
    internal CapabilityInfo(
        string typeName,
        string id,
        string version,
        bool isIdempotent,
        string[] sideEffects,
        bool declaresAuthorization,
        string authorizationMode,
        string? authorizationValue,
        string inputTypeName,
        string outputTypeName,
        string? approvedBy = null,
        string? deprecated = null,
        string? declarationLocation = null)
    {
        TypeName = typeName;
        Id = id;
        Version = version;
        IsIdempotent = isIdempotent;
        SideEffects = sideEffects;
        DeclaresAuthorization = declaresAuthorization;
        AuthorizationMode = authorizationMode;
        AuthorizationValue = authorizationValue;
        InputTypeName = inputTypeName;
        OutputTypeName = outputTypeName;
        ApprovedBy = approvedBy;
        Deprecated = deprecated;
        DeclarationLocation = declarationLocation;
    }

    /// <summary>Fully-qualified type name, as the emitted code will spell it.</summary>
    public string TypeName { get; }

    /// <summary>Business identity from the attribute's first argument.</summary>
    public string Id { get; }

    /// <summary>Contract version.</summary>
    public string Version { get; }

    /// <summary>Whether retrying is declared safe. Gates retry policies (FLOWX1014).</summary>
    public bool IsIdempotent { get; }

    /// <summary>Declared external effects.</summary>
    public string[] SideEffects { get; }

    /// <summary>Whether an authorisation stance was declared at all (FLOWX1010).</summary>
    public bool DeclaresAuthorization { get; }

    /// <summary>The declared stance: Public, Authenticated, Permission, Policy or Internal.</summary>
    public string AuthorizationMode { get; }

    /// <summary>
    /// The permission or policy the stance names, or <c>null</c> when the stance names
    /// nothing — either because the mode takes no name, or because none was declared
    /// (FLOWX1030).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read from whichever of <c>Permission</c> or <c>Policy</c> the mode actually uses,
    /// so a name declared beside a mode that does not take one is not carried. The two
    /// properties are separate on the attribute for readability at the declaration site,
    /// and there is exactly one stance, so downstream sees one value rather than a pair
    /// it would have to re-resolve against the mode — the manifest field
    /// <c>authorization.value</c> is likewise one field for both.
    /// </para>
    /// <para>
    /// <strong>This is the value <c>FLOWX-DIFF-015</c> compares.</strong> Its rule is
    /// "authorisation tightened, <em>or the named permission changed</em>", and the second
    /// half could not fire on any manifest FlowX produced while nothing carried this.
    /// </para>
    /// </remarks>
    public string? AuthorizationValue { get; }

    /// <summary>The <c>TIn</c> of <c>ICapability&lt;TIn, TOut&gt;</c>. Required by the manifest schema.</summary>
    public string InputTypeName { get; }

    /// <summary>The <c>TOut</c> of <c>ICapability&lt;TIn, TOut&gt;</c>.</summary>
    public string OutputTypeName { get; }

    /// <summary>
    /// The reviewer named by <c>[ApprovedBy]</c>, or <c>null</c> when the type carries none.
    /// </summary>
    /// <remarks>
    /// The reviewer and not the date, because the manifest field is one string and the
    /// question a reader of a <c>Public</c> capability asks is <em>who signed this off</em>.
    /// The date stays at the declaration, where <c>PublicCapabilitiesAreReviewed</c> reads
    /// it — publishing a second copy of it here would be two places to disagree about when
    /// a review happened.
    /// </remarks>
    public string? ApprovedBy { get; }

    /// <summary>
    /// The obsoletion notice from <c>[Obsolete("...")]</c>, or <c>null</c> when the type
    /// carries no attribute or the attribute carries no message.
    /// </summary>
    /// <remarks>
    /// <strong><c>[Obsolete]</c> rather than a FlowX attribute of its own.</strong>
    /// Deprecation already has a spelling in C#, every tool in the ecosystem understands
    /// it, and it is the one that also warns at the call site — so a consumer inside the
    /// same build learns from the compiler and a consumer outside it learns from this
    /// field. Inventing <c>[Deprecated]</c> beside it would be a second vocabulary for one
    /// fact, and <c>ADR-0017</c>'s table called that "a DSL addition" precisely because
    /// nobody had noticed the language already had one.
    /// <para>
    /// A bare <c>[Obsolete]</c> yields <c>null</c>: the field publishes the notice, and an
    /// attribute with no message declares no sentence to publish. Emitting a stand-in would
    /// put a string in the document that nobody wrote, which is what <c>ADR-0017</c>'s
    /// <c>F2</c> exists to refuse.
    /// </para>
    /// </remarks>
    public string? Deprecated { get; }

    /// <summary>
    /// <c>file:line</c> of the capability's declaration, absolute; relativised when written.
    /// </summary>
    /// <remarks>
    /// The <em>type's</em> location and not the <c>.Step&lt;T&gt;()</c> call site that
    /// <c>StepModel.Location</c> already carries. The schema calls this field "file:line
    /// of the declaration, so every node in the graph is navigable", and a reader who clicks a
    /// capability entry wants the capability, not one of the flows that invokes it — which is
    /// also why one entry can be reached from several steps and still name one place.
    /// </remarks>
    public string? DeclarationLocation { get; }
}

/// <summary>
/// Reads <c>[Capability]</c> metadata off a type symbol.
/// </summary>
/// <remarks>
/// This is the one place attribute shape is interpreted. Everything downstream sees a
/// <see cref="CapabilityInfo"/> — a plain object — which is what keeps the model layer
/// free of Roslyn and the emitter testable without a compilation.
/// </remarks>
public static class CapabilityReader
{
    private const string CapabilityAttribute = "FlowX.CapabilityAttribute";
    private const string CapabilityInterface = "FlowX.ICapability`2";
    private const string ApprovedByAttribute = "FlowX.ApprovedByAttribute";
    private const string ObsoleteAttribute = "System.ObsoleteAttribute";

    /// <summary>Reads the capability metadata, or returns <c>null</c> if the type is not one.</summary>
    public static CapabilityInfo? Read(ITypeSymbol? type)
    {
        if (type is null)
        {
            return null;
        }

        var attribute = type.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.ToDisplayString() == CapabilityAttribute);

        if (attribute is null || attribute.ConstructorArguments.Length == 0)
        {
            return null;
        }

        var id = attribute.ConstructorArguments[0].Value as string;

        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        var version = "1.0.0";
        var idempotent = false;
        var sideEffects = new List<string>();
        var declaresAuthorization = false;
        var authorizationMode = "Public";
        string? permission = null;
        string? policy = null;

        foreach (var named in attribute.NamedArguments)
        {
            switch (named.Key)
            {
                case "Version":
                    version = named.Value.Value as string ?? version;
                    break;

                case "Idempotent":
                    idempotent = named.Value.Value is bool flag && flag;
                    break;

                case "Authorization":
                    declaresAuthorization = true;
                    authorizationMode = AuthorizationName(named.Value.Value);
                    break;

                // Collected rather than resolved here: named arguments arrive in source
                // order, so `Permission = "x", Authorization = ...` would resolve against
                // a mode not yet read. The pairing happens once the loop is done.
                case "Permission":
                    permission = named.Value.Value as string;
                    break;

                case "Policy":
                    policy = named.Value.Value as string;
                    break;

                case "SideEffects":
                    sideEffects.AddRange(named.Value.Values
                        .Select(v => v.Value as string)
                        .Where(v => !string.IsNullOrEmpty(v))
                        .Select(v => v!));
                    break;
            }
        }

        var contract = FindCapabilityContract(type);

        return new CapabilityInfo(
            Display(type),
            id!,
            version,
            idempotent,
            sideEffects.ToArray(),
            declaresAuthorization,
            authorizationMode,
            AuthorizationValueOf(authorizationMode, permission, policy),
            contract.Input,
            contract.Output,
            ReviewerOf(type),
            ObsoletionNoticeOf(type),
            FlowAnalyzer.FormatLocation(DeclarationOf(type)));
    }

    /// <summary>The reviewer named by <c>[ApprovedBy]</c>, or <c>null</c>.</summary>
    private static string? ReviewerOf(ITypeSymbol type)
    {
        var approval = type.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.ToDisplayString() == ApprovedByAttribute);

        var reviewer = approval is { ConstructorArguments.Length: > 0 }
            ? approval.ConstructorArguments[0].Value as string
            : null;

        return string.IsNullOrWhiteSpace(reviewer) ? null : reviewer;
    }

    /// <summary>The message on <c>[Obsolete(...)]</c>, or <c>null</c> when there is none.</summary>
    private static string? ObsoletionNoticeOf(ITypeSymbol type)
    {
        var obsolete = type.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.ToDisplayString() == ObsoleteAttribute);

        var notice = obsolete is { ConstructorArguments.Length: > 0 }
            ? obsolete.ConstructorArguments[0].Value as string
            : null;

        return string.IsNullOrWhiteSpace(notice) ? null : notice;
    }

    /// <summary>
    /// Where the capability is declared: the first of its source locations by path and line.
    /// </summary>
    /// <remarks>
    /// Ordered rather than taken as they come, because a <c>partial</c> type has one location
    /// per part and Roslyn does not promise which arrives first. The manifest is compared
    /// across builds byte for byte, so an unordered pick would report a change nobody made —
    /// the same reason <c>ManifestWriter</c> sorts everything else ordinally.
    /// </remarks>
    private static Location? DeclarationOf(ITypeSymbol type) => type.Locations
        .Where(l => l.IsInSource)
        .OrderBy(l => l.GetLineSpan().Path, System.StringComparer.Ordinal)
        .ThenBy(l => l.GetLineSpan().StartLinePosition.Line)
        .FirstOrDefault();

    /// <summary>The name the stance uses, or <c>null</c> when it uses none.</summary>
    /// <remarks>
    /// Only the property the mode actually reads is carried. <c>Permission = "x"</c>
    /// declared beside <c>Authorization.Public</c> names nothing the stance consults, and
    /// publishing it would put a string in <c>authorization.value</c> that no principal
    /// is ever checked against — a claim about access control with nothing behind it,
    /// which is what <c>FLOWX1010</c> exists to prevent one level up.
    /// </remarks>
    private static string? AuthorizationValueOf(string mode, string? permission, string? policy)
    {
        var value = mode switch
        {
            "Permission" => permission,
            "Policy" => policy,
            _ => null,
        };

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>Maps the enum's underlying value back to its name.</summary>
    /// <remarks>
    /// An attribute argument arrives as the underlying <see cref="int"/>, not as the
    /// enum. The names are spelled out rather than derived so that reordering the enum
    /// — a breaking change the compiler would not otherwise catch here — shows up as a
    /// failing test rather than as a silently wrong manifest.
    /// </remarks>
    private static string AuthorizationName(object? value) => value switch
    {
        0 => "Public",
        1 => "Authenticated",
        2 => "Permission",
        3 => "Policy",
        4 => "Internal",
        _ => "Public",
    };

    /// <summary>
    /// The <c>TIn</c> of the type's <c>ICapability&lt;TIn, TOut&gt;</c> as a symbol, or
    /// <c>null</c> when it implements none.
    /// </summary>
    /// <remarks>
    /// The symbol rather than the display string <see cref="CapabilityInfo.InputTypeName"/>
    /// already carries, because FLOWX1029 asks a question about <em>conversions</em> —
    /// whether a mapping's result can be passed where this is declared — and that is a
    /// question only the compilation can answer. Comparing names would guess.
    /// </remarks>
    public static ITypeSymbol? InputContract(ITypeSymbol? type)
    {
        if (type is null)
        {
            return null;
        }

        foreach (var contract in type.AllInterfaces)
        {
            if (contract.MetadataName == "ICapability`2" && contract.TypeArguments.Length == 2)
            {
                return contract.TypeArguments[0];
            }
        }

        return null;
    }

    private static (string Input, string Output) FindCapabilityContract(ITypeSymbol type)
    {
        foreach (var contract in type.AllInterfaces)
        {
            if (contract.MetadataName == "ICapability`2" && contract.TypeArguments.Length == 2)
            {
                return (Display(contract.TypeArguments[0]), Display(contract.TypeArguments[1]));
            }
        }

        return ("object", "object");
    }

    private static string Display(ITypeSymbol symbol) =>
        symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty);

    /// <summary>How many times the type implements <c>ICapability&lt;,&gt;</c>. More than one is FLOWX1015.</summary>
    public static int CountCapabilityContracts(ITypeSymbol type)
    {
        if (type is null)
        {
            return 0;
        }

        return type.AllInterfaces.Count(i =>
            i.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                .Replace("global::", string.Empty) == CapabilityInterface.Replace("`2", "<TIn, TOut>")
            || i.OriginalDefinition.MetadataName == "ICapability`2");
    }

    /// <summary>Whether the type implements <c>ICapability&lt;,&gt;</c> at all. False is FLOWX1002.</summary>
    public static bool IsCapability(ITypeSymbol type) => CountCapabilityContracts(type) > 0;
}
