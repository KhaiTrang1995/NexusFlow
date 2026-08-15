using System.Collections.Generic;
using FlowX.Compiler.Model;

namespace FlowX.Compiler.Tests;

/// <summary>Flow models the emitter tests render.</summary>
/// <remarks>
/// Built as plain objects, with no Roslyn anywhere. That this is possible at all is
/// the point of the model layer — see <see cref="FlowModel"/> and risk R1.
/// </remarks>
internal static class Models
{
    public static StepModel Validate(int index) => StepModel.Capability(
        index, "Sample.Capabilities.ValidateOrder", "order.validate", "1.0.0", isIdempotent: true);

    public static StepModel Reserve(int index) => StepModel
        .Capability(
            index,
            "Sample.Capabilities.ReserveInventory",
            "inventory.reserve",
            "1.0.0",
            isIdempotent: true,
            sideEffects: ["inventory-ledger"])
        .WithCompensation(StepModel.Capability(
            index,
            "Sample.Capabilities.ReleaseInventory",
            "inventory.release",
            "1.0.0",
            isIdempotent: true,
            sideEffects: ["inventory-ledger"],
            authorizationMode: "Internal"));

    public static StepModel Capture(int index) => StepModel.Capability(
        index,
        "Sample.Capabilities.CapturePayment",
        "payment.capture",
        "2.1.0",
        isIdempotent: false,
        sideEffects: ["payment-gateway", "ledger"]);

    /// <summary>The P0 target: three capabilities, one compensation, one emit.</summary>
    public static FlowModel PlaceOrder() => new(
        flowId: "order.place",
        version: "1.0.0",
        profile: "Ephemeral",
        deadline: "PT30S",
        containingNamespace: "Sample.Flows",
        typeName: "PlaceOrderFlow",
        inputTypeName: "Sample.Contracts.PlaceOrder",
        outputTypeName: "Sample.Contracts.OrderPlacedResult",
        steps: [Validate(0), Reserve(1), Capture(2), StepModel.Emit(3, "order.placed")]);

    /// <summary>
    /// A conditional: <c>validate · when(reserve) · otherwise(capture) · emit</c>.
    /// </summary>
    /// <remarks>
    /// The indices are the flat ones the analyzer assigns — 0 validate, 1 branch,
    /// 2 reserve, (3 jump), 4 capture, 5 emit — because the model layer carries the
    /// layout and the emitter only renders it. The jump has no model of its own; it is
    /// derived from the fact that the alternative block is not empty.
    /// </remarks>
    public static FlowModel Conditional() => new(
        flowId: "order.review",
        version: "1.0.0",
        profile: "Ephemeral",
        deadline: null,
        containingNamespace: "Sample.Flows",
        typeName: "ReviewOrderFlow",
        inputTypeName: "Sample.Contracts.PlaceOrder",
        outputTypeName: "Sample.Contracts.OrderPlacedResult",
        steps:
        [
            Validate(0),
            StepModel.Condition(
                1,
                "ctx => ctx.Get<RiskScore>().Value > 80",
                then: [Reserve(2)],
                otherwise: [Capture(4)],
                predicateLocation: "/src/Flows/Review.cs:12"),
            StepModel.Emit(5, "order.reviewed"),
        ]);

    /// <summary>
    /// A value branch: <c>validate · switch(reserve | capture | default validate) · emit</c>.
    /// </summary>
    /// <remarks>
    /// The indices are the flat ones the analyzer assigns — 0 validate, 1 switch,
    /// 2 reserve, (3 jump), 4 capture, (5 jump), 6 validate, 7 emit — because the model
    /// layer carries the layout and the emitter only renders it. The jumps have no models
    /// of their own; they are derived from the fact that a non-empty block follows.
    /// </remarks>
    public static FlowModel Switching() => new(
        flowId: "order.price",
        version: "1.0.0",
        profile: "Ephemeral",
        deadline: null,
        containingNamespace: "Sample.Flows",
        typeName: "PriceOrderFlow",
        inputTypeName: "Sample.Contracts.PlaceOrder",
        outputTypeName: "Sample.Contracts.OrderPlacedResult",
        steps:
        [
            Validate(0),
            StepModel.Switch(
                1,
                "ctx => ctx.Get<ValidatedOrder>().Channel",
                "Sample.Contracts.Channel",
                cases:
                [
                    new SwitchCaseModel(
                        "Sample.Contracts.Channel.Retail",
                        [Reserve(2)],
                        valueLocation: "/src/Flows/Price.cs:14"),
                    new SwitchCaseModel("Sample.Contracts.Channel.Wholesale", [Capture(4)]),
                ],
                @default: [Validate(6)],
                selectorLocation: "/src/Flows/Price.cs:13"),
            StepModel.Emit(7, "order.priced"),
        ]);

    /// <summary>
    /// A fork: <c>validate · parallel(reserve | capture+validate) · emit</c>.
    /// </summary>
    /// <remarks>
    /// The indices are the flat ones the analyzer assigns — 0 validate, 1 parallel,
    /// 2 reserve, 3 capture, 4 validate, 5 emit. There are no jumps at all, which is the
    /// one place a fork's layout differs from a switch's: a branch's range ends where the
    /// next branch begins, so a closing jump would only restate the bound.
    /// </remarks>
    public static FlowModel Parallel() => new(
        flowId: "order.screen",
        version: "1.0.0",
        profile: "Ephemeral",
        deadline: null,
        containingNamespace: "Sample.Flows",
        typeName: "ScreenOrderFlow",
        inputTypeName: "Sample.Contracts.PlaceOrder",
        outputTypeName: "Sample.Contracts.OrderPlacedResult",
        steps:
        [
            Validate(0),
            StepModel.Parallel(
                1,
                [
                    new ParallelBranchModel([Reserve(2)]),
                    new ParallelBranchModel([Capture(3), Validate(4)]),
                ],
                "MergeStrategy.AllMustSucceed",
                "AllMustSucceed",
                location: "/src/Flows/Screen.cs:13"),
            StepModel.Emit(5, "order.screened"),
        ]);

    /// <summary>
    /// A loop: <c>validate · foreach(reserve · capture) · emit</c>.
    /// </summary>
    /// <remarks>
    /// The indices are the flat ones the analyzer assigns — 0 validate, 1 foreach,
    /// 2 reserve, 3 capture, 4 emit. There is no jump and no per-block target: the body is
    /// the span between the loop and its join, and it appears once however many elements
    /// the collection turns out to hold.
    /// </remarks>
    public static FlowModel Iterating() => new(
        flowId: "order.reserve",
        version: "1.0.0",
        profile: "Ephemeral",
        deadline: null,
        containingNamespace: "Sample.Flows",
        typeName: "ReserveOrderFlow",
        inputTypeName: "Sample.Contracts.PlaceOrder",
        outputTypeName: "Sample.Contracts.OrderPlacedResult",
        steps:
        [
            Validate(0),
            StepModel.ForEach(
                1,
                "ctx => ctx.Get<ValidatedOrder>().Lines",
                "Sample.Contracts.OrderLine",
                [Reserve(2), Capture(3)],
                "new ForEachOptions { MaxDegreeOfParallelism = 4, ContinueOnError = false }",
                selectorLocation: "/src/Flows/Reserve.cs:13",
                location: "/src/Flows/Reserve.cs:12"),
            StepModel.Emit(4, "order.reserved"),
        ]);

    /// <summary>
    /// A flow that composes another: <c>validate · subflow(order.fulfil) · capture</c>.
    /// </summary>
    /// <remarks>
    /// The composition occupies index 1 and nothing else. Unlike every other composite
    /// shape here, it carries no block: the child's steps are in the child's own model,
    /// compiled separately and possibly in another assembly — which is why a flow that
    /// composes a hundred-step child still has three steps in this list.
    /// </remarks>
    public static FlowModel Composing(string mode = "Inline") => new(
        flowId: "order.place",
        version: "1.0.0",
        profile: "Ephemeral",
        deadline: null,
        containingNamespace: "Sample.Flows",
        typeName: "PlaceOrderFlow",
        inputTypeName: "Sample.Contracts.PlaceOrder",
        outputTypeName: "Sample.Contracts.OrderPlacedResult",
        steps:
        [
            Validate(0),
            StepModel.SubFlow(
                1,
                "order.fulfil",
                "Sample.Flows.FulfilOrderFlow",
                "Sample.Contracts.FulfilOrder",
                "ctx => new FulfilOrder(ctx.Get<OrderId>())",
                mode,
                mapLocation: "/src/Flows/Place.cs:14",
                location: "/src/Flows/Place.cs:14"),
            Capture(2),
        ]);

    /// <summary>Every trigger kind the abstraction ships, declared on <c>order.place</c>.</summary>
    public static FlowTriggersModel Triggers() => new(
        "order.place",
        [
            new TriggerModel("Http", method: "POST", route: "/api/v1/orders", idempotent: true),
            new TriggerModel("Bus", transport: "kafka", topic: "orders.requested", group: "order-placement"),
            new TriggerModel("Schedule", cron: "0 2 * * *", timeZone: "Europe/Berlin"),
            new TriggerModel("Stream", topic: "orders.stream"),
            new TriggerModel(
                "Agent",
                description: "Place a customer order",
                confirmation: "RequiredForSideEffects"),
        ]);

    /// <summary>A complete catalogue for each capability <see cref="PlaceOrder"/> invokes.</summary>
    public static IReadOnlyList<CapabilityErrorCatalogue> ErrorCatalogues() =>
    [
        new CapabilityErrorCatalogue(
            "order.validate", "1.0.0", [new CapabilityErrorModel("order.invalid_quantity", "Validation")], true),
        new CapabilityErrorCatalogue(
            "inventory.reserve", "1.0.0", [new CapabilityErrorModel("inventory.out_of_stock", "Conflict")], true),
        new CapabilityErrorCatalogue("inventory.release", "1.0.0", [], true),
        new CapabilityErrorCatalogue(
            "payment.capture", "2.1.0", [new CapabilityErrorModel("payment.declined", "Conflict")], true),
    ];

    /// <summary>Two capability steps and no event, for the assertions about absence.</summary>
    public static FlowModel LinearQuery() => new(
        flowId: "order.get",
        version: "1.0.0",
        profile: "Durable",
        deadline: null,
        containingNamespace: "Sample.Flows",
        typeName: "GetOrderFlow",
        inputTypeName: "Sample.Contracts.PlaceOrder",
        outputTypeName: "Sample.Contracts.OrderPlacedResult",
        steps: [Validate(0), Validate(1)]);

    /// <summary>
    /// A flow that suspends: <c>send · await(offer.countersigned) · start</c>.
    /// </summary>
    /// <remarks>
    /// The shape <c>samples/workflow</c>'s <c>offer.accept</c> has, reduced to what the
    /// manifest is asked about — a wait between two capability steps, with the declared
    /// duration already folded to ISO-8601 by the analysis layer.
    /// </remarks>
    /// <param name="timeout">
    /// The folded wait, or <c>null</c> for a declaration the compiler could not evaluate —
    /// which is the case <c>ManifestWriter</c> must omit rather than guess at.
    /// </param>
    public static FlowModel Waiting(string? timeout = "P7D") => new(
        flowId: "offer.accept",
        version: "1.0.0",
        profile: "Durable",
        deadline: "P30D",
        containingNamespace: "Sample.Flows",
        typeName: "AcceptOfferFlow",
        inputTypeName: "Sample.Contracts.OfferToAccept",
        outputTypeName: "Sample.Contracts.AcceptedOffer",
        steps:
        [
            StepModel.Capability(0, "Sample.Capabilities.SendOffer", "offer.send", "1.0.0", isIdempotent: true),
            StepModel.AwaitSignal(
                1,
                "offer.countersigned",
                timeoutExpression: "Waits.Countersignature",
                contractTypeName: "Sample.Contracts.OfferCountersigned",
                timeout: timeout),
            StepModel.Capability(
                2, "Sample.Capabilities.StartOnboarding", "onboarding.start", "1.0.0", isIdempotent: true),
        ]);

    /// <summary>
    /// A flow declaring every field the schema has that the other fixtures leave out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The corpus fixture for ADR-0017 F1.</strong> <c>EveryFieldTheSchemaDeclaresIsWritten</c>
    /// asks whether each declared field appears in at least one manifest, and the answer is
    /// only worth having if some manifest is trying: a corpus assembled from flows that
    /// happen not to use a field cannot tell "unused here" from "no producer anywhere",
    /// which is the confusion the criterion exists to end.
    /// </para>
    /// <para>
    /// So this one is deliberately unrealistic. It carries a reviewed <c>Public</c>
    /// capability, an obsolete one, a permissioned one, a policied step, a fallback,
    /// sensitive members on both contracts, a declaration location, and an event whose
    /// identity is also the topic of its own <c>Bus</c> trigger — a flow that subscribes to
    /// what the application emits, which is what gives <c>consumedBy</c> something to say.
    /// </para>
    /// </remarks>
    public static FlowModel FullyDescribed() => new(
        flowId: "claim.settle",
        version: "2.0.0",
        profile: "Durable",
        deadline: "PT45S",
        containingNamespace: "Sample.Flows",
        typeName: "SettleClaimFlow",
        inputTypeName: "Sample.Contracts.ClaimToSettle",
        outputTypeName: "Sample.Contracts.SettledClaim",
        steps:
        [
            StepModel.Capability(
                0,
                "Sample.Capabilities.QuoteSettlement",
                "claim.quote",
                "1.0.0",
                isIdempotent: true,
                location: "/src/Flows/Settle.cs:20",
                authorizationMode: "Public",
                approvedBy: "s.okonkwo",
                capabilitySource: "/src/Capabilities/QuoteSettlement.cs:14"),
            StepModel.Capability(
                1,
                "Sample.Capabilities.PayClaimant",
                "claim.pay",
                "3.1.0",
                isIdempotent: false,
                sideEffects: ["payment-gateway"],
                authorizationMode: "Permission",
                authorizationValue: "claim.pay",
                capabilitySource: "/src/Capabilities/PayClaimant.cs:31")
                .WithPolicy("Policies.LedgerPost", ["Retry", "Timeout", "Audit"])
                .WithFallbackCapability(StepModel.Capability(
                    1,
                    "Sample.Capabilities.QueueManualPayment",
                    "claim.queue_manual",
                    "1.0.0",
                    isIdempotent: true,
                    authorizationMode: "Internal",
                    deprecated: "Superseded by claim.pay@3; removed in the next major.",
                    capabilitySource: "/src/Capabilities/QueueManualPayment.cs:9")),
            StepModel.Emit(2, "claim.settled"),
        ],
        declarationLocation: "/src/Flows/Settle.cs:11",
        sensitiveInputMembers: ["Claimant.NationalId"],
        sensitiveOutputMembers: ["Payment.Iban"]);

    /// <summary>
    /// The triggers <see cref="FullyDescribed"/> declares, including the one that consumes
    /// its own event.
    /// </summary>
    public static FlowTriggersModel FullyDescribedTriggers() => new(
        "claim.settle",
        [
            new TriggerModel("Bus", transport: "kafka", topic: "claim.settled", group: "settlement-audit"),
        ]);

    /// <summary>A single-step flow with no namespace, to exercise the degenerate shapes.</summary>
    public static FlowModel Minimal() => new(
        flowId: "ping.send",
        version: "1.0.0",
        profile: "Ephemeral",
        deadline: null,
        containingNamespace: string.Empty,
        typeName: "PingFlow",
        inputTypeName: "Ping",
        outputTypeName: "Pong",
        steps: [StepModel.Capability(0, "SendPing", "ping.send", "1.0.0", isIdempotent: true)]);
}
