using Ecommerce;
using FlowX.Runtime;
using FlowX.Testing;
using Shouldly;
using Xunit;

namespace Ecommerce.Tests;

/// <summary>
/// The reference flow's control flow and recovery, with no server.
/// </summary>
/// <remarks>
/// <para>
/// The flow level of the test pyramid (<c>docs/23-Testing-Strategy.md §3</c>): the real
/// generated <c>Plan</c>, the real generated <c>Dispatcher</c>, the real engine, the real
/// in-memory inventory adapter — and one capability substituted, because a declined
/// payment is the case no happy path reaches.
/// </para>
/// <para>
/// <strong>This test used to stand up a web host.</strong> It composed a
/// <c>HostBuilder</c>, a <c>TestServer</c>, a routing table, a service collection and a
/// bespoke <c>DecliningGateway</c> so that <c>CapturePayment</c> would fail, then posted
/// JSON and asserted on a counter — roughly forty lines of infrastructure to observe a
/// property of the flow that HTTP has nothing to do with. What it could not observe was
/// the ordering: an endpoint returns one status code whether the reservation was released
/// before, after or instead of anything else.
/// </para>
/// <para>
/// The wire contract is still asserted on the wire, in
/// <see cref="PlaceOrderEndpointTests"/>, because status codes and media types are
/// properties of the endpoint. This is what is left once they are separated.
/// </para>
/// </remarks>
public sealed class PlaceOrderFlowTests
{
    [Fact]
    public async Task AFailedPaymentReleasesTheReservation()
    {
        var inventory = new InMemoryInventoryStore();

        var host = FlowTestHost
            .For(
                PlaceOrderFlow.Plan,
                new PlaceOrderFlow.Dispatcher(
                    capturePayment: new CapturePayment(new AlwaysApprovesGateway()),
                    releaseInventory: new ReleaseInventory(inventory),
                    reserveInventory: new ReserveInventory(inventory),
                    validateOrder: new ValidateOrder()))

            // The gateway above always approves, so a substitution that did not take
            // effect would leave this flow succeeding — which is what makes the
            // assertions below statements about the substitution as well as the saga.
            .Substitute("payment.capture", OrderErrors.PaymentDeclined("insufficient funds"))
            .WithInvocation(new FlowInvocation("corr-5", "key-5"))

            // payment.capture declares Authorization.Permission naming payment.write, so a
            // flow run by nobody is refused at that step and never reaches the substituted
            // decline this test is about. The caller holds exactly the one permission the
            // flow needs — not a blanket one — so the stance is still doing its job here.
            .As(TestPrincipal.Holding("payment.write"))
            .Build();

        var run = await host.RunAsync(
            new PlaceOrder("SKU-1", 4, "tok"),
            TestContext.Current.CancellationToken);

        run.Error!.Code.ShouldBe("payment.declined");
        run.Compensation.ShouldBe(CompensationOutcome.Succeeded);

        // The order the endpoint test could not see: validate, reserve, capture — and
        // then the release, after the failure rather than as part of the happy path.
        run.Trace.Executed.ShouldBe(
            ["order.validate", "inventory.reserve", "payment.capture"], run.ToString());

        run.Trace.Compensated.ShouldBe(["inventory.release"], run.ToString());

        // And the effect itself, on the real adapter: the hold is back. A saga that leaks
        // inventory on a declined card is the defect this test exists to catch.
        (await inventory.AvailableAsync("SKU-1", TestContext.Current.CancellationToken)).ShouldBe(10);
    }
}
