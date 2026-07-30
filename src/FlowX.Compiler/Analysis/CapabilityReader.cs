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
        string inputTypeName,
        string outputTypeName)
    {
        TypeName = typeName;
        Id = id;
        Version = version;
        IsIdempotent = isIdempotent;
        SideEffects = sideEffects;
        DeclaresAuthorization = declaresAuthorization;
        AuthorizationMode = authorizationMode;
        InputTypeName = inputTypeName;
        OutputTypeName = outputTypeName;
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

    /// <summary>The <c>TIn</c> of <c>ICapability&lt;TIn, TOut&gt;</c>. Required by the manifest schema.</summary>
    public string InputTypeName { get; }

    /// <summary>The <c>TOut</c> of <c>ICapability&lt;TIn, TOut&gt;</c>.</summary>
    public string OutputTypeName { get; }
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
            contract.Input,
            contract.Output);
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
