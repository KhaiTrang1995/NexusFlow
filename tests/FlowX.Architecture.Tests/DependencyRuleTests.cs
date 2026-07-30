using Shouldly;
using Xunit;

namespace FlowX.Architecture.Tests;

/// <summary>
/// Executable form of the layering rules in docs/05-Architecture.md §5.1 and §12.
/// These are written before the projects they govern, so a violation is impossible to
/// introduce accidentally.
/// </summary>
public sealed class DependencyRuleTests
{
    /// <summary>
    /// ADR-0009: <c>FlowX.Abstractions</c> is what user code and every plugin reference.
    /// A dependency here propagates into every consumer of the platform, so the answer
    /// is always no — if you need one, you are designing the wrong thing.
    /// </summary>
    [Fact]
    public void AbstractionsHasNoDependencies()
    {
        var abstractions = RepositoryLayout.SourceProjects
            .SingleOrDefault(static p => p.Name == "FlowX.Abstractions.csproj");

        abstractions.ShouldNotBeNull("FlowX.Abstractions.csproj must exist under src/.");

        RepositoryLayout.PackageReferences(abstractions!).ShouldBeEmpty(
            "FlowX.Abstractions must have zero package references (ADR-0009). " +
            "It is referenced by every plugin and by all user code; a dependency here " +
            "is inherited by everyone.");

        RepositoryLayout.ProjectReferences(abstractions!).ShouldBeEmpty(
            "FlowX.Abstractions must have zero project references — it is the bottom of the graph.");
    }

    /// <summary>
    /// Dependencies point inward: Abstractions ← Core ← Runtime ← Runtime.Durable.
    /// Nothing in <c>src/</c> may reference a plugin, and no lower layer may reference
    /// a higher one.
    /// </summary>
    [Theory]
    [InlineData("FlowX.Abstractions", new string[0])]
    [InlineData("FlowX.Core", new[] { "FlowX.Abstractions" })]
    [InlineData("FlowX.Runtime", new[] { "FlowX.Abstractions", "FlowX.Core" })]
    [InlineData("FlowX.Runtime.Durable", new[] { "FlowX.Abstractions", "FlowX.Core", "FlowX.Runtime" })]
    public void LayersPointInward(string projectName, string[] allowedReferences)
    {
        var project = RepositoryLayout.SourceProjects
            .SingleOrDefault(p => p.Name == $"{projectName}.csproj");

        if (project is null)
        {
            // The project has not been built yet. The rule still stands; it simply has
            // nothing to check. Skipping silently here is correct — asserting existence
            // is AbstractionsHasNoDependencies' job for the one project that must exist.
            return;
        }

        var actual = RepositoryLayout.ProjectReferences(project);

        actual.ShouldBeSubsetOf(
            allowedReferences,
            $"{projectName} may only reference [{string.Join(", ", allowedReferences)}]. " +
            "Dependencies point inward, toward the domain (docs/05-Architecture.md §5.1).");
    }

    /// <summary>
    /// Quality goal Q6: a new transport is added without touching the runtime. That only
    /// holds if the runtime never references a plugin.
    /// </summary>
    [Fact]
    public void RuntimeDoesNotReferenceAnyPlugin()
    {
        foreach (var project in RepositoryLayout.SourceProjects)
        {
            var references = RepositoryLayout.ProjectReferences(project);

            var pluginReferences = references
                .Where(static r => r.StartsWith("FlowX.", StringComparison.Ordinal))
                .Where(static r => r is not ("FlowX.Abstractions" or "FlowX.Core" or "FlowX.Runtime" or "FlowX.Runtime.Durable"))
                .ToList();

            pluginReferences.ShouldBeEmpty(
                $"{project.Name} references a plugin. Plugins depend on the runtime, " +
                "never the other way round — otherwise adding a transport means changing " +
                "FlowX.Runtime, and quality goal Q6 is lost.");
        }
    }

    /// <summary>
    /// No dependency cycle between any two projects. MSBuild rejects a direct cycle,
    /// but it happily accepts A → B → C → A, which is the shape that actually occurs
    /// once a codebase has enough projects to lose track.
    /// </summary>
    [Fact]
    public void NoCyclicDependencies()
    {
        var edges = RepositoryLayout.SourceProjects.ToDictionary(
            static p => Path.GetFileNameWithoutExtension(p.Name),
            static p => RepositoryLayout.ProjectReferences(p),
            StringComparer.Ordinal);

        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var settled = new HashSet<string>(StringComparer.Ordinal);

        foreach (var project in edges.Keys)
        {
            var cycle = FindCycle(project, edges, visiting, settled, []);

            cycle.ShouldBeNull(
                $"Dependency cycle: {string.Join(" -> ", cycle ?? [])}. Dependencies point " +
                "inward, toward the domain, and a cycle means two projects have become one " +
                "with extra build steps.");
        }
    }

    private static List<string>? FindCycle(
        string node,
        Dictionary<string, List<string>> edges,
        HashSet<string> visiting,
        HashSet<string> settled,
        List<string> path)
    {
        if (settled.Contains(node))
        {
            return null;
        }

        path.Add(node);

        if (!visiting.Add(node))
        {
            return path;
        }

        if (edges.TryGetValue(node, out var references))
        {
            foreach (var reference in references)
            {
                var cycle = FindCycle(reference, edges, visiting, settled, path);

                if (cycle is not null)
                {
                    return cycle;
                }
            }
        }

        visiting.Remove(node);
        settled.Add(node);
        path.RemoveAt(path.Count - 1);
        return null;
    }

    /// <summary>
    /// Constraint C2: every shipped <em>runtime</em> package must stay NativeAOT- and
    /// trim-compatible, which is only enforceable if the analyzers are switched on.
    /// </summary>
    /// <remarks>
    /// Roslyn components are exempt, and the exemption is narrow on purpose. An
    /// analyzer or source generator loads into the compiler process, targets
    /// netstandard2.0 and never ships inside the user's application — NativeAOT has no
    /// meaning for it. The exemption is keyed on <c>&lt;IsRoslynComponent&gt;</c>
    /// rather than on a project name, so it cannot be claimed by a runtime assembly
    /// that simply wants the warning to go away.
    /// <para>
    /// This rule originally had no exemption and failed the moment FlowX.Compiler
    /// arrived. The rule was wrong, not the project: it conflated "shipped" with
    /// "shipped into the user's process".
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryShippedRuntimeProjectIsAotAnalyzed()
    {
        foreach (var project in RepositoryLayout.SourceProjects)
        {
            var content = File.ReadAllText(project.FullName);

            if (content.Contains("<IsRoslynComponent>true</IsRoslynComponent>", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            content.Contains("<IsAotCompatible>false</IsAotCompatible>", StringComparison.OrdinalIgnoreCase)
                .ShouldBeFalse(
                    $"{project.Name} opts out of AOT compatibility, which constraint C2 forbids " +
                    "for packages that ship into a user's process. Fix the warning instead of " +
                    "disabling the analyzer. If this is a Roslyn component, declare " +
                    "<IsRoslynComponent>true</IsRoslynComponent>.");
        }
    }

    /// <summary>
    /// A Roslyn component must target netstandard2.0, or it silently does nothing in
    /// Visual Studio.
    /// </summary>
    /// <remarks>
    /// The failure mode is the reason this is a test. A generator built for net10.0
    /// loads fine under <c>dotnet build</c> and never runs inside VS, so the code it
    /// should have produced is simply absent — and the developer sees "type not found"
    /// with no explanation anywhere.
    /// </remarks>
    [Fact]
    public void RoslynComponentsTargetNetStandard20()
    {
        foreach (var project in RepositoryLayout.SourceProjects)
        {
            var content = File.ReadAllText(project.FullName);

            if (!content.Contains("<IsRoslynComponent>true</IsRoslynComponent>", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            content.Contains("<TargetFramework>netstandard2.0</TargetFramework>", StringComparison.OrdinalIgnoreCase)
                .ShouldBeTrue(
                    $"{project.Name} is a Roslyn component but does not target netstandard2.0. " +
                    "It will load under `dotnet build` and do nothing in Visual Studio.");
        }
    }
}
