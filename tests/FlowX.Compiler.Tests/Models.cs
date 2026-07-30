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
