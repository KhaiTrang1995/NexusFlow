using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FlowX.Http;

/// <summary>
/// Serves the application's own description.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Served rather than generated at build time, and both would be reasonable.</strong> A
/// build artifact is what a pipeline publishes; an endpoint is what a running deployment can be
/// asked, which is the version that cannot be stale. This is the endpoint;
/// <see cref="OpenApi.Write(string, IJsonTypeInfoResolver?, IReadOnlyDictionary{string, Type}?)"/>
/// is public so a build step can write the same bytes to a file.
/// </para>
/// <para>
/// <strong>Anonymous by default, and that is a decision rather than an oversight.</strong> The
/// document names routes, contracts and error codes — not data — and an API description that
/// needs a credential to read is one that client generators, gateways and the person integrating
/// on a Friday afternoon cannot use. A deployment that disagrees calls
/// <c>RequireAuthorization()</c> on what this returns.
/// </para>
/// </remarks>
public static class OpenApiEndpointExtensions
{
    /// <summary>The route the document is served on unless another is given.</summary>
    public const string DefaultRoute = "/openapi.json";

    /// <summary>The route the manifest is served on unless another is given.</summary>
    public const string DefaultManifestRoute = "/flowx.manifest.json";

    /// <summary>Serves the manifest itself, unchanged.</summary>
    /// <param name="endpoints">Where to map it.</param>
    /// <param name="manifestJson">
    /// The application's manifest — <c>FlowX.Generated.FlowXManifest.Json</c>.
    /// </param>
    /// <param name="route">Where to serve it.</param>
    /// <returns>The endpoint, so a deployment can add its own conventions.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <remarks>
    /// <para>
    /// <strong>Not the same document as <see cref="MapFlowXOpenApi(IEndpointRouteBuilder, string,
    /// string)"/>, and the difference is the point.</strong> OpenAPI describes an HTTP surface:
    /// paths, bodies, status codes. The manifest describes flows — their execution profile, their
    /// deadline, and every trigger including the bus subscriptions, the schedules and the agent
    /// tools that have no path at all. A screen listing what an application does cannot be built
    /// from the first and can be built from the second.
    /// </para>
    /// <para>
    /// <strong>Anonymous for the same reason the document is.</strong> It names flow ids, profiles
    /// and routes; it names no data. A deployment that disagrees calls
    /// <c>RequireAuthorization()</c> on what this returns.
    /// </para>
    /// </remarks>
    public static IEndpointConventionBuilder MapFlowXManifest(
        this IEndpointRouteBuilder endpoints,
        string manifestJson,
        string route = DefaultManifestRoute)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(manifestJson);
        ArgumentNullException.ThrowIfNull(route);

        return endpoints.MapGet(route, (HttpContext context) =>
        {
            context.Response.ContentType = "application/json; charset=utf-8";

            // A compile-time constant: it cannot change while the process runs.
            return context.Response.WriteAsync(manifestJson);
        });
    }

    /// <summary>Serves the OpenAPI document for a manifest.</summary>
    /// <param name="endpoints">Where to map it.</param>
    /// <param name="manifestJson">
    /// The application's manifest — <c>FlowX.Generated.FlowXManifest.Json</c>.
    /// </param>
    /// <param name="resolver">
    /// The application's JSON contract metadata, so the schemas describe the real shapes. Null
    /// leaves the bodies opaque rather than guessed.
    /// </param>
    /// <param name="route">Where to serve it.</param>
    /// <returns>The endpoint, so a deployment can add its own conventions.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <remarks>
    /// The document is written once and held, because the manifest is a compile-time constant: it
    /// cannot change while the process runs, so regenerating it per request would be work whose
    /// result is provably identical.
    /// </remarks>
    [RequiresUnreferencedCode(
        "The manifest names contracts as strings. Use the overload taking the contract types to " +
        "describe them in a trimmed application.")]
    public static IEndpointConventionBuilder MapFlowXOpenApi(
        this IEndpointRouteBuilder endpoints,
        string manifestJson,
        IJsonTypeInfoResolver? resolver = null,
        string route = DefaultRoute) =>
        endpoints.MapFlowXOpenApi(OpenApi.Write(manifestJson, resolver), route);

    /// <summary>Serves an already-written OpenAPI document.</summary>
    /// <param name="endpoints">Where to map it.</param>
    /// <param name="document">The document, from <see cref="OpenApi.Write(string, IJsonTypeInfoResolver?, IReadOnlyDictionary{string, Type}?)"/>.</param>
    /// <param name="route">Where to serve it.</param>
    /// <returns>The endpoint, so a deployment can add its own conventions.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static IEndpointConventionBuilder MapFlowXOpenApi(
        this IEndpointRouteBuilder endpoints,
        string document,
        string route = DefaultRoute)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(route);

        return endpoints.MapGet(route, (HttpContext context) =>
        {
            context.Response.ContentType = "application/json; charset=utf-8";

            return context.Response.WriteAsync(document);
        });
    }

    /// <summary>Serves a self-contained page that renders the document.</summary>
    /// <param name="endpoints">Where to map it.</param>
    /// <param name="documentRoute">Where the document itself is served.</param>
    /// <param name="route">Where to serve the page.</param>
    /// <returns>The endpoint.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <remarks>
    /// <strong>The page loads its viewer from a CDN, and says so.</strong> A deployment that
    /// cannot reach one — or whose content-security policy forbids it — should serve its own
    /// viewer and point it at <paramref name="documentRoute"/>; the document is the artifact that
    /// matters and it is served independently of this.
    /// </remarks>
    public static IEndpointConventionBuilder MapFlowXOpenApiUi(
        this IEndpointRouteBuilder endpoints,
        string documentRoute = DefaultRoute,
        string route = "/openapi")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(documentRoute);
        ArgumentNullException.ThrowIfNull(route);

        var page =
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>API</title>"
            + "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
            + "<link rel=\"stylesheet\" href=\"https://unpkg.com/swagger-ui-dist/swagger-ui.css\">"
            + "</head><body><div id=\"ui\"></div>"
            + "<script src=\"https://unpkg.com/swagger-ui-dist/swagger-ui-bundle.js\"></script>"
            + "<script>SwaggerUIBundle({url:\""
            + JsonEncodedText.Encode(documentRoute)
            + "\",dom_id:'#ui'});</script></body></html>";

        return endpoints.MapGet(route, (HttpContext context) =>
        {
            context.Response.ContentType = "text/html; charset=utf-8";

            return context.Response.WriteAsync(page);
        });
    }
}
