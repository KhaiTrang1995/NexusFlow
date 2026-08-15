using System.Collections.Concurrent;

namespace FlowX.Conformance.InMemory;

/// <summary>
/// The server an <see cref="InMemoryQuotaStore"/> counts against: one dictionary of counters,
/// shared by every client pointed at it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The server and the client are separate objects, and that is the whole point of the
/// reference implementation.</strong> <c>QuotaStoreConformance</c>'s load-bearing assertion is
/// that two independently constructed clients share one budget, and it can only be passed —
/// and can only be seen to be passed honestly — by a double whose state lives somewhere both
/// clients reach. A single class holding a dictionary in a field would pass the whole suite
/// except that one, which is exactly the failure the suite exists to catch, so the double is
/// built the way a real store is built.
/// </para>
/// </remarks>
public sealed class InMemoryQuotaServer
{
    private readonly ConcurrentDictionary<string, Counter> _counters = new(StringComparer.Ordinal);

    /// <summary>Spends one unit of a key's budget for the window <paramref name="now"/> is in.</summary>
    /// <remarks>
    /// <para>
    /// <strong>Under the counter's own lock, because the read, the roll and the increment are
    /// one decision.</strong> A double that did them separately would over-grant under
    /// contention and would make <c>ConcurrentCallersAreAdmittedExactlyToTheBudget</c> a test
    /// the reference cannot pass — which would leave a store author unable to tell whether the
    /// suite or their implementation was wrong.
    /// </para>
    /// <para>
    /// The window boundary is the epoch floored to a multiple of the period, the same absolute
    /// grid <c>PostgresQuotaStore</c> computes in SQL. Deriving it from "the first call for this
    /// key" instead would put every key on its own boundary, so two nodes seeing a key for the
    /// first time at different instants would disagree about when the budget resets.
    /// </para>
    /// </remarks>
    internal QuotaVerdict Consume(string key, int budget, TimeSpan period, DateTimeOffset now)
    {
        var counter = _counters.GetOrAdd(key, static _ => new Counter());

        lock (counter)
        {
            var start = WindowStart(now, period);

            if (counter.WindowStart != start)
            {
                counter.WindowStart = start;
                counter.Spent = 0;
            }

            if (counter.Spent >= budget)
            {
                var left = start + period - now;

                return new QuotaVerdict(
                    false,
                    0,
                    left <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : left);
            }

            counter.Spent++;

            return new QuotaVerdict(true, budget - counter.Spent, TimeSpan.Zero);
        }
    }

    private static DateTimeOffset WindowStart(DateTimeOffset now, TimeSpan period) =>
        DateTimeOffset.FromUnixTimeMilliseconds(
            now.ToUnixTimeMilliseconds() / (long)period.TotalMilliseconds * (long)period.TotalMilliseconds);

    private sealed class Counter
    {
        public DateTimeOffset WindowStart { get; set; }

        public long Spent { get; set; }
    }
}

/// <summary>
/// The reference <see cref="IQuotaStore"/>: a fixed-window counter over an
/// <see cref="InMemoryQuotaServer"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It exists to prove the suite is passable, and to be the thing a store author
/// reads.</strong> That is the job <c>InMemoryFlowJournal</c> and <c>InMemoryLeaseStore</c> do
/// for their contracts, and the reason each of them lives beside its suite rather than in a
/// shipped package: a reference is a specification made executable, not an implementation
/// anybody should deploy.
/// </para>
/// <para>
/// <strong>It is not shipped and could not be.</strong> A quota counted in a process grants n ×
/// the plan across n nodes — <c>IQuotaStore</c>'s own first paragraph — so a package offering
/// this as a default would be the policy reading as enforced and not being.
/// </para>
/// </remarks>
public sealed class InMemoryQuotaStore : IQuotaStore
{
    private readonly InMemoryQuotaServer _server;
    private readonly bool _reachable;

    /// <summary>Creates a client over a server.</summary>
    /// <param name="server">The counters. Two clients over one server are two nodes.</param>
    /// <param name="reachable">
    /// <c>false</c> for the client the suite points at a server that will not answer, which must
    /// report a <see cref="Result{T}"/> failure rather than throw.
    /// </param>
    public InMemoryQuotaStore(InMemoryQuotaServer server, bool reachable = true)
    {
        ArgumentNullException.ThrowIfNull(server);

        _server = server;
        _reachable = reachable;
    }

    /// <inheritdoc />
    public ValueTask<Result<QuotaVerdict>> TryConsumeAsync(
        string key,
        int budget,
        TimeSpan period,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budget);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(period, TimeSpan.Zero);

        if (!_reachable)
        {
            return new ValueTask<Result<QuotaVerdict>>(Result.Fail<QuotaVerdict>(new Error(
                "memory.quota_unavailable",
                "The in-memory quota store is unreachable.",
                ErrorCategory.Unavailable)));
        }

        return new ValueTask<Result<QuotaVerdict>>(
            Result.Ok(_server.Consume(key, budget, period, DateTimeOffset.UtcNow)));
    }
}
