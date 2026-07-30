using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// FLOWX1003 and FLOWX1004 — what a capability is allowed to depend on.
/// </summary>
/// <remarks>
/// Both rules were documented as compile errors long before anything raised them. These
/// tests exist in both directions, because a rule that fires on everything is as useless
/// as one that fires on nothing — and the second is what the repository actually shipped.
/// </remarks>
public sealed class CapabilityAnalyzerTests
{
    private const string Preamble = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using FlowX;

        namespace Sample;

        public sealed record Order(string Sku);
        public sealed record Receipt(string Id);
        """;

    private static string With(string body) => Preamble + "\n\n" + body;

    [Fact]
    public void ACapabilityWithNoDependenciesIsClean()
    {
        GeneratorHarness.Analyze(With("""
            [Capability("order.validate", Version = "1.0.0", Authorization = Authorization.Internal)]
            public sealed class ValidateOrder : ICapability<Order, Receipt>
            {
                public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct)
                    => ValueTask.FromResult(Result.Ok(new Receipt("r")));
            }
            """)).ShouldBeEmpty();
    }

    [Fact]
    public void ACapabilityMayDependOnInfrastructure()
    {
        // The rule is about capabilities and transports, not about dependencies. A
        // capability that could not take a port would be a capability that cannot do
        // anything.
        GeneratorHarness.Analyze(With("""
            public interface IInventoryStore { }

            [Capability("inventory.reserve", Version = "1.0.0", Authorization = Authorization.Internal)]
            public sealed class ReserveInventory : ICapability<Order, Receipt>
            {
                private readonly IInventoryStore _store;

                public ReserveInventory(IInventoryStore store) => _store = store;

                public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct)
                    => ValueTask.FromResult(Result.Ok(new Receipt("r")));
            }
            """)).ShouldBeEmpty();
    }

    [Fact]
    public void ReportsFLOWX1004WhenACapabilityDependsOnAnother()
    {
        // Capabilities form a set, not a graph. A capability calling another turns the
        // set back into the call graph FlowX exists to replace.
        GeneratorHarness.Analyze(With("""
            [Capability("inventory.reserve", Version = "1.0.0", Authorization = Authorization.Internal)]
            public sealed class ReserveInventory : ICapability<Order, Receipt>
            {
                public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct)
                    => ValueTask.FromResult(Result.Ok(new Receipt("r")));
            }

            [Capability("order.place", Version = "1.0.0", Authorization = Authorization.Internal)]
            public sealed class PlaceOrder : ICapability<Order, Receipt>
            {
                private readonly ReserveInventory _reserve;

                public PlaceOrder(ReserveInventory reserve) => _reserve = reserve;

                public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct)
                    => ValueTask.FromResult(Result.Ok(new Receipt("r")));
            }
            """)).ShouldContain("FLOWX1004");
    }

    [Fact]
    public void SeesThroughAGenericWrapper()
    {
        // A single Lazy<> or IEnumerable<> would otherwise defeat both rules.
        GeneratorHarness.Analyze(With("""
            [Capability("inventory.reserve", Version = "1.0.0", Authorization = Authorization.Internal)]
            public sealed class ReserveInventory : ICapability<Order, Receipt>
            {
                public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct)
                    => ValueTask.FromResult(Result.Ok(new Receipt("r")));
            }

            [Capability("order.place", Version = "1.0.0", Authorization = Authorization.Internal)]
            public sealed class PlaceOrder : ICapability<Order, Receipt>
            {
                private readonly Lazy<ReserveInventory> _reserve;

                public PlaceOrder(Lazy<ReserveInventory> reserve) => _reserve = reserve;

                public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct)
                    => ValueTask.FromResult(Result.Ok(new Receipt("r")));
            }
            """)).ShouldContain("FLOWX1004");
    }

    [Fact]
    public void ReportsFLOWX1003ForATransportDependency()
    {
        // The moment a capability names a transport, the same flow can no longer run
        // behind HTTP, a bus and a schedule without changing — which is quality goal Q4.
        GeneratorHarness.Analyze(With("""
            [Capability("order.place", Version = "1.0.0", Authorization = Authorization.Internal)]
            public sealed class PlaceOrder : ICapability<Order, Receipt>
            {
                private readonly System.Net.Http.HttpClient _http;

                public PlaceOrder(System.Net.Http.HttpClient http) => _http = http;

                public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct)
                    => ValueTask.FromResult(Result.Ok(new Receipt("r")));
            }
            """)).ShouldContain("FLOWX1003");
    }

    [Fact]
    public void TheContractSurfaceIsNotATransport()
    {
        // FlowX's own core namespaces are allowed; only its plugins are transports. A
        // rule that flagged CapabilityContext would flag every capability there is.
        GeneratorHarness.Analyze(With("""
            [Capability("order.place", Version = "1.0.0", Authorization = Authorization.Internal)]
            public sealed class PlaceOrder : ICapability<Order, Receipt>
            {
                private readonly Error _lastError = new("x", "y", ErrorCategory.Validation);

                public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct)
                    => ValueTask.FromResult(Result.Ok(new Receipt("r")));
            }
            """)).ShouldBeEmpty();
    }

    [Fact]
    public void ANonCapabilityIsNotChecked()
    {
        // The rules are about capabilities. An ordinary service may hold whatever it likes.
        GeneratorHarness.Analyze(With("""
            public sealed class OrderService
            {
                private readonly System.Net.Http.HttpClient _http;

                public OrderService(System.Net.Http.HttpClient http) => _http = http;
            }
            """)).ShouldBeEmpty();
    }
}
