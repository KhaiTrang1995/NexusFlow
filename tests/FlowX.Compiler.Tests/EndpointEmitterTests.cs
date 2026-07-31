using System.Linq;
using FlowX.Compiler.Emit;
using FlowX.Compiler.Model;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// The endpoint emitter, as a pure function: a model in, C# out.
/// </summary>
/// <remarks>
/// Tested by comparing text, with no compilation, for the reason
/// <see cref="FlowEmitterTests"/> gives — and one more that is specific to this emitter.
/// The code it produces calls into <c>FlowX.Http</c>, which this test project does not
/// reference and must not: the whole design claim is that the generator knows the
/// transport by name only. A test that compiled the output would have to pull the plugin
/// underneath the compiler's own test project, which is the coupling being avoided.
/// <see cref="EndpointGenerationTests"/> asks the questions that need a compilation, and
/// <c>samples/ecommerce</c> plus <c>templates/verify.sh</c> prove the emitted call
/// builds, links and answers a request.
/// </remarks>
public sealed class EndpointEmitterTests
{
    private static HttpEndpointModel PlaceOrder(string? json = "Sample.SampleJsonContext") =>
        new(
            flowId: "order.place",
            flowTypeName: "Sample.Flows.PlaceOrderFlow",
            methodName: "MapPlaceOrderFlow",
            inputTypeName: "Sample.Contracts.PlaceOrder",
            outputTypeName: "Sample.Contracts.OrderPlacedResult",
            method: "POST",
            route: "/api/v1/orders",
            requiresIdempotencyKey: true,
            jsonContextTypeName: json);

    private static HttpEndpointModel OpenTicket() =>
        new(
            flowId: "ticket.open",
            flowTypeName: "Sample.Flows.OpenTicketFlow",
            methodName: "MapOpenTicketFlow",
            inputTypeName: "Sample.Contracts.OpenTicket",
            outputTypeName: "Sample.Contracts.TicketOpened",
            method: "PUT",
            route: "/api/v1/tickets/{id}",
            requiresIdempotencyKey: false,
            jsonContextTypeName: "Sample.SampleJsonContext");

    [Fact]
    public void TheEmittedSourceParses()
    {
        var tree = CSharpSyntaxTree.ParseText(
            EndpointEmitter.Emit("Sample.App", [PlaceOrder(), OpenTicket()]),
            cancellationToken: TestContext.Current.CancellationToken);

        tree.GetDiagnostics(TestContext.Current.CancellationToken).ShouldBeEmpty();
    }

    [Fact]
    public void TheAddressComesFromTheTriggerAndNothingElseDoes()
    {
        var source = EndpointEmitter.Emit("Sample.App", [PlaceOrder()]);

        source.ShouldContainText("\"POST\",", "The declared method is what the endpoint registers.");
        source.ShouldContainText("\"/api/v1/orders\",", "The declared route is what the endpoint registers.");
        source.ShouldContainText(
            "requireIdempotencyKey: true,",
            "Idempotent = true means a key is demanded at admission. Losing it here would " +
            "silently accept a request the flow declared must carry one.");

        // Everything else is the flow's own generated surface, named rather than copied.
        // This is the whole property: there is no second statement of anything.
        source.ShouldContainText("global::Sample.Flows.PlaceOrderFlow.Plan,", "The plan is the flow's.");
        source.ShouldContainText(
            "global::Sample.Flows.PlaceOrderFlow.Projection,", "The projection is the flow's.");
        source.ShouldContainText(
            "sensitiveMembers: global::Sample.Flows.PlaceOrderFlow.SensitiveMembers);",
            "Redaction routes through the flow's generated list, not a copy of it.");
        source.ShouldContainText(
            "GetRequiredService<global::Sample.Flows.PlaceOrderFlow.Dispatcher>(services)",
            "The dispatcher is resolved from the request's own services, as the hand-written " +
            "registration did.");
    }

    [Fact]
    public void IdempotencyOffIsEmittedAsFalseRatherThanOmitted()
    {
        // Written out rather than defaulted, so the emitted call states the whole
        // admission rule and a reader does not have to know MapFlow's defaults.
        EndpointEmitter.Emit("Sample.App", [OpenTicket()]).ShouldContainText(
            "requireIdempotencyKey: false,", "The flag is always stated.");
    }

    [Fact]
    public void EachFlowGetsItsOwnMethodAndMapFlowXCallsThemAll()
    {
        var source = EndpointEmitter.Emit("Sample.App", [PlaceOrder(), OpenTicket()]);

        source.ShouldContainText("MapPlaceOrderFlow(endpoints);", "MapFlowX maps the first flow.");
        source.ShouldContainText("MapOpenTicketFlow(endpoints);", "MapFlowX maps the second flow.");
    }

    [Fact]
    public void AResolvedContextGetsANoArgumentOverload()
    {
        var source = EndpointEmitter.Emit("Sample.App", [PlaceOrder()]);

        source.ShouldContainText(
            "MapPlaceOrderFlow(endpoints, global::Sample.SampleJsonContext.Default);",
            "One unambiguous serialiser context means the caller has nothing to pass.");
    }

    /// <summary>
    /// A flow whose serialiser this compilation could not choose still gets an endpoint —
    /// and <c>MapFlowX()</c> does not appear at all.
    /// </summary>
    /// <remarks>
    /// The absence is the point. Emitting a no-argument <c>MapFlowX</c> that quietly
    /// skipped the unresolved flow would produce an application answering on some of its
    /// declared routes and saying nothing about the rest. A missing method is a compile
    /// error at the call site, which is the loud form of the same fact.
    /// </remarks>
    [Fact]
    public void AnUnresolvedContextLeavesTheCallerToNameOneAndWithdrawsMapFlowX()
    {
        var source = EndpointEmitter.Emit("Sample.App", [PlaceOrder(json: null)]);

        source.ShouldContainText(
            "global::System.Text.Json.Serialization.JsonSerializerContext json",
            "The endpoint is still generated; only the choice of serialiser is left open.");
        source.ShouldContainText("\"/api/v1/orders\",", "The address is generated either way.");

        Squashed(source).ShouldNotContainText(
            "MapFlowX(thisglobal::Microsoft.AspNetCore.Routing.IEndpointRouteBuilderendpoints)",
            "MapFlowX() must not exist when some declared endpoint could not be resolved: " +
            "it would map the rest and stay silent about the one it skipped. The overload " +
            "taking a context is still there, and takes one.");

        Squashed(source).ShouldContainText(
            "MapFlowX(thisglobal::Microsoft.AspNetCore.Routing.IEndpointRouteBuilderendpoints," +
            "global::System.Text.Json.Serialization.JsonSerializerContextjson)",
            "The aggregate that takes a serialiser is always generated.");
    }

    /// <summary>
    /// The class is named from the assembly, so two flow libraries can coexist.
    /// </summary>
    /// <remarks>
    /// A fixed name would put two <c>FlowX.Generated.FlowXEndpoints</c> in front of an
    /// application referencing both, and the resulting ambiguous <c>app.MapFlowX()</c>
    /// could not be qualified — the disambiguating syntax is the class name, and both
    /// classes would have the same one. The extension method a developer writes is
    /// unchanged either way.
    /// </remarks>
    [Theory]
    [InlineData("Ecommerce", "EcommerceEndpoints")]
    [InlineData("Acme.Ordering", "AcmeOrderingEndpoints")]
    [InlineData("", "FlowXEndpoints")]
    [InlineData("2Fast", "FlowXEndpoints")]
    public void TheClassIsNamedFromTheAssembly(string assemblyName, string expected)
    {
        EndpointEmitter.ClassNameFor(assemblyName).ShouldBe(expected);

        EndpointEmitter.Emit(assemblyName, [PlaceOrder()]).ShouldContainText(
            "public static class " + expected, "The emitted class carries that name.");
    }

    /// <summary>The source with every space and newline removed.</summary>
    /// <remarks>
    /// A signature is emitted across several lines for readability, so asserting on one
    /// is either brittle about indentation or vague about which overload it matched.
    /// Squashing removes the first problem without introducing the second.
    /// </remarks>
    private static string Squashed(string source) =>
        new string([.. source.Where(static c => !char.IsWhiteSpace(c))]);

    [Fact]
    public void TheFileCarriesTheGeneratedHeader()
    {
        // Same header as the plan, and for the same reason: whoever opens this file
        // under obj/generated should be told immediately what wrote it.
        EndpointEmitter.Emit("Sample.App", [PlaceOrder()]).ShouldContainText(
            "// <auto-generated>", "Generated source announces itself.");
    }
}
