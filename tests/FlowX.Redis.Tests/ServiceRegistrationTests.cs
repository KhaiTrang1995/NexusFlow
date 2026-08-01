using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using StackExchange.Redis;
using Xunit;

namespace FlowX.Redis.Tests;

/// <summary>What a host actually resolves after calling <c>AddFlowXRedis</c>.</summary>
/// <remarks>
/// Asserted by building a provider and resolving, rather than by reading the
/// <c>ServiceDescriptor</c> list, because the descriptor list says what was written down and a
/// host cares what comes out. The same reasoning as <c>FlowX.Postgres.Tests</c>'s registration
/// test, and the reason both projects reference the container implementation rather than only
/// its abstractions.
/// </remarks>
public sealed class ServiceRegistrationTests
{
    /// <summary>The lease store resolves, and it is this package's.</summary>
    /// <remarks>
    /// The connection is built lazily by the container, so this resolves without a server —
    /// which is worth having: a registration that only works when Redis is up is a registration
    /// whose failures surface as a container that will not start.
    /// </remarks>
    [Fact]
    public void TheLeaseStoreResolves()
    {
        var services = new ServiceCollection().AddFlowXRedis("localhost:1,abortConnect=false");

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ILeaseStore>().ShouldBeOfType<RedisLeaseStore>();
    }

    /// <summary>
    /// This package registers a lease store and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The assertion is about what is <em>not</em> registered.</strong> WP-53 shipped
    /// with no <c>IRecoveryIndex</c> and the recovery scan silently swept nothing on every
    /// Postgres-backed host, because the host resolves that service as optional — a gap
    /// <em>between</em> two packages that each met its own exit criterion. The lesson
    /// generalises: a store that registers half a durable stack leaves a host believing it has
    /// a whole one. This package implements <see cref="ILeaseStore"/> only, and says so here
    /// rather than in a comment.
    /// </para>
    /// <para>
    /// A host wanting durable execution wires a journal too. That is the split-store
    /// arrangement <see cref="ILeaseStore"/>'s remarks describe, and it is two registrations on
    /// purpose: they share no transaction, so nothing is gained by pretending they are one.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoJournalAndNoRecoveryIndexAreRegistered()
    {
        var services = new ServiceCollection().AddFlowXRedis("localhost:1,abortConnect=false");

        using var provider = services.BuildServiceProvider();

        provider.GetService<IFlowJournal>().ShouldBeNull(
            "this package is a lease store. A host that needs a journal wires one, and one " +
            "that thinks it got a journal here would run durable flows that record nothing.");

        provider.GetService<IRecoveryIndex>().ShouldBeNull(
            "and it supplies no recovery index either — stated, because the failure mode of " +
            "an optional service nobody supplies is a scan that finds nothing, silently.");

        provider.GetService<IEventPublisher>().ShouldBeNull(
            "nor a publisher. This package now contains one, and a host that got it by asking " +
            "for a lease store would be publishing to a broker nobody chose.");
    }

    /// <summary>The publisher resolves, and it is this package's.</summary>
    /// <remarks>
    /// Resolved without a server for the reason <see cref="TheLeaseStoreResolves"/> gives: a
    /// registration that only works when Redis is up is one whose failures surface as a
    /// container that will not start.
    /// </remarks>
    [Fact]
    public void ThePublisherResolves()
    {
        var services = new ServiceCollection().AddFlowXRedisStreams("localhost:1,abortConnect=false");

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IEventPublisher>().ShouldBeOfType<RedisStreamEventPublisher>();
    }

    /// <summary>Registering the publisher registers a publisher and nothing else.</summary>
    /// <remarks>
    /// The converse of <see cref="NoJournalAndNoRecoveryIndexAreRegistered"/>, and the same
    /// lesson: a package that registers half a durable stack leaves a host believing it has a
    /// whole one. A broker is not a lease store, and a host that acquired one by asking for the
    /// other would run durable flows fenced by a store nobody chose.
    /// </remarks>
    [Fact]
    public void NoLeaseStoreIsRegisteredByThePublisher()
    {
        var services = new ServiceCollection().AddFlowXRedisStreams("localhost:1,abortConnect=false");

        using var provider = services.BuildServiceProvider();

        provider.GetService<ILeaseStore>().ShouldBeNull(
            "publishing events and owning an instance are unrelated decisions, taken by " +
            "unrelated parts of a deployment.");
    }

    /// <summary>Wiring both adapters shares one connection rather than opening two.</summary>
    /// <remarks>
    /// The reason both registrations use <c>TryAdd</c> for the multiplexer. A host that takes
    /// its leases and its broker from the same Redis is the ordinary case, and two multiplexers
    /// against one server is a connection leak that only shows up under a connection limit —
    /// which is to say, in production.
    /// </remarks>
    [Fact]
    public void BothAdaptersShareOneConnection()
    {
        var services = new ServiceCollection()
            .AddFlowXRedis("localhost:1,abortConnect=false")
            .AddFlowXRedisStreams("localhost:1,abortConnect=false");

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ILeaseStore>().ShouldBeOfType<RedisLeaseStore>();
        provider.GetRequiredService<IEventPublisher>().ShouldBeOfType<RedisStreamEventPublisher>();

        provider.GetServices<IConnectionMultiplexer>().Count().ShouldBe(
            1,
            "two registrations, one connection. StackExchange.Redis multiplexes every command " +
            "in a process over one, and a second is a leak rather than a throughput gain.");
    }

    /// <summary>The configured stream prefix reaches the publisher rather than stopping at the options.</summary>
    [Fact]
    public void TheConfiguredStreamPrefixIsUsed()
    {
        var options = new RedisStreamOptions { KeyPrefix = "tenant-a" };

        var services = new ServiceCollection()
            .AddFlowXRedisStreams("localhost:1,abortConnect=false", options);

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<RedisStreamOptions>().KeyPrefix.ShouldBe("tenant-a");

        RedisKeys.EventStream(options.KeyPrefix, "customer-1").ShouldBe("tenant-a:{customer-1}:events");
    }

    /// <summary>The multiplexer is a singleton, because more than one is a connection leak.</summary>
    [Fact]
    public void TheConnectionIsShared()
    {
        var services = new ServiceCollection().AddFlowXRedis("localhost:1,abortConnect=false");

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IConnectionMultiplexer>()
            .ShouldBeSameAs(provider.GetRequiredService<IConnectionMultiplexer>());
    }

    /// <summary>The configured key prefix reaches the store rather than stopping at the options.</summary>
    [Fact]
    public void TheConfiguredKeyPrefixIsUsed()
    {
        var options = new RedisLeaseOptions { KeyPrefix = "tenant-a" };

        var services = new ServiceCollection().AddFlowXRedis("localhost:1,abortConnect=false", options);

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<RedisLeaseOptions>().KeyPrefix.ShouldBe("tenant-a");

        RedisKeys.Lease(options.KeyPrefix, Guid.Empty).ShouldStartWith("tenant-a:");
    }
}
