using System.Text.Json;

namespace FlowX.Mcp;

/// <summary>
/// Renders tool descriptors as MCP publishes them, with <see cref="Utf8JsonWriter"/> and
/// no reflection.
/// </summary>
/// <remarks>
/// Hand-written for <c>ProblemDetailsJson</c>'s reason: this is a small closed shape, and a
/// serialiser would need metadata for types that only exist to be serialised. It is also
/// the boundary at which the manifest's vocabulary becomes MCP's, and keeping that mapping
/// in one readable method is what makes it reviewable against docs/13-AI-Native.md §6.
/// </remarks>
public static class McpToolJson
{
    /// <summary>The property names of a rendered tool descriptor, in the order written.</summary>
    /// <remarks>
    /// <para>
    /// <strong>Exposed so a test can enumerate what this package publishes.</strong>
    /// <c>EveryPublishedFieldOfADescriptorMovesWithTheManifest</c> mutates the manifest once
    /// per field and asserts the rendered descriptor moved with it. That gate is only a
    /// proof if it knows the full field list, and a list it reads off the implementation is
    /// one a new field joins automatically — where a list restated in the test would let a
    /// field be added with no mutation case and no failure.
    /// </para>
    /// <para>
    /// Annotation names are prefixed so the flat list is unambiguous, since
    /// <c>annotations</c> is a nested object on the wire.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> PublishedFields { get; } =
    [
        "name",
        "description",
        "inputSchema.x-flowx-contract",
        "inputSchema.x-flowx-sensitive",
        "annotations.flowId",
        "annotations.idempotent",
        "annotations.sideEffects",
        "annotations.requiredPermissions",
        "annotations.confirmationRequired",
    ];

    /// <summary>Writes the <c>result</c> body of a <c>tools/list</c> response.</summary>
    /// <param name="writer">The response writer.</param>
    /// <param name="catalogue">The projection of this application's manifest.</param>
    public static void WriteToolList(Utf8JsonWriter writer, McpToolCatalog catalogue)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(catalogue);

        writer.WriteStartObject();
        writer.WritePropertyName("tools");
        writer.WriteStartArray();

        foreach (var tool in catalogue.Tools)
        {
            WriteTool(writer, tool);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>Writes one tool descriptor.</summary>
    /// <param name="writer">The response writer.</param>
    /// <param name="tool">The descriptor, projected from the manifest.</param>
    public static void WriteTool(Utf8JsonWriter writer, McpToolDescriptor tool)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(tool);

        writer.WriteStartObject();
        writer.WriteString("name", tool.Name);

        if (tool.Description is { Length: > 0 } description)
        {
            writer.WriteString("description", description);
        }

        // The manifest carries no JSON Schema for a contract — its top-level `schemas` map
        // is one of ADR-0017's thirteen declared-but-unwritten fields — so this is an open
        // object naming the contract it accepts. Everything the manifest knows, and nothing
        // it does not. See McpToolDescriptor.InputContract.
        writer.WritePropertyName("inputSchema");
        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WriteString("x-flowx-contract", tool.InputContract);
        WriteStrings(writer, "x-flowx-sensitive", tool.SensitiveInputMembers);
        writer.WriteEndObject();

        writer.WritePropertyName("annotations");
        writer.WriteStartObject();
        writer.WriteString("flowId", tool.FlowId);
        writer.WriteBoolean("idempotent", tool.Idempotent);
        writer.WriteBoolean("confirmationRequired", tool.ConfirmationRequired);
        WriteStrings(writer, "sideEffects", tool.SideEffects);
        WriteStrings(writer, "requiredPermissions", tool.RequiredPermissions);
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes a string array, or nothing at all when it is empty.
    /// </summary>
    /// <remarks>
    /// Omitted rather than written empty, so a descriptor states what the flow declares and
    /// stays silent about what it does not. An empty <c>sideEffects</c> and an absent one
    /// read the same to a client, and the shorter document is the one a model has to read.
    /// </remarks>
    private static void WriteStrings(
        Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        writer.WritePropertyName(name);
        writer.WriteStartArray();

        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }
}
