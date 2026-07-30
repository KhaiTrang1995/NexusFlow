using System.Text.Json;
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
/// A hand-written registration for P0. WP-5's generator will emit these calls from
/// <c>[HttpTrigger]</c>, at which point the route, the binder and the OpenAPI operation
/// all come from one declaration. Writing it by hand first keeps the shape honest: if
/// this is awkward to call, the generated version would be awkward to debug.
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

    private static async Task WriteProblemAsync(HttpContext context, Error error, string correlationId)
    {
        var problem = ProblemDetailsMapper.ToProblemDetails(error, context.Request.Path, correlationId);

        context.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        context.Response.ContentType = ProblemDetailsJson.ContentType;

        // Echoed so a caller can quote it in a support request without parsing the body.
        context.Response.Headers[FlowXHeaders.CorrelationId] = correlationId;

        await context.Response.Body
            .WriteAsync(ProblemDetailsJson.ToUtf8(problem), context.RequestAborted)
            .ConfigureAwait(false);
    }
}
