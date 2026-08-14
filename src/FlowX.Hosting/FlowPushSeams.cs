using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FlowX.Runtime;

namespace FlowX.Hosting;

/// <summary>
/// The doors a pushed item comes through, resolved as one service so that a generated
/// serverless entry point is one call and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This adds no decision.</strong> Every method here ends in a seam that already
/// existed — <see cref="FlowBusScan.AdmitAsync"/>, <see cref="FlowScheduleScan.RunOnceAsync"/>,
/// <see cref="FlowChangeScan.RunOnceAsync"/>,
/// <see cref="FlowHost.RunAsync{TIn, TOut}(ExecutionPlan, IStepDispatcher, FlowInvocation, TIn, Func{FlowContext, TOut}, CancellationToken)"/>
/// — and what is between is the translation a platform's shape needs and nothing more:
/// finding the registration a topic names, turning an unparseable event id into ADR-0038's
/// unreadable delivery, mapping an outcome onto a status code. It exists so that
/// <c>FlowX.Functions</c> can generate an entry point containing no logic of its own, which
/// is the whole of that package's constraint. A decision written here would be a second copy
/// of one of the four the scans make, and that is the defect WP-140 was written against.
/// </para>
/// <para>
/// <strong>Why the scans are held here rather than resolved from the container.</strong>
/// <c>AddFlowX</c> registers each scan as a constructor argument of its hosted service and not
/// as a service of its own, because a sweep is the only thing that ever needed one. A push
/// host runs no sweep and still needs the seam, so the four are assembled once, here, by the
/// same private resolution the hosted services use — rather than by a second copy of the "is
/// there a journal, is there a broker" rules that would answer differently on some host
/// nobody tested.
/// </para>
/// <para>
/// <strong>Any of them may be absent, and that is a configuration rather than a fault.</strong>
/// A host with no journal has no <see cref="Bus"/> and no <see cref="Schedule"/>; one with no
/// change feed has no <see cref="Change"/>. Each method says what it does about that, and none
/// of them pretends to have run something.
/// </para>
/// </remarks>
public sealed class FlowPushSeams
{
    /// <summary>
    /// The correlation header a caller may set when it does not speak W3C trace context.
    /// </summary>
    /// <remarks>
    /// The same name <c>FlowX.Http.FlowXHeaders.CorrelationId</c> publishes, repeated rather
    /// than referenced because this assembly does not reference that plugin and must not start.
    /// Compared case-insensitively, which is what the header is.
    /// <strong>No tenant is read from a header here or anywhere else</strong> —
    /// <c>HttpTriggerReader</c> takes the tenant from claims alone, and a tenant the caller
    /// chooses is a cross-tenant read waiting to happen.
    /// </remarks>
    public const string CorrelationHeader = "X-Correlation-ID";

    /// <summary>
    /// The caller-supplied deduplication key a mutating endpoint demands.
    /// </summary>
    /// <remarks>
    /// <c>FlowX.Http.FlowXHeaders.IdempotencyKey</c>'s name, repeated for
    /// <see cref="CorrelationHeader"/>'s reason. An endpoint whose declaration says
    /// <c>Idempotent = true</c> and does not enforce this is worse than one that never promised
    /// — <c>EveryMutatingHttpFlowRequiresAnIdempotencyKey</c> is the gate that reads the
    /// declaration, and this is what makes the declaration true on a worker.
    /// </remarks>
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    private readonly FlowHost _host;
    private readonly FlowBusCatalog _subscriptions;

    /// <summary>Assembles the seams this host can offer.</summary>
    /// <param name="host">Where every pushed item is ultimately run.</param>
    /// <param name="subscriptions">Which subscriptions this node serves, for the topic lookup.</param>
    /// <param name="bus">The bus seam, or null when this host has no journal.</param>
    /// <param name="schedule">The schedule sweep, or null when this host has no journal.</param>
    /// <param name="change">The change sweep, or null when this host has no feed or no journal.</param>
    /// <exception cref="ArgumentNullException"><paramref name="host"/> or <paramref name="subscriptions"/> is null.</exception>
    public FlowPushSeams(
        FlowHost host,
        FlowBusCatalog subscriptions,
        FlowBusScan? bus,
        FlowScheduleScan? schedule,
        FlowChangeScan? change)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(subscriptions);

        _host = host;
        _subscriptions = subscriptions;
        Bus = bus;
        Schedule = schedule;
        Change = change;
    }

    /// <summary>The bus admission seam, or null when this host has no journal.</summary>
    public FlowBusScan? Bus { get; }

    /// <summary>The schedule sweep, or null when this host has no journal.</summary>
    public FlowScheduleScan? Schedule { get; }

    /// <summary>The change sweep, or null when this host has no feed or no journal.</summary>
    public FlowChangeScan? Change { get; }

    /// <summary>
    /// Admits one pushed message, as the platform that holds its lock described it.
    /// </summary>
    /// <param name="flowId">The subscribing flow's business identity, from the declaration.</param>
    /// <param name="topic">The topic, exactly as the manifest published it.</param>
    /// <param name="eventId">The platform's message id, which must be a GUID to be readable.</param>
    /// <param name="type">The event type, or null to take the topic's name.</param>
    /// <param name="schemaVersion">The declared schema version, or null for <c>1.0.0</c>.</param>
    /// <param name="partitionKey">The key whose order this delivery belongs to, or null.</param>
    /// <param name="payload">The body, as the platform read it.</param>
    /// <param name="tenantId">The tenant the publishing side wrote beside the body, or null.</param>
    /// <param name="token">What settles this exact delivery, opaque here.</param>
    /// <param name="deliveryCount">How many times it has been handed out, including now.</param>
    /// <param name="ct">Cancels the flow this admission starts.</param>
    /// <returns>The disposition, for the caller to settle with its platform's own verbs.</returns>
    /// <exception cref="InvalidOperationException">
    /// No subscription for that flow and topic is registered on this node, which is a wiring
    /// defect rather than a message problem: the generated entry point names a declaration the
    /// composition root did not add. Thrown rather than returned so the platform retries and an
    /// operator sees it, because settling a message against a subscription nobody registered
    /// would discard it with nothing anywhere describing it.
    /// </exception>
    public ValueTask<BusAdmission> BusAsync(
        string flowId,
        string topic,
        string? eventId,
        string? type,
        string? schemaVersion,
        string? partitionKey,
        string? payload,
        string? tenantId,
        string token,
        int deliveryCount,
        CancellationToken ct = default)
    {
        if (Bus is null)
        {
            throw new InvalidOperationException(
                $"Flow '{flowId}' was pushed a message on '{topic}' and this host has no journal. " +
                "A subscription without one starts a flow per delivery of the same message with " +
                "nothing recording that it did, so admission is refused rather than served. " +
                "Register a journal — AddFlowXPostgres, or another IFlowJournal.");
        }

        var registration = RegistrationFor(flowId, topic);

        // ADR-0038's first condition, met here because this is where a platform's string
        // becomes an event id. An id that is not a GUID never becomes one, so the delivery is
        // built unreadable and AdmitAsync dead-letters it on its first delivery — the same
        // answer, through the same code, that a plugin's own reader produces on the pull path.
        var delivery = Guid.TryParse(eventId, out var identifier)
            ? BusDelivery.Of(
                new BusMessage(
                    identifier,
                    topic,
                    type ?? topic,
                    schemaVersion ?? "1.0.0",
                    partitionKey,
                    payload,
                    tenantId),
                token,
                deliveryCount)
            : BusDelivery.Unreadable(
                token,
                deliveryCount,
                partitionKey,
                $"its message id '{eventId}' is not a GUID");

        return Bus.AdmitAsync(registration, delivery, ct);
    }

    /// <summary>Runs one schedule pass, or reports that this host has nothing to fire into.</summary>
    /// <param name="ct">Cancels the pass, and every flow it started.</param>
    /// <returns>The counts, or <see cref="ScheduleScanReport.Nothing"/> when there is no journal.</returns>
    /// <remarks>
    /// A pass rather than one occurrence, because that is the only entry the sweep has and
    /// because an occurrence is computed from an expression rather than delivered: the platform
    /// timer says <em>when</em>, and ADR-0031's derived instance id is what makes a second node's
    /// firing of the same occurrence inert. A timer that fires while a sweep also runs would
    /// double-fire nothing for the same reason, which is why the sweeps are still switched off —
    /// see <c>HostSweeps.None</c> — for cost rather than for correctness.
    /// </remarks>
    public ValueTask<ScheduleScanReport> ScheduleAsync(CancellationToken ct = default) =>
        Schedule is null ? new ValueTask<ScheduleScanReport>(ScheduleScanReport.Nothing) : Schedule.RunOnceAsync(ct);

    /// <summary>Runs one change pass, or reports that this host has nothing to read.</summary>
    /// <param name="ct">Cancels the pass, and every flow it started.</param>
    /// <returns>The counts, or <see cref="ChangeScanReport.Nothing"/> when there is no feed.</returns>
    /// <remarks>
    /// <strong>A timer, and not a listener, and that is the decision rather than a shortcut.</strong>
    /// The change feed is a cursor over the outbox and WP-142 accelerates it with <c>LISTEN</c> on
    /// a dedicated connection; a host that scales to zero holds no connection between invocations
    /// and cannot listen at all. So a scaled-to-zero deployment reads the cursor on the platform's
    /// timer and pays that interval as its latency — the backstop WP-142 kept, running alone.
    /// </remarks>
    public ValueTask<ChangeScanReport> ChangeAsync(CancellationToken ct = default) =>
        Change is null ? new ValueTask<ChangeScanReport>(ChangeScanReport.Nothing) : Change.RunOnceAsync(ct);

    /// <summary>
    /// Runs one flow for a pushed request, and says what to answer with.
    /// </summary>
    /// <typeparam name="TIn">The flow's input contract.</typeparam>
    /// <typeparam name="TOut">The flow's output contract.</typeparam>
    /// <param name="plan">The compiled flow, off the generated partial.</param>
    /// <param name="dispatcher">The flow's generated dispatcher.</param>
    /// <param name="body">The request body, as the platform hands it over.</param>
    /// <param name="headers">The request headers, for the correlation fallback and nothing else.</param>
    /// <param name="projection">Turns the finished context into the response contract.</param>
    /// <param name="json">
    /// The source-generated context carrying both of this flow's contracts. A context rather
    /// than two <c>JsonTypeInfo</c>s for <c>FlowX.Http.MapFlow</c>'s reason: the generated call
    /// site names one thing the author already declared, and a contract missing from it is a
    /// start-up failure naming the type rather than a reflective fallback that quietly works
    /// until the assembly is trimmed.
    /// </param>
    /// <param name="requireIdempotencyKey">
    /// True for a trigger declared <c>Idempotent = true</c>. A request without the header is
    /// then refused rather than silently treated as unique — an endpoint that promises
    /// deduplication and does not deduplicate is worse than one that never promised.
    /// </param>
    /// <param name="ct">Cancels the flow.</param>
    /// <returns>The status and the body to write.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// <para>
    /// <strong>This is deliberately not <c>FlowX.Http</c>'s surface, and the difference is
    /// stated rather than hidden.</strong> That plugin answers RFC 7807 <c>ProblemDetails</c>,
    /// reads the tenant from claims, enforces the <c>Idempotency-Key</c> rule and publishes a
    /// suspension's signal routes — all of it built on ASP.NET Core routing, which a worker
    /// that has its own host cannot take a dependency on. What is here is the subset a
    /// serverless entry point can honour with no ASP.NET types at all: run the flow, answer
    /// the projection, and give a refusal the status its category already names. A deployment
    /// that needs the full surface runs the ASP.NET host, which is the same binary.
    /// </para>
    /// <para>
    /// <strong>No tenant is read.</strong> The tenant comes from claims
    /// (<c>ClaimTenantResolver</c>) and a worker's <c>HttpRequestData</c> carries none this
    /// seam can see, so the invocation names none and a tenanted deployment refuses the
    /// request at admission rather than serving it as somebody. That refusal is the correct
    /// answer and is not the same as support: a tenanted Functions deployment over HTTP is
    /// owed the claims plumbing and does not have it.
    /// </para>
    /// </remarks>
    public async ValueTask<FlowFunctionResponse> HttpAsync<TIn, TOut>(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        Stream body,
        IEnumerable<KeyValuePair<string, IEnumerable<string>>>? headers,
        Func<FlowContext, TOut> projection,
        JsonSerializerContext json,
        bool requireIdempotencyKey = false,
        CancellationToken ct = default)
        where TIn : notnull
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(json);

        var input = TypeInfoFor<TIn>(json);
        var output = TypeInfoFor<TOut>(json);

        // Read before the body, because a request this endpoint will refuse should be refused
        // without deserialising anything.
        var idempotencyKey = HeaderFrom(headers, IdempotencyKeyHeader);

        if (requireIdempotencyKey && idempotencyKey is null)
        {
            return FlowFunctionResponse.Refusal(
                400,
                "flow.idempotency_key_required",
                $"This endpoint declares Idempotent = true, so an '{IdempotencyKeyHeader}' " +
                "header is required. Without one a retried request would start a second flow.");
        }

        TIn? request;

        try
        {
            request = await JsonSerializer.DeserializeAsync(body, input, ct).ConfigureAwait(false);
        }
        catch (JsonException malformed)
        {
            // 400 and the parser's own sentence. A body that is not the contract is the
            // caller's error and no flow has been started, so nothing is journalled.
            return FlowFunctionResponse.Refusal(400, "flow.input_malformed", malformed.Message);
        }

        if (request is null)
        {
            return FlowFunctionResponse.Refusal(
                400, "flow.input_missing", "The request body is empty and this flow takes an input.");
        }

        var correlation = HeaderFrom(headers, CorrelationHeader) ?? Guid.NewGuid().ToString("d");

        var result = await _host
            .RunAsync(
                plan,
                dispatcher,

                // The caller's key when it supplied one, and the correlation id otherwise —
                // which is what an admission with no caller-supplied key already means: one
                // request, one instance. Not attested: nothing here established a tenant, and
                // OnlyAPlatformTriggerAttestsATenant is the gate that keeps an HTTP path from
                // claiming one.
                new FlowInvocation(correlation, idempotencyKey ?? correlation),
                request,
                projection,
                ct)
            .ConfigureAwait(false);

        if (result.IsSuccess)
        {
            return new FlowFunctionResponse(200, JsonSerializer.Serialize(result.Value, output));
        }

        if (result.IsSuspended)
        {
            // 202 and the instance, which is the only thing a caller can act on: it is what a
            // signal is delivered to. FlowX.Http publishes the signal routes beside it; this
            // surface has none to publish, and says so rather than inventing one.
            return FlowFunctionResponse.Refusal(
                202,
                "flow.suspended",
                $"The flow is waiting. Instance '{result.InstanceId?.ToString() ?? "unknown"}'.");
        }

        var error = result.Error!;

        // The category's own status, not a second map of it. ErrorCategory.ToHttpStatusCode is
        // what FlowX.Http answers with too, so the two surfaces cannot disagree about what a
        // Forbidden is — which is the drift a hand-written switch here would have introduced.
        return FlowFunctionResponse.Refusal(
            error.Category.ToHttpStatusCode(), error.Code, error.Message);
    }

    /// <summary>One contract's source-generated metadata, or a failure naming what is missing.</summary>
    /// <remarks>
    /// Thrown rather than fallen back to reflection, for <c>FlowX.Http</c>'s reason: a
    /// reflective serialiser works in development and fails after a trimmed or NativeAOT
    /// publish, which is the worst possible place to find out. The message names the type and
    /// the attribute to add.
    /// </remarks>
    private static JsonTypeInfo<T> TypeInfoFor<T>(JsonSerializerContext json) =>
        json.GetTypeInfo(typeof(T)) as JsonTypeInfo<T> ??
        throw new InvalidOperationException(
            $"'{json.GetType().FullName}' does not declare '{typeof(T).FullName}'. Add " +
            $"[JsonSerializable(typeof({typeof(T).Name}))] to it — a flow's contracts are " +
            "serialised through the source generator so the worker survives a trimmed publish.");

    /// <summary>The subscription this flow declared for this topic, on this node.</summary>
    private BusRegistration RegistrationFor(string flowId, string topic)
    {
        foreach (var registration in _subscriptions.Registrations)
        {
            if (string.Equals(registration.Subscription.FlowId, flowId, StringComparison.Ordinal) &&
                string.Equals(registration.Subscription.Topic, topic, StringComparison.Ordinal))
            {
                return registration;
            }
        }

        throw new InvalidOperationException(
            $"No subscription of flow '{flowId}' to '{topic}' is registered on this node. The " +
            "generated entry point names the declaration the manifest published, so the " +
            "composition root did not call the generated AddFlowXSubscriptions.");
    }

    /// <summary>One header's first non-blank value, or null when the caller sent none.</summary>
    /// <remarks>
    /// Compared case-insensitively, which is what an HTTP header name is. A blank value is
    /// treated as absent rather than as a key: <c>Idempotency-Key:</c> with nothing after it
    /// would otherwise deduplicate every request in the deployment onto one instance.
    /// </remarks>
    private static string? HeaderFrom(
        IEnumerable<KeyValuePair<string, IEnumerable<string>>>? headers, string name)
    {
        if (headers is null)
        {
            return null;
        }

        foreach (var header in headers)
        {
            if (!string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var value in header.Value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

}

/// <summary>What a serverless entry point should answer with.</summary>
/// <param name="Status">The status code.</param>
/// <param name="Body">The body, already serialised.</param>
/// <param name="ContentType">What the body is.</param>
/// <remarks>
/// A value rather than a platform response object, because the platform's own type is what the
/// generated entry point holds and this assembly has never heard of it. Two lines of generated
/// code turn one into the other, and no decision is among them.
/// </remarks>
public readonly record struct FlowFunctionResponse(
    int Status, string Body, string ContentType = "application/json")
{
    /// <summary>An answer that is not the flow's output.</summary>
    /// <param name="status">The status code.</param>
    /// <param name="code">The error code, verbatim, so it is greppable in a log.</param>
    /// <param name="detail">What happened, in a sentence.</param>
    /// <returns>The response.</returns>
    /// <remarks>
    /// Shaped like RFC 7807 without claiming to be it: <c>type</c> and <c>instance</c> are what
    /// <c>FlowX.Http</c> adds and what needs the routing this surface does not have.
    /// </remarks>
    public static FlowFunctionResponse Refusal(int status, string code, string detail) =>
        new(
            status,
            JsonSerializer.Serialize(
                new FlowFunctionProblem(code, detail, status),
                FlowFunctionProblemJson.Default.FlowFunctionProblem),
            "application/problem+json");
}

/// <summary>The body of a refusal.</summary>
/// <param name="Code">The error code the runtime produced.</param>
/// <param name="Detail">What happened.</param>
/// <param name="Status">The status code, repeated in the body as RFC 7807 does.</param>
public sealed record FlowFunctionProblem(string Code, string Detail, int Status);

/// <summary>Serialiser metadata for the one contract this assembly puts on a wire.</summary>
/// <remarks>
/// Source-generated rather than reflective, because a worker publishes NativeAOT and a
/// reflection-based serialiser is exactly what that publish cannot keep.
/// </remarks>
[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(FlowFunctionProblem))]
public sealed partial class FlowFunctionProblemJson : System.Text.Json.Serialization.JsonSerializerContext
{
}
