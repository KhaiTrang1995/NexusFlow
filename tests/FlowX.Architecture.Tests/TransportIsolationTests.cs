using Mono.Cecil;
using Shouldly;
using Xunit;

namespace FlowX.Architecture.Tests;

/// <summary>
/// P3's rule that a flow cannot see its transport, and P2's rule that capabilities form a
/// set rather than a graph — both named in docs/05-Architecture.md §12 and neither present.
/// </summary>
/// <remarks>
/// <para>
/// Analyzers cover the neighbouring cases and stop short of these. FLOWX1003 reports a
/// <em>capability</em> that holds a transport dependency; FLOWX1004 reports a
/// <em>capability</em> that holds another capability. Both read declared dependencies —
/// constructor parameters, fields, properties — which is the right choice for a diagnostic
/// that has to point somewhere a developer can fix, and it leaves two things unchecked.
/// </para>
/// <para>
/// The first is the flow itself, and everything it reaches. P3 is stated over a flow's
/// <em>transitive closure</em>: the flow may hold nothing transport-shaped and still reach
/// a helper that does, at which point the flow can no longer move to Kafka and nobody finds
/// out until they try. The second is use rather than dependency: a capability that writes
/// <c>new ValidateOrder().ExecuteAsync(…)</c> inside a method body declares no dependency at
/// all, and FLOWX1004 says nothing.
/// </para>
/// <para>
/// Both are answered from IL, so what is checked is what shipped — including the half of
/// each flow the generator wrote.
/// </para>
/// </remarks>
public sealed class TransportIsolationTests
{
    private const string CapabilityInterface = "FlowX.ICapability`2";
    private const string FlowBaseType = "FlowX.Flow`2";

    /// <summary>
    /// Namespace prefixes that mean "transport".
    /// </summary>
    /// <remarks>
    /// Deliberately the same list as <c>CapabilityAnalyzer.TransportNamespaces</c>, and
    /// deliberately still <strong>a list, not a proof</strong> — see
    /// docs/diagnostics/FLOWX1003.md. Two copies of a list is a real cost; the alternative
    /// is for this project to reference the analyzer, which targets <c>netstandard2.0</c>
    /// and is forbidden from being referenced at run time by
    /// <c>RoslynComponentsReferenceNoRuntimeAssemblies</c>. Keeping them equal is checked by
    /// <see cref="TheTransportListMatchesTheAnalyzers"/>.
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
    /// The FlowX namespaces a flow may reach. Everything else under <c>FlowX.</c> is a
    /// plugin or a host, and both are transports as far as P3 is concerned.
    /// </summary>
    private static readonly string[] AllowedFlowXNamespaces =
    [
        "FlowX",
        "FlowX.Runtime",
        "FlowX.Generated",
    ];

    /// <summary>
    /// Principle P3: nothing a flow reaches knows how the flow was triggered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The promise is that changing HTTP → Kafka → cron is an attribute change. That holds
    /// exactly as long as no type in the flow's closure names a transport; the first one
    /// that does converts the promise into a migration.
    /// </para>
    /// <para>
    /// The closure is bounded at the assembly the flow is declared in: every type of that
    /// assembly the flow transitively reaches, plus everything those types directly name.
    /// Walking further would descend into the BCL and answer a question about
    /// <c>System.Private.CoreLib</c> instead. That boundary is where the rule bites anyway —
    /// a transport arrives in an application's own types, not in someone else's.
    /// </para>
    /// </remarks>
    [Fact]
    public void FlowsAreTransportFree()
    {
        var flows = 0;
        var findings = new List<string>();

        foreach (var assembly in CompiledAssemblies.ShippingAssemblies)
        {
            using var module = CompiledAssemblies.Read(assembly);

            foreach (var flow in IlSurvey.AllTypes(module).Where(IsFlow))
            {
                flows++;
                findings.AddRange(TransportsReachableFrom(module, flow));
            }
        }

        flows.ShouldBeGreaterThan(
            0,
            "No type deriving from Flow<,> was found in any shipping assembly, so this gate " +
            "inspected nothing. Either the base type moved, or the sample stopped being built.");

        findings.ShouldBeEmpty(
            "A flow reaches a transport. The same flow can no longer run behind HTTP, a bus " +
            "and a schedule unchanged, and quality goal Q4 is gone (principle P3):" +
            Environment.NewLine + string.Join(Environment.NewLine, findings));
    }

    /// <summary>
    /// The closure walk finds a transport when there is one to find.
    /// </summary>
    /// <remarks>
    /// The gate above passes today, and a gate that passes is indistinguishable from a gate
    /// that cannot see. <c>Ecommerce.Program</c> is the sample's composition root: it maps an
    /// HTTP endpoint, so it reaches <c>FlowX.Http</c> and <c>Microsoft.AspNetCore</c> by
    /// design and correctly — a composition root is the one place a transport belongs. If
    /// the same walk started there and came back empty, it is not walking.
    /// </remarks>
    [Fact]
    public void TheClosureWalkFindsATransportWhenThereIsOne()
    {
        using var module = CompiledAssemblies.Read("Ecommerce");

        var composition = IlSurvey.AllTypes(module).SingleOrDefault(static t => t.Name == "Program");

        composition.ShouldNotBeNull(
            "Ecommerce.Program has moved. It is this gate's known-positive; without it, " +
            "FlowsAreTransportFree has nothing proving it can fail.");

        TransportsReachableFrom(module, composition!).ShouldNotBeEmpty(
            "The closure walk found no transport in the sample's composition root, which " +
            "maps an HTTP endpoint. FlowsAreTransportFree is now passing vacuously.");
    }

    /// <summary>
    /// Principle P2 / §2: a capability never invokes another capability.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Capabilities are a set, not a graph. That is what makes each one testable by
    /// constructing it and calling the method, and it is what makes the manifest a complete
    /// description of what calls what — a capability that calls a capability creates an edge
    /// no flow declared and no manifest records.
    /// </para>
    /// <para>
    /// Wider than FLOWX1004 in exactly one way, and it is the way that matters: this reads
    /// method bodies. A held dependency is what the analyzer sees and what a reviewer sees;
    /// a <c>new</c> inside a method is neither.
    /// </para>
    /// </remarks>
    [Fact]
    public void CapabilitiesDoNotCallCapabilities()
    {
        var findings = new List<string>();

        foreach (var assembly in CompiledAssemblies.ShippingAssemblies)
        {
            using var module = CompiledAssemblies.Read(assembly);

            var capabilities = Capabilities(module);

            foreach (var capability in capabilities)
            {
                var others = IlSurvey.UsagesDeep(capability)
                    .Select(static usage => usage.UsedName)
                    .Where(name => capabilities.Any(other =>
                        other.FullName.Equals(name, StringComparison.Ordinal)
                        && !other.FullName.Equals(capability.FullName, StringComparison.Ordinal)))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                findings.AddRange(others.Select(other => $"{capability.FullName} reaches {other}"));
            }
        }

        findings.ShouldBeEmpty(
            "A capability depends on another capability. Compose them in a flow instead — " +
            "capabilities form a set, not a graph, and an edge no flow declared is an edge " +
            "the manifest cannot describe (FLOWX1004):" +
            Environment.NewLine + string.Join(Environment.NewLine, findings));
    }

    /// <summary>
    /// The IL survey finds the capabilities the sample ships.
    /// </summary>
    /// <remarks>
    /// Same reason as <c>TheCapabilitySurveyFindsTheShippedCapabilities</c>, one layer down:
    /// that one guards the source scan, this one guards the metadata scan. An interface name
    /// that changed arity, a module that failed to open, a tree that stopped being built —
    /// each empties the set, and an empty set contains no capability calling another.
    /// </remarks>
    [Fact]
    public void TheCapabilityScanFindsTheShippedCapabilities()
    {
        using var module = CompiledAssemblies.Read("Ecommerce");

        Capabilities(module).Select(static c => c.Name).ShouldBe(
            ["CapturePayment", "ReleaseInventory", "RepriceBasket", "ReserveInventory", "ValidateOrder"],
            ignoreOrder: true,
            "The IL scan no longer finds the sample's capabilities, so " +
            "CapabilitiesDoNotCallCapabilities is passing vacuously.");
    }

    /// <summary>
    /// This gate's transport list and the analyzer's are the same list.
    /// </summary>
    /// <remarks>
    /// The copy exists because a <c>netstandard2.0</c> Roslyn component cannot be referenced
    /// from a test that runs on <c>net10.0</c> — <c>RoslynComponentsReferenceNoRuntimeAssemblies</c>
    /// is the other half of that rule. A copy that drifts is worse than a copy: the analyzer
    /// would reject a namespace this gate waves through, and the two would disagree about
    /// what a transport is. Compared against the analyzer's source text, which is the
    /// definition.
    /// </remarks>
    [Fact]
    public void TheTransportListMatchesTheAnalyzers()
    {
        var analyzer = new FileInfo(Path.Combine(
            RepositoryLayout.Root.FullName,
            "src", "FlowX.Compiler", "Analysis", "CapabilityAnalyzer.cs"));

        analyzer.Exists.ShouldBeTrue($"{analyzer.FullName} is where the transport list is defined.");

        var text = File.ReadAllText(analyzer.FullName);

        foreach (var transport in TransportNamespaces)
        {
            text.ShouldContain(
                $"\"{transport}\"",
                Case.Sensitive,
                $"CapabilityAnalyzer no longer lists '{transport}'. The analyzer's list is " +
                "the definition; this copy has drifted, and the two now disagree about what " +
                "a transport is.");
        }
    }

    // ------------------------------------------------------------------ helpers

    private static bool IsFlow(TypeDefinition type)
    {
        for (var current = type.BaseType; current is not null;)
        {
            if (current.GetElementType().FullName.Equals(FlowBaseType, StringComparison.Ordinal))
            {
                return true;
            }

            current = current is TypeDefinition definition ? definition.BaseType : null;
        }

        return false;
    }

    private static List<TypeDefinition> Capabilities(ModuleDefinition module) =>
        IlSurvey.AllTypes(module)
            .Where(static type => type.Interfaces.Any(static i =>
                i.InterfaceType.GetElementType().FullName.Equals(CapabilityInterface, StringComparison.Ordinal)))
            .ToList();

    /// <summary>
    /// Every transport reachable from a type, by way of the types its own assembly declares.
    /// </summary>
    private static List<string> TransportsReachableFrom(ModuleDefinition module, TypeDefinition root)
    {
        var findings = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { root.FullName };
        var pending = new Queue<TypeDefinition>();

        pending.Enqueue(root);

        while (pending.Count > 0)
        {
            var type = pending.Dequeue();

            // Nested types are part of the type, not something it reaches: the generated
            // dispatcher lives inside the flow, and an async method's body lives in a state
            // machine the compiler nested there. A transport in either is in the flow.
            foreach (var usage in IlSurvey.UsagesDeep(type))
            {
                if (TransportNamespaceOf(usage.UsedNamespace) is { } transport)
                {
                    findings.Add(
                        $"{root.FullName} reaches {usage.UsedName} via {usage.Member} ({transport})");

                    continue;
                }

                if (module.GetType(usage.UsedName) is { } local && seen.Add(local.FullName))
                {
                    pending.Enqueue(local);
                }
            }
        }

        return findings.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The transport namespace a type belongs to, or <c>null</c>. Matched exactly as
    /// <c>CapabilityAnalyzer.TransportNamespaceOf</c> matches it.
    /// </summary>
    private static string? TransportNamespaceOf(string @namespace)
    {
        if (@namespace.Length == 0)
        {
            return null;
        }

        if (TransportNamespaces.Any(prefix => @namespace.StartsWith(prefix, StringComparison.Ordinal)))
        {
            return @namespace;
        }

        // A FlowX namespace that is not one of the core ones is a plugin, and a plugin is
        // reached from a composition root — never from a flow.
        if (@namespace is "FlowX" || @namespace.StartsWith("FlowX.", StringComparison.Ordinal))
        {
            return AllowedFlowXNamespaces.Contains(@namespace, StringComparer.Ordinal) ? null : @namespace;
        }

        return null;
    }
}
