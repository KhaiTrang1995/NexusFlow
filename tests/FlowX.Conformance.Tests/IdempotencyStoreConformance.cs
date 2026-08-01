using Shouldly;
using Xunit;

namespace FlowX.Conformance;

/// <summary>
/// What an <see cref="IIdempotencyStore"/> must do. Derive, supply a store, and the whole suite
/// runs against it unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The load-bearing assertion is
/// <see cref="ConcurrentCallersOfOneKeyProduceExactlyOneClaim"/>.</strong>
/// <c>docs/10-Policy-Framework.md</c> §7 names it: "the in-flight state matters: without it, two
/// concurrent requests with the same key both execute. This is the most common bug in
/// hand-rolled idempotency." A store whose <c>BeginAsync</c> is a read followed by a write
/// passes every sequential test in this file and fails that one — under exactly the concurrency
/// the policy exists for.
/// </para>
/// <para>
/// <strong>Two clients, for <see cref="RateLimiterConformance"/>'s reason.</strong> A record
/// held in a process deduplicates only the callers that happened to land on the same replica,
/// which is a deduplication rate equal to one over the replica count and nothing that says so.
/// </para>
/// <para>
/// <strong>Expiry is waited out in real time</strong>, like the lease suite's: a store whose
/// windows are Redis's <c>PEXPIRE</c> or PostgreSQL's <c>now()</c> has no clock to inject.
/// </para>
/// <para>
/// <strong>What this suite never asserts is the shape of a record.</strong> The contract takes
/// and returns an opaque string, and what goes in it — a state-bag document that has been
/// through <see cref="JournalPayload"/>'s redacting exit — is the engine's business and
/// ADR-0038's. A store that inspected it would be a store that could leak it.
/// </para>
/// </remarks>
public abstract class IdempotencyStoreConformance
{
    /// <summary>A fresh store over an empty key space. Called once per test.</summary>
    protected abstract ValueTask<IdempotencyStoreUnderTest> CreateAsync();

    /// <summary>The window the expiry assertions use. Short, so the suite stays quick.</summary>
    protected virtual TimeSpan ShortWindow => TimeSpan.FromMilliseconds(400);

    /// <summary>A window long enough that nothing expires inside a test that is not about expiry.</summary>
    protected virtual TimeSpan LongWindow => TimeSpan.FromMinutes(5);

    /// <summary>How long a claim is held in the assertions that are not about a lapsed one.</summary>
    protected virtual TimeSpan LongLease => TimeSpan.FromMinutes(5);

    /// <summary>The ambient test cancellation token.</summary>
    protected static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A key nothing else in this run uses.</summary>
    protected static string FreshKey() => $"conformance:{Guid.NewGuid():n}";

    private const string Record = """{"schemaVersion":"1.0.0","validated":{"amount":42}}""";

    // -------------------------------------------------------------------- the three states

    /// <summary>A key nobody holds is claimed by the caller that presents it.</summary>
    [Fact]
    public async Task AnUnheldKeyIsClaimed()
    {
        await using var store = await CreateAsync();

        var began = await store.Store.BeginAsync(FreshKey(), LongWindow, LongLease, Cancellation);

        ShouldSucceed(began, "nobody held it.");

        began.Value.State.ShouldBe(
            IdempotencyState.Started,
            "the caller now owns the outcome and is expected to produce it.");

        began.Value.Record.ShouldBeNull("there is nothing recorded to replay.");
    }

    /// <summary>A key a completed record sits under is replayed rather than claimed.</summary>
    /// <remarks>
    /// The whole point of the policy, and it has to come back <em>byte for byte</em>: a store
    /// that re-encoded the record would hand the engine a document its generated
    /// <c>RestoreState</c> might read differently, and a replay that differs from the original
    /// is the failure this contract is arranged to prevent.
    /// </remarks>
    [Fact]
    public async Task ACompletedKeyReplaysExactlyWhatWasRecorded()
    {
        await using var store = await CreateAsync();
        var key = FreshKey();

        ShouldSucceed(
            await store.Store.BeginAsync(key, LongWindow, LongLease, Cancellation),
            "the first caller claims it.");

        ShouldSucceed(
            await store.Store.CompleteAsync(key, Record, LongWindow, Cancellation),
            "and records what the step produced.");

        var again = await store.Store.BeginAsync(key, LongWindow, LongLease, Cancellation);

        ShouldSucceed(again, "a second caller presents the same key.");

        again.Value.State.ShouldBe(
            IdempotencyState.Completed,
            "the step ran, so it must not run again — which is the whole of what the window is " +
            "declared for.");

        again.Value.Record.ShouldBe(
            Record,
            "byte for byte. A store that re-encoded the document would hand the engine " +
            "something its generated reader might interpret differently, and a replay that " +
            "differs from the original is worse than no replay at all.");
    }

    /// <summary>A key another caller is inside reads as in flight, with a wait.</summary>
    [Fact]
    public async Task AClaimedKeyReadsAsInFlight()
    {
        await using var store = await CreateAsync();
        var key = FreshKey();

        ShouldSucceed(
            await store.Store.BeginAsync(key, LongWindow, LongLease, Cancellation),
            "the first caller claims it and has not finished.");

        var second = await store.Store.BeginAsync(key, LongWindow, LongLease, Cancellation);

        ShouldSucceed(second, "the second caller asks.");

        second.Value.State.ShouldBe(
            IdempotencyState.InFlight,
            "somebody is running it. Answering Started here is the bug docs/10 §7 names: two " +
            "concurrent requests with one key both execute.");

        second.Value.RetryAfter.ShouldBeGreaterThan(
            TimeSpan.Zero,
            "the caller is being told to come back, so it needs to know when; a wait of zero " +
            "produces a hot loop against the holder.");

        second.Value.Record.ShouldBeNull("nothing has been produced yet, so there is nothing to hand back.");
    }

    // ------------------------------------------------------------------- claiming is atomic

    /// <summary>
    /// Concurrent callers of one key produce exactly one claim.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The assertion this suite exists for.</strong> A store implementing
    /// <c>BeginAsync</c> as a read followed by a write passes every sequential test above and
    /// fails this — and it fails it only under contention, which is the condition the policy is
    /// declared for. So the defect appears in production and never in a quiet test.
    /// </para>
    /// <para>
    /// Split across both clients, so the race is between two connections rather than inside one
    /// client's pipelining. Every caller that is not the claimant must read as in flight: a
    /// store that answered <c>Completed</c> with no record, or <c>Started</c> twice, would let
    /// two callers run the step.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ConcurrentCallersOfOneKeyProduceExactlyOneClaim()
    {
        await using var store = await CreateAsync();
        var key = FreshKey();

        const int Callers = 32;

        var attempts = new Task<Result<IdempotencyEntry>>[Callers];

        for (var i = 0; i < Callers; i++)
        {
            var target = i % 2 == 0 ? store.Store : store.SecondClient;

            attempts[i] = Task.Run(
                async () => await target.BeginAsync(key, LongWindow, LongLease, Cancellation),
                Cancellation);
        }

        var results = await Task.WhenAll(attempts);

        foreach (var result in results)
        {
            ShouldSucceed(result, "every caller got an answer.");
        }

        results.Count(static r => r.Value.State == IdempotencyState.Started).ShouldBe(
            1,
            $"{Callers} callers presented one key at once, across two clients. Any other " +
            "number means the check and the claim are separable — two claimants run the step " +
            "twice, none of them runs it at all.");

        results.Count(static r => r.Value.State == IdempotencyState.InFlight).ShouldBe(
            Callers - 1,
            "and everybody else was told somebody has it, rather than being handed a record " +
            "that does not exist yet.");
    }

    /// <summary>Two keys do not interfere.</summary>
    [Fact]
    public async Task ClaimsOnDifferentKeysDoNotExclude()
    {
        await using var store = await CreateAsync();

        ShouldSucceed(
            await store.Store.BeginAsync(FreshKey(), LongWindow, LongLease, Cancellation),
            "one key is claimed.");

        var other = await store.Store.BeginAsync(FreshKey(), LongWindow, LongLease, Cancellation);

        ShouldSucceed(other, "a different key is a different outcome.");

        other.Value.State.ShouldBe(
            IdempotencyState.Started,
            "exclusivity is per key, not global. A store that serialised every caller would " +
            "turn one window into a lock on the whole deployment.");
    }

    // ------------------------------------------------------------------------- it is shared

    /// <summary>A record written by one client is replayed by another.</summary>
    /// <remarks>
    /// <strong>The assertion a single-client suite cannot make.</strong> A record held in a
    /// process deduplicates only the callers that land on the same replica — a deduplication
    /// rate of one over the replica count, with nothing declaring it and nothing reporting it.
    /// </remarks>
    [Fact]
    public async Task ARecordWrittenByOneClientIsReplayedByAnother()
    {
        await using var store = await CreateAsync();
        var key = FreshKey();

        ShouldSucceed(
            await store.Store.BeginAsync(key, LongWindow, LongLease, Cancellation),
            "this client claims it.");

        ShouldSucceed(
            await store.Store.CompleteAsync(key, Record, LongWindow, Cancellation),
            "and records the outcome.");

        var elsewhere = await store.SecondClient.BeginAsync(key, LongWindow, LongLease, Cancellation);

        ShouldSucceed(elsewhere, "the other client presents the same key.");

        elsewhere.Value.State.ShouldBe(
            IdempotencyState.Completed,
            "A record only the writing process can see deduplicates one caller in n, where n " +
            "is the replica count — and every node's own numbers look correct while it happens.");

        elsewhere.Value.Record.ShouldBe(Record);
    }

    // ------------------------------------------------------------ abandoning and expiring

    /// <summary>An abandoned claim leaves the key free.</summary>
    /// <remarks>
    /// This is what makes "only a success is recorded" implementable (ADR-0037 §2.3). A failed
    /// step gives its key back, so the next caller runs it — and a store that kept the claim
    /// would make one transient failure lock a key out for the whole in-flight lease.
    /// </remarks>
    [Fact]
    public async Task AnAbandonedClaimFreesTheKey()
    {
        await using var store = await CreateAsync();
        var key = FreshKey();

        ShouldSucceed(
            await store.Store.BeginAsync(key, LongWindow, LongLease, Cancellation),
            "claimed.");

        var released = await store.Store.AbandonAsync(key, Cancellation);

        ShouldSucceed(released, "the step failed, so the caller gives the key back.");
        released.Value.ShouldBeTrue("the call reports that it did the releasing.");

        var next = await store.Store.BeginAsync(key, LongWindow, LongLease, Cancellation);

        ShouldSucceed(next, "the next caller presents the same key.");

        next.Value.State.ShouldBe(
            IdempotencyState.Started,
            "and runs it. A store that held the claim would make one transient failure lock the " +
            "key out for the whole lease, while the caller's only remedy is to present it again.");
    }

    /// <summary>Abandoning a key nobody holds is a value, not an error.</summary>
    /// <remarks>
    /// The engine abandons on every failure path, including ones where its claim has already
    /// lapsed. A store that treated that as an error would turn a routine unwind into a second
    /// failure to report.
    /// </remarks>
    [Fact]
    public async Task AbandoningAnUnheldKeyIsAValue()
    {
        await using var store = await CreateAsync();

        var released = await store.Store.AbandonAsync(FreshKey(), Cancellation);

        ShouldSucceed(released, "there was nothing to release, which is not an error condition.");
        released.Value.ShouldBeFalse("and the call says so rather than claiming it released one.");
    }

    /// <summary>An abandon never removes a completed record.</summary>
    /// <remarks>
    /// The quietest way to lose the policy. A caller whose claim lapsed, whose step then failed,
    /// and which tidily abandons would otherwise delete the record a <em>different</em> caller
    /// successfully wrote — and the next presenter of the key would run a step that has already
    /// happened.
    /// </remarks>
    [Fact]
    public async Task AbandoningDoesNotRemoveACompletedRecord()
    {
        await using var store = await CreateAsync();
        var key = FreshKey();

        ShouldSucceed(await store.Store.BeginAsync(key, LongWindow, LongLease, Cancellation), "claimed.");
        ShouldSucceed(await store.Store.CompleteAsync(key, Record, LongWindow, Cancellation), "recorded.");

        await store.Store.AbandonAsync(key, Cancellation);

        var again = await store.Store.BeginAsync(key, LongWindow, LongLease, Cancellation);

        ShouldSucceed(again, "the key is presented again.");

        again.Value.State.ShouldBe(
            IdempotencyState.Completed,
            "an abandon gives up an in-flight claim; it cannot un-record a success somebody " +
            "else wrote. A store that deleted here would re-run a step that already happened.");

        again.Value.Record.ShouldBe(Record);
    }

    /// <summary>A record stops being replayed once its window has passed.</summary>
    /// <remarks>
    /// The window is the author's declaration, and a store that ignored it would replay a
    /// result for ever — answering a caller in a year with what happened today, under a key it
    /// has every right to reuse.
    /// </remarks>
    [Fact]
    public async Task ARecordExpiresWithItsWindow()
    {
        await using var store = await CreateAsync();
        var key = FreshKey();

        ShouldSucceed(await store.Store.BeginAsync(key, ShortWindow, LongLease, Cancellation), "claimed.");
        ShouldSucceed(await store.Store.CompleteAsync(key, Record, ShortWindow, Cancellation), "recorded.");

        var freed = await ReachesStateWithinAsync(
            store.Store, key, IdempotencyState.Started, ShortWindow, ShortWindow * 10);

        freed.ShouldBeTrue(
            $"The window is {ShortWindow} and the record was still being replayed ten windows " +
            "later. A record that never expires answers a caller in a year with what happened " +
            "today, under a key they are entitled to reuse.");
    }

    /// <summary>A claim whose holder went away lapses, and the key becomes claimable.</summary>
    /// <remarks>
    /// <strong>The reason the lease is a separate parameter from the window.</strong> A node
    /// that takes a key and then dies must not wedge every repeat of it for a declared
    /// <c>PT24H</c>. Recovery latency is bounded by the lease, which is a tuning trade-off;
    /// nothing about correctness depends on it, because a lapsed claim frees a key nobody
    /// recorded against.
    /// </remarks>
    [Fact]
    public async Task ALapsedClaimBecomesClaimableAgain()
    {
        await using var store = await CreateAsync();
        var key = FreshKey();

        ShouldSucceed(
            await store.Store.BeginAsync(key, LongWindow, ShortWindow, Cancellation),
            "claimed by a node that is about to die.");

        var reclaimed = await ReachesStateWithinAsync(
            store.Store, key, IdempotencyState.Started, LongWindow, ShortWindow * 10);

        reclaimed.ShouldBeTrue(
            $"The claim's lease was {ShortWindow} and the key was still refusing ten leases " +
            "later. A claim that never lapses turns one crashed node into a key nobody can use " +
            "for as long as the author declared the window.");
    }

    // ------------------------------------------------------------------------- it is refused

    /// <summary>A store that cannot be reached answers with an error, not an exception.</summary>
    /// <remarks>
    /// The engine turns this into a refusal, which it can only do if it is a value —
    /// <see cref="RateLimiterConformance"/>'s argument, one stage down.
    /// </remarks>
    [Fact]
    public async Task AnUnreachableStoreAnswersWithAnErrorRatherThanThrowing()
    {
        await using var store = await CreateAsync();
        var unreachable = await store.UnreachableAsync(Cancellation);

        var began = await unreachable.BeginAsync(FreshKey(), LongWindow, LongLease, Cancellation);

        began.IsFailure.ShouldBeTrue(
            "the store is not there, so it did not decide anything — which is a different " +
            "thing from deciding the key is free, and dispatching on that difference is the " +
            "duplicate the window was declared to prevent.");
    }

    // ---------------------------------------------------------------------------- helpers

    /// <summary>Polls until the key reads as the wanted state, or gives up.</summary>
    private static async Task<bool> ReachesStateWithinAsync(
        IIdempotencyStore store,
        string key,
        IdempotencyState wanted,
        TimeSpan window,
        TimeSpan giveUpAfter)
    {
        var deadline = DateTimeOffset.UtcNow + giveUpAfter;

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), Cancellation);

            var began = await store.BeginAsync(key, window, TimeSpan.FromMilliseconds(50), Cancellation);

            if (began.IsSuccess && began.Value.State == wanted)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Asserts a store call answered, printing its own refusal if not.</summary>
    protected static void ShouldSucceed<T>(Result<T> result, string because) =>
        result.IsSuccess.ShouldBeTrue(
            $"{because} The store failed instead: {(result.IsFailure ? result.Error.ToString() : "no error")}");
}

/// <summary>
/// The two clients and the failure case an idempotency suite needs, supplied by the implementer.
/// </summary>
public abstract class IdempotencyStoreUnderTest : IAsyncDisposable
{
    /// <summary>The store under test.</summary>
    public abstract IIdempotencyStore Store { get; }

    /// <summary>
    /// A second store, independently constructed, over the same server and the same key space.
    /// </summary>
    /// <remarks>
    /// It must share nothing with <see cref="Store"/> but the server — see
    /// <c>RateLimiterUnderTest.SecondClient</c>, which says why at length. Returning
    /// <see cref="Store"/> itself would make the two assertions that justify this being a plugin
    /// contract pass vacuously.
    /// </remarks>
    public abstract IIdempotencyStore SecondClient { get; }

    /// <summary>A store of the same kind, pointed at a server that will not answer.</summary>
    /// <param name="cancellationToken">Cancels the construction.</param>
    /// <returns>The store. Disposed with this harness.</returns>
    public abstract ValueTask<IIdempotencyStore> UnreachableAsync(CancellationToken cancellationToken);

    /// <summary>Releases whatever the harness opened.</summary>
    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
