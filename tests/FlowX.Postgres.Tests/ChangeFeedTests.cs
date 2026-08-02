using Shouldly;
using Xunit;

namespace FlowX.Postgres.Tests;

/// <summary>
/// The outbox read as a change feed: what a cursor may be built on, and what it may not.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The barrier is the whole of the correctness argument</strong>
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0048-a-change-feed-advances-a-cursor.md">ADR-0048</a>).
/// <c>staged_seq</c> is allocated before commit and transactions commit out of order, so a
/// reader can see 7 before 6 exists — <c>0004_outbox_publication.sql</c> says so in as many
/// words. A cursor over it skips the late arrival for ever, silently. A cursor over
/// <c>staged_xid</c> below <c>pg_snapshot_xmin(pg_current_snapshot())</c> cannot, because every
/// transaction below the barrier has finished and none of them can insert again.
/// </para>
/// <para>
/// So the first test here does not exercise a feed at all. It exercises the property a feed
/// would be wrong without, against two concurrent transactions on the real server, because that
/// is the only arrangement in which the hazard exists.
/// </para>
/// </remarks>
public sealed class ChangeFeedTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>
    /// A row staged by a transaction that has not committed is invisible to the feed, and so is
    /// every row staged after it — even one whose own transaction has committed.
    /// </summary>
    /// <remarks>
    /// The gap, arranged deliberately. Transaction A opens and stages nothing yet; transaction B
    /// stages a row and commits. B's row has the higher <c>staged_seq</c>, so a
    /// <c>staged_seq</c> cursor would take it and move past A's — which has not been written
    /// yet. Under the barrier, B's row is held back until A finishes, and both then appear in
    /// one pass.
    /// </remarks>
    [Fact]
    public async Task ARowIsInvisibleUntilEveryOlderTransactionHasFinished()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var instance = await schema.StageableInstanceAsync(Cancellation);

        // A opens and takes a transaction id, which is what pins the barrier below B's row.
        await using var slow = await schema.BeginAsync(Cancellation);

        await slow.StageAsync(instance, "order.placed", "{\"n\":1}", Cancellation);

        await schema.StageAsync(instance, "order.placed", "{\"n\":2}", Cancellation);

        var visible = await schema.BelowBarrierAsync("order.placed", Cancellation);

        visible.ShouldBeEmpty(
            "the second row's transaction has committed and the first's has not, so the barrier " +
            "holds both back. A staged_seq cursor would have taken the second and skipped the " +
            "first for ever.");

        await slow.CommitAsync(Cancellation);

        var afterwards = await WaitForAsync(schema, 2);

        afterwards.ShouldBe(
            ["{\"n\":1}", "{\"n\":2}"],
            "both are below the barrier now, in staging order, and nothing was lost.");
    }

    /// <summary>
    /// Waits until the barrier has cleared, or reports what it saw instead.
    /// </summary>
    /// <remarks>
    /// <strong><c>pg_snapshot_xmin</c> is cluster-wide, not schema-wide.</strong> Any transaction
    /// open anywhere in the database holds the barrier down, including one belonging to another
    /// test running in parallel against the same server — so "these rows are visible the instant
    /// the staging transaction commits" is not a property the barrier offers, and asserting it
    /// would make this test fail on a busy database and pass on an idle one. That the wait is
    /// bounded by <em>somebody else's</em> transaction rather than by anything this code does is
    /// the trade-off
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0048-a-change-feed-advances-a-cursor.md">ADR-0048</a>
    /// records as the price of the guarantee.
    /// </remarks>
    private static async Task<IReadOnlyList<string>> WaitForAsync(PostgresTestSchema schema, int rows)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        var visible = await schema.BelowBarrierAsync("order.placed", Cancellation);

        while (visible.Count < rows && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25, Cancellation);

            visible = await schema.BelowBarrierAsync("order.placed", Cancellation);
        }

        return visible;
    }
}
