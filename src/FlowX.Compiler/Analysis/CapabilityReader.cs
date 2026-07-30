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
        bool declaresAuthorization)
    {
        TypeName = typeName;
        Id = id;
        Version = version;
        IsIdempotent = isIdempotent;
        SideEffects = sideEffects;
        DeclaresAuthorization = declaresAuthorization;
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
                    break;

                case "SideEffects":
                    sideEffects.AddRange(named.Value.Values
                        .Select(v => v.Value as string)
                        .Where(v => !string.IsNullOrEmpty(v))
                        .Select(v => v!));
                    break;
            }
        }

        return new CapabilityInfo(
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                .Replace("global::", string.Empty),
            id!,
            version,
            idempotent,
            sideEffects.ToArray(),
            declaresAuthorization);
    }

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
