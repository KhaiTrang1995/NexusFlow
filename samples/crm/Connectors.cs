using FlowX;

namespace Crm;

// ------------------------------------------------------------------------------- what a kind is

/// <summary>What sort of system is on the other end of a connector.</summary>
/// <remarks>
/// <strong>Closed, and that is the difference between a connector registry and a URL in a
/// column.</strong> Each kind is a shape this build knows how to render a payload for and to
/// authenticate; an open set would be a string nothing dispatches on, and every deployment would
/// discover at run time which ones actually worked. Adding one is a code change, and it is a
/// small one — <see cref="IConnectorTransport"/> is the whole surface.
/// </remarks>
public enum ConnectorKind
{
    /// <summary>An HTTP endpoint of the tenant's choosing.</summary>
    Webhook = 0,

    /// <summary>A Slack incoming webhook.</summary>
    Slack = 1,

    /// <summary>A mail gateway.</summary>
    Email = 2,

    /// <summary>Salesforce, as a system this CRM sends to rather than one it imitates.</summary>
    Salesforce = 3,

    /// <summary>HubSpot.</summary>
    HubSpot = 4,
}

/// <summary>Where a delivery has got to.</summary>
public enum DeliveryStatus
{
    /// <summary>Written, and not yet attempted.</summary>
    Pending = 0,

    /// <summary>The far end accepted it.</summary>
    Delivered = 1,

    /// <summary>The far end refused it, or could not be reached.</summary>
    Failed = 2,
}

// -------------------------------------------------------------------------------- what is asked

/// <summary>Registers a system this tenant sends to.</summary>
/// <param name="Name">The identifier a transition names. Lower case, snake case.</param>
/// <param name="Kind">What is on the other end.</param>
/// <param name="Endpoint">Where to send.</param>
/// <param name="SecretName">
/// The name of a credential held wherever the deployment holds secrets, or null when the
/// endpoint carries its own. <strong>Never the credential itself</strong> — a secret in a row is
/// a secret in every backup, every replica and every support session.
/// </param>
public sealed record DefineConnector(
    string Name,
    ConnectorKind Kind,
    string Endpoint,
    string? SecretName);

/// <summary>The connector that was registered.</summary>
/// <param name="ConnectorId">Its id.</param>
/// <param name="Name">Its name.</param>
public sealed record ConnectorDefined(Guid ConnectorId, string Name);

/// <summary>Turns a connector on or off without losing what it has already sent.</summary>
/// <param name="ConnectorId">Which connector.</param>
/// <param name="IsEnabled">Whether new deliveries are accepted.</param>
public sealed record SetConnectorEnabled(Guid ConnectorId, bool IsEnabled);

/// <summary>What the connector is now.</summary>
/// <param name="ConnectorId">The connector.</param>
/// <param name="IsEnabled">Whether it is on.</param>
public sealed record ConnectorEnablementSet(Guid ConnectorId, bool IsEnabled);

/// <summary>Queues something for a connector to send.</summary>
/// <param name="ConnectorId">Which connector.</param>
/// <param name="Subject">What it is about, in the administrator's words.</param>
/// <param name="Payload">What to send, by name.</param>
public sealed record PublishToConnector(
    Guid ConnectorId,
    string Subject,
    IReadOnlyDictionary<string, string?> Payload);

/// <summary>The delivery that was queued.</summary>
/// <param name="DeliveryId">Its id.</param>
/// <param name="Status">Where it is. <c>Pending</c> until the sweep reaches it.</param>
public sealed record DeliveryQueued(Guid DeliveryId, DeliveryStatus Status);

/// <summary>What one sweep of the outbound queue did.</summary>
/// <param name="Delivered">How many the far end accepted.</param>
/// <param name="Failed">How many it did not.</param>
/// <param name="At">The occurrence the schedule fired for.</param>
public sealed record DeliveriesSwept(int Delivered, int Failed, DateTimeOffset At);

// -------------------------------------------------------------------------------- what was read

/// <summary>A registered connector, as much of it as a sweep needs.</summary>
/// <param name="Id">The connector.</param>
/// <param name="Name">Its name.</param>
/// <param name="Kind">What is on the other end.</param>
/// <param name="Endpoint">Where to send.</param>
/// <param name="SecretName">Which credential to fetch, or null.</param>
public sealed record ConnectorRow(
    Guid Id,
    string Name,
    ConnectorKind Kind,
    string Endpoint,
    string? SecretName);

/// <summary>One thing owed to a connector.</summary>
/// <param name="Id">The delivery.</param>
/// <param name="Connector">Who it is for.</param>
/// <param name="Subject">What it is about.</param>
/// <param name="Payload">The document, as stored.</param>
/// <param name="Attempts">How many times it has been tried already.</param>
public sealed record PendingDelivery(
    Guid Id,
    ConnectorRow Connector,
    string Subject,
    string Payload,
    int Attempts);

// ------------------------------------------------------------------------------ the far end

/// <summary>
/// What actually reaches somebody else's system.
/// </summary>
/// <remarks>
/// <para>
/// <strong>An interface because this is the one thing in the sample that cannot be tested
/// against the real thing.</strong> Every other claim here is checked against a real PostgreSQL;
/// a connector's far end is a network somebody else owns, and a test that needed one would be a
/// test that fails when their gateway is slow. The seam is the same one
/// <c>EnrichmentProvider</c> uses and for the same reason.
/// </para>
/// <para>
/// <strong>It is also where a kind becomes a request.</strong> A Slack incoming webhook and a
/// Salesforce platform event want different bodies and different authentication; the enumeration
/// is closed precisely so an implementation can switch on it exhaustively.
/// </para>
/// </remarks>
public interface IConnectorTransport
{
    /// <summary>Sends one delivery.</summary>
    /// <param name="delivery">What is owed.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Null when the far end accepted it, or why it did not.</returns>
    /// <remarks>
    /// A reason rather than an exception, because a connector refusing a payload is an ordinary
    /// outcome that belongs in <c>connector_delivery.last_error</c> where an administrator can
    /// read it — not a fault that ends the sweep and leaves the rest of the queue unattempted.
    /// </remarks>
    ValueTask<string?> SendAsync(PendingDelivery delivery, CancellationToken cancellationToken);
}

// ------------------------------------------------------------------------ what can go wrong

/// <summary>Refusals the connector capabilities can produce.</summary>
public static class ConnectorErrors
{
    /// <summary>That connector is not in this tenant.</summary>
    /// <param name="connectorId">What was named.</param>
    public static Error NotFound(Guid connectorId) =>
        new Error(
            "crm.connector_not_found",
            "That connector is not in this tenant.",
            ErrorCategory.NotFound)
            .With("connectorId", connectorId);

    /// <summary>The connector is registered and switched off.</summary>
    /// <param name="connectorId">Which one.</param>
    /// <remarks>
    /// A conflict rather than a not-found, deliberately. The caller named something real and an
    /// administrator turned it off; telling them it does not exist would send them looking for a
    /// configuration mistake they did not make.
    /// </remarks>
    public static Error IsDisabled(Guid connectorId) =>
        new Error(
            "crm.connector_disabled",
            "That connector is registered and an administrator has switched it off.",
            ErrorCategory.Conflict)
            .With("connectorId", connectorId);

    /// <summary>Something with that name is already registered.</summary>
    /// <param name="name">What was sent.</param>
    public static Error NameIsTaken(string name) =>
        new Error(
            "crm.connector_name_taken",
            "This tenant already has a connector with that name.",
            ErrorCategory.Conflict)
            .With("name", name);

    /// <summary>The endpoint is not one this build will send to.</summary>
    /// <param name="endpoint">What was sent.</param>
    /// <remarks>
    /// <strong>HTTPS and absolute, checked before the row is written.</strong> An endpoint is a
    /// place this server will later make a request to on a tenant's say-so, which is the shape of
    /// a server-side request forgery; refusing anything that is not an absolute <c>https</c> URL
    /// is the cheapest rule that keeps <c>file://</c>, a relative path and a plain-text POST of
    /// somebody's pipeline out of the table. It is not a complete answer — an allow-list of hosts
    /// is — and saying so is better than implying this is one.
    /// </remarks>
    public static Error EndpointIsNotUsable(string endpoint) =>
        new Error(
            "crm.connector_endpoint_not_usable",
            "An endpoint must be an absolute https URL.",
            ErrorCategory.Validation)
            .With("endpoint", endpoint);
}

/// <summary>Renders a payload into the document a delivery stores.</summary>
/// <remarks>
/// <strong>Every value a string, unlike <see cref="CustomValues.ToJson"/>.</strong> A custom
/// field has a declared type to write with; a connector payload has none — it is whatever the
/// administrator's action put in it, on the way to a system whose schema this build does not
/// know. Typing it here would be a guess, and a guess that turned <c>"0123"</c> into
/// <c>123</c> is the kind that reaches somebody else's account number.
/// </remarks>
public static class ConnectorPayload
{
    /// <summary>Renders the document.</summary>
    /// <param name="payload">What to send, by name.</param>
    /// <returns>The JSON object.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="payload"/> is null.</exception>
    public static string ToJson(IReadOnlyDictionary<string, string?> payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        using var buffer = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            foreach (var (name, value) in payload)
            {
                if (value is null)
                {
                    writer.WriteNull(name);
                }
                else
                {
                    writer.WriteString(name, value);
                }
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}
