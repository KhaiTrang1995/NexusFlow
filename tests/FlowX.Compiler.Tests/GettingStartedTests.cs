using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FlowX.Compiler.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// Compiles every code block in <c>docs/24-Getting-Started.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// A getting-started guide that does not compile is the worst document this repository
/// could ship: it is the first thing a newcomer copies, and every later claim inherits
/// whatever trust it spends. So the page is not reviewed for plausibility — it is built.
/// </para>
/// <para>
/// Each fenced block carries an HTML comment saying how it is verified, and a block with
/// no such comment fails the test. That is the load-bearing half: without it, the page
/// could grow a snippet that nothing compiles and still be green.
/// </para>
/// <list type="table">
/// <item>
/// <term><c>verify:preamble</c></term>
/// <description>
/// A C# block that later blocks may build on. Compiled itself, and added to the set of
/// syntax trees every subsequent compiled block is compiled with.
/// </description>
/// </item>
/// <item>
/// <term><c>verify:compiles</c></term>
/// <description>
/// A C# block that must compile with no errors and raise no FlowX diagnostic at all.
/// </description>
/// </item>
/// <item>
/// <term><c>verify:reports FLOWX1010 CS9035</c></term>
/// <description>
/// A C# block the page presents as a mistake. It is compiled, and the diagnostics it
/// produces must be exactly the listed ones — so the page cannot claim a rule fires
/// where it does not, or keep claiming it after the rule changes.
/// </description>
/// </item>
/// <item>
/// <term><c>verify:excerpt &lt;path&gt;</c></term>
/// <description>
/// The block is a verbatim quotation of a file in this repository that the solution or
/// <c>templates/verify.sh</c> already builds. Used for the snippets whose references —
/// <c>FlowX.Hosting</c>, <c>FlowX.Http</c>, <c>FlowX.Testing</c> — this test project
/// deliberately does not have. Blocks quoting the template are matched after the
/// substitution <c>dotnet new</c> performs.
/// </description>
/// </item>
/// <item>
/// <term><c>verify:prose &lt;why&gt;</c></term>
/// <description>
/// Not C#: a shell transcript, JSON, a manifest. Never accepted on a <c>csharp</c>
/// block, so nothing can be exempted by mislabelling it.
/// </description>
/// </item>
/// </list>
/// <para>
/// <strong>Compiled blocks are separate syntax trees, not one concatenated file.</strong>
/// Two blocks that declare the same type therefore collide exactly as two files would,
/// which is the behaviour a reader assumes when a page shows them one after the other.
/// A fixed header supplies the usings and the namespace, so the page can show a
/// declaration rather than a file preamble seventeen times.
/// </para>
/// </remarks>
public sealed class GettingStartedTests
{
    private const string Page = "docs/24-Getting-Started.md";

    /// <summary>The template's source name, and what <c>dotnet new flowx -n Ordering</c> makes of it.</summary>
    private const string TemplateSourceName = "FlowXStarter";

    private const string GeneratedName = "Ordering";

    /// <summary>
    /// The file preamble every compiled block is given, so the page can show declarations.
    /// </summary>
    /// <remarks>
    /// These are the usings the template's own files carry, plus the implicit usings a
    /// <c>net10.0</c> project has. Nothing here is a FlowX type: a block that needs one
    /// must name it.
    /// </remarks>
    private const string Header = """
        using System;
        using System.Collections.Generic;
        using System.Text.Json.Serialization;
        using System.Threading;
        using System.Threading.Tasks;
        using FlowX;

        namespace Ordering;

        """;

    private static readonly Regex MarkerPattern = new(
        @"^<!--\s*verify:\s*(?<body>.+?)\s*-->\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Every FlowX analyzer, so a block cannot dodge a rule this project ships.</summary>
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

    /// <summary>Every block on the page is verified, and the page says how.</summary>
    [Fact]
    public void EveryCodeBlockInTheGettingStartedGuideIsVerified()
    {
        var blocks = Blocks(File.ReadAllText(RepositoryFile(Page)));

        blocks.ShouldNotBeEmpty(
            $"No fenced block was found in {Page}. A gate handed nothing to inspect " +
            "passes for the wrong reason.");

        var unmarked = blocks
            .Where(static block => block.Marker is null)
            .Select(static block => $"line {block.Line}: ```{block.Language}")
            .ToArray();

        unmarked.ShouldBeEmpty(
            $"Every fenced block in {Page} must be preceded by a <!-- verify: … --> " +
            "comment saying how it is checked. Unmarked:\n" + string.Join("\n", unmarked));

        var failures = new List<string>();
        var preamble = new List<(string Path, string Source)>();

        foreach (var block in blocks)
        {
            var marker = block.Marker!;
            var verb = marker.Split(' ', 2)[0];
            var argument = marker.Length > verb.Length ? marker[(verb.Length + 1)..].Trim() : string.Empty;

            switch (verb)
            {
                case "prose":
                    if (string.Equals(block.Language, "csharp", StringComparison.Ordinal))
                    {
                        failures.Add($"line {block.Line}: a csharp block cannot be 'prose'.");
                    }
                    else if (argument.Length == 0)
                    {
                        failures.Add($"line {block.Line}: 'prose' must say why the block is not compiled.");
                    }

                    break;

                case "excerpt":
                    failures.AddRange(CheckExcerpt(block, argument));
                    break;

                case "preamble":
                case "compiles":
                case "reports":
                    failures.AddRange(CheckCompiled(block, verb, argument, preamble));

                    if (string.Equals(verb, "preamble", StringComparison.Ordinal))
                    {
                        preamble.Add(($"/docs/24/block{block.Line}.cs", Header + block.Code));
                    }

                    break;

                default:
                    failures.Add($"line {block.Line}: unknown verification '{marker}'.");
                    break;
            }
        }

        failures.ShouldBeEmpty(
            $"{Page} does not hold up:\n\n" + string.Join("\n\n", failures));
    }

    // ---------------------------------------------------------------------------------
    // The two checks
    // ---------------------------------------------------------------------------------

    /// <summary>Compiles one block with the preamble before it, and reads what came out.</summary>
    private static IEnumerable<string> CheckCompiled(
        Block block,
        string verb,
        string argument,
        List<(string Path, string Source)> preamble)
    {
        if (!string.Equals(block.Language, "csharp", StringComparison.Ordinal))
        {
            return [$"line {block.Line}: '{verb}' needs a ```csharp block, not ```{block.Language}."];
        }

        var expected = argument.Length == 0
            ? []
            : argument.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (string.Equals(verb, "reports", StringComparison.Ordinal) && expected.Length == 0)
        {
            return [$"line {block.Line}: 'reports' must name the diagnostics it expects."];
        }

        var files = preamble
            .Append(($"/docs/24/block{block.Line}.cs", Header + block.Code))
            .ToArray();

        var compilation = GeneratorHarness.CompilationOf("FlowX.GettingStarted", [], files);

        CSharpGeneratorDriver
            .Create(new FlowPlanGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var generated);

        // The updated compilation, so the generated plan and dispatcher have to compile
        // too. Emitting text that parses and does not bind is the failure mode a page of
        // hand-checked snippets never catches.
        var reported = updated.GetDiagnostics()
            .Concat(generated)
            .Concat(GeneratorHarness.Report(compilation, AllAnalyzers()))
            .Where(static d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .Where(static d => !Ignorable(d.Id))
            .Where(static d => d.Severity == DiagnosticSeverity.Error || IsOnThePage(d))
            .Select(static d => (d.Id, Text: Describe(d)))
            .DistinctBy(static d => d.Id, StringComparer.Ordinal)
            .OrderBy(static d => d.Id, StringComparer.Ordinal)
            .ToArray();

        var actual = reported.Select(static d => d.Id).ToArray();
        var wanted = expected.OrderBy(static id => id, StringComparer.Ordinal).ToArray();

        return actual.SequenceEqual(wanted, StringComparer.Ordinal)
            ? []
            : [$"line {block.Line}: expected [{string.Join(", ", wanted)}], got " +
               $"[{string.Join(", ", actual)}].\n" +
               string.Join("\n", reported.Select(static d => "        " + d.Text))];
    }

    /// <summary>Checks the block is a verbatim quotation of a file that is already built.</summary>
    private static IEnumerable<string> CheckExcerpt(Block block, string path)
    {
        if (path.Length == 0)
        {
            return [$"line {block.Line}: 'excerpt' must name the file it quotes."];
        }

        var absolute = Path.Combine(RepositoryRoot().FullName, path);

        if (!File.Exists(absolute))
        {
            return [$"line {block.Line}: '{path}' does not exist."];
        }

        var source = File.ReadAllText(absolute).Replace("\r\n", "\n", StringComparison.Ordinal);

        // `dotnet new` rewrites the template's source name; so does this, for the same
        // reason — the page quotes the file a reader gets, not the file in templates/.
        if (path.StartsWith("templates/", StringComparison.Ordinal))
        {
            source = source.Replace(TemplateSourceName, GeneratedName, StringComparison.Ordinal);
        }

        var quoted = block.Code.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();

        return source.Contains(quoted, StringComparison.Ordinal)
            ? []
            : [$"line {block.Line}: the block is not in {path} verbatim. First line that is " +
               $"missing:\n        {FirstMissingLine(source, quoted)}"];
    }

    private static string FirstMissingLine(string source, string quoted) =>
        quoted.Split('\n').FirstOrDefault(line => line.Trim().Length > 0 && !source.Contains(line, StringComparison.Ordinal))
        ?? "(every line is present, but not contiguously)";

    // ---------------------------------------------------------------------------------
    // Reading the page
    // ---------------------------------------------------------------------------------

    private sealed record Block(int Line, string Language, string Code, string? Marker);

    /// <summary>Every fenced block, with the verification comment that precedes it.</summary>
    /// <remarks>
    /// A hand-written scan rather than a Markdown library: the only structure that matters
    /// is a fence at the start of a line, and adding a parser dependency to read four
    /// characters would be a larger claim on this repository than the test is worth.
    /// </remarks>
    private static ImmutableArray<Block> Blocks(string page)
    {
        var lines = page.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var blocks = ImmutableArray.CreateBuilder<Block>();
        string? marker = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var match = MarkerPattern.Match(lines[i]);

            if (match.Success)
            {
                marker = match.Groups["body"].Value;
                continue;
            }

            if (!lines[i].StartsWith("```", StringComparison.Ordinal))
            {
                // A marker applies to the next fence, across blank lines only. Anything
                // else between the two means the comment was left behind by an edit.
                if (lines[i].Trim().Length > 0)
                {
                    marker = null;
                }

                continue;
            }

            var language = lines[i][3..].Trim();
            var body = new StringBuilder();
            var start = i + 1;

            for (i++; i < lines.Length && !lines[i].StartsWith("```", StringComparison.Ordinal); i++)
            {
                body.Append(lines[i]).Append('\n');
            }

            blocks.Add(new Block(start, language, body.ToString(), marker));
            marker = null;
        }

        return blocks.ToImmutable();
    }

    // ---------------------------------------------------------------------------------
    // Plumbing
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Diagnostics that are noise for this test rather than a statement about the page.
    /// </summary>
    /// <remarks>
    /// CS8019 fires on the fixed header's unused usings, which is an artefact of giving
    /// every block the same one. CS1591 is the missing-XML-doc warning, which the page's
    /// snippets deliberately do not carry — a tutorial that documents every record before
    /// showing what it is for teaches nothing.
    /// </remarks>
    private static bool Ignorable(string id) => id is "CS8019" or "CS1591";

    /// <summary>Whether the diagnostic is about code on the page rather than code generated from it.</summary>
    /// <remarks>
    /// <para>
    /// Errors are kept wherever they are — generated code that does not bind is the failure
    /// this test most wants to catch. Warnings are kept only for the page's own blocks,
    /// because a real build does not report the generator's: MSBuild writes the generated
    /// files with an <c>&lt;auto-generated&gt;</c> header and they are warning-free in the
    /// build that matters, which is the one <c>templates/verify.sh</c> runs with
    /// <c>TreatWarningsAsErrors</c>. Reporting them here would make the page carry a
    /// property of the emitter instead of a property of itself — <c>CS1998</c> on the
    /// generated <c>CompensateAsync</c> of a flow with no compensation, for one.
    /// </para>
    /// </remarks>
    private static bool IsOnThePage(Diagnostic diagnostic) =>
        diagnostic.Location.SourceTree?.FilePath.StartsWith(BlockPathPrefix, StringComparison.Ordinal) == true;

    private const string BlockPathPrefix = "/docs/24/";

    private static string Describe(Diagnostic diagnostic) =>
        $"{diagnostic.Id} at {diagnostic.Location.GetLineSpan().StartLinePosition}: " +
        diagnostic.GetMessage(CultureInfo.InvariantCulture);

    private static string RepositoryFile(string relativePath)
    {
        var candidate = Path.Combine(RepositoryRoot().FullName, relativePath);

        return File.Exists(candidate)
            ? candidate
            : throw new FileNotFoundException($"Could not locate '{relativePath}'.", candidate);
    }

    private static DirectoryInfo RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FlowX.slnx")))
            {
                return directory;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the repository root walking up from {AppContext.BaseDirectory}.");
    }
}
