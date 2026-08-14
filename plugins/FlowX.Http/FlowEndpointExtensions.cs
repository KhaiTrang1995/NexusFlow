using System.Globalization;
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

    /// <summary>
    /// Maps the route that delivers one signal to one waiting instance.
    /// </summary>
    /// <typeparam name="TSignal">
    /// The contract the flow named in <c>.AwaitSignal&lt;TSignal&gt;(...)</c>. A type
    /// parameter, instantiated by generated code in the user's assembly, which is how this
    /// plugin deserialises a contract it has never heard of with no reflection at all.
    /// </typeparam>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="method">HTTP method.</param>
    /// <param name="route">
    /// The template, with the identity as a literal and <c>{instanceId:guid}</c> as the only
    /// parameter — <c>{run route}/{instanceId:guid}/signals/{identity}</c>.
    /// </param>
    /// <param name="signalType">The identity the plan carries, e.g. <c>offer.countersigned</c>.</param>
    /// <param name="plan">The compiled plan the instance is pinned to.</param>
    /// <param name="dispatcherFactory">Resolves the generated dispatcher from the container.</param>
    /// <param name="signalTypeInfo">Source-generated metadata for <typeparamref name="TSignal"/>.</param>
    /// <remarks>
    /// <para>
    /// <strong>One endpoint per (flow, signal), with the identity baked into the route.</strong>
    /// An identity nothing waits for is then a routing miss — a <c>404</c> before any code
    /// runs, which is what <c>docs/09-Trigger-Model.md §6</c>'s table always specified — and
    /// each endpoint closes over the right <see cref="JsonTypeInfo{T}"/>. The alternative, one
    /// endpoint with a <c>{signalType}</c> parameter, needs a runtime map from identity to
    /// type metadata: a second copy of what the plan already says, kept in step by hand
    /// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0022-http-shape-of-a-suspending-flow.md">ADR-0022 §2.2</a>).
    /// </para>
    /// <para>
    /// <strong>The handler is <c>FlowHost.SignalAsync</c> and nothing else.</strong> There is
    /// no second way to run a flow: the lease is acquired, the fence is raised, the frontier is
    /// read, and the same <c>FlowEngine.ExecuteAsync</c> a recovery scan enters is entered.
    /// </para>
    /// </remarks>
    public static IEndpointConventionBuilder MapFlowSignal<TSignal>(
        this IEndpointRouteBuilder endpoints,
        string method,
        string route,
        string signalType,
        ExecutionPlan plan,
        Func<IServiceProvider, IStepDispatcher> dispatcherFactory,
        JsonTypeInfo<TSignal> signalTypeInfo)
        where TSignal : notnull
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(signalType);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(dispatcherFactory);
        ArgumentNullException.ThrowIfNull(signalTypeInfo);

        var builder = endpoints.Map(
            route,
            async (HttpContext context) => await DeliverAsync(
                context, signalType, plan, dispatcherFactory, signalTypeInfo).ConfigureAwait(false));

        builder.WithMetadata(new HttpMethodMetadata([method]));
        return builder;
    }

    /// <summary>
    /// Maps a signal route, taking <typeparamref name="TSignal"/>'s metadata out of one
    /// source-generated <see cref="JsonSerializerContext"/>.
    /// </summary>
    /// <typeparam name="TSignal">The contract the flow waits for.</typeparam>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="method">HTTP method.</param>
    /// <param name="route">Route template, with the identity as a literal.</param>
    /// <param name="signalType">The identity the plan carries.</param>
    /// <param name="plan">The compiled plan the instance is pinned to.</param>
    /// <param name="dispatcherFactory">Resolves the generated dispatcher from the container.</param>
    /// <param name="json">A source-generated context declaring the signal contract.</param>
    /// <remarks>
    /// The overload the endpoint generator calls, for the reason its sibling on
    /// <c>MapFlow</c> exists: generated source names the serialiser once instead of reaching
    /// for a <c>Default.&lt;Type&gt;</c> property whose name another generator chose.
    /// </remarks>
    public static IEndpointConventionBuilder MapFlowSignal<TSignal>(
        this IEndpointRouteBuilder endpoints,
        string method,
        string route,
        string signalType,
        ExecutionPlan plan,
        Func<IServiceProvider, IStepDispatcher> dispatcherFactory,
        JsonSerializerContext json)
        where TSignal : notnull
    {
        ArgumentNullException.ThrowIfNull(json);

        return endpoints.MapFlowSignal(
            method, route, signalType, plan, dispatcherFactory, TypeInfoFor<TSignal>(json));
    }

    /// <summary>
    /// Reads the payload, delivers it, and acknowledges with what the instance did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong><c>202</c> on success, never the flow's projected output</strong>, and that is a
    /// decision rather than a limitation (ADR-0022 §2.3). The person who countersigns an offer
    /// is not the person who requested it, so projecting the flow's <c>.Return(...)</c> here
    /// would hand the second party a document assembled from the first party's request. And a
    /// delivery may complete the instance, suspend it again at a second wait, fail it, or be
    /// inert because the wait already had a committed row — <c>202 Accepted</c> is an honest
    /// description of all four.
    /// </para>
    /// <para>
    /// <strong>No <c>Idempotency-Key</c> rule.</strong> Redelivery is already inert by
    /// construction: the second delivery re-enters an instance whose wait now has a committed
    /// row, so the frontier steps over it and over everything after it. Demanding a key on top
    /// would be a second mechanism for a property that already holds.
    /// </para>
    /// </remarks>
    private static async Task DeliverAsync<TSignal>(
        HttpContext context,
        string signalType,
        ExecutionPlan plan,
        Func<IServiceProvider, IStepDispatcher> dispatcherFactory,
        JsonTypeInfo<TSignal> signalTypeInfo)
        where TSignal : notnull
    {
        var correlationId = HttpTriggerReader.ReadCorrelationId(context);

        // The route constrains it to a GUID, so this is unreachable through the router and is
        // written anyway: the overload is public and an application may map a route of its own.
        if (context.Request.RouteValues["instanceId"] is not string raw ||
            !Guid.TryParse(raw, out var instanceId))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;

            return;
        }

        TSignal? payload;

        try
        {
            payload = await JsonSerializer
                .DeserializeAsync(context.Request.Body, signalTypeInfo, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            await WriteProblemAsync(context, HttpErrors.MalformedBody(exception.Message), correlationId)
                .ConfigureAwait(false);

            return;
        }

        if (payload is null)
        {
            await WriteProblemAsync(context, HttpErrors.MissingBody(), correlationId)
                .ConfigureAwait(false);

            return;
        }

        var host = context.RequestServices.GetRequiredService<FlowHost>();

        var result = await host.SignalAsync(
                instanceId,
                new FlowRegistration(plan, dispatcherFactory(context.RequestServices)),
                FlowSignal.Of(signalType, payload),

                // The signal's deliverer, and not the caller who started the instance. The
                // journal row carries no claims (ADR-0028), so the steps after the wait are
                // authorised against whoever is delivering the signal now — which is also the
                // only principal this request has validated.
                context.User,
                context.RequestAborted)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            await WriteProblemAsync(context, result.Error!, correlationId).ConfigureAwait(false);

            return;
        }

        var awaiting = result.IsSuspended ? SuspensionJson.AwaitedBy(plan) : [];

        context.Response.StatusCode = StatusCodes.Status202Accepted;
        context.Response.ContentType = SuspensionJson.ContentType;

        await context.Response.Body
            .WriteAsync(
                SuspensionJson.ToUtf8(
                    instanceId,
                    result.IsSuspended ? "suspended" : "completed",
                    awaiting,

                    // The run route, recovered by dropping the two segments this route adds.
                    // Composing it from the request keeps a path base and a proxy prefix in it
                    // without the generator having to know about either.
                    RunPathOf(context, signalType)),
                context.RequestAborted)
            .ConfigureAwait(false);
    }

    /// <summary>The run endpoint's path, read back out of a signal endpoint's path.</summary>
    /// <remarks>
    /// The template is <c>{run route}/{instanceId}/signals/{identity}</c>, so removing the
    /// trailing <c>/{instanceId}/signals/{identity}</c> gives the run route as this request
    /// saw it — prefix and route parameters already resolved. Falls back to the whole path if
    /// the suffix is not there, which happens only for a hand-mapped route of another shape.
    /// </remarks>
    private static string RunPathOf(HttpContext context, string signalType)
    {
        var path = SuspensionJson.PathOf(context);
        var suffix = "/signals/" + signalType;

        if (!path.EndsWith(suffix, StringComparison.Ordinal))
        {
            return path;
        }

        var trimmed = path.Substring(0, path.Length - suffix.Length);
        var separator = trimmed.LastIndexOf('/');

        return separator > 0 ? trimmed.Substring(0, separator) : trimmed;
    }

    /// <summary>
    /// Treats several endpoints as one, so a convention applied to the result reaches all of
    /// them.
    /// </summary>
    /// <param name="builders">The endpoints one generated method registered.</param>
    /// <remarks>
    /// <para>
    /// <strong>This exists to close a footgun the generator would otherwise create.</strong> A
    /// flow that suspends publishes a run route and one route per signal, from a single
    /// generated <c>MapAcceptOfferFlow()</c>. If that returned only the run route's builder,
    /// <c>app.MapAcceptOfferFlow().RequireAuthorization()</c> would authorise the run route and
    /// leave the delivery routes open — a security hole created on the author's behalf, in a
    /// file they did not write, with nothing in their source to show it.
    /// </para>
    /// <para>
    /// Public because generated code calls it by name, like everything else in this class.
    /// </para>
    /// </remarks>
    public static IEndpointConventionBuilder Together(params IEndpointConventionBuilder[] builders)
    {
        ArgumentNullException.ThrowIfNull(builders);

        return new CompositeEndpointConventionBuilder([.. builders]);
    }

    /// <summary>Applies every convention to every endpoint it was built from.</summary>
    private sealed class CompositeEndpointConventionBuilder(
        IReadOnlyList<IEndpointConventionBuilder> builders) : IEndpointConventionBuilder
    {
        public void Add(Action<EndpointBuilder> convention)
        {
            foreach (var builder in builders)
            {
                builder.Add(convention);
            }
        }

        public void Finally(Action<EndpointBuilder> finallyConvention)
        {
            foreach (var builder in builders)
            {
                builder.Finally(finallyConvention);
            }
        }
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

        var host = context.RequestServices.GetRequiredService<FlowHost>();

        // Before the body is read, for docs/16 §4's reason — "rejecting expensively is how rate
        // limiting becomes the DoS". A shed request has cost this deployment one service lookup
        // and one interlocked read, and nothing is journalled because nothing ran. The slot is
        // held to the end of the request, which is what makes it an in-flight count rather than
        // an arrival-rate one.
        using var slot = host.AdmissionGate.TryAcquire();

        if (!slot.Admitted)
        {
            await WriteShedAsync(context, host.AdmissionGate.Ceiling!.Value, invocation.Value.CorrelationId)
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

        // The third outcome, and the one this endpoint had no answer for until ADR-0022.
        // `IsFailure` is false because nothing failed and `IsSuccess` is false because nothing
        // finished, so the 200 below used to be taken and `result.Value` threw — deliberately,
        // since the flow's `.Return(...)` reads values the steps after the wait have not
        // produced. 202 is what that shape means on the wire.
        if (result.IsSuspended)
        {
            await WriteSuspendedAsync(context, plan, result.InstanceId).ConfigureAwait(false);

            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";

        await JsonSerializer
            .SerializeAsync(context.Response.Body, result.Value, responseTypeInfo, context.RequestAborted)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Answers a suspended flow: <c>202</c>, the instance, and where to continue it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong><c>Location</c> is set only when the flow declares exactly one wait.</strong>
    /// A header that can name one of three addresses misleads two callers out of three, and
    /// <c>Location</c> has no plural form where the body does. It points at the signal
    /// endpoint rather than at an instance resource because the signal endpoint is the one
    /// that exists — <c>GET /flows/{instanceId}</c> is designed in
    /// <c>docs/09-Trigger-Model.md §6</c> and built by nothing.
    /// </para>
    /// <para>
    /// The awaited set comes off the compiled plan, so it is the same set the manifest
    /// publishes and the same set the generator emitted routes for: one declaration, three
    /// consumers, no copy to drift.
    /// </para>
    /// </remarks>
    private static async Task WriteSuspendedAsync(
        HttpContext context, ExecutionPlan plan, Guid? instanceId)
    {
        var awaiting = SuspensionJson.AwaitedBy(plan);
        var path = SuspensionJson.PathOf(context);

        context.Response.StatusCode = StatusCodes.Status202Accepted;
        context.Response.ContentType = SuspensionJson.ContentType;

        if (awaiting.Count == 1 && instanceId is { } id)
        {
            context.Response.Headers.Location = SuspensionJson.DeliveryPath(path, id, awaiting[0]);
        }

        await context.Response.Body
            .WriteAsync(
                SuspensionJson.ToUtf8(instanceId, "suspended", awaiting, path), context.RequestAborted)
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

        // The ceiling, as on the overload that takes a body and for the same reason. This one has
        // no body to save reading, so the saving is the flow itself.
        using var slot = host.AdmissionGate.TryAcquire();

        if (!slot.Admitted)
        {
            await WriteShedAsync(context, host.AdmissionGate.Ceiling!.Value, invocation.Value.CorrelationId)
                .ConfigureAwait(false);

            return;
        }

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

    /// <summary>
    /// Answers a request this node is at its in-flight ceiling for: <c>429</c>, a
    /// <c>Retry-After</c>, and the same problem document every other refusal here is shaped like.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Not <see cref="WriteProblemAsync"/> with an argument, because the status is the
    /// one thing that must not come from the category.</strong> <c>FlowAdmissionGate.Shed</c> is
    /// <see cref="ErrorCategory.Unavailable"/> — correctly, it is the category a caller retries
    /// on — and <c>ToHttpStatusCode</c> maps that to <c>503</c>. A shed is the one place
    /// <c>429</c> is the more useful of the two true answers: the deployment is healthy, and it
    /// is the request rate that is over. The mapper is otherwise untouched, so no existing
    /// refusal changes status.
    /// </para>
    /// <para>
    /// <strong>The body is <see cref="ProblemDetailsMapper"/>'s, unmodified.</strong> The type
    /// URI, the title and the <c>retryAfter</c> extension all come out of the one mapper every
    /// endpoint uses, so a shed is not a second problem-document dialect — only its status line
    /// and its header are this method's.
    /// </para>
    /// </remarks>
    private static async Task WriteShedAsync(HttpContext context, int ceiling, string correlationId)
    {
        var problem = ProblemDetailsMapper.ToProblemDetails(
            FlowAdmissionGate.Shed(ceiling), context.Request.Path, correlationId);

        problem.Status = FlowAdmissionGate.TooManyRequests;

        context.Response.StatusCode = FlowAdmissionGate.TooManyRequests;
        context.Response.ContentType = ProblemDetailsJson.ContentType;
        context.Response.Headers[FlowXHeaders.CorrelationId] = correlationId;

        // Seconds rather than an HTTP-date, which RFC 9110 allows either of: a delta is immune to
        // clock skew between this node and the caller, and the delay being advised here is short
        // enough that skew would be most of it.
        context.Response.Headers.RetryAfter = ((int)Math.Ceiling(
            FlowAdmissionGate.RetryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

        await context.Response.Body
            .WriteAsync(ProblemDetailsJson.ToUtf8(problem), context.RequestAborted)
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
