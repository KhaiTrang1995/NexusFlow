using Shouldly;
using Xunit;

namespace FlowX.Conformance;

/// <summary>
/// What an <see cref="IRecoveryIndex"/> must do. Derive, supply a store, and the whole suite
/// runs against it unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This file exists because two implementations agreed by reading.</strong>
/// ADR-0016 decision 4 names the gap it left open: "which states count as abandoned" was
/// settled between <c>PostgresRecoveryIndex</c> and the host tests' in-memory double by each
/// author drawing the same three states, and the reasoning "lived in two comments and no
/// assertion". A comment is not a contract. Every semantic below was in a comment on one side
/// or the other before it was here.
/// </para>
/// <para>
/// <strong>The index answers "looks abandoned", and the suite is careful never to ask for
/// more.</strong> Nothing here asserts that a returned instance is genuinely ownerless — the
/// index cannot know that, because leases live in a different store and possibly a different
/// technology. <see cref="ILeaseStore.AcquireAsync"/> is the arbiter, and losing that race is
/// a skip rather than a failure. What an index owes a sweep is a bounded, oldest-first page of
/// unfinished cold rows, and that is exactly what is pinned.
/// </para>
/// <para>
/// <strong>The interface is optional, so not deriving is a legitimate answer.</strong> A store
/// that cannot serve this query cheaply implements no <see cref="IRecoveryIndex"/> and runs no
/// sweep, which <c>FlowRecoveryScan.IsEnabled</c> reports as a host that recovers only its own
/// instances. That is a different thing from a store that answers this query wrongly, and only
/// the second is what this suite is for.
/// </para>
/// <para>
/// <strong>The suite can fail, and it is proved to.</strong>
/// <c>TheSuiteRejectsAStoreThatIsWrongTests</c> runs an index that reads "unfinished" as "not
/// terminal", and one that returns its page in whatever order it found it, and asserts each is
/// caught by the assertion whose name says why.
/// </para>
/// <para>
/// <strong>Arranging needs a write path the contract does not have.</strong>
/// <see cref="IRecoveryIndex"/> is one read method, so a suite over it cannot put anything in
/// front of itself — which is why the factory hands back a <see cref="RecoveryStore"/> rather
/// than an index. A store supplies its own arrangement, through its own writes, so no
/// assertion here can pass against a row the store under test would never produce. Making a
/// row cold is the one thing done out of band, by both implementations, because the honest
/// alternative is a test that sleeps for a lease TTL.
/// </para>
/// </remarks>
public abstract class RecoveryIndexConformance
{
    /// <summary>
    /// Longer than any lease TTL a sweep would set, so a row left this stale is certainly cold.
    /// </summary>
    protected static readonly TimeSpan LongIdle = TimeSpan.FromHours(1);

    /// <summary>
    /// The idleness the queries below ask for, standing in for a lease TTL.
    /// </summary>
    /// <remarks>
    /// Private because it is the suite's own arithmetic and not a knob a store adjusts. The
    /// only assertion whose correctness depends on a clock is
    /// <see cref="AnInstanceWrittenToRecentlyIsNotACandidate"/>, and thirty seconds is far
    /// wider than the skew between a test process and a database on the other end of a socket.
    /// <see cref="TheIdleThresholdIsExclusiveAtItsBoundary"/> avoids the question entirely by
    /// asking the store what it stored.
    /// </remarks>
    private static readonly TimeSpan IdleThreshold = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A fresh, empty store: the index to query, and the writes that give it something to find.
    /// Called once per test; no state may survive between them.
    /// </summary>
    protected abstract ValueTask<RecoveryStore> CreateStoreAsync();

    /// <summary>The ambient test cancellation token.</summary>
    protected static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------- what is a candidate

    /// <summary>
    /// A stale unfinished instance is a candidate, and arrives with what a sweep needs to
    /// decide about it.
    /// </summary>
    /// <remarks>
    /// The fields matter one at a time. <c>FlowId</c> and <c>FlowVersion</c> are what the
    /// catalogue is asked about, and an instance is pinned to its version for its whole life —
    /// so a version read back wrong would have a node resuming an instance against a plan it
    /// was never pinned to, which is the failure a rolling deployment is supposed to be safe
    /// from. <c>UpdatedAt</c> is the staleness the ordering is built on, so a store that
    /// returned an approximation of it would order its own page wrongly.
    /// </remarks>
    [Fact]
    public async Task AStaleUnfinishedInstanceIsACandidate()
    {
        var store = await CreateStoreAsync();

        var instance = await AbandonAsync(store, FlowInstanceState.Running, LongIdle, "acme");

        var candidate = (await ListAsync(store, Query())).ShouldHaveSingleItem();

        candidate.InstanceId.ShouldBe(instance);
        candidate.FlowId.ShouldBe(RecoveryStore.FlowId);
        candidate.FlowVersion.ShouldBe(
            RecoveryStore.FlowVersion,
            "the version the instance is pinned to for its whole life. A node that does not " +
            "carry it must skip the instance rather than resume it against another plan.");
        candidate.TenantId.ShouldBe("acme", "a sweep sharded by tenant has nothing else to shard on.");
        candidate.State.ShouldBe(
            FlowInstanceState.Running,
            "what the row says it was doing, unchanged. A scan reports; it does not interpret.");
        candidate.UpdatedAt.ShouldBeLessThan(
            DateTimeOffset.UtcNow - IdleThreshold,
            "the candidate is as stale as the query asked for.");
    }

    /// <summary>
    /// Every state a node can die holding is a candidate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The three states, asserted rather than left to the reader of an <c>IN</c> list. Each is
    /// a state in which a node was executing an instance and could stop existing mid-write.
    /// </para>
    /// <para>
    /// <c>Pending</c> is the narrow window between the instance row being opened and its first
    /// step committing — small, and precisely where a node that dies during startup leaves
    /// work. <c>Compensating</c> is the one most easily dropped by a store author reading
    /// "unfinished" as "still going forwards": an unwinding saga is still writing rows, and
    /// excluding it would make half-undone sagas the one thing recovery never finishes, which
    /// is the case the whole compensation model exists for.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(FlowInstanceState.Pending)]
    [InlineData(FlowInstanceState.Running)]
    [InlineData(FlowInstanceState.Compensating)]
    public async Task EveryStateANodeCanDieHoldingIsACandidate(FlowInstanceState state)
    {
        var store = await CreateStoreAsync();

        var instance = await AbandonAsync(store, state, LongIdle);

        var candidate = (await ListAsync(store, Query())).ShouldHaveSingleItem();

        candidate.InstanceId.ShouldBe(
            instance,
            $"'{state}' is a state a node holds an instance in, so a node can die holding it.");
        candidate.State.ShouldBe(state);
    }

    /// <summary>
    /// A suspended instance is unfinished and is deliberately not a candidate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The one exclusion that is a judgement rather than a definition, which is why it
    /// gets an assertion of its own.</strong> Four states are non-terminal and only three are
    /// above. A store author reading <see cref="IRecoveryIndex"/> and implementing "unfinished
    /// and cold" gets this wrong by writing the obvious thing, because <c>Suspended</c> is
    /// unfinished and is colder than anything else in the table.
    /// </para>
    /// <para>
    /// <c>Suspended</c> means "waiting for a signal, a timer or a child flow". No node holds a
    /// parked instance, so no node died holding it: it is stale by design rather than by
    /// accident, and staleness is the only evidence this query has.
    /// </para>
    /// <para>
    /// Returning it would be actively harmful and not merely wasteful. A sweep would resume a
    /// flow that is parked, fence out whatever eventually delivers the signal it is parked for,
    /// and — because the instance goes straight back to being permanently stale — do it again
    /// on every sweep of every node for as long as it waits. It is the one candidate that
    /// cannot fall out of the set by being worked on.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ASuspendedInstanceIsNotACandidate()
    {
        var store = await CreateStoreAsync();

        await AbandonAsync(store, FlowInstanceState.Suspended, LongIdle);

        (await ListAsync(store, Query())).ShouldBeEmpty(
            "a suspended instance is waiting, not abandoned. Nobody holds it, so nobody died " +
            "holding it, and a sweep that took it over would fence out the signal it is " +
            "waiting for — on every sweep, for as long as it waits.");
    }

    /// <summary>An instance that has finished is never a candidate, however long ago.</summary>
    /// <remarks>
    /// The four terminal states. Returning one would have a node take a lease on a flow that is
    /// over, read its frontier, find nothing to run and give the lease back — every sweep, for
    /// as long as retention keeps the row, which for a failed instance is a hundred and eighty
    /// days.
    /// </remarks>
    [Theory]
    [InlineData(FlowInstanceState.Completed)]
    [InlineData(FlowInstanceState.Failed)]
    [InlineData(FlowInstanceState.TimedOut)]
    [InlineData(FlowInstanceState.CompensationFailed)]
    public async Task ATerminalInstanceIsNeverACandidate(FlowInstanceState state)
    {
        var store = await CreateStoreAsync();

        await AbandonAsync(store, state, LongIdle);

        (await ListAsync(store, Query())).ShouldBeEmpty(
            $"'{state}' is terminal; there is nothing left for a node to take over.");
    }

    /// <summary>An instance written to recently is filtered out at the store.</summary>
    /// <remarks>
    /// The first and cheapest of the anti-stampede measures, and the reason
    /// <see cref="AbandonedInstanceQuery.IdleBefore"/> is on the query rather than applied by
    /// the caller. A healthy fleet writes every live instance's row at every step boundary, so
    /// in steady state the query returns nothing and a sweep is one indexed read — rather than
    /// every node fetching every live instance and being refused a lease on each of them.
    /// </remarks>
    [Fact]
    public async Task AnInstanceWrittenToRecentlyIsNotACandidate()
    {
        var store = await CreateStoreAsync();

        await AbandonAsync(store, FlowInstanceState.Running, TimeSpan.Zero);

        (await ListAsync(store, Query())).ShouldBeEmpty(
            "its row was touched inside the idle window, so it either has a live owner or is " +
            "inside a single step outliving its own lease — and only acquisition settles the " +
            "second.");
    }

    /// <summary>
    /// The idle threshold excludes the instant it names, and includes everything before it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="AbandonedInstanceQuery.IdleBefore"/> says "has not changed since this
    /// instant", so a row written exactly at it has changed since — the comparison is strictly
    /// less than. An inclusive store would be wrong by one clock tick, which sounds harmless
    /// and is the difference between a sweep that returns an instance a node is actively
    /// stepping through and one that does not.
    /// </para>
    /// <para>
    /// The boundary is taken from the store's own answer rather than computed here. Two clocks
    /// are involved whenever the store is a database — the test process's and the server's —
    /// and a boundary assertion written against the wrong one measures skew instead of
    /// semantics.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheIdleThresholdIsExclusiveAtItsBoundary()
    {
        var store = await CreateStoreAsync();

        await AbandonAsync(store, FlowInstanceState.Running, LongIdle);

        var candidate = (await ListAsync(store, Query())).ShouldHaveSingleItem();

        var atTheInstantItWasWritten = await ListAsync(
            store, Query() with { IdleBefore = candidate.UpdatedAt });

        atTheInstantItWasWritten.ShouldBeEmpty(
            "the row changed at exactly that instant, so it has not been idle since it. " +
            "'Has not changed since' is strictly less than, not less than or equal.");

        var oneTickLater = await ListAsync(
            store,
            Query() with { IdleBefore = candidate.UpdatedAt + TimeSpan.FromMilliseconds(1) });

        oneTickLater.ShouldHaveSingleItem().InstanceId.ShouldBe(
            candidate.InstanceId,
            "and a threshold a moment past the write includes it. A store that excluded both " +
            "sides would return nothing until a row was arbitrarily older than asked for.");
    }

    /// <summary>An empty answer is the ordinary answer, and is not an error.</summary>
    /// <remarks>
    /// The state a healthy deployment is in on every sweep but a handful. ADR-0007 puts
    /// expected outcomes in signatures, and there is nothing here for a caller to be refused:
    /// a query that found nothing has already answered. A store that reported "not found"
    /// would put <c>FlowRecoveryScan</c> into its error branch on every pass, where the one
    /// metric an operator watches would show a scan that is permanently broken.
    /// </remarks>
    [Fact]
    public async Task ASweepThatFindsNothingSucceedsWithAnEmptyList()
    {
        var store = await CreateStoreAsync();

        var listed = await store.Index.ListAbandonedAsync(Query(), Cancellation);

        ShouldSucceed(listed, "an empty backlog is a healthy fleet, not a fault.");
        listed.Value.ShouldBeEmpty();
    }

    // ---------------------------------------------------------------- order and page

    /// <summary>Candidates come back oldest first.</summary>
    /// <remarks>
    /// <para>
    /// <strong>The contract's anti-starvation property, and the reason
    /// <see cref="IRecoveryIndex.ListAbandonedAsync"/> has remarks about ordering at all.</strong>
    /// An instance pinned to a flow version no deployed node carries can never be resumed.
    /// Ordered by id it would hold the head of every page for ever and nothing behind it would
    /// ever be reached. Ordered by staleness it is still returned — and so is everything behind
    /// it, and every instance that <em>is</em> resumed has its row touched and moves to the
    /// back. The page advances.
    /// </para>
    /// <para>
    /// The three are written in an order that is neither the answer nor its reverse, so a store
    /// whose ordering happens to follow insertion cannot pass this by accident.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CandidatesComeBackOldestFirst()
    {
        var store = await CreateStoreAsync();

        var newest = await AbandonAsync(store, FlowInstanceState.Running, TimeSpan.FromMinutes(5));
        var oldest = await AbandonAsync(store, FlowInstanceState.Running, TimeSpan.FromMinutes(150));
        var middle = await AbandonAsync(store, FlowInstanceState.Running, TimeSpan.FromMinutes(30));

        var listed = await ListAsync(store, Query());

        listed.Select(static candidate => candidate.InstanceId).ShouldBe(
            [oldest, middle, newest],
            "ordered by staleness, oldest first — which is what stops an instance nothing can " +
            "resume from occupying the head of every page for ever.");
    }

    /// <summary>The page size bounds what comes back, and what it keeps is the oldest.</summary>
    /// <remarks>
    /// Both halves matter, and a store gets one without the other by cutting the page before
    /// ordering it. Unbounded, one node's recovery becomes every node's memory pressure — the
    /// backlog after an outage has no size a caller can predict. Bounded but arbitrary, the
    /// ordering the anti-starvation argument rests on is thrown away at the last step, and the
    /// page is once again whatever the store found first.
    /// </remarks>
    [Fact]
    public async Task ThePageSizeBoundsWhatComesBackAndKeepsTheOldest()
    {
        var store = await CreateStoreAsync();

        await AbandonAsync(store, FlowInstanceState.Running, TimeSpan.FromMinutes(80));
        var oldest = await AbandonAsync(store, FlowInstanceState.Running, TimeSpan.FromMinutes(150));
        await AbandonAsync(store, FlowInstanceState.Running, TimeSpan.FromMinutes(50));
        var second = await AbandonAsync(store, FlowInstanceState.Running, TimeSpan.FromMinutes(115));

        var listed = await ListAsync(store, Query(limit: 2));

        listed.Select(static candidate => candidate.InstanceId).ShouldBe(
            [oldest, second],
            "two rows, and the two stalest of the four. They were written in an order that is " +
            "neither, so a store that cut the page before ordering it keeps the wrong pair.");
    }

    /// <summary>Asking for at most nothing is answered with nothing.</summary>
    /// <remarks>
    /// A limit of zero or below is a caller's arithmetic — a node computing its remaining
    /// recovery capacity and finding it has none — rather than a store fault, and it has an
    /// obvious answer. The distinction is the one ADR-0007 draws: an exception from a store
    /// means the store could not be reached, so a store that let a negative limit reach a
    /// <c>LIMIT</c> clause, or an array slice, turns a caller's slip into an unreachable-store
    /// report and sends an operator looking at the database.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ALimitOfZeroOrLessAsksForNothing(int limit)
    {
        var store = await CreateStoreAsync();

        await AbandonAsync(store, FlowInstanceState.Running, LongIdle);

        var listed = await store.Index.ListAbandonedAsync(Query(limit: limit), Cancellation);

        ShouldSucceed(listed, "and it is an answer, not a refusal and not a throw.");
        listed.Value.ShouldBeEmpty();
    }

    // ---------------------------------------------------------------- tenancy

    /// <summary>A sweep scoped to one tenant sees only that tenant.</summary>
    /// <remarks>
    /// Nothing in the runtime sets this yet — <c>FlowRecoveryScan</c> sweeps every tenant a
    /// node serves — but a deployment that shards its nodes by tenant is what the parameter
    /// exists for, and a filter nothing exercises is a filter that is wrong the first time it
    /// is used. The untenanted instance is in the fixture deliberately: a store that expressed
    /// the filter as "not some other tenant" would return it.
    /// </remarks>
    [Fact]
    public async Task ASweepScopedToOneTenantSeesOnlyThatTenant()
    {
        var store = await CreateStoreAsync();

        var mine = await AbandonAsync(store, FlowInstanceState.Running, LongIdle, "acme");

        await AbandonAsync(store, FlowInstanceState.Running, LongIdle, "globex");
        await AbandonAsync(store, FlowInstanceState.Running, LongIdle);

        var listed = await ListAsync(store, Query(tenantId: "acme"));

        listed.ShouldHaveSingleItem().InstanceId.ShouldBe(
            mine,
            "one tenant's node must not take over another tenant's instance, and must not be " +
            "handed the rows of one that has no tenant either.");
    }

    /// <summary>An unscoped sweep sees every tenant, and the untenanted rows with them.</summary>
    /// <remarks>
    /// The other half, and the case every deployment actually runs today. A store that read a
    /// null <see cref="AbandonedInstanceQuery.TenantId"/> as a literal tenant to match would
    /// return only the untenanted rows here, and a multi-tenant deployment would recover a
    /// third of what it should while every test that used one tenant stayed green.
    /// </remarks>
    [Fact]
    public async Task AnUnscopedSweepSeesEveryTenant()
    {
        var store = await CreateStoreAsync();

        await AbandonAsync(store, FlowInstanceState.Running, LongIdle, "acme");
        await AbandonAsync(store, FlowInstanceState.Running, LongIdle, "globex");
        await AbandonAsync(store, FlowInstanceState.Running, LongIdle);

        var listed = await ListAsync(store, Query());

        listed.Count.ShouldBe(
            3,
            "null is 'every tenant this node serves', not a tenant whose id is null.");
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The query a sweep would issue, with a lease TTL's worth of idleness.</summary>
    private static AbandonedInstanceQuery Query(int limit = 64, string? tenantId = null) => new()
    {
        IdleBefore = DateTimeOffset.UtcNow - IdleThreshold,
        Limit = limit,
        TenantId = tenantId,
    };

    /// <summary>
    /// Puts one instance into the store, in a state, as cold as a dead node would have left it.
    /// </summary>
    private static async Task<Guid> AbandonAsync(
        RecoveryStore store,
        FlowInstanceState state,
        TimeSpan idleFor,
        string? tenantId = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        return await store.AbandonAsync(state, idleFor, tenantId, Cancellation);
    }

    /// <summary>Runs the query and asserts the store answered it.</summary>
    private static async Task<IReadOnlyList<AbandonedInstance>> ListAsync(
        RecoveryStore store,
        AbandonedInstanceQuery query)
    {
        var listed = await store.Index.ListAbandonedAsync(query, Cancellation);

        ShouldSucceed(listed, "a query that finds nothing has answered; there is nothing here to refuse.");

        return listed.Value;
    }

    /// <summary>Asserts a store call succeeded, printing the store's own refusal if not.</summary>
    protected static void ShouldSucceed<T>(Result<T> result, string because) =>
        result.IsSuccess.ShouldBeTrue(
            $"{because} The store refused instead: {(result.IsFailure ? result.Error.ToString() : "no error")}");
}

/// <summary>
/// An <see cref="IRecoveryIndex"/> under test, and the writes that put something in front of
/// it.
/// </summary>
/// <remarks>
/// <para>
/// The journal and lease suites take a factory returning the interface itself, because those
/// interfaces can arrange their own fixtures — a journal test starts an instance by calling
/// <c>StartAsync</c>. <see cref="IRecoveryIndex"/> is one read method over rows something else
/// wrote, so a suite over it needs a second half, and this is it.
/// </para>
/// <para>
/// <strong>An implementation arranges through its own write path.</strong> The point of a
/// conformance suite is that no assertion passes against a row shape the store under test would
/// not produce, so <see cref="AbandonAsync"/> is expected to reach each state the way the
/// runtime does — a commit for the states a running flow passes through, a completion for the
/// states it ends in — rather than by writing a row directly.
/// </para>
/// <para>
/// <strong>Coldness is the exception, and both shipped implementations take it.</strong>
/// <paramref name="idleFor"/> is applied out of band — an <c>UPDATE … SET updated_at</c>
/// against PostgreSQL, a backdate against the reference journal — because staleness is the
/// premise of every assertion here and the only honest in-band alternative is a test that
/// sleeps for a lease TTL.
/// </para>
/// </remarks>
public abstract class RecoveryStore
{
    /// <summary>The flow every instance the suite arranges is an instance of.</summary>
    /// <remarks>
    /// Fixed here rather than passed in, so that
    /// <see cref="RecoveryIndexConformance.AStaleUnfinishedInstanceIsACandidate"/> can assert
    /// the identity round-trips without the suite and the store agreeing a value between them.
    /// </remarks>
    public const string FlowId = "order.place";

    /// <summary>The version every instance the suite arranges is pinned to.</summary>
    public const string FlowVersion = "1.2.0";

    /// <summary>The index under test.</summary>
    public abstract IRecoveryIndex Index { get; }

    /// <summary>Opens one instance, moves it to a state, and makes its row that cold.</summary>
    /// <param name="state">The state to leave the instance in.</param>
    /// <param name="idleFor">
    /// How long ago the row was last written. <see cref="TimeSpan.Zero"/> leaves it as
    /// freshly written, which is how the suite arranges a healthy instance.
    /// </param>
    /// <param name="tenantId">The partition key, or null for an untenanted instance.</param>
    /// <param name="cancellationToken">Cancels the arrangement.</param>
    /// <returns>The instance that was opened.</returns>
    public abstract ValueTask<Guid> AbandonAsync(
        FlowInstanceState state,
        TimeSpan idleFor,
        string? tenantId,
        CancellationToken cancellationToken);
}
