using System.Reflection;
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
    /// <remarks>
    /// <para>
    /// <strong>One row names a project that does not exist, and it is kept deliberately.</strong>
    /// <c>FlowX.Runtime.Durable</c> is planned (docs/05-Architecture.md §5.3) and unbuilt;
    /// <see cref="RuntimeDoesNotReferenceAnyPlugin"/> already allows the same name for the
    /// same reason. The row is inert — the theory returns early when the project is absent
    /// — so today it asserts nothing, and it begins asserting on the commit that adds the
    /// project rather than on some later commit where somebody remembers to widen the list.
    /// That is the whole point of writing these rules before the code they govern, stated
    /// at the top of this file.
    /// </para>
    /// <para>
    /// The cost is that a reader can mistake a row for evidence the project exists, which
    /// is the same failure the "Lives in" column of docs/05 §12 was rewritten to remove.
    /// Hence this comment, and hence the row is the only forward declaration here: one is
    /// a decision, a habit of them is a wish list.
    /// </para>
    /// <para>
    /// <see cref="EverySourceProjectIsCoveredByTheLayeringRule"/> does not object to it,
    /// and that is the behaviour wanted rather than a hole. It checks one direction — every
    /// project under <c>src/</c> is named here — because that is the direction in which
    /// something can be missed silently. A row naming nothing cannot hide a project; a
    /// <em>mistyped</em> row cannot either, because the real project then goes unnamed and
    /// that test fails. What the one-way check does not catch is a row left behind after a
    /// project is deleted, which is a stale comment rather than a lost gate.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("FlowX.Abstractions", new string[0])]
    [InlineData("FlowX.Core", new[] { "FlowX.Abstractions" })]
    [InlineData("FlowX.Runtime", new[] { "FlowX.Abstractions", "FlowX.Core" })]
    // Forward declaration: FlowX.Runtime.Durable does not exist yet. See the remarks above
    // before adding a second row like this one.
    [InlineData("FlowX.Runtime.Durable", new[] { "FlowX.Abstractions", "FlowX.Core", "FlowX.Runtime" })]
    [InlineData("FlowX.Hosting", new[] { "FlowX.Abstractions", "FlowX.Core", "FlowX.Runtime" })]
    // FlowX.Testing gained Core and Runtime with FlowTestHost (WP-49), which runs the
    // real engine over the real compiled plan inside the test process. Still points
    // inward, and still narrower than Hosting: no transport, no journal, no container.
    [InlineData("FlowX.Testing", new[] { "FlowX.Abstractions", "FlowX.Core", "FlowX.Runtime" })]
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
    /// Every project under <c>src/</c> is named by <see cref="LayersPointInward"/>.
    /// </summary>
    /// <remarks>
    /// The layering rule enumerates its projects by hand, which means a project added
    /// later is not checked — it simply is not in the list, and the theory passes without
    /// ever looking at it. Adding <c>FlowX.Testing</c> is what made that visible: the new
    /// project could have referenced anything at all and no fitness function would have
    /// noticed. A rule with a hand-maintained subject list needs a rule about the list.
    /// </remarks>
    [Fact]
    public void EverySourceProjectIsCoveredByTheLayeringRule()
    {
        // Read the attribute's constructor arguments rather than calling GetData: the
        // latter needs a MethodInfo and a DisposalTracker, and this only needs the names.
        var covered = typeof(DependencyRuleTests)
            .GetMethod(nameof(LayersPointInward))!
            .GetCustomAttributesData()
            .Where(a => a.AttributeType == typeof(InlineDataAttribute))
            .Select(a => (IReadOnlyList<CustomAttributeTypedArgument>)a.ConstructorArguments[0].Value!)
            .Select(args => (string)args[0].Value!)
            .ToHashSet(StringComparer.Ordinal);

        var uncovered = RepositoryLayout.SourceProjects
            // A Roslyn component cannot reference the runtime at all — it targets
            // netstandard2.0 — so the layering rule has nothing to say about it, and
            // RoslynComponentsTargetNetStandard20 covers it instead. Read off the
            // project rather than matched against the name "FlowX.Compiler": the
            // exemption was written when there was one Roslyn component, and the second
            // one would otherwise have slipped past this rule in exactly the way the
            // rule exists to prevent.
            .Where(static p => !RepositoryLayout.IsRoslynComponent(p))
            .Select(static p => Path.GetFileNameWithoutExtension(p.Name))
            .Where(name => !covered.Contains(name))
            // The CLI has its own stricter rule: it references no FlowX assembly.
            .Where(name => name != "FlowX.Cli")
            .ToList();

        uncovered.ShouldBeEmpty(
            $"[{string.Join(", ", uncovered)}] is under src/ but not named by " +
            "LayersPointInward, so nothing checks what it may reference. Add an " +
            "[InlineData] row for it.");
    }

    /// <summary>
    /// <c>FlowX.Cli.csproj</c> has no <c>ProjectReference</c>: the CLI links no FlowX
    /// assembly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The strongest available evidence that the manifest is genuinely self-describing
    /// (ADR-0005). The CLI is its first consumer that is not the compiler; if it needed
    /// to import a FlowX type to make sense of the file, the manifest would be an
    /// internal serialisation format wearing a contract's clothes, and no third-party
    /// tool could consume it either.
    /// </para>
    /// <para>
    /// <strong>This counts links, not inputs, and the name says so on purpose.</strong>
    /// It was called <c>CliDependsOnNothingButTheManifest</c> until
    /// <a href="../../docs/adr/ADR-0020-cli-reads-the-journal-as-rows.md">ADR-0020</a>,
    /// which is a stronger claim than the assertion below has ever made — and one that was
    /// already false when the CLI shipped, because <c>flowx manifest --assembly</c> reads a
    /// built assembly and <c>flowx diff</c> reads two arbitrary files. ADR-0020 then added
    /// a journal as a second input via a <c>PackageReference</c> on <c>Npgsql</c>, which
    /// this rule does not count and must not be read as forbidding. The gap between the old
    /// name and the assertion is what put a false collision between <c>flowx replay</c> and
    /// this gate into the plan; the name is now the assertion, so there is nothing left to
    /// misread.
    /// </para>
    /// <para>
    /// "The CLI runs against an artifact with no database" is <em>not</em> asserted here and
    /// never was. It has its own test — <c>EveryVerbButReplayRunsWithNoStore</c>, in
    /// <c>tests/FlowX.Cli.Tests</c> — per ADR-0020 §3.
    /// </para>
    /// </remarks>
    [Fact]
    public void CliLinksNoFlowXAssembly()
    {
        var cli = RepositoryLayout.SourceProjects
            .SingleOrDefault(static p => p.Name == "FlowX.Cli.csproj");

        if (cli is null)
        {
            return;
        }

        RepositoryLayout.ProjectReferences(cli).ShouldBeEmpty(
            "FlowX.Cli must consume the manifest exactly as a third-party tool would. " +
            "A project reference here would prove the document is not self-describing. " +
            "This rule counts project links only: a PackageReference (Npgsql, " +
            "System.Reflection.MetadataLoadContext) is not a violation, and neither is " +
            "reading a second published contract as data (ADR-0020).");
    }

    /// <summary>
    /// A Roslyn component cannot reference the runtime assemblies at all.
    /// </summary>
    /// <remarks>
    /// It targets netstandard2.0 and loads into the compiler process; the runtime
    /// targets net10.0 and loads into the user's application. The generator emits source
    /// that references FlowX.Core — it never links against it.
    /// <para>
    /// Stated as a test because the mistake is easy and its symptom is confusing: adding
    /// the reference appears to work locally and then fails to load in Visual Studio,
    /// where the generator silently produces nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public void RoslynComponentsReferenceNoRuntimeAssemblies()
    {
        foreach (var project in RepositoryLayout.SourceProjects)
        {
            if (!RepositoryLayout.IsRoslynComponent(project))
            {
                continue;
            }

            RepositoryLayout.ProjectReferences(project).ShouldBeEmpty(
                $"{project.Name} is a Roslyn component and must not reference the runtime " +
                "assemblies. It emits source that references them; it does not link against " +
                "them. A project reference here loads fine under `dotnet build` and makes the " +
                "generator do nothing inside Visual Studio.");
        }
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
                .Where(static r => r is not (
                    "FlowX.Abstractions" or "FlowX.Core" or "FlowX.Runtime"
                    or "FlowX.Runtime.Durable" or "FlowX.Hosting"))
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
            if (RepositoryLayout.IsRoslynComponent(project))
            {
                continue;
            }

            File.ReadAllText(project.FullName)
                .Contains("<IsAotCompatible>false</IsAotCompatible>", StringComparison.OrdinalIgnoreCase)
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
            if (!RepositoryLayout.IsRoslynComponent(project))
            {
                continue;
            }

            File.ReadAllText(project.FullName)
                .Contains("<TargetFramework>netstandard2.0</TargetFramework>", StringComparison.OrdinalIgnoreCase)
                .ShouldBeTrue(
                    $"{project.Name} is a Roslyn component but does not target netstandard2.0. " +
                    "It will load under `dotnet build` and do nothing in Visual Studio.");
        }
    }
}
