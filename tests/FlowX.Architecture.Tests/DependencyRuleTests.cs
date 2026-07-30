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
    /// Constraint C2: every shipped package must stay NativeAOT- and trim-compatible,
    /// which is only enforceable if the analyzers are actually switched on.
    /// </summary>
    [Fact]
    public void EveryShippedProjectIsAotAnalyzed()
    {
        // Directory.Build.props sets these for all of src/. This test guards against a
        // project quietly opting out to silence a warning.
        foreach (var project in RepositoryLayout.SourceProjects)
        {
            var content = File.ReadAllText(project.FullName);

            content.Contains("<IsAotCompatible>false</IsAotCompatible>", StringComparison.OrdinalIgnoreCase)
                .ShouldBeFalse(
                    $"{project.Name} opts out of AOT compatibility, which constraint C2 forbids " +
                    "for shipped packages. Fix the warning instead of disabling the analyzer.");
        }
    }
}
