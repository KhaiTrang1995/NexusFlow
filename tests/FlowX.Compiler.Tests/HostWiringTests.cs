using System.Linq;
using FlowX.Compiler.Emit;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// Whether the aggregate call starts everything the assembly declared, and nothing it did not.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The failure it removes is arithmetic.</strong> An application declaring four trigger
/// kinds needs four registration calls plus a route mapping, each with a different name, and
/// omitting any one of them is silent. <c>samples/crm</c> shipped for weeks with two schedules
/// nobody registered for exactly that reason.
/// </para>
/// <para>
/// <strong>The dangerous direction is over-emission, not under.</strong> A call to a registration
/// class that was not emitted does not compile, so the aggregate is built from the same
/// predicates each individual emitter uses — and the assertion that matters here is the negative
/// one: a bus-only application must not reach for a schedules class that does not exist.
/// </para>
/// </remarks>
public sealed class HostWiringTests
{
    /// <summary>Stands in for the hosting and routing assemblies a real project references.</summary>
    private const string HostingStub = """
        namespace FlowX.Hosting
        {
            public static class FlowBusSubscriptionRegistration
            {
            }

            public static class FlowScheduleRegistration
            {
            }
        }

        namespace Microsoft.AspNetCore.Routing
        {
            public interface IEndpointRouteBuilder
            {
            }
        }

        namespace FlowX.Http
        {
            public static class FlowEndpointExtensions
            {
            }
        }
        """;

    [Fact]
    public void TheAggregateRegistersEveryKindTheAssemblyDeclares()
    {
        var generated = WiringIn(RunOn(Declaring("[BusTrigger(\"order.placed\", Group = \"p\")]"), HostingStub));

        generated.ShouldNotBeNull();
        generated.ShouldContain("UseFlowX");
        generated.ShouldContain("AddFlowXSubscriptions(services)");
    }

    /// <summary>
    /// A schedules class is emitted only for a flow that declares one, so the aggregate must not
    /// name it otherwise.
    /// </summary>
    [Fact]
    public void TheAggregateNamesNoRegistrationThatWasNotEmitted()
    {
        var generated = WiringIn(RunOn(Declaring("[BusTrigger(\"order.placed\", Group = \"p\")]"), HostingStub))!;

        generated.ShouldNotContain(
            "AddFlowXSchedules",
            customMessage:
                "the class it would call is emitted only when a flow declares a schedule, so " +
                "naming it here is a generated file that does not compile.");
    }

    /// <summary>An assembly that declares nothing to wire gets no aggregate.</summary>
    [Fact]
    public void AnAssemblyWithNothingToWireGetsNoAggregate() =>
        WiringIn(RunOn(Declaring(string.Empty), HostingStub)).ShouldBeNull(
            "a library of flows other applications compose has no wiring of its own.");

    private static string Declaring(string trigger) => $$"""
        using System.Threading;
        using System.Threading.Tasks;
        using FlowX;

        namespace Sample;

        public sealed record Priced(string Id);

        [Capability("orders.price", Version = "1.0.0", Authorization = Authorization.Internal)]
        public sealed class Price : ICapability<BusMessage, Priced>
        {
            public ValueTask<Result<Priced>> ExecuteAsync(
                BusMessage input, CapabilityContext ctx, CancellationToken ct) =>
                ValueTask.FromResult(Result.Ok(new Priced("p")));
        }

        [Flow("orders.price", Version = "1.0.0", Profile = ExecutionProfile.Durable)]
        {{trigger}}
        public sealed partial class PriceOrderFlow : Flow<BusMessage, Priced>
        {
            protected override void Define(IFlowBuilder<BusMessage, Priced> flow) =>
                flow.Step<Price>().Return(ctx => ctx.Get<Priced>());
        }
        """;

    private static GeneratorRun RunOn(params string[] sources) => GeneratorHarness.Run(
        GeneratorHarness.CompilationOf(
            [.. sources.Select((source, i) => ($"/src/File{i}.cs", source))]));

    private static string? WiringIn(GeneratorRun run) => run.Sources
        .Where(static s => s.HintName == HostWiringEmitter.FileName)
        .Select(static s => s.Source)
        .FirstOrDefault();
}
