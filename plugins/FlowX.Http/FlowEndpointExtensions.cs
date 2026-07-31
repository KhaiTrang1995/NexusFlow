using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FlowX.Hosting;
using FlowX.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FlowX.Http;

/// <summary>Maps a compiled flow onto an HTTP endpoint.</summary>
/// <remarks>
/// <para>
/// <strong>The generator calls these; it does not replace them.</strong>
/// <c>FlowX.Compiler</c> emits one <c>FlowX.Generated.FlowXEndpoints</c> method per
/// <c>[HttpTrigger]</c>, and each is a call to the <see cref="JsonSerializerContext"/>
/// overload below — so the route, the idempotency rule and the redaction list come from
/// one declaration, while the behaviour they configure stays here, in a package with
/// tests, rather than being re-emitted per project. Writing this by hand first is what
/// made that possible: a registration that is awkward to call would have been a
/// generated call that is awkward to debug.
/// </para>
/// <para>
/// The hand-written overloads remain public and supported. A flow reached through a
/// route the attribute cannot express, or served by a project that never runs the
/// generator, is mapped by calling one of them directly — which is also what the
/// reference sample's endpoint tests do, so the generated path and the manual one are
/// both exercised.
/// </para>
/// <para>
/// <strong>No minimal-API delegate binding, deliberately.</strong> The obvious
/// implementation — <c>MapPost(route, (Request r, HttpContext c) =&gt; ...)</c> — was
/// written first and rejected by the trim analyzer: delegate binding reflects over the
/// handler's parameters, which constraint C2 forbids for anything that ships into a
/// user's process. A <see cref="RequestDelegate"/> plus a caller-supplied
/// <see cref="JsonTypeInfo{T}"/> does the same job with no reflection at all.
/// </para>
/// <para>
/// The endpoint's whole job is translation. It reads an invocation, runs the flow, and
/// maps the outcome — it makes no business decision, which is why the same flow moves
/// to Kafka without any of this changing.
/// </para>
/// </remarks>
public static class FlowEndpointExtensions
{
    /// <summary>Maps a flow to a route.</summary>
    /// <typeparam name="TResponse">The success response contract.</typeparam>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="method">HTTP method.</param>
    /// <param name="route">Route template.</param>
    /// <param name="plan">The compiled plan for this flow.</param>
    /// <param name="dispatcherFactory">Resolves the generated dispatcher from the container.</param>
    /// <param name="project">Turns the flow's result into the response body.</param>
    /// <param name="responseTypeInfo">
    /// Source-generated metadata for <typeparamref name="TResponse"/>. Required rather
    /// than optional: it is what keeps the response path free of runtime serialisation.
    /// </param>
    /// <param name="requireIdempotencyKey">Whether an <c>Idempotency-Key</c> header is mandatory.</param>
    public static IEndpointConventionBuilder MapFlow<TResponse>(
        this IEndpointRouteBuilder endpoints,
        string method,
        string route,
        ExecutionPlan plan,
        Func<IServiceProvider, IStepDispatcher> dispatcherFactory,
        Func<FlowExecutionResult, TResponse> project,
        JsonTypeInfo<TResponse> responseTypeInfo,
        bool requireIdempotencyKey = false)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(dispatcherFactory);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(responseTypeInfo);

        var builder = endpoints.Map(
            route,
            async (HttpContext context) => await HandleAsync(
                context, plan, dispatcherFactory, project, responseTypeInfo, requireIdempotencyKey)
                .ConfigureAwait(false));

        builder.WithMetadata(new HttpMethodMetadata([method]));
        return builder;
    }

    /// <summary>Maps a flow that takes a JSON request body and returns its declared output.</summary>
    /// <typeparam name="TRequest">The flow's input contract, read from the request body.</typeparam>
    /// <typeparam name="TResponse">The flow's output contract, from its <c>.Return(...)</c> clause.</typeparam>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="method">HTTP method.</param>
    /// <param name="route">Route template.</param>
    /// <param name="plan">The compiled plan for this flow.</param>
    /// <param name="dispatcherFactory">Resolves the generated dispatcher from the container.</param>
    /// <param name="projection">
    /// The generated <c>Projection</c> field on the flow's partial class. Passing it rather
    /// than a hand-written lambda is what makes the wire contract the same thing the flow
    /// declared, instead of a second copy that can drift.
    /// </param>
    /// <param name="requestTypeInfo">Source-generated metadata for <typeparamref name="TRequest"/>.</param>
    /// <param name="responseTypeInfo">Source-generated metadata for <typeparamref name="TResponse"/>.</param>
    /// <param name="requireIdempotencyKey">Whether an <c>Idempotency-Key</c> header is mandatory.</param>
    /// <param name="sensitiveMembers">
    /// The flow's generated <c>SensitiveMembers</c>. Structured error detail whose key
    /// names one of them is redacted rather than sent — the one path in this release that
    /// serialises anything a capability attached to an error.
    /// </param>
    public static IEndpointConventionBuilder MapFlow<TRequest, TResponse>(
        this IEndpointRouteBuilder endpoints,
        string method,
        string route,
        ExecutionPlan plan,
        Func<IServiceProvider, IStepDispatcher> dispatcherFactory,
        Func<FlowContext, TResponse> projection,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<TResponse> responseTypeInfo,
        bool requireIdempotencyKey = false,
        IReadOnlyCollection<string>? sensitiveMembers = null)
        where TRequest : notnull
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(dispatcherFactory);
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(requestTypeInfo);
        ArgumentNullException.ThrowIfNull(responseTypeInfo);

        var builder = endpoints.Map(
            route,
            async (HttpContext context) => await HandleAsync(
                context, plan, dispatcherFactory, projection,
                requestTypeInfo, responseTypeInfo, requireIdempotencyKey, sensitiveMembers)
                .ConfigureAwait(false));

        builder.WithMetadata(new HttpMethodMetadata([method]));
        return builder;
    }

    /// <summary>
    /// Maps a flow, taking its two <see cref="JsonTypeInfo{T}"/> out of one
    /// source-generated <see cref="JsonSerializerContext"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the overload the endpoint generator calls.</strong> It exists so
    /// that generated source names the serialiser once instead of reaching for two
    /// <c>Default.&lt;Type&gt;</c> properties whose names are chosen by <em>another</em>
    /// generator: <c>System.Text.Json</c> derives them from the type's own name, mangles
    /// them for generics and nested types, and suffixes them on collision. Asking the
    /// context for the metadata by <see cref="Type"/> is the documented, stable route to
    /// the same object, and it moves the cast, the null check and the message below out
    /// of emitted code and into a package that has tests.
    /// </para>
    /// <para>
    /// Still no reflection. <c>GetTypeInfo</c> on a source-generated context is a switch
    /// over <c>typeof</c> comparisons that the STJ generator wrote, so this resolves at
    /// the same cost and with the same trim behaviour as naming the property would.
    /// </para>
    /// </remarks>
    /// <typeparam name="TRequest">The flow's input contract, read from the request body.</typeparam>
    /// <typeparam name="TResponse">The flow's output contract, from its <c>.Return(...)</c> clause.</typeparam>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="method">HTTP method.</param>
    /// <param name="route">Route template.</param>
    /// <param name="plan">The compiled plan for this flow.</param>
    /// <param name="dispatcherFactory">Resolves the generated dispatcher from the container.</param>
    /// <param name="projection">The generated <c>Projection</c> field on the flow's partial class.</param>
    /// <param name="json">
    /// A source-generated context declaring <c>[JsonSerializable]</c> for both contracts.
    /// </param>
    /// <param name="requireIdempotencyKey">Whether an <c>Idempotency-Key</c> header is mandatory.</param>
    /// <param name="sensitiveMembers">The flow's generated <c>SensitiveMembers</c>.</param>
    public static IEndpointConventionBuilder MapFlow<TRequest, TResponse>(
        this IEndpointRouteBuilder endpoints,
        string method,
        string route,
        ExecutionPlan plan,
        Func<IServiceProvider, IStepDispatcher> dispatcherFactory,
        Func<FlowContext, TResponse> projection,
        JsonSerializerContext json,
        bool requireIdempotencyKey = false,
        IReadOnlyCollection<string>? sensitiveMembers = null)
        where TRequest : notnull
    {
        ArgumentNullException.ThrowIfNull(json);

        return endpoints.MapFlow(
            method,
            route,
            plan,
            dispatcherFactory,
            projection,
            TypeInfoFor<TRequest>(json),
            TypeInfoFor<TResponse>(json),
            requireIdempotencyKey,
            sensitiveMembers);
    }

    /// <summary>Reads one contract's metadata out of a source-generated context.</summary>
    /// <remarks>
    /// Fails at start-up rather than on the first request, and names the attribute to
    /// add. A contract missing from the context is a mapping that could never have
    /// served a request, so discovering it when the route is registered — before the
    /// process reports itself ready — is the only useful moment to say so.
    /// </remarks>
    private static JsonTypeInfo<T> TypeInfoFor<T>(JsonSerializerContext json) =>
        json.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
        ?? throw new InvalidOperationException(
            $"{json.GetType().Name} carries no metadata for {typeof(T)}. Add " +
            $"[JsonSerializable(typeof({typeof(T).Name}))] to it — the flow declares that " +
            "contract on the wire, so the serialiser has to know it.");

    private static async Task HandleAsync<TRequest, TResponse>(
        HttpContext context,
        ExecutionPlan plan,
        Func<IServiceProvider, IStepDispatcher> dispatcherFactory,
        Func<FlowContext, TResponse> projection,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<TResponse> responseTypeInfo,
        bool requireIdempotencyKey,
        IReadOnlyCollection<string>? sensitiveMembers)
        where TRequest : notnull
    {
        var invocation = HttpTriggerReader.Read(context, requireIdempotencyKey);

        if (invocation.IsFailure)
        {
            await WriteProblemAsync(
                context, invocation.Error, HttpTriggerReader.ReadCorrelationId(context), sensitiveMembers)
                .ConfigureAwait(false);

            return;
        }

        TRequest? input;

        try
        {
            input = await JsonSerializer
                .DeserializeAsync(context.Request.Body, requestTypeInfo, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            // The parser's message names the offending member and offset, which is exactly
            // what a caller needs. It describes their payload, not our internals, so
            // echoing it leaks nothing — see docs/16-Security.md on error hygiene.
            await WriteProblemAsync(
                context,
                HttpErrors.MalformedBody(exception.Message),
                invocation.Value.CorrelationId,
                sensitiveMembers)
                .ConfigureAwait(false);

            return;
        }

        if (input is null)
        {
            await WriteProblemAsync(
                context, HttpErrors.MissingBody(), invocation.Value.CorrelationId, sensitiveMembers)
                .ConfigureAwait(false);

            return;
        }

        var host = context.RequestServices.GetRequiredService<FlowHost>();
        var dispatcher = dispatcherFactory(context.RequestServices);

        var result = await host
            .RunAsync(plan, dispatcher, invocation.Value, input, projection, context.RequestAborted)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            await WriteProblemAsync(
                context, result.Error!, invocation.Value.CorrelationId, sensitiveMembers)
                .ConfigureAwait(false);

            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";

        await JsonSerializer
            .SerializeAsync(context.Response.Body, result.Value, responseTypeInfo, context.RequestAborted)
            .ConfigureAwait(false);
    }

    private static async Task HandleAsync<TResponse>(
        HttpContext context,
        ExecutionPlan plan,
        Func<IServiceProvider, IStepDispatcher> dispatcherFactory,
        Func<FlowExecutionResult, TResponse> project,
        JsonTypeInfo<TResponse> responseTypeInfo,
        bool requireIdempotencyKey)
    {
        var invocation = HttpTriggerReader.Read(context, requireIdempotencyKey);

        if (invocation.IsFailure)
        {
            await WriteProblemAsync(
                context, invocation.Error, HttpTriggerReader.ReadCorrelationId(context))
                .ConfigureAwait(false);

            return;
        }

        var host = context.RequestServices.GetRequiredService<FlowHost>();
        var dispatcher = dispatcherFactory(context.RequestServices);

        var result = await host
            .RunAsync(plan, dispatcher, invocation.Value, context.RequestAborted)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            await WriteProblemAsync(context, result.Error!, invocation.Value.CorrelationId)
                .ConfigureAwait(false);

            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";

        await JsonSerializer
            .SerializeAsync(context.Response.Body, project(result), responseTypeInfo, context.RequestAborted)
            .ConfigureAwait(false);
    }

    private static async Task WriteProblemAsync(
        HttpContext context,
        Error error,
        string correlationId,
        IReadOnlyCollection<string>? sensitiveMembers = null)
    {
        var problem = ProblemDetailsMapper.ToProblemDetails(
            error, context.Request.Path, correlationId, sensitiveMembers);

        context.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        context.Response.ContentType = ProblemDetailsJson.ContentType;

        // Echoed so a caller can quote it in a support request without parsing the body.
        context.Response.Headers[FlowXHeaders.CorrelationId] = correlationId;

        await context.Response.Body
            .WriteAsync(ProblemDetailsJson.ToUtf8(problem), context.RequestAborted)
            .ConfigureAwait(false);
    }
}
