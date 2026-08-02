using System.Text;
using System.Text.Json;

namespace FlowX.Mcp;

/// <summary>What a client's human said about one tool call.</summary>
public enum ElicitationOutcome
{
    /// <summary>The client did not answer, or answered something this surface cannot read.</summary>
    Unanswered = 0,

    /// <summary>A human approved the call.</summary>
    Accepted = 1,

    /// <summary>A human refused it.</summary>
    Declined = 2,

    /// <summary>A human dismissed the prompt without deciding.</summary>
    Cancelled = 3,
}

/// <summary>
/// The confirmation prompt, and what it is allowed to say.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every sentence in the prompt is read out of the manifest or out of the agent's own
/// arguments, and this is the whole reason the prompt is accurate.</strong> The tool's
/// description is the flow author's; the consequences are the <c>SideEffects</c> the
/// capabilities declare; the permissions are their stances. Nothing is inferred from a method
/// name and nothing is composed here that the build did not publish.
/// </para>
/// <para>
/// <strong>What it deliberately does not say, and it is the line docs/13 §6's example crosses.</strong>
/// That example prompts <c>"This will charge EUR 19.98 and reserve 2× SKU-1"</c>. The
/// <em>quantity</em> is in the arguments and is shown. The <em>amount</em> is not: the manifest
/// carries the side effect <c>payment-gateway</c> and carries no price, and computing 19.98 means
/// running the pricing capability — which is running the flow this prompt exists to gate. A
/// prompt that guessed it would be the inaccurate confirmation the section claims cannot happen,
/// so the amount is absent and the effect it lands on is named instead.
/// </para>
/// <para>
/// <strong>A <c>[Sensitive]</c> argument is withheld from the prompt.</strong> The manifest
/// publishes which members of the input contract carry the marker, so the same declaration that
/// keeps a payment token out of an RFC 7807 body and out of a journal payload keeps it out of the
/// text a human reads and a client transcribes. The member is listed as withheld rather than
/// omitted silently, because a prompt that hides that it is hiding something is a worse prompt.
/// </para>
/// </remarks>
public static class McpElicitation
{
    /// <summary>The JSON-RPC method that asks a client's human for a decision.</summary>
    public const string Method = "elicitation/create";

    /// <summary>The single member of the schema the client is asked to fill in.</summary>
    public const string ApproveMember = "approve";

    /// <summary>Writes the <c>params</c> of an <c>elicitation/create</c> request.</summary>
    /// <param name="writer">The request writer.</param>
    /// <param name="tool">The descriptor, projected from the manifest.</param>
    /// <param name="arguments">The agent's own <c>params.arguments</c>, or <c>null</c>.</param>
    public static void WriteRequest(
        Utf8JsonWriter writer, McpToolDescriptor tool, JsonElement? arguments)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(tool);

        writer.WriteStartObject();
        writer.WriteString("message", Message(tool, arguments));

        // MCP requires a flat object schema of primitives, so the decision is one boolean and
        // the detail is in the message. A richer schema would be a form the human fills in for
        // a call whose arguments the agent already chose.
        writer.WritePropertyName("requestedSchema");
        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WritePropertyName("properties");
        writer.WriteStartObject();
        writer.WritePropertyName(ApproveMember);
        writer.WriteStartObject();
        writer.WriteString("type", "boolean");
        writer.WriteString("description", "Approve this call.");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WritePropertyName("required");
        writer.WriteStartArray();
        writer.WriteStringValue(ApproveMember);
        writer.WriteEndArray();
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    /// <summary>The prompt a human is shown.</summary>
    /// <param name="tool">The descriptor, projected from the manifest.</param>
    /// <param name="arguments">The agent's own <c>params.arguments</c>, or <c>null</c>.</param>
    /// <remarks>
    /// Public because it is the claim: <c>samples/ai-agent</c>'s test asserts the text names the
    /// declared effects and does not name a <c>[Sensitive]</c> value, and a prompt assembled
    /// inside a request handler could not be asserted at all.
    /// </remarks>
    public static string Message(McpToolDescriptor tool, JsonElement? arguments)
    {
        ArgumentNullException.ThrowIfNull(tool);

        var text = new StringBuilder();

        text.Append(tool.Description is { Length: > 0 } description
            ? description
            : $"Run the flow '{tool.FlowId}'.");

        AppendArguments(text, tool, arguments);

        if (tool.SideEffects.Count > 0)
        {
            text.Append("\n\nDeclared consequences: ")
                .Append(string.Join(", ", tool.SideEffects))
                .Append('.');

            // Stated because the reader is deciding whether to allow a repeat, and because the
            // conjunction the descriptor publishes is the honest reading: a flow is safe to call
            // twice only if every capability in it is.
            text.Append(tool.Idempotent
                ? " Every step of this flow declares itself idempotent, so running it twice is declared safe."
                : " At least one step of this flow is not idempotent, so running it twice is not the same as running it once.");
        }
        else
        {
            text.Append("\n\nThe flow declares no side effects.");
        }

        if (tool.RequiredPermissions.Count > 0)
        {
            // The permissions are advisory here and the sentence says so. The decision is taken
            // in the step loop against the caller's own claims (ADR-0027); approving this prompt
            // grants nothing, and a caller lacking a grant is refused after the approval exactly
            // as it would have been without one.
            text.Append("\n\nThe caller must already hold ")
                .Append(string.Join(", ", tool.RequiredPermissions))
                .Append(". Approving does not grant it.");
        }

        return text.ToString();
    }

    /// <summary>Reads the client's answer.</summary>
    /// <param name="resultJson">The <c>result</c> member of the client's response, as JSON text.</param>
    /// <remarks>
    /// <para>
    /// MCP's three actions map to three outcomes and an unreadable answer maps to a fourth, which
    /// the caller treats exactly as it treats a decline. Reading a missing or malformed answer as
    /// approval is the one interpretation this method may not make.
    /// </para>
    /// <para>
    /// An <c>accept</c> whose content says <c>approve: false</c> is a decline. The action says the
    /// human engaged with the form; the member says what they chose.
    /// </para>
    /// </remarks>
    public static ElicitationOutcome ReadOutcome(string? resultJson)
    {
        if (resultJson is null)
        {
            return ElicitationOutcome.Unanswered;
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(resultJson);
        }
        catch (JsonException)
        {
            return ElicitationOutcome.Unanswered;
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("action", out var action) ||
                action.ValueKind != JsonValueKind.String)
            {
                return ElicitationOutcome.Unanswered;
            }

            return action.GetString() switch
            {
                "accept" => Approved(root) ? ElicitationOutcome.Accepted : ElicitationOutcome.Declined,
                "decline" => ElicitationOutcome.Declined,
                "cancel" => ElicitationOutcome.Cancelled,
                _ => ElicitationOutcome.Unanswered,
            };
        }
    }

    /// <summary>Whether the accepted form carries approval.</summary>
    /// <remarks>
    /// A form with no <c>approve</c> member at all is read as approval, because the action was
    /// <c>accept</c> and the schema declares the member required — a client that accepted without
    /// it has answered the question with the action rather than with the content.
    /// </remarks>
    private static bool Approved(JsonElement result) =>
        !result.TryGetProperty("content", out var content) ||
        content.ValueKind != JsonValueKind.Object ||
        !content.TryGetProperty(ApproveMember, out var approve) ||
        approve.ValueKind != JsonValueKind.False;

    /// <summary>Appends the agent's arguments, withholding the ones declared sensitive.</summary>
    private static void AppendArguments(
        StringBuilder text, McpToolDescriptor tool, JsonElement? arguments)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } given)
        {
            return;
        }

        var shown = new List<string>();
        var withheld = new List<string>();

        foreach (var member in given.EnumerateObject())
        {
            if (IsSensitive(tool, member.Name))
            {
                withheld.Add(member.Name);
            }
            else
            {
                shown.Add(member.Name + " = " + Rendered(member.Value));
            }
        }

        if (shown.Count > 0)
        {
            text.Append("\n\nArguments: ").Append(string.Join(", ", shown)).Append('.');
        }

        if (withheld.Count > 0)
        {
            text.Append("\n\nWithheld as [Sensitive]: ").Append(string.Join(", ", withheld)).Append('.');
        }
    }

    /// <summary>
    /// Whether the contract declares this argument sensitive.
    /// </summary>
    /// <remarks>
    /// Compared case-insensitively, because the manifest publishes the CLR member name —
    /// <c>PaymentToken</c> — and the argument arrives spelled as the flow's own serialiser
    /// context writes it, which is <c>paymentToken</c> for every contract in this repository. A
    /// case-sensitive comparison here would put a token into a prompt for exactly the contracts
    /// that marked it.
    /// </remarks>
    private static bool IsSensitive(McpToolDescriptor tool, string member)
    {
        foreach (var sensitive in tool.SensitiveInputMembers)
        {
            if (string.Equals(sensitive, member, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>One argument value, as a human reads it.</summary>
    /// <remarks>
    /// A nested object or array is summarised by its kind rather than transcribed. The prompt is
    /// a sentence, and a request body pasted into it is the shape a human stops reading.
    /// </remarks>
    private static string Rendered(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => "\"" + value.GetString() + "\"",
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
        JsonValueKind.Null => "null",
        JsonValueKind.Array => "[" + value.GetArrayLength() + " items]",
        _ => "{object}",
    };
}
