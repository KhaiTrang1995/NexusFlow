using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using FlowX.Compiler.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// Compiles the flow declaration <c>docs/09-Trigger-Model.md</c> §9 prints.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The page printed a <c>.Window(…)</c> / <c>.Aggregate(…)</c> surface
/// <c>IFlowBuilder</c> has never had, and a <c>Lateness</c> the host refuses at startup, from the
/// day it was written until
/// <a href="../../docs/adr/ADR-0065-a-window-is-declared-where-it-is-served.md">ADR-0065</a>.</strong>
/// Three documents and a sample remarked on it; nothing compiled it. This test compiles the bytes
/// on the page — read out of the file at run time, not pasted here — so the example and the
/// builder cannot drift apart again without a red build.
/// </para>
/// <para>
/// <strong>Scoped to §9 rather than to the whole page</strong>, unlike
/// <see cref="GettingStartedTests"/>, which requires a <c>verify:</c> marker on every fenced block
/// of docs/24. That page is a tutorial whose every block is a thing a reader types; this one is a
/// reference whose blocks are mostly interface excerpts, HTTP transcripts and Mermaid. Marking all
/// of them would be a large edit to a document to make a small claim about one part of it. §9 is
/// the part that was wrong.
/// </para>
/// <para>
/// <strong>The heading is the anchor, so renaming it fails this test rather than silencing
/// it.</strong> <see cref="TheSectionIsFound"/> is what says so.
/// </para>
/// </remarks>
public sealed class TriggerModelDocTests
{
    private const string Page = "docs/09-Trigger-Model.md";

    private const string Section = "## 9. Stream trigger";

    /// <summary>The path the block is compiled under, so a diagnostic can be traced to it.</summary>
    private const string BlockPath = "/docs/09/stream-trigger.cs";

    /// <summary>
    /// What a reader of §9 already has: the contracts and capabilities the example names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The page shows a flow declaration rather than a file, because §9 is about what a trigger
    /// binds. Everything the declaration reaches is here, and nothing else is — in particular
    /// there is no stand-in for a builder method, so a page that grew one back would not compile.
    /// </para>
    /// <para>
    /// <c>FoldReadings</c> is the shape the argument in ADR-0065 turns on: the fold over a closed
    /// window's records is a named capability with a contract, not a lambda in the flow's graph.
    /// </para>
    /// <para>
    /// <strong><c>TelemetryJson</c> is written out rather than source-generated.</strong> Only
    /// <c>FlowPlanGenerator</c> runs over this compilation, so the STJ generator's half of a
    /// <c>partial</c> context is never emitted and the class would not bind. What the emitted plan
    /// needs from it is <c>Default</c>; what <c>FLOWX1006</c> needs is the
    /// <c>[JsonSerializable]</c> declarations, and those are real.
    /// </para>
    /// </remarks>
    private const string Header = """
        using System;
        using System.Text.Json.Serialization;
        using System.Threading;
        using System.Threading.Tasks;
        using FlowX;

        namespace Telemetry;

        """;

    private const string Preamble = Header + """
        public sealed record DeviceStats(int Readings, double MeanCelsius);

        public sealed record PersistedAggregate(string Key);

        public sealed record AggregateComputed(string Source, DateTimeOffset WindowStart, int Readings);

        [Capability("telemetry.fold", Version = "1.0.0", Authorization = Authorization.Internal)]
        public sealed class FoldReadings : ICapability<StreamWindowBatch, DeviceStats>
        {
            public ValueTask<Result<DeviceStats>> ExecuteAsync(
                StreamWindowBatch input, CapabilityContext ctx, CancellationToken ct) =>
                ValueTask.FromResult(Result.Ok(new DeviceStats(input.Records.Count, 0d)));
        }

        [Capability("telemetry.persist", Version = "1.0.0", Authorization = Authorization.Internal,
            SideEffects = ["writes the window's aggregate"])]
        public sealed class PersistAggregate : ICapability<DeviceStats, PersistedAggregate>
        {
            public ValueTask<Result<PersistedAggregate>> ExecuteAsync(
                DeviceStats input, CapabilityContext ctx, CancellationToken ct) =>
                ValueTask.FromResult(Result.Ok(new PersistedAggregate("k")));
        }

        [JsonSerializable(typeof(StreamWindowBatch))]
        [JsonSerializable(typeof(DeviceStats))]
        [JsonSerializable(typeof(PersistedAggregate))]
        [JsonSerializable(typeof(AggregateComputed))]
        public sealed partial class TelemetryJson() : JsonSerializerContext(null)
        {
            public static TelemetryJson Default { get; } = new();

            protected override System.Text.Json.JsonSerializerOptions? GeneratedSerializerOptions => null;

            public override System.Text.Json.Serialization.Metadata.JsonTypeInfo? GetTypeInfo(Type type) => null;
        }
        """;

    /// <summary>Every analyzer this project ships, so the block cannot dodge a rule.</summary>
    private static DiagnosticAnalyzer[] AllAnalyzers() =>
    [
        new CapabilityAnalyzer(),
        new CapabilityThrowAnalyzer(),
        new CompensationDurabilityAnalyzer(),
        new DeadlineCoherenceAnalyzer(),
        new DeterminismAnalyzer(),
        new ExecutionProfileAnalyzer(),
        new ParallelSlotAnalyzer(),
        new PredicatePurityAnalyzer(),
        new StepBindingAnalyzer(),
        new SubFlowCycleAnalyzer(),
        new TriggerDeclarationAnalyzer(),
    ];

    /// <summary>The section exists and carries exactly one C# block.</summary>
    /// <remarks>
    /// The half that stops this test passing for the wrong reason. A renamed heading, a deleted
    /// example or a second one added beside it all land here rather than producing a green run
    /// over nothing.
    /// </remarks>
    [Fact]
    public void TheSectionIsFound()
    {
        CSharpBlocksInSection().ShouldHaveSingleItem(
            $"{Page}'s '{Section}' must carry exactly one ```csharp block: the flow declaration " +
            "this test compiles. A second one is not checked by anything.");
    }

    /// <summary>The declaration §9 prints compiles, and no FlowX rule reports it.</summary>
    /// <remarks>
    /// <para>
    /// The generated plan, dispatcher and stream registration are compiled with it — the updated
    /// compilation is what is read — because emitting text that parses and does not bind is the
    /// failure a hand-checked page never catches.
    /// </para>
    /// <para>
    /// <strong>What this asserts about the decision, not only about the page.</strong> A
    /// <c>.Window(…)</c> or <c>.Aggregate(…)</c> restored to the example fails on <c>CS1061</c>;
    /// a <c>Lateness</c> written in <c>Window</c>'s short form fails on <c>FLOWX1049</c>; an input
    /// contract narrower than <c>StreamWindowBatch</c> fails on <c>FLOWX1042</c>. Each is a way
    /// the page has actually been wrong.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheStreamTriggerExampleCompilesAndRaisesNothing()
    {
        var reported = Build(CSharpBlocksInSection().Single());

        reported.ShouldBeEmpty(
            $"{Page} §9 prints a declaration that does not build:\n" + string.Join("\n", reported));
    }

    /// <summary>Compiles one block with the preamble, and reads what came out.</summary>
    /// <remarks>
    /// <para>
    /// The generated plan and dispatcher are compiled with it — the <em>updated</em> compilation
    /// is what is read — because emitting text that parses and does not bind is the failure a
    /// hand-checked page never catches.
    /// </para>
    /// <para>
    /// <strong>No stand-in for <c>FlowX.Hosting</c>, so no subscription registration is
    /// emitted.</strong> The emitted registration calls into
    /// <c>Microsoft.Extensions.DependencyInjection</c>, which this compilation does not reference;
    /// <c>StreamGenerationTests</c> is where the registration's text is asserted, and
    /// <c>samples/realtime-stream</c> is where it is built for real.
    /// </para>
    /// </remarks>
    private static string[] Build(string block)
    {
        var compilation = GeneratorHarness.CompilationOf(
            "FlowX.TriggerModelDoc",
            [],
            ("/docs/09/preamble.cs", Preamble),
            (BlockPath, Header + block));

        CSharpGeneratorDriver
            .Create(new FlowPlanGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var generated);

        return [.. updated.GetDiagnostics()
            .Concat(generated)
            .Concat(GeneratorHarness.Report(compilation, AllAnalyzers()))
            .Where(static d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .Where(static d => !Ignorable(d.Id))
            .Where(static d => d.Severity == DiagnosticSeverity.Error || IsOnThePage(d))
            .Select(Describe)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
    }

    // ---------------------------------------------------------------- reading the page

    /// <summary>Every fenced C# block between §9's heading and the next one.</summary>
    private static List<string> CSharpBlocksInSection()
    {
        var lines = File.ReadAllText(RepositoryFile(Page))
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');

        var start = Array.FindIndex(lines, line => string.Equals(line.TrimEnd(), Section, StringComparison.Ordinal));

        start.ShouldBeGreaterThanOrEqualTo(
            0, $"{Page} no longer has a section headed '{Section}'. This test anchors on it.");

        var blocks = new List<string>();

        for (var i = start + 1; i < lines.Length && !lines[i].StartsWith("## ", StringComparison.Ordinal); i++)
        {
            if (!string.Equals(lines[i].Trim(), "```csharp", StringComparison.Ordinal))
            {
                continue;
            }

            var body = new List<string>();

            for (i++; i < lines.Length && !lines[i].StartsWith("```", StringComparison.Ordinal); i++)
            {
                body.Add(lines[i]);
            }

            blocks.Add(string.Join("\n", body));
        }

        return blocks;
    }

    // ------------------------------------------------------------------------ plumbing

    /// <summary>
    /// Diagnostics that are noise for this test rather than a statement about the page.
    /// </summary>
    /// <remarks>
    /// <see cref="GettingStartedTests"/>'s two, for its reasons: CS8019 fires on the shared
    /// header's unused usings, and CS1591 on a snippet that deliberately carries no XML
    /// documentation — a reference page that documented every record before showing what it is
    /// for would teach nothing.
    /// </remarks>
    private static bool Ignorable(string id) => id is "CS8019" or "CS1591";

    /// <summary>Whether the diagnostic is about the page's own block.</summary>
    /// <remarks>
    /// Errors are kept wherever they land — generated code that does not bind is what this test
    /// most wants to catch. Warnings are kept only for the block, because MSBuild writes the
    /// generated files with an <c>&lt;auto-generated&gt;</c> header and a real build does not
    /// report them.
    /// </remarks>
    private static bool IsOnThePage(Diagnostic diagnostic) =>
        diagnostic.Location.SourceTree?.FilePath.StartsWith("/docs/09/", StringComparison.Ordinal) == true;

    private static string Describe(Diagnostic diagnostic) =>
        $"{diagnostic.Id} at {diagnostic.Location.GetLineSpan().StartLinePosition}: " +
        diagnostic.GetMessage(CultureInfo.InvariantCulture);

    private static string RepositoryFile(string relativePath)
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

        throw new FileNotFoundException($"Could not locate '{relativePath}'.", relativePath);
    }
}
