using System;
using System.IO;
using System.Linq;
using Shouldly;
using Xunit;

namespace FlowX.Functions.Tests;

/// <summary>
/// The constraint that the trigger semantics come from one reading, checked rather than
/// intended.
/// </summary>
/// <remarks>
/// <para>
/// <c>FlowX.Functions.csproj</c> links <c>TriggerReader.cs</c> out of <c>FlowX.Compiler</c>
/// rather than referencing it, and the reason is packaging: a Roslyn component ships as one
/// assembly and nothing puts a project reference's output beside it. The risk that arrangement
/// carries is that a link is one edit away from becoming a copy — and a copied reader would
/// pass every other test in this suite while drifting from the one the manifest is written by.
/// </para>
/// <para>
/// So this asserts the mechanism: the file is included from the compiler's directory, and no
/// file with that name exists in this project.
/// </para>
/// </remarks>
public sealed class TriggerSemanticsAreOneReadingTests
{
    /// <summary>The reader is linked out of the compiler, and not copied into this project.</summary>
    [Fact]
    public void TheTriggerReaderIsLinkedFromTheCompilerRatherThanCopied()
    {
        var project = File.ReadAllText(Path.Combine(GeneratorDirectory, "FlowX.Functions.csproj"));

        project.ShouldContain(
            """<Compile Include="../FlowX.Compiler/Analysis/TriggerReader.cs" """,
            customMessage: "the whole of WP-141's one-reading constraint is this line. A " +
            "TriggerReader.cs of this project's own would map kinds that the manifest does " +
            "not, and nothing would fail until a deployment served an address nobody published.");

        Directory
            .EnumerateFiles(GeneratorDirectory, "TriggerReader.cs", SearchOption.AllDirectories)
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ShouldBeEmpty("there is one TriggerReader.cs in this repository and it is the compiler's.");
    }

    /// <summary>
    /// Every kind the reader can return is either bound or recorded as unbound.
    /// </summary>
    /// <remarks>
    /// <strong>This is the drift the fallback exists to make impossible.</strong> A
    /// <c>TriggerKind</c> added to the abstraction reaches this generator through the linked
    /// reader, and without this test it would reach the default arm and be recorded with a
    /// generic reason — which is correct behaviour and a silent one. Asserting the whole
    /// enumeration here means adding a kind is a decision somebody makes on purpose.
    /// </remarks>
    [Fact]
    public void EveryKindTheReaderCanReturnIsAccountedFor()
    {
        var kinds = Enumerable.Range(0, 8)
            .Select(FlowX.Compiler.Analysis.TriggerReader.ManifestKindName)
            .ToArray();

        kinds.ShouldAllBe(static name => name != null);

        kinds.ShouldBe(
            ["Manual", "Http", "Bus", "Schedule", "Stream", "Change", "Agent", "Cli"],
            ignoreOrder: false);

        FlowX.Compiler.Analysis.TriggerReader.ManifestKindName(8).ShouldBeNull(
            "a value outside TriggerKind is not a declared kind, and this generator's " +
            "default arm is what a plugin's unreadable declaration reaches.");
    }

    /// <summary>Where this test assembly can find the generator's project directory.</summary>
    /// <remarks>
    /// Walked up from the test binary rather than passed in, for the reason the architecture
    /// suite walks: a path baked at build time is a path that is wrong the first time somebody
    /// changes the output layout.
    /// </remarks>
    private static string GeneratorDirectory
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null &&
                !Directory.Exists(Path.Combine(directory.FullName, "src", "FlowX.Functions")))
            {
                directory = directory.Parent;
            }

            directory.ShouldNotBeNull("the repository root is above the test binary.");

            return Path.Combine(directory.FullName, "src", "FlowX.Functions");
        }
    }
}
