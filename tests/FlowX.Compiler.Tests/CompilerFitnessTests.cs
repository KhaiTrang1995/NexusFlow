using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using FlowX.Compiler.Diagnostics;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// Fitness functions for the compiler itself — the executable half of the mitigation
/// for risk <strong>R1</strong>, "source-generator complexity becomes the platform's
/// own legacy".
/// </summary>
public sealed class CompilerFitnessTests
{
    private static readonly Assembly Compiler = typeof(FlowXDiagnostics).Assembly;

    /// <summary>
    /// The model layer must stay free of Roslyn.
    /// </summary>
    /// <remarks>
    /// This is the load-bearing rule. The moment a <c>FlowModel</c> holds an
    /// <c>ITypeSymbol</c>, deciding what a flow means requires a compilation, the
    /// emitter can no longer be tested with plain objects, and the generator becomes
    /// the thing R1 warns about. The rule is cheap to state and impossible to keep by
    /// intention alone, which is exactly what a fitness function is for.
    /// </remarks>
    [Fact]
    public void ModelLayerHasNoRoslynDependency()
    {
        var offenders = Compiler.GetTypes()
            .Where(static t => t.Namespace?.StartsWith("FlowX.Compiler.Model", StringComparison.Ordinal) == true)
            .SelectMany(static t => t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Select(member => new { Type = t, Member = member, Referenced = ReferencedTypes(member) }))
            .Where(static x => x.Referenced.Any(static r =>
                r.Namespace?.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal) == true))
            .Select(static x => $"{x.Type.Name}.{x.Member.Name}")
            .ToArray();

        offenders.ShouldBeEmpty(
            "The model layer must be expressible without Roslyn. If it is not, the " +
            "emitter can only be tested through a compilation, and risk R1 has landed.");
    }

    /// <summary>The emitter must stay free of Roslyn for the same reason.</summary>
    [Fact]
    public void EmitLayerHasNoRoslynDependency()
    {
        var offenders = Compiler.GetTypes()
            .Where(static t => t.Namespace?.StartsWith("FlowX.Compiler.Emit", StringComparison.Ordinal) == true)
            .SelectMany(static t => t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Select(member => new { Type = t, Member = member, Referenced = ReferencedTypes(member) }))
            .Where(static x => x.Referenced.Any(static r =>
                r.Namespace?.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal) == true))
            .Select(static x => $"{x.Type.Name}.{x.Member.Name}")
            .ToArray();

        offenders.ShouldBeEmpty("Emission takes a model and returns a string. Nothing else.");
    }

    /// <summary>
    /// Every diagnostic must be actionable without leaving the editor.
    /// </summary>
    /// <remarks>
    /// A diagnostic that says only what is wrong, and not what to do instead, costs
    /// more than the mistake it catches: the developer stops, searches, guesses. The
    /// help URI matters for the same reason — a code with no documentation behind it
    /// is a code people learn to suppress.
    /// </remarks>
    [Fact]
    public void EveryDiagnosticIsHelpful()
    {
        FlowXDiagnostics.All.ShouldNotBeEmpty();

        foreach (var descriptor in FlowXDiagnostics.All)
        {
            var id = descriptor.Id;

            id.ShouldStartWith("FLOWX");
            descriptor.Title.ToString(CultureInfo.InvariantCulture).ShouldNotBeNullOrWhiteSpace($"{id} has no title.");

            descriptor.MessageFormat.ToString(CultureInfo.InvariantCulture).ShouldNotBeNullOrWhiteSpace($"{id} has no message.");
            descriptor.MessageFormat.ToString(CultureInfo.InvariantCulture).ShouldContain(
                "{0}",
                Case.Sensitive,
                $"{id}'s message must name the offending symbol. A diagnostic that does not " +
                "say which symbol it is about makes the developer go looking.");

            var description = descriptor.Description.ToString(CultureInfo.InvariantCulture);
            description.ShouldNotBeNullOrWhiteSpace(
                $"{id} has no description. The description is where the fix goes.");
            description.Length.ShouldBeGreaterThan(
                40,
                $"{id}'s description is too short to explain what to do instead.");

            descriptor.HelpLinkUri.ShouldNotBeNullOrWhiteSpace($"{id} has no help link.");
            descriptor.HelpLinkUri.ShouldEndWith($"{id}.md");
        }
    }

    [Fact]
    public void DiagnosticIdsAreUnique()
    {
        var ids = FlowXDiagnostics.All.Select(static d => d.Id).ToArray();

        ids.Distinct(StringComparer.Ordinal).Count().ShouldBe(ids.Length);
    }

    /// <summary>
    /// Every diagnostic must appear in the release-tracking file.
    /// </summary>
    /// <remarks>
    /// Roslyn's RS2008 already enforces this at build time. Asserting it again here is
    /// not redundant: it produces a readable failure naming the missing id, whereas the
    /// analyzer failure is a wall of identical messages. The build gate stops the
    /// mistake; this one explains it.
    /// </remarks>
    [Fact]
    public void EveryDiagnosticIsReleaseTracked()
    {
        var tracking = File.ReadAllText(FindRepositoryFile("src/FlowX.Compiler/AnalyzerReleases.Unshipped.md"))
            + File.ReadAllText(FindRepositoryFile("src/FlowX.Compiler/AnalyzerReleases.Shipped.md"));

        var missing = FlowXDiagnostics.All
            .Select(static d => d.Id)
            .Where(id => !tracking.Contains(id, StringComparison.Ordinal))
            .ToArray();

        missing.ShouldBeEmpty(
            "A diagnostic id is public surface — teams write suppressions against it — " +
            "so adding one must be a reviewable diff in AnalyzerReleases.Unshipped.md.");
    }

    /// <summary>Diagnostics are errors, not warnings, when they encode a safety rule.</summary>
    [Fact]
    public void SafetyDiagnosticsAreErrorsRatherThanWarnings()
    {
        var safetyRules = new[] { "FLOWX1010", "FLOWX1014", "FLOWX1017", "FLOWX1018" };

        foreach (var id in safetyRules)
        {
            var descriptor = FlowXDiagnostics.All.Single(d => d.Id == id);

            descriptor.DefaultSeverity.ShouldBe(
                Microsoft.CodeAnalysis.DiagnosticSeverity.Error,
                $"{id} encodes a safety rule. A warning is a rule nobody has to obey.");
        }
    }

    private static Type[] ReferencedTypes(MemberInfo member) => member switch
    {
        PropertyInfo property => [property.PropertyType],
        FieldInfo field => [field.FieldType],
        MethodInfo method => [method.ReturnType, .. method.GetParameters().Select(static p => p.ParameterType)],
        ConstructorInfo ctor => [.. ctor.GetParameters().Select(static p => p.ParameterType)],
        _ => [],
    };

    private static string FindRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);

            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate '{relativePath}' from {AppContext.BaseDirectory}.");
    }
}
