using System.Text;
using System.Text.Json;
using FlowX.Generated;
using Shouldly;
using Xunit;

namespace FlowX.Mcp.Tests;

/// <summary>
/// The load-bearing property: a tool descriptor cannot say something the manifest does not.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two gates, and neither of them is "the descriptor matches the manifest
/// today".</strong> An equality assertion over the current document passes for any
/// projection that happens to agree with it, including one that read the same facts off the
/// compiled types by reflection and coincided. What has to be shown is that the manifest is
/// the <em>only</em> input — that changing it changes the descriptor, and that there is
/// nothing else a descriptor could have been computed from.
/// </para>
/// <para>
/// <see cref="EveryPublishedFieldOfADescriptorMovesWithTheManifest"/> is the first: it
/// mutates one manifest field per published descriptor field and requires the rendered
/// descriptor to move with it. A field sourced anywhere else would not move, and its case
/// would fail. <see cref="EveryPublishedFieldHasAMutationCase"/> is the second, and it is
/// what stops the first from decaying: it reads the published field list off
/// <see cref="McpToolJson.PublishedFields"/> rather than restating it, so a field added to
/// the descriptor with no mutation case fails here rather than passing everywhere.
/// </para>
/// <para>
/// The manifest under test is <c>FlowX.Generated.FlowXManifest.Json</c> — this assembly's
/// own, written by the real compiler from the real flows in
/// <c>AgentToolFlows.cs</c>, not a fixture. So the projection is exercised against the exact
/// bytes a build publishes.
/// </para>
/// </remarks>
public sealed class ToolDescriptorsCannotDriftTests
{
    /// <summary>
    /// One manifest field, the descriptor field it feeds, and how to change it.
    /// </summary>
    /// <param name="DescriptorField">
    /// The published name, as <see cref="McpToolJson.PublishedFields"/> spells it.
    /// </param>
    /// <param name="Find">The exact substring to replace in the manifest.</param>
    /// <param name="ReplaceWith">What to replace it with.</param>
    /// <param name="Expect">
    /// What the rendered descriptor must then contain. Absent for a mutation whose only
    /// required effect is that the descriptor stopped saying what it said.
    /// </param>
    public sealed record Mutation(
        string DescriptorField, string Find, string ReplaceWith, string? Expect = null);

    /// <summary>
    /// One mutation per published field of a descriptor.
    /// </summary>
    /// <remarks>
    /// Every <c>Find</c> is a fragment of this assembly's own generated manifest, so a
    /// change to <c>AgentToolFlows.cs</c> or to <c>ManifestWriter</c> that moves a field
    /// breaks these loudly rather than silently making them no-ops —
    /// <see cref="EveryMutationChangesTheManifest"/> is the explicit check for that.
    /// </remarks>
    public static IEnumerable<Mutation> Mutations =>
    [
        // The flow's id feeds both the tool's name and the flowId annotation, which is the
        // point: they are one value read once, not two that happen to agree.
        new("name", @"""id"": ""booking.book""", @"""id"": ""booking.reserve""", "booking_reserve"),
        new("annotations.flowId", @"""id"": ""booking.book""", @"""id"": ""booking.reserve""", "booking.reserve"),

        new(
            "description",
            "Book a room for a number of nights and charge the card on file.",
            "Reserve a suite.",
            "Reserve a suite."),

        new(
            "inputSchema.x-flowx-contract",
            @"""type"": ""FlowX.Mcp.Tests.BookRoom""",
            @"""type"": ""FlowX.Mcp.Tests.ReserveSuite""",
            "FlowX.Mcp.Tests.ReserveSuite"),

        new("inputSchema.x-flowx-sensitive", @"""CardNumber""", @"""Passport""", "Passport"),

        // The manifest states idempotency on the capability, and the descriptor states it
        // for the flow. Flipping the capability's flag has to move the flow's annotation, or
        // the annotation was not derived from it.
        new(
            "annotations.idempotent",
            @"""id"": ""booking.charge"",
      ""version"": ""1.0.0"",
      ""input"": ""FlowX.Mcp.Tests.BookRoom"",
      ""output"": ""FlowX.Mcp.Tests.Booking"",
      ""authorization"": {
        ""mode"": ""Permission"",
        ""value"": ""booking:write""
      },
      ""idempotent"": false",
            @"""id"": ""booking.charge"",
      ""version"": ""1.0.0"",
      ""input"": ""FlowX.Mcp.Tests.BookRoom"",
      ""output"": ""FlowX.Mcp.Tests.Booking"",
      ""authorization"": {
        ""mode"": ""Permission"",
        ""value"": ""booking:write""
      },
      ""idempotent"": true",
            @"""idempotent"": true"),

        new("annotations.sideEffects", @"""payment-gateway""", @"""ledger""", "ledger"),

        new(
            "annotations.requiredPermissions",
            @"""value"": ""booking:write""",
            @"""value"": ""suite:write""",
            "suite:write"),

        // The confirmation requirement is the conjunction of the trigger's mode and the
        // capability's side effects, so either half must be able to move it. This mutates
        // the mode; the sideEffects case above proves the other half is read at all.
        new(
            "annotations.confirmationRequired",
            @"""confirmation"": ""RequiredForSideEffects""",
            @"""confirmation"": ""Never""",
            @"""confirmationRequired"": false"),
    ];

    public static TheoryData<Mutation> MutationCases
    {
        get
        {
            var data = new TheoryData<Mutation>();

            foreach (var mutation in Mutations)
            {
                data.Add(mutation);
            }

            return data;
        }
    }

    /// <summary>
    /// Change the manifest, and the descriptor changes with it.
    /// </summary>
    /// <remarks>
    /// The proof that the manifest is the descriptor's source rather than something the
    /// descriptor is checked against. A projection that read the flow's CLR types, or that
    /// carried a copy emitted into generated source, would answer identically before and
    /// after this edit — and every one of these cases would fail.
    /// </remarks>
    [Theory]
    [MemberData(nameof(MutationCases))]
    public void EveryPublishedFieldOfADescriptorMovesWithTheManifest(Mutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);

        var before = Render(FlowXManifest.Json);
        var after = Render(Mutate(FlowXManifest.Json, mutation));

        after.ShouldNotBe(
            before,
            $"'{mutation.DescriptorField}' did not move when the manifest field behind it " +
            "did. It is being read from somewhere other than the manifest, which is a " +
            "second description of the same flow that flowx diff cannot see.");

        if (mutation.Expect is { } expected)
        {
            after.ShouldContain(
                expected,
                Case.Sensitive,
                $"'{mutation.DescriptorField}' moved, and not to the manifest's new value.");
        }
    }

    /// <summary>
    /// Every field the descriptor publishes has a mutation case above.
    /// </summary>
    /// <remarks>
    /// The gate on the gate. <see cref="McpToolJson.PublishedFields"/> is read off the
    /// implementation, so adding a field to a descriptor and forgetting a mutation for it
    /// fails here — where restating the list in this file would have let the new field be
    /// derived from anything at all with nothing objecting.
    /// </remarks>
    [Fact]
    public void EveryPublishedFieldHasAMutationCase()
    {
        var covered = Mutations
            .Select(static mutation => mutation.DescriptorField)
            .ToHashSet(StringComparer.Ordinal);

        McpToolJson.PublishedFields
            .Where(field => !covered.Contains(field))
            .ShouldBeEmpty(
                "A descriptor publishes a field that no mutation case drives, so nothing " +
                "shows it came from the manifest. Add a case to Mutations naming the " +
                "manifest field it is read from.");

        covered
            .Where(field => !McpToolJson.PublishedFields.Contains(field, StringComparer.Ordinal))
            .ShouldBeEmpty(
                "A mutation case names a descriptor field nothing publishes. Either the " +
                "field was removed and the case is stale, or its name has drifted from " +
                "McpToolJson.PublishedFields.");
    }

    /// <summary>
    /// Every mutation actually edits the manifest.
    /// </summary>
    /// <remarks>
    /// A <c>Find</c> string that no longer occurs would make its case a no-op that still
    /// passes the inequality above only by accident — or worse, fails for a reason nobody
    /// can read. Asserted separately so the failure says "your fixture is stale" rather than
    /// "your projection has drifted".
    /// </remarks>
    [Fact]
    public void EveryMutationChangesTheManifest()
    {
        foreach (var mutation in Mutations)
        {
            FlowXManifest.Json.ShouldContain(
                mutation.Find,
                Case.Sensitive,
                $"The mutation for '{mutation.DescriptorField}' no longer matches anything " +
                "in the generated manifest, so its case proves nothing. The manifest's " +
                "shape has changed; update the fixture.");
        }
    }

    /// <summary>
    /// The tools published are exactly the flows the manifest gives an <c>Agent</c> trigger.
    /// </summary>
    /// <remarks>
    /// The set, where the mutations above are about each descriptor's content. This
    /// assembly declares a third flow with no <c>[AgentTrigger]</c> precisely so that a
    /// projection publishing every flow fails here.
    /// </remarks>
    [Fact]
    public void TheToolSetIsExactlyTheManifestsAgentTriggeredFlows()
    {
        using var manifest = JsonDocument.Parse(FlowXManifest.Json);

        var declared = manifest.RootElement.GetProperty("flows")
            .EnumerateArray()
            .Where(static flow => flow.TryGetProperty("triggers", out var triggers) &&
                triggers.EnumerateArray().Any(static trigger =>
                    trigger.GetProperty("kind").GetString() == "Agent"))
            .Select(static flow => flow.GetProperty("id").GetString()!)
            .ToList();

        declared.ShouldNotContain(
            "booking.audit",
            "booking.audit declares no [AgentTrigger]. If the manifest now gives it one, " +
            "this gate's negative case has gone and a projection publishing every flow " +
            "would pass.");

        McpToolCatalog.From(FlowXManifest.Json).Tools
            .Select(static tool => tool.FlowId)
            .ShouldBe(declared, ignoreOrder: true);
    }

    private static string Mutate(string manifest, Mutation mutation)
    {
        var mutated = manifest.Replace(mutation.Find, mutation.ReplaceWith, StringComparison.Ordinal);

        mutated.ShouldNotBe(
            manifest,
            $"The mutation for '{mutation.DescriptorField}' matched nothing.");

        return mutated;
    }

    /// <summary>The rendered <c>tools/list</c> body for one manifest.</summary>
    private static string Render(string manifest)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            McpToolJson.WriteToolList(writer, McpToolCatalog.From(manifest));
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
