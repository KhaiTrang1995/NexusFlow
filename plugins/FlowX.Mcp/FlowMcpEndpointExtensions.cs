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
/// <strong>It serves three request directions, and the third is why this file grew.</strong> A
/// client request is answered; a client notification is accepted; and a client <em>response</em>
/// — a message with an id and no method — is the answer to something this server asked, and is
/// routed to whichever <c>tools/call</c> is blocked waiting for it. That third direction is what
/// makes <c>elicitation/create</c> and <c>sampling/createMessage</c> possible over HTTP at all.
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
    /// <param name="options">
    /// What this deployment allows the surface to do beyond answering. Defaults to
    /// <see cref="McpOptions.Default"/>, which is the behaviour this endpoint has served since it
    /// shipped: publish <c>confirmationRequired</c> and leave the asking to the client.
    /// </param>
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
        this IEndpointRouteBuilder endpoints, string route = DefaultRoute, McpOptions? options = null)
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

        var pending = endpoints.ServiceProvider.GetService<McpPendingClientRequests>()
            ?? throw new InvalidOperationException(
                "No McpPendingClientRequests is registered. It is registered alongside " +
                "McpServer by FlowAgentToolRegistration, so a host that has one and not the " +
                "other has replaced one of them by hand.");

        var settings = options ?? McpOptions.Default;

        var builder = endpoints.Map(
            route,
            async (HttpContext context) =>
                await HandleAsync(context, server, pending, settings).ConfigureAwait(false));

        builder.WithMetadata(new HttpMethodMetadata([HttpMethods.Post]));

        return builder;
    }

    private static async Task HandleAsync(
        HttpContext context, McpServer server, McpPendingClientRequests pending, McpOptions options)
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
            await DispatchAsync(context, server, pending, options, request.RootElement)
                .ConfigureAwait(false);
        }
    }

    private static async Task DispatchAsync(
        HttpContext context,
        McpServer server,
        McpPendingClientRequests pending,
        McpOptions options,
        JsonElement message)
    {
        var id = message.ValueKind == JsonValueKind.Object && message.TryGetProperty("id", out var value)
            ? value
            : (JsonElement?)null;

        if (message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("method", out var method) ||
            method.ValueKind != JsonValueKind.String)
        {
            // A message with an id and no method is a *response* — the client answering
            // something this server asked over an open event stream. It is dispatched here
            // rather than reported as invalid, because it is the return leg of the only
            // exchange in MCP that the server starts.
            if (id is { } answered && TryDeliver(pending, answered, message))
            {
                context.Response.StatusCode = StatusCodes.Status202Accepted;

                return;
            }

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
                        "This surface serves 'initialize', 'tools/list', 'tools/call', " +
                        "'resources/list' and 'resources/read', and accepts a response to a " +
                        "request it has sent.",
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

            case "resources/list":
                await WriteResultAsync(context, id, server.WriteResourceList).ConfigureAwait(false);

                return;

            case "resources/read":
                await ReadResourceAsync(context, server, id, Params(message)).ConfigureAwait(false);

                return;

            case "tools/call":
                await CallAsync(context, server, pending, options, id, message).ConfigureAwait(false);

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
                            "'initialize', 'tools/list', 'tools/call', 'resources/list' and " +
                            "'resources/read'.",
                            ErrorCategory.NotFound)))
                    .ConfigureAwait(false);

                return;
        }
    }

    /// <summary>
    /// Hands a client's response to whatever is waiting for it.
    /// </summary>
    /// <remarks>
    /// A response to an id nobody is waiting for is not delivered and not an error either — the
    /// asker may have timed out, or the client may be replaying. Returning <c>false</c> lets the
    /// caller answer with the invalid-request message that names what this surface serves, which
    /// is the more useful thing to tell a client that sent a response nobody asked for.
    /// </remarks>
    private static bool TryDeliver(
        McpPendingClientRequests pending, JsonElement id, JsonElement message)
    {
        if (id.ValueKind != JsonValueKind.String || id.GetString() is not { Length: > 0 } key)
        {
            return false;
        }

        // A JSON-RPC error from the client is delivered as a null result. The channel reads that
        // as "the client would not answer", which is the only distinction any caller here makes:
        // a client that cannot elicit and a human who declined both mean the flow does not run.
        var result = message.TryGetProperty("result", out var value)
            ? value.GetRawText()
            : null;

        return pending.Complete(key, result);
    }

    /// <summary>Serves one <c>resources/read</c>.</summary>
    /// <remarks>
    /// The two refusals mirror <see cref="CallAsync"/>'s: a missing <c>uri</c> is a malformed
    /// request and an unpublished one is a fact about this application, and both are JSON-RPC
    /// errors because no flow ran either way.
    /// </remarks>
    private static async Task ReadResourceAsync(
        HttpContext context, McpServer server, JsonElement? id, JsonElement? parameters)
    {
        if (parameters is not { } given ||
            !given.TryGetProperty("uri", out var addressed) ||
            addressed.ValueKind != JsonValueKind.String ||
            addressed.GetString() is not { Length: > 0 } uri)
        {
            await WriteAsync(
                context,
                StatusCodes.Status200OK,
                writer => McpJsonRpc.WriteError(
                    writer,
                    id,
                    McpJsonRpc.InvalidParams,
                    new Error(
                        "mcp.resource_not_named",
                        "resources/read requires params.uri to be the address of a published " +
                        "resource. Call resources/list for the set this application serves.",
                        ErrorCategory.Validation)))
                .ConfigureAwait(false);

            return;
        }

        if (server.Resources.Read(uri) is not { } json)
        {
            await WriteAsync(
                context,
                StatusCodes.Status200OK,
                writer => McpJsonRpc.WriteError(
                    writer, id, McpJsonRpc.InvalidParams, McpErrors.UnknownResource(uri)))
                .ConfigureAwait(false);

            return;
        }

        await WriteResultAsync(
            context, id, writer => McpResourceCatalog.WriteContents(writer, uri, json))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one <c>tools/call</c>, or explains why the call did not reach a flow.
    /// </summary>
    /// <remarks>
    /// The refusals here are the ones that happen before the arguments become the flow's
    /// input, and each is a JSON-RPC error because the call never happened. Once a
    /// flow has run, everything it says — including a <see cref="ErrorCategory.Forbidden"/>
    /// refusal from the step loop — is a <c>result</c> carrying <c>isError</c>, because the
    /// call did happen and this is the answer.
    /// </remarks>
    private static async Task CallAsync(
        HttpContext context,
        McpServer server,
        McpPendingClientRequests pending,
        McpOptions options,
        JsonElement? id,
        JsonElement message)
    {
        var parameters = Params(message);

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

        if (!McpEventChannel.IsAccepted(context.Request))
        {
            // The client cannot read an event stream, so there is no way to ask it anything.
            // Under Elicit that is a refusal for a tool the manifest says needs a human; under
            // Annotate it is the plain JSON exchange this surface has always served.
            if (Unconfirmable(server, options, name) is { } refusal)
            {
                await WriteResultAsync(
                    context,
                    id,
                    writer => McpJsonRpc.WriteToolResult(writer, McpToolOutcome.Failed(refusal)))
                    .ConfigureAwait(false);

                return;
            }

            var plain = await server
                .CallAsync(name, arguments, invocation.Value, context.RequestServices, context.RequestAborted)
                .ConfigureAwait(false);

            await AnswerAsync(context, id, plain).ConfigureAwait(false);

            return;
        }

        var stream = await McpEventChannel
            .OpenAsync(context, pending, options.ClientRequestTimeout)
            .ConfigureAwait(false);

        await using var _ = stream.ConfigureAwait(false);

        // The channel is put into the request scope before the flow runs, so a capability that
        // takes IAgentSampler is talking to this call's client and to no other. It is cleared
        // afterwards because the scope outlives the stream by the length of the response write,
        // and a sampler holding a closed stream is worse than one holding nothing.
        var scope = context.RequestServices.GetService<McpCallScope>();

        if (scope is not null)
        {
            scope.Channel = stream;
        }

        try
        {
            var confirmed = await ConfirmAsync(server, options, stream, name, arguments, context.RequestAborted)
                .ConfigureAwait(false);

            var outcome = confirmed is { } declined
                ? McpToolOutcome.Failed(declined)
                : await server
                    .CallAsync(name, arguments, invocation.Value, context.RequestServices, context.RequestAborted)
                    .ConfigureAwait(false);

            await stream
                .WriteAsync(writer => WriteAnswer(writer, id, outcome), context.RequestAborted)
                .ConfigureAwait(false);
        }
        finally
        {
            if (scope is not null)
            {
                scope.Channel = null;
            }
        }
    }

    /// <summary>
    /// The refusal a tool needing a human gets from a client that cannot be asked, or
    /// <c>null</c> when the call may proceed.
    /// </summary>
    private static Error? Unconfirmable(McpServer server, McpOptions options, string name) =>
        options.Confirmation == ConfirmationPolicy.Elicit &&
        server.Catalog.Find(name) is { ConfirmationRequired: true }
            ? McpErrors.ConfirmationRefused(
                name,
                "unelicitable",
                "this call cannot be answered by a human: the client did not accept " +
                "'text/event-stream', so there is no channel to ask one over.")
            : null;

    /// <summary>
    /// Asks the client's human, when this deployment requires it, and reports a refusal.
    /// </summary>
    /// <remarks>
    /// Returns <c>null</c> for "proceed", which covers three cases that are one decision: the
    /// deployment does not elicit, the tool declares no consequence to confirm, or a human
    /// approved. The three that stop the call — declined, cancelled, unanswered — are one
    /// <see cref="McpErrors.ConfirmationRefused"/> distinguished by its <c>outcome</c> detail,
    /// because they are one fact to the agent: the flow was not entered.
    /// </remarks>
    private static async ValueTask<Error?> ConfirmAsync(
        McpServer server,
        McpOptions options,
        McpEventChannel channel,
        string name,
        JsonElement? arguments,
        CancellationToken ct)
    {
        if (options.Confirmation != ConfirmationPolicy.Elicit ||
            server.Catalog.Find(name) is not { ConfirmationRequired: true } tool)
        {
            return null;
        }

        var answer = await channel
            .RequestAsync(
                McpElicitation.Method,
                writer => McpElicitation.WriteRequest(writer, tool, arguments),
                ct)
            .ConfigureAwait(false);

        return McpElicitation.ReadOutcome(answer.ResultJson) switch
        {
            ElicitationOutcome.Accepted => null,

            ElicitationOutcome.Declined => McpErrors.ConfirmationRefused(
                name, "declined", "a human refused it."),

            ElicitationOutcome.Cancelled => McpErrors.ConfirmationRefused(
                name, "cancelled", "a human dismissed the prompt without deciding."),

            _ => answer.Error ?? McpErrors.ConfirmationRefused(
                name, "unanswered", "the client's answer could not be read as a decision."),
        };
    }

    /// <summary>Answers a plain-JSON <c>tools/call</c>.</summary>
    private static Task AnswerAsync(HttpContext context, JsonElement? id, McpToolOutcome outcome) =>
        outcome.Error is { } error && IsSurfaceRefusal(error)
            ? WriteAsync(
                context,
                StatusCodes.Status200OK,
                writer => McpJsonRpc.WriteError(writer, id, McpJsonRpc.InvalidParams, error))
            : WriteResultAsync(context, id, writer => McpJsonRpc.WriteToolResult(writer, outcome));

    /// <summary>Writes one outcome as a complete JSON-RPC message, for an event-stream frame.</summary>
    private static void WriteAnswer(Utf8JsonWriter writer, JsonElement? id, McpToolOutcome outcome)
    {
        if (outcome.Error is { } error && IsSurfaceRefusal(error))
        {
            McpJsonRpc.WriteError(writer, id, McpJsonRpc.InvalidParams, error);

            return;
        }

        McpJsonRpc.WriteResult(writer, id, inner => McpJsonRpc.WriteToolResult(inner, outcome));
    }

    /// <summary>
    /// Whether the refusal came from this surface rather than from a flow.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Matching on the code rather than on the category is what keeps a flow's own
    /// <c>ErrorCategory.Validation</c> failure — which a capability is entitled to return —
    /// out of the protocol error channel, where a client would read it as "your request was
    /// malformed" about a request that was fine.
    /// </para>
    /// <para>
    /// <strong>A refused confirmation is deliberately not in this list.</strong> The other two
    /// codes mean the request could not be understood; this one means it was understood
    /// perfectly and a human said no. That is an outcome of the call, so it travels as a
    /// <c>result</c> with <c>isError</c> like a flow's own refusal — which is also what lets a
    /// model read the reason and stop, rather than see a protocol fault and retry.
    /// </para>
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
