using Shouldly;
using Xunit;

namespace FlowX.Cli.Tests;

/// <summary>
/// A manifest carrying <c>extensions</c> survives every verb the CLI has, and a change
/// inside one is reported by none of them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is ADR-0017 F6, and F6 is a criterion rather than a nice-to-have because
/// <c>extensions</c> is the whole of what ADR-0005 traded for.</strong> That record accepted
/// "a public contract we must support forever" on the strength of one escape hatch: a field
/// where "a one-off need never forces a breaking change". Until this file existed nothing
/// wrote the field, nothing read it, no fixture carried it and 22-CLI said nothing about what
/// a diff does with one — so the relief valve on a permanent contract was a sentence in a
/// schema description. The first one-off need after v1.0 would have been a schema change,
/// which is the exact outcome the field exists to prevent.
/// </para>
/// <para>
/// <strong>Nothing produces one, and that is the point.</strong> The compiler writes no
/// <c>extensions</c> block; the keys belong to whoever adds them downstream. So the fixture
/// here is the consumer, and what is being asserted is that FlowX tolerates a document it
/// did not fully write — including one whose extension block is deeply nested, which is what
/// <c>additionalProperties: true</c> promises and what a strict reader would choke on.
/// </para>
/// <para>
/// <strong>Silence is the classification, not an oversight.</strong> 22-CLI §2.2 carries the
/// row: FlowX cannot say whether a change to a field whose meaning it does not know is
/// breaking, and a hatch whose contents could block a merge on an unclassifiable finding
/// would not be a hatch. <see cref="AChangeInsideExtensionsIsReportedByNothing"/> is that row
/// with an exit code behind it.
/// </para>
/// </remarks>
[Collection(CliConsoleGroup.Name)]
public sealed class ExtensionsEscapeHatchTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "flowx-extensions-tests-" + Guid.NewGuid().ToString("n"));

    public ExtensionsEscapeHatchTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>
    /// The document a downstream consumer produces: FlowX's manifest with a block of
    /// somebody else's metadata bolted on.
    /// </summary>
    /// <remarks>
    /// Nested two deep and carrying an array, a number and a string, because the schema
    /// declares <c>additionalProperties: true</c> and a hatch that only admitted flat string
    /// pairs would not be one. The keys are deliberately not FlowX-shaped.
    /// </remarks>
    private const string WithExtensions = """
        {
          "schemaVersion": "0.1.0",
          "application": { "name": "Sample.App", "version": "1.0.0" },
          "flows": [{
            "id": "order.place", "version": "1.0.0", "profile": "Ephemeral",
            "input": { "type": "Sample.PlaceOrder" },
            "output": { "type": "Sample.OrderPlaced" },
            "steps": [{ "id": 0, "kind": "Capability", "capability": "order.validate@1.0.0" }],
            "emits": []
          }],
          "capabilities": [
            {
              "id": "order.validate", "version": "1.0.0",
              "input": "Sample.PlaceOrder", "output": "Sample.Validated",
              "authorization": { "mode": "Authenticated" },
              "idempotent": true, "sideEffects": []
            }
          ],
          "events": [],
          "extensions": {
            "acme.cost-centre": "retail-platform",
            "acme.review": { "board": ["ana", "bo"], "quarter": 3, "signed": true }
          }
        }
        """;

    /// <summary>The same document with one value inside <c>extensions</c> moved.</summary>
    private const string WithExtensionsChanged = """
        {
          "schemaVersion": "0.1.0",
          "application": { "name": "Sample.App", "version": "1.0.0" },
          "flows": [{
            "id": "order.place", "version": "1.0.0", "profile": "Ephemeral",
            "input": { "type": "Sample.PlaceOrder" },
            "output": { "type": "Sample.OrderPlaced" },
            "steps": [{ "id": 0, "kind": "Capability", "capability": "order.validate@1.0.0" }],
            "emits": []
          }],
          "capabilities": [
            {
              "id": "order.validate", "version": "1.0.0",
              "input": "Sample.PlaceOrder", "output": "Sample.Validated",
              "authorization": { "mode": "Authenticated" },
              "idempotent": true, "sideEffects": []
            }
          ],
          "events": [],
          "extensions": {
            "acme.cost-centre": "wholesale-platform",
            "acme.review": { "board": ["ana"], "quarter": 4, "signed": false },
            "acme.added-later": "a key that did not exist in the baseline"
          }
        }
        """;

    [Fact]
    public void GraphRendersAManifestCarryingExtensions()
    {
        var (exitCode, output, error) = CliRunner.Run(
            "graph", "--manifest", Write("with-extensions.json", WithExtensions));

        exitCode.ShouldBe(0, $"`flowx graph` refused a manifest with an extensions block: {error}");
        output.ShouldContain("order.place");
    }

    [Fact]
    public void VerifyCostReadsAManifestCarryingExtensions()
    {
        var (exitCode, output, error) = CliRunner.Run(
            "verify", "--cost", "--manifest", Write("with-extensions.json", WithExtensions));

        exitCode.ShouldBe(0, $"`flowx verify --cost` refused a manifest with an extensions block: {error}");
        output.ShouldNotBeEmpty();
    }

    [Fact]
    public void DiffComparesTwoManifestsThatBothCarryExtensions()
    {
        var (exitCode, _, error) = CliRunner.Run(
            "diff",
            "--old", Write("old.json", WithExtensions),
            "--new", Write("new.json", WithExtensions));

        exitCode.ShouldBe(0, $"`flowx diff` refused a manifest with an extensions block: {error}");
    }

    /// <summary>
    /// Values changed, a key added, a nested array shortened — and the report is empty.
    /// </summary>
    /// <remarks>
    /// The assertion is on the finding list and not only on the exit code, because a Neutral
    /// finding would also exit 0 and would still be the wrong answer: it would put a line a
    /// reader cannot act on into the report that ADR-0005 says is the manifest's only
    /// enforcement, every time somebody edited their own metadata.
    /// </remarks>
    [Fact]
    public void AChangeInsideExtensionsIsReportedByNothing()
    {
        var (exitCode, output, error) = CliRunner.Run(
            "diff",
            "--old", Write("old.json", WithExtensions),
            "--new", Write("new.json", WithExtensionsChanged));

        exitCode.ShouldBe(0, $"a change confined to `extensions` blocked a merge: {error}");

        output.ShouldNotContain(
            "FLOWX-DIFF",
            Case.Sensitive,
            "`flowx diff` classified a change inside `extensions`. It cannot: the keys belong " +
            "to whoever wrote them, and FlowX does not know what a change to one means. " +
            "docs/22-CLI.md §2.2 is where that is recorded.");
    }

    /// <summary>
    /// An extension block survives being written out and read back by the tool itself.
    /// </summary>
    /// <remarks>
    /// The round trip F6 asks for. <c>flowx diff</c> parses both sides into
    /// <c>ManifestDocument</c>, and a parser that silently dropped unknown properties would
    /// pass every test above by comparing two documents with no extensions in them. Comparing
    /// a document against one whose *only* difference is an extension key proves the block
    /// reached the comparison and was ignored there, rather than never arriving.
    /// </remarks>
    [Fact]
    public void AManifestWithExtensionsDiffsCleanlyAgainstOneWithout()
    {
        var without = WithExtensions[..WithExtensions.LastIndexOf(",\n  \"extensions\"", StringComparison.Ordinal)]
            + "\n}";

        var (exitCode, output, error) = CliRunner.Run(
            "diff",
            "--old", Write("bare.json", without),
            "--new", Write("hatched.json", WithExtensions));

        exitCode.ShouldBe(0, $"adding an extensions block was reported as a break: {error}");
        output.ShouldNotContain("FLOWX-DIFF", Case.Sensitive);
    }

    private string Write(string name, string json)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, json);
        return path;
    }
}
