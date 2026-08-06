using System.Net.Http.Json;
using FlowX;

namespace Crm;

/// <summary>Registers a system this tenant sends to.</summary>
[Flow("crm.connector.register", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/connectors", Idempotent = true)]
public sealed partial class DefineConnectorFlow : Flow<DefineConnector, ConnectorDefined>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<DefineConnector, ConnectorDefined> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<DefineCrmConnector>()
            .Return(ctx => ctx.Get<ConnectorDefined>());
    }
}

/// <summary>Turns a connector on or off.</summary>
[Flow("crm.connector.enablement", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/connectors/enablement", Idempotent = true)]
public sealed partial class SetConnectorEnabledFlow : Flow<SetConnectorEnabled, ConnectorEnablementSet>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<SetConnectorEnabled, ConnectorEnablementSet> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<SetCrmConnectorEnabled>()
            .Return(ctx => ctx.Get<ConnectorEnablementSet>());
    }
}

/// <summary>Queues something for a connector to send.</summary>
/// <remarks>
/// <strong>The response says <c>Pending</c> and means it.</strong> A caller is told the delivery
/// was accepted, not that it arrived — those are different facts and a route that conflated them
/// would be lying whenever the far end was slow. What happened to it is
/// <c>connector_delivery.status</c>, written by the sweep.
/// </remarks>
[Flow("crm.connector.publication", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/connectors/deliveries", Idempotent = true)]
public sealed partial class PublishToConnectorFlow : Flow<PublishToConnector, DeliveryQueued>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<PublishToConnector, DeliveryQueued> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<PublishToCrmConnector>()
            .Return(ctx => ctx.Get<DeliveryQueued>());
    }
}

/// <summary>Drains the outbound queue, every minute, once across the fleet.</summary>
/// <remarks>
/// <para>
/// <strong>Every minute, because this is the latency of every integration the tenant has.</strong>
/// The two sweeps beside it are hourly and nightly and can be, because nobody acts on an
/// escalation count within the minute; a notification a customer is waiting on is different.
/// </para>
/// <para>
/// <strong>Once across the fleet is the scheduler's, not this flow's</strong>, and
/// <c>SKIP LOCKED</c> in the claim is what makes a second sweeper harmless anyway. Both, because
/// they fail differently: the lease stops the work being done twice, the lock stops a delivery
/// being sent twice if it ever is.
/// </para>
/// </remarks>
[Flow("crm.connector.delivery_sweep", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT60S")]
[CronTrigger("* * * * *", PerTenant = true)]
public sealed partial class SweepDeliveriesFlow : Flow<ScheduledFire, DeliveriesSwept>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<ScheduledFire, DeliveriesSwept> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<SweepConnectorDeliveries>()
            .Return(ctx => ctx.Get<DeliveriesSwept>());
    }
}

/// <summary>
/// The transport a deployment actually runs: an HTTP request per delivery.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One shape for every kind, and that is this sample's limit rather than the
/// design's.</strong> A real Slack connector posts <c>{"text": …}</c>, a real Salesforce one
/// authenticates with OAuth and posts a platform event. Rendering each properly needs five
/// vendors' contracts, and a sample that pretended to have them would be five stubs claiming to
/// be integrations. What is real here is everything around it: the registry, the queue, the
/// claim, the retry accounting and the error an administrator reads.
/// </para>
/// <para>
/// <strong>The secret is fetched by name and never read from the row.</strong>
/// <c>connector.secret_name</c> is a key into whatever the deployment holds secrets in; this
/// implementation reads an environment variable, which is what a container gets, and a
/// deployment with a vault replaces this class and nothing else.
/// </para>
/// </remarks>
public sealed class HttpConnectorTransport : IConnectorTransport
{
    private readonly HttpClient _client;

    /// <summary>Creates the transport.</summary>
    /// <param name="client">The client to send on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is null.</exception>
    public HttpConnectorTransport(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        _client = client;
    }

    /// <inheritdoc />
    public async ValueTask<string?> SendAsync(
        PendingDelivery delivery,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        using var request = new HttpRequestMessage(HttpMethod.Post, delivery.Connector.Endpoint)
        {
            Content = JsonContent.Create(
                new ConnectorEnvelope(delivery.Subject, delivery.Payload),
                CrmJsonContext.Default.ConnectorEnvelope),
        };

        if (delivery.Connector.SecretName is { Length: > 0 } secret &&
            Environment.GetEnvironmentVariable(secret) is { Length: > 0 } credential)
        {
            request.Headers.Add("Authorization", "Bearer " + credential);
        }

        try
        {
            using var response = await _client.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? null
                : $"{delivery.Connector.Kind} answered {(int)response.StatusCode}.";
        }
        catch (HttpRequestException unreachable)
        {
            // Recorded rather than thrown, so one connector being down does not leave the rest
            // of the batch unattempted. The message is what lands in `last_error`.
            return unreachable.Message;
        }
        catch (TaskCanceledException timedOut) when (!cancellationToken.IsCancellationRequested)
        {
            return "The request timed out: " + timedOut.Message;
        }
    }
}

/// <summary>What goes on the wire.</summary>
/// <param name="Subject">What it is about.</param>
/// <param name="Payload">The document, verbatim as it was stored.</param>
/// <remarks>
/// <c>Payload</c> is the stored JSON as text rather than a re-serialised object, so what the far
/// end receives is byte-for-byte what an administrator can read in <c>connector_delivery</c>.
/// A round trip through a dictionary would let the two drift on a number or an escape.
/// </remarks>
public sealed record ConnectorEnvelope(string Subject, string Payload);
