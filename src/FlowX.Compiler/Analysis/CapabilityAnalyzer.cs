using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using FlowX.Compiler.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FlowX.Compiler.Analysis;

/// <summary>
/// Checks what a capability <em>depends on</em>: FLOWX1003 and FLOWX1004.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="FlowAnalyzer"/> because it asks a different question. The
/// generator reads a flow's <c>Define</c> chain; these two rules read a capability's own
/// dependencies, which no flow mentions. Both were documented as compile errors and
/// raised by nothing until this existed — the same gap FLOWX1014 was in.
/// </para>
/// <para>
/// A <see cref="DiagnosticAnalyzer"/> rather than more work inside the generator, so the
/// rules apply to every capability in the compilation, including ones no flow has a step
/// for yet. A capability that violates Q4 is wrong whether or not anything calls it.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CapabilityAnalyzer : DiagnosticAnalyzer
{
    private const string CapabilityMetadataName = "ICapability`2";
    private const string FlowXNamespace = "FlowX";

    /// <summary>
    /// Namespace prefixes that mean "transport".
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A list, not a proof.</strong> There is no general way to recognise a
    /// transport type, so this recognises the ones teams actually reach for. A transport
    /// not named here is not detected, and that is a known limit rather than a claim of
    /// completeness — see <c>docs/diagnostics/FLOWX1003.md</c>.
    /// </para>
    /// <para>
    /// The alternative — an attribute that transport authors apply to their own types —
    /// would be sound and would depend on third parties adopting it, which makes it worth
    /// nothing on the day someone needs this rule.
    /// </para>
    /// </remarks>
    private static readonly string[] TransportNamespaces =
    [
        "Microsoft.AspNetCore",
        "Microsoft.Extensions.Http",
        "System.Net.Http",
        "Grpc.",
        "Confluent.Kafka",
        "RabbitMQ.",
        "Azure.Messaging",
        "Amazon.SQS",
        "Amazon.SimpleNotificationService",
        "MQTTnet",
        "NATS.",
        "StackExchange.Redis",
    ];

    /// <summary>
    /// FlowX namespaces a capability may reference. Everything else under <c>FlowX.</c>
    /// is a plugin, and a plugin is a transport.
    /// </summary>
    private static readonly string[] AllowedFlowXNamespaces =
    [
        "FlowX",
        "FlowX.Runtime",
        "FlowX.Generated",
    ];

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(
            FlowXDiagnostics.CapabilityReferencesTransport,
            FlowXDiagnostics.CapabilityInvokesCapability);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            return;
        }

        // Generated code is ours and is exempt: the dispatcher legitimately holds every
        // capability the flow invokes, which is precisely what FLOWX1004 forbids in
        // hand-written code.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSymbolAction(Analyze, SymbolKind.NamedType);
    }

    private static void Analyze(SymbolAnalysisContext context)
    {
        if (context.Symbol is not INamedTypeSymbol type || !IsCapability(type))
        {
            return;
        }

        foreach (var (dependency, location) in Dependencies(type))
        {
            if (IsCapability(dependency))
            {
                // FLOWX1004 — capabilities form a set, not a graph.
                context.ReportDiagnostic(Diagnostic.Create(
                    FlowXDiagnostics.CapabilityInvokesCapability,
                    location,
                    type.Name,
                    dependency.Name));

                continue;
            }

            if (TransportNamespaceOf(dependency) is { } transport)
            {
                // FLOWX1003 — a capability that names a transport can only run behind it.
                context.ReportDiagnostic(Diagnostic.Create(
                    FlowXDiagnostics.CapabilityReferencesTransport,
                    location,
                    type.Name,
                    transport));
            }
        }
    }

    /// <summary>
    /// Every type the capability depends on, with somewhere to point a diagnostic.
    /// </summary>
    /// <remarks>
    /// Constructor parameters, fields and properties — the three ways a dependency is
    /// held. Both documented examples are constructor-injected, which is how a dependency
    /// arrives in practice; a local variable is a use, not a dependency, and is left to
    /// the reader's judgement rather than reported at every call site.
    /// </remarks>
    private static IEnumerable<(INamedTypeSymbol Type, Location Location)> Dependencies(INamedTypeSymbol capability)
    {
        foreach (var constructor in capability.InstanceConstructors)
        {
            foreach (var parameter in constructor.Parameters)
            {
                if (Named(parameter.Type) is { } named)
                {
                    yield return (named, LocationOf(parameter));
                }
            }
        }

        foreach (var member in capability.GetMembers())
        {
            var memberType = member switch
            {
                IFieldSymbol field when !field.IsImplicitlyDeclared => field.Type,
                IPropertySymbol property => property.Type,
                _ => null,
            };

            if (memberType is not null && Named(memberType) is { } named)
            {
                yield return (named, LocationOf(member));
            }
        }
    }

    /// <summary>Unwraps the type a dependency is really about.</summary>
    /// <remarks>
    /// An <c>IEnumerable&lt;ReserveInventory&gt;</c> or a <c>Lazy&lt;HttpContext&gt;</c>
    /// is the same dependency wearing a hat. Checking only the outer type would let a
    /// single generic wrapper defeat both rules.
    /// </remarks>
    private static INamedTypeSymbol? Named(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named)
        {
            return null;
        }

        if (named.IsGenericType && named.TypeArguments.Length == 1)
        {
            return Named(named.TypeArguments[0]) ?? named;
        }

        return named;
    }

    private static Location LocationOf(ISymbol symbol) =>
        symbol.Locations.FirstOrDefault(l => l.IsInSource) ?? Location.None;

    private static bool IsCapability(INamedTypeSymbol type) =>
        type.AllInterfaces.Any(i =>
            i.MetadataName == CapabilityMetadataName &&
            i.ContainingNamespace?.ToDisplayString() == FlowXNamespace);

    /// <summary>The transport namespace this type belongs to, or <c>null</c>.</summary>
    private static string? TransportNamespaceOf(INamedTypeSymbol type)
    {
        if (type.ContainingNamespace is null || type.ContainingNamespace.IsGlobalNamespace)
        {
            return null;
        }

        var ns = type.ContainingNamespace.ToDisplayString();

        foreach (var prefix in TransportNamespaces)
        {
            if (ns.StartsWith(prefix, System.StringComparison.Ordinal))
            {
                return ns;
            }
        }

        // A FlowX namespace that is not one of the core ones is a plugin, and a plugin is
        // a transport. This is what keeps the rule honest for FlowX's own packages
        // without listing each one as it is written.
        if (ns == "FlowX" || ns.StartsWith("FlowX.", System.StringComparison.Ordinal))
        {
            return System.Array.IndexOf(AllowedFlowXNamespaces, ns) >= 0 ? null : ns;
        }

        return null;
    }
}
