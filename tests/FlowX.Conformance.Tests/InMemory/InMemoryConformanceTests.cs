using FlowX.Conformance.InMemory;
using Xunit;

namespace FlowX.Conformance;

/// <summary>
/// Runs the whole journal suite against the reference store.
/// </summary>
/// <remarks>
/// Four lines, and that is the point: claiming conformance is deriving and supplying a store.
/// A store author who has to edit the suite to make it pass has found a disagreement about
/// the contract, which is a conversation rather than an override.
/// </remarks>
public sealed class InMemoryJournalConformanceTests : JournalConformance
{
    /// <inheritdoc />
    protected override ValueTask<IFlowJournal> CreateJournalAsync() =>
        new(new InMemoryFlowJournal());
}

/// <summary>Runs the whole lease suite against the reference store.</summary>
public sealed class InMemoryLeaseStoreConformanceTests : LeaseStoreConformance
{
    /// <inheritdoc />
    protected override ValueTask<ILeaseStore> CreateStoreAsync() =>
        new(new InMemoryLeaseStore());
}

/// <summary>
/// Runs the whole recovery-index suite against the reference store.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is where the second implementation of <see cref="IRecoveryIndex"/> now
/// lives.</strong> Until this suite existed it was a private nested class inside
/// <c>DurableHostTests</c> — a hand-rolled index, in a test file, that nothing held to a
/// contract, in a project whose own build file says durable execution is exercised "against the
/// reference <see cref="IFlowJournal"/> and <see cref="ILeaseStore"/> rather than doubles
/// written here" because "a second in-memory pair would be a pair nothing holds to the
/// conformance suites". That was true of the journal and the lease store and quietly untrue of
/// the index.
/// </para>
/// <para>
/// It belongs beside the other two references for the same reason they are here: it is the
/// implementation a store author reads when a conformance failure message is not enough, and it
/// is the one that proves the suite is passable at all. <c>DurableHostTests</c> now delegates
/// to it, so the double the host tests run against is the one this suite holds.
/// </para>
/// </remarks>
public sealed class InMemoryRecoveryIndexConformanceTests : RecoveryIndexConformance
{
    /// <inheritdoc />
    protected override ValueTask<RecoveryStore> CreateStoreAsync() =>
        new(new InMemoryRecoveryStore());
}

/// <summary>
/// Runs the whole publisher suite against the recording double.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is one of the two implementations
/// <see href="../../../docs/adr/ADR-0018-outbox-publication-and-ordering.md">ADR-0018</see>
/// named as the condition for the suite existing at all.</strong> The other is
/// <c>RedisStreamPublisherConformanceTests</c>, in <c>tests/FlowX.Redis.Tests</c>. Neither
/// derivation touches the suite, which is the finding that matters: a suite written against one
/// implementation is a suite shaped like that implementation, and nobody can tell from inside it.
/// </para>
/// <para>
/// The double is disposed with the class rather than per test, matching the other three suites
/// here — it holds a list, so there is nothing to release, and the shape is kept uniform so a
/// publisher author copying this file gets the pattern that works for a real client.
/// </para>
/// </remarks>
public sealed class RecordingPublisherConformanceTests : PublisherConformance, IAsyncLifetime
{
    private readonly List<BrokerUnderTest> _brokers = [];

    /// <inheritdoc />
    protected override ValueTask<BrokerUnderTest> CreateBrokerAsync()
    {
        var broker = new RecordingBrokerUnderTest();

        _brokers.Add(broker);

        return new ValueTask<BrokerUnderTest>(broker);
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var broker in _brokers)
        {
            await broker.DisposeAsync();
        }

        _brokers.Clear();
    }
}

/// <summary>
/// Runs the whole quota suite against the reference store.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The reference for stage 1's second store, and the only derivation that runs
/// everywhere.</strong> <c>PostgresQuotaStoreConformanceTests</c> is the other, and it needs a
/// database; this one needs nothing, so a change to <see cref="QuotaStoreConformance"/> is
/// judged on every machine rather than only on the ones with a container running.
/// </para>
/// <para>
/// The two clients are two objects over one <see cref="InMemoryQuotaServer"/> — the
/// relationship two nodes have, and the only one a single test process can construct.
/// </para>
/// </remarks>
public sealed class InMemoryQuotaStoreConformanceTests : QuotaStoreConformance
{
    /// <inheritdoc />
    protected override ValueTask<QuotaStoreUnderTest> CreateAsync() =>
        new(new InMemoryQuotaStoreUnderTest());

    private sealed class InMemoryQuotaStoreUnderTest : QuotaStoreUnderTest
    {
        private readonly InMemoryQuotaServer _server = new();

        public override IQuotaStore Quota => field ??= new InMemoryQuotaStore(_server);

        public override IQuotaStore SecondClient => field ??= new InMemoryQuotaStore(_server);

        public override ValueTask<IQuotaStore> UnreachableAsync(CancellationToken cancellationToken) =>
            new(new InMemoryQuotaStore(new InMemoryQuotaServer(), reachable: false));
    }
}
