using System.Linq;
using FlowX.Compiler.Emit;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// Whether the container registration a composition root used to hand-write is emitted, and
/// whether it still yields to one that is hand-written.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The registration is generated because the runtime already chose the lifetime.</strong>
/// <c>FlowCatalog</c> and its four siblings hold a <em>resolved</em> dispatcher for the life of
/// the node, because a recovery sweep resumes an instance long after the invocation that started
/// it and has no scope to resolve one from. Singleton is therefore not a preference the emitter
/// expresses but the only lifetime the runtime can honour — which is what makes emitting it
/// publishing a fact rather than inventing one.
/// </para>
/// <para>
/// <strong><c>TryAdd</c> is the part that has to be asserted rather than assumed.</strong> A
/// capability behind an interface, decorated, or scoped on a path that is only ever reached over
/// HTTP is registered by hand; a generated <c>AddSingleton</c> would append a second descriptor
/// and win, silently converting somebody's deliberate lifetime into the default.
/// </para>
/// </remarks>
public sealed class CapabilityRegistrationTests
{
    /// <summary>Stands in for the container the user's project would reference.</summary>
    private const string ContainerStub = """
        namespace Microsoft.Extensions.DependencyInjection
        {
            public interface IServiceCollection
            {
            }
        }

        namespace Microsoft.Extensions.DependencyInjection.Extensions
        {
            public static class ServiceCollectionDescriptorExtensions
            {
                public static void TryAddSingleton<T>(
                    Microsoft.Extensions.DependencyInjection.IServiceCollection services)
                {
                }
            }
        }
        """;

    [Fact]
    public void EveryCapabilityADispatcherTakesIsRegistered()
    {
        var generated = RegistrationsIn(RunOn(Pricing, ContainerStub));

        generated.ShouldNotBeNull("a flow with steps has capabilities to register.");
        generated.ShouldContain("global::Sample.Price>");
        generated.ShouldContain("global::Sample.Publish>");
        generated.ShouldContain(
            "global::Sample.PriceOrderFlow.Dispatcher>",
            customMessage:
                "the dispatcher is what the catalogues hold; registering its parts and not it " +
                "leaves the same start-up failure this file exists to remove.");
    }

    /// <summary>
    /// A compensation is reached only when a step is undone, and is a constructor parameter like
    /// any other.
    /// </summary>
    [Fact]
    public void ACompensationIsRegisteredThoughNoHappyPathReachesIt()
    {
        RegistrationsIn(RunOn(Pricing, ContainerStub))!.ShouldContain(
            "global::Sample.Unpublish>",
            customMessage:
                "a compensation missing from the container fails when a saga unwinds, which is " +
                "the worst moment to discover a registration.");
    }

    [Fact]
    public void ARegistrationYieldsToOneThatWasWrittenByHand()
    {
        var generated = RegistrationsIn(RunOn(Pricing, ContainerStub))!;

        generated.ShouldContain("TryAddSingleton<");

        generated.ShouldNotContain(
            ".AddSingleton<",
            customMessage:
                "a plain AddSingleton appends a second descriptor and wins, which would convert " +
                "a deliberately scoped capability into a singleton without saying so.");
    }

    /// <summary>
    /// A flow library referencing only the abstractions is a legitimate shape and must keep
    /// compiling.
    /// </summary>
    [Fact]
    public void ACompilationWithNoContainerGetsNoRegistrationAtAll()
    {
        RegistrationsIn(RunOn(Pricing)).ShouldBeNull(
            "emitting an extension method over a type that is not referenced would turn a " +
            "working library into a build error for a convenience it never asked for.");
    }

    private const string Pricing = """
        using System.Threading;
        using System.Threading.Tasks;
        using FlowX;

        namespace Sample;

        public sealed record Order(string Id);
        public sealed record Priced(string Id);
        public sealed record Published(string Id);

        [Capability("orders.price", Version = "1.0.0", Authorization = Authorization.Internal)]
        public sealed class Price : ICapability<Order, Priced>
        {
            public ValueTask<Result<Priced>> ExecuteAsync(
                Order input, CapabilityContext ctx, CancellationToken ct) =>
                ValueTask.FromResult(Result.Ok(new Priced("p")));
        }

        [Capability("orders.publish", Version = "1.0.0", Authorization = Authorization.Internal)]
        public sealed class Publish : ICapability<Priced, Published>
        {
            public ValueTask<Result<Published>> ExecuteAsync(
                Priced input, CapabilityContext ctx, CancellationToken ct) =>
                ValueTask.FromResult(Result.Ok(new Published("q")));
        }

        [Capability("orders.unpublish", Version = "1.0.0", Authorization = Authorization.Internal)]
        public sealed class Unpublish : ICapability<Priced, Published>
        {
            public ValueTask<Result<Published>> ExecuteAsync(
                Priced input, CapabilityContext ctx, CancellationToken ct) =>
                ValueTask.FromResult(Result.Ok(new Published("q")));
        }

        [Flow("orders.price", Version = "1.0.0", Profile = ExecutionProfile.Durable)]
        public sealed partial class PriceOrderFlow : Flow<Order, Published>
        {
            protected override void Define(IFlowBuilder<Order, Published> flow) =>
                flow
                    .Step<Price>()
                    .Step<Publish>()
                        .CompensateWith<Unpublish>()
                    .Return(ctx => ctx.Get<Published>());
        }
        """;

    private static GeneratorRun RunOn(params string[] sources) => GeneratorHarness.Run(
        GeneratorHarness.CompilationOf(
            [.. sources.Select((source, i) => ($"/src/File{i}.cs", source))]));

    private static string? RegistrationsIn(GeneratorRun run) => run.Sources
        .Where(static s => s.HintName == CapabilityRegistrationEmitter.FileName)
        .Select(static s => s.Source)
        .FirstOrDefault();
}
