using System.Linq;
using FlowX.Compiler.Emit;
using FlowX.Compiler.Model;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// What a flow that suspends gets generated for it
/// (<a href="../../../docs/adr/ADR-0022-http-shape-of-a-suspending-flow.md">ADR-0022 §2.2</a>):
/// its run route, and one route per signal it waits for.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The identity is a literal in the route, not a <c>{signalType}</c> parameter.</strong>
/// So an identity nothing waits for is a routing miss before any code runs, and each route
/// closes over the right contract — which is what lets the plugin deserialise a payload it has
/// never heard of with no reflection, because the type is a generic argument instantiated in
/// the user's own assembly.
/// </para>
/// <para>
/// Text, with no compilation, for the reason <see cref="EndpointEmitterTests"/> gives: the
/// emitted code calls into <c>FlowX.Http</c>, which this project does not reference and must
/// not.
/// </para>
/// </remarks>
public sealed class SignalEndpointEmitterTests
{
    private static HttpEndpointModel AcceptOffer(params SignalEndpointModel[] signals) =>
        new(
            flowId: "offer.accept",
            flowTypeName: "Sample.Flows.AcceptOfferFlow",
            methodName: "MapAcceptOfferFlow",
            inputTypeName: "Sample.Contracts.OfferToAccept",
            outputTypeName: "Sample.Contracts.AcceptedOffer",
            method: "POST",
            route: "/api/v1/offers",
            requiresIdempotencyKey: false,
            jsonContextTypeName: "Sample.SampleJsonContext",
            signals: signals);

    private static SignalEndpointModel Countersigned() =>
        new("offer.countersigned", "Sample.Contracts.OfferCountersigned");

    [Fact]
    public void TheEmittedSourceParses()
    {
        var tree = CSharpSyntaxTree.ParseText(
            EndpointEmitter.Emit("Sample.App", [AcceptOffer(Countersigned())]),
            cancellationToken: TestContext.Current.CancellationToken);

        tree.GetDiagnostics(TestContext.Current.CancellationToken).ShouldBeEmpty();
    }

    /// <summary>
    /// The signal route is the run route plus the instance and the identity.
    /// </summary>
    /// <remarks>
    /// Derived rather than declared, so a caller who knows where to start a flow can
    /// construct where to continue it — and so the <c>deliverTo</c> the <c>202</c> hands back
    /// matches the route registered here by construction rather than by agreement.
    /// </remarks>
    [Fact]
    public void TheSignalRouteIsTheRunRoutePlusTheInstanceAndTheIdentity()
    {
        var source = EndpointEmitter.Emit("Sample.App", [AcceptOffer(Countersigned())]);

        source.ShouldContainText(
            "\"/api/v1/offers/{instanceId:guid}/signals/offer.countersigned\",",
            "The identity is a literal segment, so an unknown one is a 404 from the router.");

        source.ShouldContainText(
            "\"offer.countersigned\",",
            "And the same identity is handed to FlowSignal.Of, because that is what the plan " +
            "matches the waiting step on.");
    }

    [Fact]
    public void TheSignalContractIsTheTypeArgument()
        => EndpointEmitter.Emit("Sample.App", [AcceptOffer(Countersigned())]).ShouldContainText(
            "MapFlowSignal<global::Sample.Contracts.OfferCountersigned>(",
            "A generic argument instantiated in the user's assembly is what keeps the " +
            "deserialisation reflection-free under constraint C2.");

    /// <summary>
    /// Two waits produce two routes, each closed over its own contract.
    /// </summary>
    /// <remarks>
    /// The alternative — one route with a <c>{signalType}</c> parameter — would need a runtime
    /// map from identity to <c>JsonTypeInfo</c>, which is a second copy of what the plan
    /// already says.
    /// </remarks>
    [Fact]
    public void EachWaitGetsItsOwnRoute()
    {
        var source = EndpointEmitter.Emit(
            "Sample.App",
            [AcceptOffer(Countersigned(), new SignalEndpointModel("offer.approved", "Sample.Contracts.OfferApproved"))]);

        source.ShouldContainText("signals/offer.countersigned\",", "The first wait keeps its route.");
        source.ShouldContainText("signals/offer.approved\",", "And the second gets one of its own.");
        source.ShouldContainText(
            "MapFlowSignal<global::Sample.Contracts.OfferApproved>(",
            "Each route closes over the contract its own wait declared.");
    }

    /// <summary>
    /// The method returns every route it mapped, so a convention reaches all of them.
    /// </summary>
    /// <remarks>
    /// <strong>This is the security-shaped half of the design.</strong>
    /// <c>app.MapAcceptOfferFlow().RequireAuthorization()</c> that authorised the run route and
    /// left the signal route open would be a footgun generated on the author's behalf, and the
    /// author would have no way to see it. The plugin's <c>Together</c> fans a convention out
    /// across every builder the method produced.
    /// </remarks>
    [Fact]
    public void AConventionAppliedToTheMethodReachesTheSignalRoutesToo()
        => EndpointEmitter.Emit("Sample.App", [AcceptOffer(Countersigned())]).ShouldContainText(
            "global::FlowX.Http.FlowEndpointExtensions.Together(",
            "The returned builder covers the run route and every signal route.");

    /// <summary>A flow that never suspends is emitted exactly as it was.</summary>
    /// <remarks>
    /// No composite, no extra route, no change to the one-line body. Most flows do not wait,
    /// and the generated file for them should not carry the machinery of the ones that do.
    /// </remarks>
    [Fact]
    public void AFlowThatDoesNotWaitIsUnchanged()
    {
        var source = EndpointEmitter.Emit("Sample.App", [AcceptOffer()]);

        source.ShouldNotContainText("MapFlowSignal", "Nothing to deliver, so no route for it.");
        source.ShouldNotContainText("Together(", "One route needs no composite.");
    }
}
