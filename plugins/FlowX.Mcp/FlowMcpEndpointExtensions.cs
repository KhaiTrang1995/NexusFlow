using System.Text.Json;
using FlowX.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FlowX.Mcp;

/// <summary>
/// Serves the agent surface over MCP's Streamable HTTP transport: one route, JSON-RPC in,
/// JSON-RPC out.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The endpoint's whole job is translation, exactly like
/// <c>FlowEndpointExtensions</c>'s.</strong> It reads a JSON-RPC message, asks
/// <see cref="McpServer"/>, and writes the answer. It makes no authorisation decision and
/// holds no tool list of its own — the first belongs to the step loop and the second to the
/// manifest.
/// </para>
/// <para>
/// <strong>The invocation is built by <see cref="HttpTriggerReader"/>, which is the same
/// code an <c>[HttpTrigger]</c> route uses.</strong> That is the mechanism behind
/// docs/25-Remaining-Platform.md §3's "the stance is the same one HTTP enforces": the
/// principal an agent's call carries is resolved by the same reader, from the same
/// validated claims, into the same <c>FlowInvocation</c> field the engine decides against.
/// An agent gets no separate authorisation path because there is no second reader for one
/// to live in.
/// </para>
/// <para>
/// <strong>No minimal-API delegate binding</strong>, for the reason
/// <c>FlowEndpointExtensions</c> gives: delegate binding reflects over the handler's
/// parameters, which constraint C2 forbids for anything shipping into a user's process.
/// </para>
/// </remarks>
public static class FlowMcpEndpointExtensions
{
    /// <summary>The route the agent surface is served on unless the host names another.</summary>
    public const string DefaultRoute = "/mcp";

    /// <summary>Maps the MCP endpoint.</summary>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="route">The route, defaulting to <see cref="DefaultRoute"/>.</param>
    /// <returns>
    /// The endpoint, so a convention — authentication, rate limiting, CORS — can be applied
    /// to it. Applying an <c>.RequireAuthorization()</c> here is how a deployment demands
    /// that an agent be authenticated before it can even enumerate the tools; the stances
    /// on the capabilities remain what decides whether a call proceeds.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// No <see cref="McpServer"/> is registered, or the manifest and the bound tools
    /// disagree. Both are start-up failures: the second is checked while this method
    /// resolves the server, so a host that publishes a tool nothing serves never becomes
    /// ready.
    /// </exception>
    public static IEndpointConventionBuilder MapFlowXMcp(
        this IEndpointRouteBuilder endpoints, string route = DefaultRoute)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(route);

        // Resolved now rather than per request, so the catalogue is projected and checked
        // against the bindings while the routes are being registered — before the process
        // reports itself ready. The alternative is a pod that passes its health check and
        // fails the first tools/call.
        var server = endpoints.ServiceProvider.GetService<McpServer>()
            ?? throw new InvalidOperationException(
                "No McpServer is registered, so there is no agent surface to map. Call the " +
                "generated AddFlowXAgentTools() on the service collection — it registers " +
                "the manifest the tools are projected from and one binding per flow that " +
                "declares [AgentTrigger].");

        var builder = endpoints.Map(
            route,
            async (HttpContext context) => await HandleAsync(context, server).ConfigureAwait(false));

        builder.WithMetadata(new HttpMethodMetadata([HttpMethods.Post]));

        return builder;
    }

    private static async Task HandleAsync(HttpContext context, McpServer server)
    {
        JsonDocument request;

        try
        {
            request = await JsonDocument
                .ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            await WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                writer => McpJsonRpc.WriteError(
                    writer,
                    id: null,
                    McpJsonRpc.ParseError,
                    new Error(
                        "mcp.parse_error",
                        "The request body is not a JSON document: " + exception.Message,
                        ErrorCategory.Validation)))
                .ConfigureAwait(false);

            return;
        }

        using (request)
        {
            await DispatchAsync(context, server, request.RootElement).ConfigureAwait(false);
        }
    }

    private static async Task DispatchAsync(
        HttpContext context, McpServer server, JsonElement message)
    {
        var id = message.ValueKind == JsonValueKind.Object && message.TryGetProperty("id", out var value)
            ? value
            : (JsonElement?)null;

        if (message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("method", out var method) ||
            method.ValueKind != JsonValueKind.String)
        {
            await WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                writer => McpJsonRpc.WriteError(
                    writer,
                    id,
                    McpJsonRpc.InvalidRequest,
                    new Error(
                        "mcp.invalid_request",
                        "A JSON-RPC request carries a string 'method' member. " +
                        "This surface serves 'initialize', 'tools/list' and 'tools/call'.",
                        ErrorCategory.Validation)))
                .ConfigureAwait(false);

            return;
        }

        switch (method.GetString())
        {
            case "initialize":
                await WriteResultAsync(
                    context,
                    id,
                    writer => McpJsonRpc.WriteInitialize(writer, ApplicationNameOf(context)))
                    .ConfigureAwait(false);

                return;

            case "tools/list":
                await WriteResultAsync(context, id, server.WriteToolList).ConfigureAwait(false);

                return;

            case "tools/call":
                await CallAsync(context, server, id, Params(message)).ConfigureAwait(false);

                return;

            // A notification — no id, nothing to answer. `notifications/initialized` is the
            // one every MCP client sends, and answering it with a "method not found" would
            // make a correct client look broken.
            case { } name when id is null && name.StartsWith("notifications/", StringComparison.Ordinal):
                context.Response.StatusCode = StatusCodes.Status202Accepted;

                return;

            default:
                await WriteAsync(
                    context,
                    StatusCodes.Status200OK,
                    writer => McpJsonRpc.WriteError(
                        writer,
                        id,
                        McpJsonRpc.MethodNotFound,
                        new Error(
                            "mcp.method_not_found",
                            $"'{method.GetString()}' is not served. This surface serves " +
                            "'initialize', 'tools/list' and 'tools/call'.",
                            ErrorCategory.NotFound)))
                    .ConfigureAwait(false);

                return;
        }
    }

    /// <summary>
    /// Runs one <c>tools/call</c>, or explains why the call did not reach a flow.
    /// </summary>
    /// <remarks>
    /// The three refusals here are the ones that happen before the arguments become the
    /// flow's input, and each is a JSON-RPC error because the call never happened. Once a
    /// flow has run, everything it says — including a <see cref="ErrorCategory.Forbidden"/>
    /// refusal from the step loop — is a <c>result</c> carrying <c>isError</c>, because the
    /// call did happen and this is the answer.
    /// </remarks>
    private static async Task CallAsync(
        HttpContext context, McpServer server, JsonElement? id, JsonElement? parameters)
    {
        if (parameters is not { } given ||
            !given.TryGetProperty("name", out var named) ||
            named.ValueKind != JsonValueKind.String ||
            named.GetString() is not { Length: > 0 } name)
        {
            await WriteAsync(
                context,
                StatusCodes.Status200OK,
                writer => McpJsonRpc.WriteError(
                    writer,
                    id,
                    McpJsonRpc.InvalidParams,
                    new Error(
                        "mcp.tool_not_named",
                        "tools/call requires params.name to be the name of a published tool. " +
                        "Call tools/list for the set this application serves.",
                        ErrorCategory.Validation)))
                .ConfigureAwait(false);

            return;
        }

        // The same reader an [HttpTrigger] route uses, so the agent's principal, tenant and
        // correlation reach the engine by the path every other caller's do. No idempotency
        // key is demanded: `Idempotent` is an [HttpTrigger] declaration and [AgentTrigger]
        // makes no such promise, so demanding one would be an admission rule invented here
        // rather than published in the manifest.
        var invocation = HttpTriggerReader.Read(context, requireIdempotencyKey: false);

        if (invocation.IsFailure)
        {
            await WriteAsync(
                context,
                StatusCodes.Status200OK,
                writer => McpJsonRpc.WriteError(
                    writer, id, McpJsonRpc.InvalidParams, invocation.Error))
                .ConfigureAwait(false);

            return;
        }

        var arguments = given.TryGetProperty("arguments", out var value) ? value : (JsonElement?)null;

        var outcome = await server
            .CallAsync(name, arguments, invocation.Value, context.RequestServices, context.RequestAborted)
            .ConfigureAwait(false);

        if (outcome.Error is { } error && IsSurfaceRefusal(error))
        {
            await WriteAsync(
                context,
                StatusCodes.Status200OK,
                writer => McpJsonRpc.WriteError(writer, id, McpJsonRpc.InvalidParams, error))
                .ConfigureAwait(false);

            return;
        }

        await WriteResultAsync(
            context, id, writer => McpJsonRpc.WriteToolResult(writer, outcome)).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the refusal came from this surface rather than from a flow.
    /// </summary>
    /// <remarks>
    /// Two codes, and they are the only two <see cref="McpErrors"/> raises. Matching on the
    /// code rather than on the category is what keeps a flow's own
    /// <c>ErrorCategory.Validation</c> failure — which a capability is entitled to return —
    /// out of the protocol error channel, where a client would read it as "your request was
    /// malformed" about a request that was fine.
    /// </remarks>
    private static bool IsSurfaceRefusal(Error error) =>
        error.Code is McpErrors.UnknownToolCode or McpErrors.MalformedArgumentsCode;

    private static JsonElement? Params(JsonElement message) =>
        message.TryGetProperty("params", out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    /// <summary>
    /// What the server calls itself in <c>initialize</c>.
    /// </summary>
    /// <remarks>
    /// The host's configured application name — the same string <c>FlowXOptions</c> carries
    /// into every span and every journal row — so an operator reading an agent's transcript
    /// and an operator reading a trace are reading one name.
    /// </remarks>
    private static string ApplicationNameOf(HttpContext context) =>
        context.RequestServices
            .GetService<Microsoft.Extensions.Options.IOptions<Hosting.FlowXOptions>>()
            ?.Value.ApplicationName
        ?? "FlowX";

    private static Task WriteResultAsync(
        HttpContext context, JsonElement? id, Action<Utf8JsonWriter> result) =>
        WriteAsync(
            context,
            StatusCodes.Status200OK,
            writer => McpJsonRpc.WriteResult(writer, id, result));

    private static async Task WriteAsync(HttpContext context, int status, Action<Utf8JsonWriter> body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = McpJsonRpc.ContentType;

        var writer = new Utf8JsonWriter(context.Response.BodyWriter);

        await using (writer.ConfigureAwait(false))
        {
            body(writer);
        }

        await context.Response.BodyWriter.FlushAsync(context.RequestAborted).ConfigureAwait(false);
    }
}
