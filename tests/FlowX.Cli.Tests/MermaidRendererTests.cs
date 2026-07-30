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

        var declared = Regex.Matches(diagram, @"^\s{8}(\w+)[\[\(>/{]", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        // The optional label matters: a labelled branch edge this pattern skipped would be
        // exempt from the one structural check the renderer has.
        var edges = Regex.Matches(
            diagram,
            @"^\s{8}(\w+)\s+-\.?->(?:\|[^|]*\|)?\s+(\w+)$",
            RegexOptions.Multiline);

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

    /// <summary>A flow with a <c>When</c>/<c>Otherwise</c>, as the compiler publishes it.</summary>
    /// <remarks>
    /// Step 3 is absent on purpose: it is the jump that closes the <c>then</c> block, and
    /// the manifest does not publish it. A renderer that assumed contiguous ids would draw
    /// an edge to a node that does not exist.
    /// </remarks>
    private const string ConditionalManifest = """
        {
          "schemaVersion": "0.1.0",
          "application": { "name": "Sample.App", "version": "1.0.0" },
          "flows": [
            {
              "id": "loan.review", "version": "1.0.0", "profile": "Ephemeral",
              "steps": [
                { "id": 0, "kind": "Capability", "capability": "risk.assess@1.0.0" },
                { "id": 1, "kind": "Condition", "branches": [
                    [ { "id": 2, "kind": "Capability", "capability": "review.request@1.0.0" } ],
                    [ { "id": 4, "kind": "Capability", "capability": "review.auto@1.0.0" } ]
                  ] },
                { "id": 5, "kind": "Capability", "capability": "applicant.notify@1.0.0" }
              ],
              "emits": []
            }
          ],
          "capabilities": []
        }
        """;

    [Fact]
    public void DrawsAConditionalAsADiamondWithLabelledBranches()
    {
        var diagram = MermaidRenderer.Render(Parse(ConditionalManifest));

        // A diamond, so a reader sees the shape before reading a single label.
        diagram.ShouldContain("f0s1{\"condition\"}");

        diagram.ShouldContain("f0s0 --> f0s1");
        diagram.ShouldContain("f0s1 -->|yes| f0s2");
        diagram.ShouldContain("f0s1 -->|no| f0s4");
    }

    [Fact]
    public void BothBranchesRejoinTheStepThatFollowsTheConditional()
    {
        // The failure this catches is drawing only one exit, which produces a diagram
        // where one branch runs off the end of the flow — precisely the thing a reviewer
        // is looking at the picture to check.
        var diagram = MermaidRenderer.Render(Parse(ConditionalManifest));

        diagram.ShouldContain("f0s2 --> f0s5");
        diagram.ShouldContain("f0s4 --> f0s5");
    }

    [Fact]
    public void EveryEdgeOfAConditionalReferencesADeclaredNode()
    {
        var diagram = MermaidRenderer.Render(Parse(ConditionalManifest));

        var declared = Regex.Matches(diagram, @"^\s{8}(\w+)[\[\(>/{]", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        declared.ShouldContain("f0s2", "A step inside a branch still needs a node of its own.");
        declared.ShouldNotContain("f0s3", "The jump is not published, so nothing may draw it.");

        foreach (Match edge in Regex.Matches(
            diagram, @"^\s{8}(\w+)\s+-\.?->(?:\|[^|]*\|)?\s+(\w+)$", RegexOptions.Multiline))
        {
            declared.ShouldContain(edge.Groups[1].Value);
            declared.ShouldContain(edge.Groups[2].Value);
        }
    }

    [Fact]
    public void AConditionalWithNoAlternativeIsAlsoItsOwnFalseExit()
    {
        // One populated block means the false path skips the conditional entirely, so the
        // conditional connects straight to what follows as well as through its branch.
        const string NoOtherwise = """
            {
              "schemaVersion": "0.1.0",
              "application": { "name": "Sample.App", "version": "1.0.0" },
              "flows": [
                {
                  "id": "loan.review", "version": "1.0.0", "profile": "Ephemeral",
                  "steps": [
                    { "id": 0, "kind": "Condition", "branches": [
                        [ { "id": 1, "kind": "Capability", "capability": "review.request@1.0.0" } ]
                      ] },
                    { "id": 2, "kind": "Capability", "capability": "applicant.notify@1.0.0" }
                  ],
                  "emits": []
                }
              ],
              "capabilities": []
            }
            """;

        var diagram = MermaidRenderer.Render(Parse(NoOtherwise));

        diagram.ShouldContain("f0s0 -->|yes| f0s1");
        diagram.ShouldContain("f0s1 --> f0s2");
        diagram.ShouldContain("f0s0 --> f0s2");
    }

    /// <summary>A flow with a <c>Switch</c>, as the compiler publishes it.</summary>
    /// <remarks>
    /// Two cases and a <c>Default</c>, so <c>branches</c> has three entries and the last
    /// is the default. Steps 3 and 5 are absent on purpose: they are the jumps closing the
    /// case blocks, and the manifest does not publish a jump.
    /// </remarks>
    private const string SwitchManifest = """
        {
          "schemaVersion": "0.1.0",
          "application": { "name": "Sample.App", "version": "1.0.0" },
          "flows": [
            {
              "id": "order.price", "version": "1.0.0", "profile": "Ephemeral",
              "steps": [
                { "id": 0, "kind": "Capability", "capability": "order.validate@1.0.0" },
                { "id": 1, "kind": "Switch", "branches": [
                    [ { "id": 2, "kind": "Capability", "capability": "pricing.retail@1.0.0" } ],
                    [ { "id": 4, "kind": "Capability", "capability": "pricing.wholesale@1.0.0" } ],
                    [ { "id": 6, "kind": "Capability", "capability": "order.reject@1.0.0" } ]
                  ] },
                { "id": 7, "kind": "Capability", "capability": "order.confirm@1.0.0" }
              ],
              "emits": []
            }
          ],
          "capabilities": []
        }
        """;

    [Fact]
    public void DrawsASwitchAsAHexagonWithOneEdgePerArm()
    {
        var diagram = MermaidRenderer.Render(Parse(SwitchManifest));

        // A hexagon, distinct from a conditional's diamond, so a reader sees a many-way
        // decision before reading a single label.
        diagram.ShouldContain("f0s1{{\"switch\"}}");

        // Positional, because the case *values* are business data and the manifest
        // deliberately does not carry them. The label says which arm, never on what.
        diagram.ShouldContain("f0s1 -->|case 0| f0s2");
        diagram.ShouldContain("f0s1 -->|case 1| f0s4");
        diagram.ShouldContain("f0s1 -->|default| f0s6");
    }

    [Fact]
    public void EveryArmOfASwitchRejoinsTheStepThatFollowsIt()
    {
        var diagram = MermaidRenderer.Render(Parse(SwitchManifest));

        diagram.ShouldContain("f0s2 --> f0s7");
        diagram.ShouldContain("f0s4 --> f0s7");
        diagram.ShouldContain("f0s6 --> f0s7");

        diagram.Contains("f0s1 --> f0s7", StringComparison.Ordinal).ShouldBeFalse(
            "Every arm is populated and one of them is the default, so no value bypasses " +
            "the switch and it is not an exit of its own.");
    }

    /// <summary>A flow with a <c>Parallel</c>, as the compiler publishes it.</summary>
    /// <remarks>
    /// Three branches, every one of which runs. Unlike a switch there are no jump steps to
    /// be absent: a branch's range ends where the next branch begins, so the ids are
    /// contiguous.
    /// </remarks>
    private const string ParallelManifest = """
        {
          "schemaVersion": "0.1.0",
          "application": { "name": "Sample.App", "version": "1.0.0" },
          "flows": [
            {
              "id": "order.screen", "version": "1.0.0", "profile": "Ephemeral",
              "steps": [
                { "id": 0, "kind": "Capability", "capability": "order.validate@1.0.0" },
                { "id": 1, "kind": "Parallel", "merge": "AllMustSucceed", "branches": [
                    [ { "id": 2, "kind": "Capability", "capability": "risk.credit@1.0.0" } ],
                    [ { "id": 3, "kind": "Capability", "capability": "risk.fraud@1.0.0" } ],
                    [ { "id": 4, "kind": "Capability", "capability": "risk.sanctions@1.0.0" } ]
                  ] },
                { "id": 5, "kind": "Capability", "capability": "order.decide@1.0.0" }
              ],
              "emits": []
            }
          ],
          "capabilities": []
        }
        """;

    [Fact]
    public void DrawsAForkWithItsMergeRuleAndOneEdgePerBranch()
    {
        var diagram = MermaidRenderer.Render(Parse(ParallelManifest));

        // Not a diamond and not a hexagon: a fork makes no decision, so it must not wear
        // the shape of one. The merge rule is on the label because it is the one thing
        // about a fork worth reading off a diagram — it says what one branch failing means.
        diagram.ShouldContain("f0s1[/\"parallel · AllMustSucceed\"/]");

        // Numbered, because every branch runs. "yes" and "no" would read as a choice.
        diagram.ShouldContain("f0s1 -->|branch 0| f0s2");
        diagram.ShouldContain("f0s1 -->|branch 1| f0s3");
        diagram.ShouldContain("f0s1 -->|branch 2| f0s4");
    }

    [Fact]
    public void EveryBranchOfAForkRejoinsTheStepThatFollowsIt()
    {
        var diagram = MermaidRenderer.Render(Parse(ParallelManifest));

        diagram.ShouldContain("f0s2 --> f0s5");
        diagram.ShouldContain("f0s3 --> f0s5");
        diagram.ShouldContain("f0s4 --> f0s5");

        diagram.Contains("f0s1 --> f0s5", StringComparison.Ordinal).ShouldBeFalse(
            "Every branch is populated, so nothing bypasses the fork and it is not an " +
            "exit of its own.");
    }

    [Fact]
    public void AForkWhoseMergeCouldNotBeReadIsStillDrawnAsAFork()
    {
        // The compiler omits `merge` when the strategy came from an expression it could
        // not name. An absent field is a consumer asking; a guessed one is a consumer
        // misled — so the diagram says less rather than something untrue.
        const string Unlabelled = """
            {
              "schemaVersion": "0.1.0",
              "application": { "name": "Sample.App", "version": "1.0.0" },
              "flows": [
                {
                  "id": "order.screen", "version": "1.0.0", "profile": "Ephemeral",
                  "steps": [
                    { "id": 0, "kind": "Parallel", "branches": [
                        [ { "id": 1, "kind": "Capability", "capability": "risk.credit@1.0.0" } ],
                        [ { "id": 2, "kind": "Capability", "capability": "risk.fraud@1.0.0" } ]
                      ] },
                    { "id": 3, "kind": "Capability", "capability": "order.decide@1.0.0" }
                  ],
                  "emits": []
                }
              ],
              "capabilities": []
            }
            """;

        var diagram = MermaidRenderer.Render(Parse(Unlabelled));

        diagram.ShouldContain("f0s0[/\"parallel\"/]");
        diagram.ShouldContain("f0s0 -->|branch 0| f0s1");
        diagram.ShouldContain("f0s0 -->|branch 1| f0s2");
    }

    [Fact]
    public void ASwitchWhoseDefaultIsEmptyIsAlsoItsOwnExit()
    {
        // An empty last block is how the manifest says "a value matching nothing falls
        // through". The switch therefore connects straight to what follows as well as
        // through its cases — the many-way form of the missing-`Otherwise` case above.
        const string NoDefault = """
            {
              "schemaVersion": "0.1.0",
              "application": { "name": "Sample.App", "version": "1.0.0" },
              "flows": [
                {
                  "id": "order.price", "version": "1.0.0", "profile": "Ephemeral",
                  "steps": [
                    { "id": 0, "kind": "Switch", "branches": [
                        [ { "id": 1, "kind": "Capability", "capability": "pricing.retail@1.0.0" } ],
                        [ { "id": 3, "kind": "Capability", "capability": "pricing.wholesale@1.0.0" } ],
                        []
                      ] },
                    { "id": 4, "kind": "Capability", "capability": "order.confirm@1.0.0" }
                  ],
                  "emits": []
                }
              ],
              "capabilities": []
            }
            """;

        var diagram = MermaidRenderer.Render(Parse(NoDefault));

        diagram.ShouldContain("f0s0 -->|case 0| f0s1");
        diagram.ShouldContain("f0s0 -->|case 1| f0s3");
        diagram.ShouldContain("f0s1 --> f0s4");
        diagram.ShouldContain("f0s3 --> f0s4");
        diagram.ShouldContain("f0s0 --> f0s4");
    }
}
