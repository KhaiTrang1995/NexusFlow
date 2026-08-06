using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// The connector registry and its outbound queue: registered, published to, swept, and refused.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What is real here and what is not.</strong> The registry, the queue, the claim, the
/// status accounting and the error an administrator reads are all real rows in a real
/// PostgreSQL. The far end is <see cref="RecordingConnectorTransport"/>, because it is a network
/// somebody else owns — a test that needed one would fail whenever their gateway was slow, which
/// is the one kind of red this repository refuses to ship.
/// </para>
/// <para>
/// <strong>Queued is not delivered, and the tests keep them apart.</strong> A route that told a
/// caller their notification had arrived when it had only been written would be lying whenever
/// the far end was slow; <c>DeliveryQueued.Status</c> says <c>Pending</c> and the sweep is what
/// changes it.
/// </para>
/// </remarks>
public sealed class ConnectorApiTests
{
    private const string Connectors = "/api/v1/crm/connectors";
    private const string Enablement = "/api/v1/crm/connectors/enablement";
    private const string Deliveries = "/api/v1/crm/connectors/deliveries";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A published delivery is queued, then swept to the far end and marked.</summary>
    [Fact]
    public async Task APublishedDeliveryIsQueuedThenDelivered()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var connector = await RegisterAsync(app, "ops_webhook");

        var response = await app.PostAsync(
            Deliveries,
            new PublishToConnector(
                connector, "opportunity.advanced", new Dictionary<string, string?> { ["stage"] = "closing" }),
            CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var queued = await CrmApplication.ReadAsync<DeliveryQueued>(response);

        queued.Status.ShouldBe(
            DeliveryStatus.Pending,
            "the caller was told it had arrived, and nothing had been sent yet.");

        (await Status(app, queued.DeliveryId)).ShouldBe(nameof(DeliveryStatus.Pending));

        var (delivered, failed) = await app.SweepAsync(CrmSchemaHarness.Northwind);

        delivered.ShouldBe(1);
        failed.ShouldBe(0);

        app.Transport.Sent.ShouldHaveSingleItem().Subject.ShouldBe("opportunity.advanced");

        (await Status(app, queued.DeliveryId)).ShouldBe(nameof(DeliveryStatus.Delivered));

        (await app.Crm.ScalarAsTenantAsync<int>(
            CrmSchemaHarness.Northwind,
            "SELECT attempts FROM connector_delivery WHERE delivery_id = @id",
            Cancellation,
            ("id", queued.DeliveryId)))
            .ShouldBe(1, "a delivery that was sent once must not read as never attempted.");
    }

    /// <summary>A refused delivery is marked Failed and keeps the reason.</summary>
    /// <remarks>
    /// The whole reason the queue is a table: an administrator asking why a customer never got a
    /// notification needs the answer to be a column, not a line in a log nobody kept.
    /// </remarks>
    [Fact]
    public async Task ARefusedDeliveryKeepsTheReasonTheFarEndGave()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var connector = await RegisterAsync(app, "ops_webhook");

        app.Transport.Refusal = "Webhook answered 503.";

        var queued = await CrmApplication.ReadAsync<DeliveryQueued>(
            await app.PostAsync(
                Deliveries,
                new PublishToConnector(connector, "x", new Dictionary<string, string?>()),
                CrmTokens.Northwind));

        var (delivered, failed) = await app.SweepAsync(CrmSchemaHarness.Northwind);

        delivered.ShouldBe(0);
        failed.ShouldBe(1);

        (await Status(app, queued.DeliveryId)).ShouldBe(nameof(DeliveryStatus.Failed));

        (await app.Crm.ScalarAsTenantAsync<string>(
            CrmSchemaHarness.Northwind,
            "SELECT last_error FROM connector_delivery WHERE delivery_id = @id",
            Cancellation,
            ("id", queued.DeliveryId)))
            .ShouldBe("Webhook answered 503.");
    }

    /// <summary>A disabled connector accepts nothing new and keeps what it already sent.</summary>
    [Fact]
    public async Task ADisabledConnectorRefusesNewDeliveriesAndKeepsItsHistory()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var connector = await RegisterAsync(app, "ops_webhook");

        var first = await CrmApplication.ReadAsync<DeliveryQueued>(
            await app.PostAsync(
                Deliveries,
                new PublishToConnector(connector, "before", new Dictionary<string, string?>()),
                CrmTokens.Northwind));

        (await app.PostAsync(
            Enablement, new SetConnectorEnabled(connector, false), CrmTokens.NorthwindManager))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var refused = await app.PostAsync(
            Deliveries,
            new PublishToConnector(connector, "after", new Dictionary<string, string?>()),
            CrmTokens.Northwind);

        refused.StatusCode.ShouldBe(
            HttpStatusCode.Conflict,
            "a switched-off connector accepted a delivery. Conflict rather than not-found on " +
            "purpose: the caller named something real.");

        (await Problem(refused)).GetProperty("code").GetString().ShouldBe("crm.connector_disabled");

        (await app.Crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Northwind,
            "SELECT count(*) FROM connector_delivery WHERE delivery_id = @id",
            Cancellation,
            ("id", first.DeliveryId)))
            .ShouldBe(1, "disabling a connector erased what it had already sent.");

        // And the sweep leaves a disabled connector's queue alone rather than draining it.
        (await app.SweepAsync(CrmSchemaHarness.Northwind)).Delivered.ShouldBe(
            0, "a disabled connector was swept, so switching one off does not stop it sending.");
    }

    /// <summary>A representative cannot register a connector; a manager can.</summary>
    /// <remarks>
    /// A connector is an address this server will later make requests to, carrying this tenant's
    /// data. That is <c>crm.admin</c> for the same reason declaring a field is.
    /// </remarks>
    [Fact]
    public async Task ARepresentativeCannotRegisterAConnector()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var refused = await app.PostAsync(
            Connectors,
            new DefineConnector("ops_webhook", ConnectorKind.Webhook, "https://example.test/hook", null),
            CrmTokens.Northwind);

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await app.Crm.ScalarAsOwnerAsync<long>("SELECT count(*) FROM connector", Cancellation))
            .ShouldBe(0);
    }

    /// <summary>An endpoint this build will not send to is refused before it is stored.</summary>
    /// <remarks>
    /// The endpoint is an address this server later makes a request to on a tenant's say-so,
    /// which is the shape of a server-side request forgery. Absolute and <c>https</c> is not a
    /// complete answer — an allow-list of hosts is — and it is the cheapest rule that keeps
    /// <c>file://</c> and a plain-text POST of somebody's pipeline out of the table.
    /// </remarks>
    [Theory]
    [InlineData("http://example.test/hook")]
    [InlineData("file:///etc/passwd")]
    [InlineData("/relative/path")]
    [InlineData("not a url")]
    public async Task AnEndpointThatIsNotAbsoluteHttpsIsRefused(string endpoint)
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var response = await app.PostAsync(
            Connectors,
            new DefineConnector("ops_webhook", ConnectorKind.Webhook, endpoint, null),
            CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, endpoint + " was accepted.");

        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.connector_endpoint_not_usable");
    }

    /// <summary>One tenant's sweep does not send another tenant's deliveries.</summary>
    [Fact]
    public async Task ASweepSendsOnlyItsOwnTenantsDeliveries()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var connector = await RegisterAsync(app, "ops_webhook");

        await app.PostAsync(
            Deliveries,
            new PublishToConnector(connector, "northwind", new Dictionary<string, string?>()),
            CrmTokens.Northwind);

        (await app.SweepAsync(CrmSchemaHarness.Contoso)).Delivered.ShouldBe(
            0,
            "Contoso's sweep sent Northwind's notification, which is this tenant's data leaving " +
            "through another tenant's integration.");

        app.Transport.Sent.ShouldBeEmpty();

        (await app.SweepAsync(CrmSchemaHarness.Northwind)).Delivered.ShouldBe(1);
    }

    private static async Task<Guid> RegisterAsync(CrmApplication app, string name)
    {
        var response = await app.PostAsync(
            Connectors,
            new DefineConnector(name, ConnectorKind.Webhook, "https://example.test/hook", null),
            CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "registering '" + name + "' failed.");

        return (await CrmApplication.ReadAsync<ConnectorDefined>(response)).ConnectorId;
    }

    private static ValueTask<string?> Status(CrmApplication app, Guid delivery) =>
        app.Crm.ScalarAsTenantAsync<string>(
            CrmSchemaHarness.Northwind,
            "SELECT status FROM connector_delivery WHERE delivery_id = @id",
            Cancellation,
            ("id", delivery));

    private static async Task<JsonElement> Problem(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Cancellation);

        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
