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
