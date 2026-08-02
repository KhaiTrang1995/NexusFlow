using FlowX.Mcp;
using Shouldly;
using Xunit;

namespace FlowX.Mcp.Tests;

/// <summary>
/// The projection itself: a manifest in, tool descriptors out, and nothing else consulted.
/// </summary>
public sealed class McpToolCatalogTests
{
    /// <summary>
    /// A manifest of the shape <c>ManifestWriter</c> emits, carrying one agent-triggered
    /// flow whose single capability declares a permission and a side effect.
    /// </summary>
    private const string Manifest =
        """
        {
          "schemaVersion": "0.1.0",
          "application": { "name": "Ordering", "version": "2.4.0" },
          "flows": [
            {
              "id": "order.place",
              "version": "1.2.0",
              "profile": "Durable",
              "input": { "type": "Ordering.Contracts.PlaceOrder", "sensitive": ["CardNumber"] },
              "output": { "type": "Ordering.Contracts.OrderPlaced" },
              "triggers": [
                { "kind": "Agent", "description": "Place a customer order.", "confirmation": "RequiredForSideEffects" }
              ],
              "steps": [ { "id": 0, "capability": "payment.capture@2.1.0" } ],
              "emits": []
            }
          ],
          "capabilities": [
            {
              "id": "payment.capture",
              "version": "2.1.0",
              "input": "Ordering.Contracts.CaptureRequest",
              "output": "Ordering.Contracts.Capture",
              "authorization": { "mode": "Permission", "value": "payment:capture" },
              "idempotent": true,
              "sideEffects": ["payment-gateway"]
            }
          ],
          "events": []
        }
        """;

    /// <summary>
    /// The gap this package closes: a flow declares itself an agent tool and something
    /// publishes it.
    /// </summary>
    [Fact]
    public void AFlowDeclaringAnAgentTriggerBecomesATool()
    {
        var catalogue = McpToolCatalog.From(Manifest);

        var tool = catalogue.Tools.ShouldHaveSingleItem();

        tool.Name.ShouldBe("order_place");
        tool.FlowId.ShouldBe("order.place");
        tool.Description.ShouldBe("Place a customer order.");
        tool.InputContract.ShouldBe("Ordering.Contracts.PlaceOrder");
        tool.SensitiveInputMembers.ShouldBe(["CardNumber"]);
        tool.RequiredPermissions.ShouldBe(["payment:capture"]);
        tool.SideEffects.ShouldBe(["payment-gateway"]);
        tool.Idempotent.ShouldBeTrue();

        // RequiredForSideEffects, and the flow has one.
        tool.ConfirmationRequired.ShouldBeTrue();
    }
}
