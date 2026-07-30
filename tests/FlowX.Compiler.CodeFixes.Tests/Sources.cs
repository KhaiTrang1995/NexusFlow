namespace FlowX.Compiler.CodeFixes.Tests;

/// <summary>
/// The two source files every fix test compiles, with the one thing under test left as
/// a hole.
/// </summary>
/// <remarks>
/// <para>
/// Split across two documents on purpose. FLOWX1010 is reported at the
/// <c>.Step&lt;T&gt;()</c> in the flow and repaired on the capability's own declaration,
/// so a single-document fixture would let a fix that only ever edits
/// <c>context.Document</c> pass. Splitting them is the difference between testing the
/// fix and testing the happy path of the fix.
/// </para>
/// <para>
/// Assertions compare whole files, so the holes are the only thing that varies: any
/// difference in the output is a difference the fix made.
/// </para>
/// </remarks>
internal static class Sources
{
    /// <summary>
    /// The flow document. <paramref name="declaration"/> replaces the modifiers and the
    /// <c>class</c> keyword; <paramref name="flowAttribute"/> replaces the whole
    /// <c>[Flow(...)]</c> attribute; <paramref name="steps"/> replaces the chain body.
    /// </summary>
    public static string Flow(
        string declaration = "public sealed partial class",
        string flowAttribute = """[Flow("order.place")]""",
        string steps = ".Step<ReserveInventory>()") =>
        $$"""
        using System;
        using FlowX;

        namespace Sample;

        {{flowAttribute}}
        {{declaration}} PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
        {
            protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                {{steps}}
                .Return(ctx => new OrderResult("id"));
        }

        """;

    /// <summary>
    /// The capability document. <paramref name="capabilityAttribute"/> replaces the whole
    /// <c>[Capability(...)]</c> attribute so a test can leave the authorisation stance out.
    /// </summary>
    public static string Capabilities(
        string capabilityAttribute =
            """[Capability("inventory.reserve", Version = "1.2.0", Authorization = Authorization.Authenticated)]""") =>
        $$"""
        using System.Threading;
        using System.Threading.Tasks;
        using FlowX;

        namespace Sample;

        public sealed record PlaceOrder(string Sku);
        public sealed record Reservation(string Sku);
        public sealed record OrderResult(string Id);
        public sealed record PaymentConfirmed(string Sku);

        {{capabilityAttribute}}
        public sealed class ReserveInventory : ICapability<PlaceOrder, Reservation>
        {
            public ValueTask<Result<Reservation>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new Reservation(input.Sku)));
        }

        """;

    /// <summary>The pair, with both documents at their defaults unless overridden.</summary>
    public static TestDocument[] Project(string? flow = null, string? capabilities = null) =>
        [
            new TestDocument("Flow.cs", flow ?? Flow()),
            new TestDocument("Capabilities.cs", capabilities ?? Capabilities()),
        ];
}
