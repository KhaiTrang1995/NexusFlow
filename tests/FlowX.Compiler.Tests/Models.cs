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
