using System;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlowX.Cli.Manifest;
using FlowX.Cli.Rendering;
using Shouldly;
using Xunit;

namespace FlowX.Cli.Tests;

/// <summary>
/// The diagram. Mermaid rather than an image, because a diagram that lives in a pull
/// request diff is a diagram people actually look at — the architecture stops being a
/// drawing somebody updated once and becomes something a reviewer sees change.
/// </summary>
public sealed class MermaidRendererTests
{
    private const string SampleManifest = """
        {
          "schemaVersion": "0.1.0",
          "application": { "name": "Sample.App", "version": "1.0.0" },
          "flows": [
            {
              "id": "order.place", "version": "1.0.0", "profile": "Ephemeral",
              "steps": [
                { "id": 0, "kind": "Capability", "capability": "order.validate@1.0.0" },
                { "id": 1, "kind": "Capability", "capability": "inventory.reserve@1.0.0",
                  "compensation": "inventory.release@1.0.0" },
                { "id": 2, "kind": "Capability", "capability": "payment.capture@2.1.0" },
                { "id": 3, "kind": "Emit", "event": "order.placed" }
              ],
              "emits": ["order.placed"]
            },
            {
              "id": "order.get", "version": "1.0.0", "profile": "Ephemeral",
              "steps": [ { "id": 0, "kind": "Capability", "capability": "order.read@1.0.0" } ],
              "emits": []
            }
          ],
          "capabilities": [
            { "id": "order.validate", "version": "1.0.0", "idempotent": true, "sideEffects": [] },
            { "id": "order.read", "version": "1.0.0", "idempotent": true, "sideEffects": [] },
            { "id": "inventory.reserve", "version": "1.0.0", "idempotent": true,
              "sideEffects": ["inventory-ledger"] },
            { "id": "payment.capture", "version": "2.1.0", "idempotent": false,
              "sideEffects": ["payment-gateway"] }
          ]
        }
        """;

    private static ManifestDocument Parse(string json = SampleManifest) =>
        JsonSerializer.Deserialize(json, ManifestJsonContext.Default.ManifestDocument)!;

    private static string Render(string? flowId = null) => MermaidRenderer.Render(Parse(), flowId);

    [Fact]
    public void StartsWithAFlowchartDeclaration()
        => Render().ShouldStartWith("flowchart TD");

    /// <summary>
    /// The structural check that makes the others worth having.
    /// </summary>
    /// <remarks>
    /// An edge pointing at a node that was never declared renders as a mysterious extra
    /// box, which is exactly the failure a human eye skims past. Checking it here is
    /// cheaper and stricter than checking that the file "looks like Mermaid".
    /// </remarks>
    [Fact]
    public void EveryEdgeReferencesADeclaredNode()
    {
        var diagram = Render();

        var declared = Regex.Matches(diagram, @"^\s{8}(\w+)[\[\(>/]", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var edges = Regex.Matches(diagram, @"^\s{8}(\w+)\s+-\.?->\s+(\w+)$", RegexOptions.Multiline);

        edges.Count.ShouldBeGreaterThan(0, "A four-step flow must produce edges.");

        foreach (Match edge in edges)
        {
            declared.ShouldContain(edge.Groups[1].Value, $"Edge source '{edge.Groups[1].Value}' is undeclared.");
            declared.ShouldContain(edge.Groups[2].Value, $"Edge target '{edge.Groups[2].Value}' is undeclared.");
        }
    }

    [Fact]
    public void BalancesEverySubgraphWithAnEnd()
    {
        var diagram = Render();

        Regex.Count(diagram, @"^\s*subgraph ", RegexOptions.Multiline)
            .ShouldBe(Regex.Count(diagram, @"^\s*end$", RegexOptions.Multiline),
                "An unbalanced subgraph makes the whole diagram fail to render.");
    }

    [Fact]
    public void PutsEachFlowInItsOwnSubgraphLabelledWithVersionAndProfile()
    {
        var diagram = Render();

        diagram.ShouldContain("subgraph f0[\"order.get@1.0.0 (Ephemeral)\"]");
        diagram.ShouldContain("subgraph f1[\"order.place@1.0.0 (Ephemeral)\"]");
    }

    [Fact]
    public void ConnectsStepsInExecutionOrder()
    {
        var diagram = Render("order.place");

        diagram.ShouldContain("f0s0 --> f0s1");
        diagram.ShouldContain("f0s1 --> f0s2");
        diagram.ShouldContain("f0s2 --> f0s3");
    }

    [Fact]
    public void DrawsCompensationBackwardsAndDashed()
    {
        var diagram = Render("order.place");

        diagram.ShouldContain("f0s1 -.-> f0s1c");
        diagram.ShouldContain("undo inventory.release");
        // Backwards because that is what compensation does: the failure path unwinds in
        // reverse. Drawing it forwards would be tidier and would misrepresent the one
        // thing a saga has to get right.
    }

    [Fact]
    public void MarksStepsWithSideEffectsAndStepsThatAreNotRetryable()
    {
        var diagram = Render("order.place");

        diagram.ShouldContain("inventory.reserve ⚡", Case.Sensitive);
        diagram.ShouldContain("payment.capture ⚡ ⚠", Case.Sensitive);
        diagram.ShouldNotContain("order.validate ⚡", Case.Sensitive);
        // The two marks a reader wants at a glance: this step changes something outside
        // the process, and this one is not safe to retry.
    }

    [Fact]
    public void GivesEmitStepsADistinctShape()
        => Render("order.place").ShouldContain("([\"emit order.placed\"])");

    [Fact]
    public void IsolatesASingleFlowWhenAskedTo()
    {
        var diagram = Render("order.place");

        diagram.ShouldContain("order.place");
        diagram.ShouldNotContain("order.get");
    }

    [Fact]
    public void IsDeterministic()
    {
        Render().ShouldBe(Render(),
            "A diagram committed to a repository must not change between builds, or " +
            "every pull request shows a diagram diff nobody made.");
    }

    [Fact]
    public void OrdersFlowsIndependentlyOfTheirOrderInTheManifest()
    {
        var reversed = Parse();
        reversed.Flows.Reverse();

        MermaidRenderer.Render(reversed).ShouldBe(Render());
    }

    [Fact]
    public void RendersAValidDiagramForAnApplicationWithNoFlows()
    {
        var diagram = MermaidRenderer.Render(new ManifestDocument());

        diagram.ShouldStartWith("flowchart TD");
        diagram.Contains("No flows found", StringComparison.Ordinal).ShouldBeTrue(
            "An empty file looks like the tool crashed. A valid diagram saying nothing " +
            "was found does not.");
    }

    [Fact]
    public void RendersAValidDiagramWhenTheRequestedFlowDoesNotExist()
        => MermaidRenderer.Render(Parse(), "no.such_flow").ShouldContain("No flows found");

    [Fact]
    public void ReplacesQuotesRatherThanEmittingADiagramThatWillNotRender()
    {
        var awkward = new ManifestDocument
        {
            Flows =
            [
                new ManifestFlow
                {
                    Id = "a.b",
                    Version = "1.0.0",
                    Profile = "Ephemeral \"quoted\"",
                    Steps = [new ManifestStep { Id = 0, Kind = "Capability", Capability = "x.y@1.0.0" }],
                },
            ],
        };

        var diagram = MermaidRenderer.Render(awkward);

        diagram.Contains("\\\"", StringComparison.Ordinal).ShouldBeFalse();
        diagram.Contains('”', StringComparison.Ordinal).ShouldBeTrue(
            "Mermaid has no escape for a quote inside a quoted label, so one is replaced. " +
            "A diagram that fails to render is worse than a label that reads differently.");
    }

    [Fact]
    public void RejectsANullManifest()
        => Should.Throw<ArgumentNullException>(() => MermaidRenderer.Render(null!));
}
