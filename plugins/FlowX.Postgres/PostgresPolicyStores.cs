using Npgsql;

namespace FlowX.Postgres;

/// <summary>
/// A token bucket shared by every node that consults it, in PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The second implementation, and that is what it is for.</strong> A conformance suite
/// with one implementation is a suite shaped like that implementation and nobody can tell from
/// inside it — <c>LeaseStoreConformance</c>'s remarks make the argument and <c>RedisLeaseStore</c>
/// was the evidence. This class and <c>RedisRateLimiterStore</c> agree on nothing except the
/// contract: one does the arithmetic in Lua against <c>TIME</c>, the other in SQL against
/// <c>now()</c>, and <c>RateLimiterConformance</c> is the only thing that makes them one seam.
/// </para>
/// <para>
/// <strong>One statement, and the atomicity is the row lock rather than a script.</strong>
/// <c>INSERT … ON CONFLICT DO UPDATE … RETURNING</c> reads, refills, decides, writes and reports
/// in a single statement, so two connections contending for one bucket serialise on the row and
/// neither can decide against a level the other has already spent.
/// <c>ConcurrentCallersAreAdmittedExactlyToTheBudget</c> is what holds this to it — and it is
/// the assertion a read-then-write implementation fails only under load, which is the condition
/// a limiter exists for.
/// </para>
/// <para>
/// <strong><c>now()</c> and never the caller's clock</strong>, which is
/// <c>PostgresLeaseStore</c>'s decision and <c>PolicyScripts</c>'s independently: two nodes
/// disagreeing about the time would disagree about the rate, and a limiter whose budget depends
/// on NTP is one whose declared rate is a hope.
/// </para>
/// </remarks>
public sealed class PostgresRateLimiterStore : IRateLimiterStore
{
    /// <summary>
    /// Refills the bucket to <c>now()</c>, spends a token if there is one, and reports.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>One statement, and it has to be one.</strong> The refill, the decision and the
    /// decrement are three things about one level, and a second connection that ran between any
    /// two of them would decide against a level the first had already spent. Two statements
    /// would need a transaction and a <c>FOR UPDATE</c>; one <c>INSERT … ON CONFLICT DO
    /// UPDATE</c> gets the same exclusion from the row lock the upsert already takes, in one
    /// round trip.
    /// </para>
    /// <para>
    /// <strong>The refused branch leaves the level alone rather than going into debt.</strong> A
    /// burst of refusals that each decremented would extend the wait for the caller that comes
    /// after them, so a bucket under sustained overload would take longer and longer to recover
    /// from a load it was already refusing.
    /// </para>
    /// <para>
    /// <c>admitted</c> is written rather than derived — see the migration, which says why the
    /// new level alone cannot answer the question.
    /// </para>
    /// </remarks>
    private const string TryAcquire =
        """
        INSERT INTO ratelimit_bucket AS b (bucket_key, tokens, touched_at, admitted)
        VALUES (@key, @permits::double precision - 1, now(), true)
        ON CONFLICT (bucket_key) DO UPDATE
           SET tokens = CASE
                   WHEN LEAST(
                            @permits::double precision,
                            b.tokens + EXTRACT(EPOCH FROM (now() - b.touched_at))
                                       * (@permits::double precision / @window_seconds::double precision)
                        ) >= 1
                   THEN LEAST(
                            @permits::double precision,
                            b.tokens + EXTRACT(EPOCH FROM (now() - b.touched_at))
                                       * (@permits::double precision / @window_seconds::double precision)
                        ) - 1
                   ELSE LEAST(
                            @permits::double precision,
                            b.tokens + EXTRACT(EPOCH FROM (now() - b.touched_at))
                                       * (@permits::double precision / @window_seconds::double precision)
                        )
                   END,
               touched_at = now(),
               admitted = LEAST(
                       @permits::double precision,
                       b.tokens + EXTRACT(EPOCH FROM (now() - b.touched_at))
                                  * (@permits::double precision / @window_seconds::double precision)
                   ) >= 1
        RETURNING admitted, tokens
        """;

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates a limiter over a data source.</summary>
    /// <param name="dataSource">The data source; its connection string selects the schema.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> is null.</exception>
    public PostgresRateLimiterStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _dataSource = dataSource;
    }

    /// <inheritdoc />
    public async ValueTask<Result<RateLimitVerdict>> TryAcquireAsync(
        string key,
        int permits,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permits);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);

        try
        {
            var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            await using var closing = connection.ConfigureAwait(false);

            using var command = connection.CreateCommand();

            command.CommandText = TryAcquire;
            command.Parameters.Add(Db.Text("key", key));
            command.Parameters.Add(Db.Int("permits", permits));
            command.Parameters.Add(Db.Double("window_seconds", window.TotalSeconds));

            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            await using var closingReader = reader.ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return PostgresPolicyErrors.RateLimiterAnsweredNothing();
            }

            var admitted = await reader.GetFieldValueAsync<bool>(0, cancellationToken).ConfigureAwait(false);
            var level = await reader.GetFieldValueAsync<double>(1, cancellationToken).ConfigureAwait(false);

            // The wait for the next whole token, from the level that was just written. A
            // refused caller left the level where it was, so this is the time for the bucket to
            // reach one — never longer than the window, because that is what a full refill takes.
            var retryAfter = admitted
                ? TimeSpan.Zero
                : Wait(level, permits, window);

            return Result.Ok(new RateLimitVerdict(admitted, (long)Math.Max(0d, Math.Floor(level)), retryAfter));
        }
        catch (NpgsqlException failure)
        {
            return PostgresPolicyErrors.RateLimiterUnreachable(failure);
        }
    }

    /// <summary>How long until the bucket holds one whole token.</summary>
    /// <remarks>
    /// Floored at one millisecond so a refusal never says "come back now", which is a refusal
    /// that produces a hot loop against the store that just refused.
    /// </remarks>
    private static TimeSpan Wait(double level, int permits, TimeSpan window)
    {
        var perSecond = permits / window.TotalSeconds;
        var seconds = (1d - Math.Max(0d, level)) / perSecond;

        return TimeSpan.FromMilliseconds(Math.Max(1d, Math.Ceiling(seconds * 1000d)));
    }
}

/// <summary>
/// Idempotency records, shared by every node that consults them, in PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// <strong><c>BeginAsync</c> is one statement, and it has to be.</strong> Two callers presenting
/// one key at the same instant is the case the whole policy exists for, and <c>docs/10 §7</c>
/// calls the missing atomicity "the most common bug in hand-rolled idempotency". The claim is an
/// <c>INSERT … ON CONFLICT DO UPDATE … WHERE</c> whose predicate is "nobody holds it and nothing
/// is recorded": the row lock serialises the contenders and the <c>WHERE</c> decides, so exactly
/// one caller's update takes and the rest read what is there.
/// </para>
/// <para>
/// <strong>The record is <c>text</c> and is never parsed.</strong> What this store is handed has
/// already been through <see cref="JournalPayload"/>'s single redacting exit
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0042-a-recorded-result-is-replayed-only-when-recording-lost-nothing.md">ADR-0042</a>),
/// and a store that parsed it would be a store that could reshape it — so a replay could hand
/// back something the engine's generated reader reads differently from what was written. That is
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0016-postgres-journal-adapter.md">ADR-0016</a>'s
/// <c>json</c>-not-<c>jsonb</c> argument, one column across and one step further: not even
/// <c>json</c>, because there is nothing here a query has any business looking inside.
/// </para>
/// </remarks>
public sealed class PostgresIdempotencyStore : IIdempotencyStore
{
    /// <summary>Claims the key, or reports who has it and what they produced.</summary>
    /// <remarks>
    /// The <c>WHERE</c> on the conflict branch is the whole of the exclusion: it takes only when
    /// no live record and no live claim is there. A caller whose update is refused falls through
    /// to the <c>SELECT</c> below and reads the state that beat it.
    /// </remarks>
    private const string Claim =
        """
        INSERT INTO idempotency_record AS r (idempotency_key, claimed_until)
        VALUES (@key, now() + @lease)
        ON CONFLICT (idempotency_key) DO UPDATE
           SET claimed_until = now() + @lease,
               record        = NULL,
               expires_at    = NULL
         WHERE (r.record IS NULL OR r.expires_at <= now())
           AND (r.claimed_until IS NULL OR r.claimed_until <= now())
        RETURNING idempotency_key
        """;

    /// <summary>Reads the state that beat this caller to the key.</summary>
    /// <remarks>
    /// <para>
    /// <strong>A second statement rather than a CTE beside the claim, and PostgreSQL leaves no
    /// choice.</strong> A <c>SELECT</c> in the same statement as a data-modifying CTE reads the
    /// snapshot the statement began with, so it cannot see the row the <c>INSERT</c> beside it
    /// just wrote — a fresh key would come back as no row at all. Splitting it also makes the
    /// read see the winner's committed state rather than a snapshot taken before the contention
    /// was resolved.
    /// </para>
    /// <para>
    /// It is on the contended path only. A key nobody holds is claimed in one round trip and
    /// never reaches this, which is the common case by a long way.
    /// </para>
    /// </remarks>
    private const string ReadState =
        """
        SELECT record, claimed_until, expires_at, now() AS server_now
          FROM idempotency_record
         WHERE idempotency_key = @key
        """;

    /// <summary>Records an outcome against a claimed key.</summary>
    /// <remarks>
    /// Refuses when a live record is already there. Two callers cannot both have claimed the
    /// key, but a caller whose claim lapsed can return to find that its successor already
    /// recorded — and overwriting would replace a result somebody has already replayed with one
    /// a different execution produced.
    /// </remarks>
    private const string Complete =
        """
        UPDATE idempotency_record
           SET record        = @record,
               expires_at    = now() + @window,
               claimed_until = NULL
         WHERE idempotency_key = @key
           AND (record IS NULL OR expires_at <= now())
        """;

    /// <summary>
    /// Gives up an in-flight claim without recording anything.
    /// </summary>
    /// <remarks>
    /// <strong>Never removes a live record.</strong> A caller whose claim lapsed, whose step then
    /// failed, and which tidily abandons would otherwise delete the record its successor wrote,
    /// and the next presenter of the key would run a step that has already happened. The
    /// <c>record IS NULL</c> guard is <c>PostgresLeaseStore</c>'s fencing-token guard wearing
    /// different clothes.
    /// </remarks>
    private const string Abandon =
        """
        DELETE FROM idempotency_record
         WHERE idempotency_key = @key
           AND (record IS NULL OR expires_at <= now())
        """;

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates a store over a data source.</summary>
    /// <param name="dataSource">The data source; its connection string selects the schema.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> is null.</exception>
    public PostgresIdempotencyStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _dataSource = dataSource;
    }

    /// <inheritdoc />
    public async ValueTask<Result<IdempotencyEntry>> BeginAsync(
        string key,
        TimeSpan window,
        TimeSpan inFlightFor,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(inFlightFor, TimeSpan.Zero);

        try
        {
            var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            await using var closing = connection.ConfigureAwait(false);

            using (var claim = connection.CreateCommand())
            {
                claim.CommandText = Claim;
                claim.Parameters.Add(Db.Text("key", key));
                claim.Parameters.Add(Db.Interval("lease", inFlightFor));

                if (await claim.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                {
                    return Result.Ok(new IdempotencyEntry(IdempotencyState.Started, null, TimeSpan.Zero));
                }
            }

            using var read = connection.CreateCommand();

            read.CommandText = ReadState;
            read.Parameters.Add(Db.Text("key", key));

            var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            await using var closingReader = reader.ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // The claim was refused and the row has since gone — a concurrent abandon, or a
                // record that expired between the two statements. Reported rather than guessed
                // at: the caller retries the whole thing, which is cheaper than either default,
                // because one of them dispatches a step that may already have run.
                return PostgresPolicyErrors.IdempotencyAnsweredNothing();
            }

            var record = await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false)
                ? null
                : await reader.GetFieldValueAsync<string>(0, cancellationToken).ConfigureAwait(false);

            var claimedUntil = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false)
                ? (DateTimeOffset?)null
                : await reader.GetFieldValueAsync<DateTimeOffset>(1, cancellationToken).ConfigureAwait(false);

            var expiresAt = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false)
                ? (DateTimeOffset?)null
                : await reader.GetFieldValueAsync<DateTimeOffset>(2, cancellationToken).ConfigureAwait(false);

            var serverNow = await reader.GetFieldValueAsync<DateTimeOffset>(3, cancellationToken)
                .ConfigureAwait(false);

            if (record is not null && expiresAt > serverNow)
            {
                return Result.Ok(new IdempotencyEntry(IdempotencyState.Completed, record, TimeSpan.Zero));
            }

            // Not claimed, not completed: somebody else's claim is live. The wait is derived from
            // the server's own clock rather than from this process's, so a caller whose clock is
            // wrong is still told the truth about when the holder's lease ends.
            var retryAfter = claimedUntil > serverNow ? claimedUntil.Value - serverNow : TimeSpan.Zero;

            return Result.Ok(new IdempotencyEntry(IdempotencyState.InFlight, null, retryAfter));
        }
        catch (NpgsqlException failure)
        {
            return PostgresPolicyErrors.IdempotencyUnreachable(failure);
        }
    }

    /// <inheritdoc />
    public async ValueTask<Result<bool>> CompleteAsync(
        string key,
        string record,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);

        return await ExecuteAsync(
            Complete,
            key,
            command =>
            {
                command.Parameters.Add(Db.Text("record", record));
                command.Parameters.Add(Db.Interval("window", window));
            },
            PostgresPolicyErrors.IdempotencyUnreachable,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<Result<bool>> AbandonAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        return await ExecuteAsync(
            Abandon,
            key,
            static _ => { },
            PostgresPolicyErrors.IdempotencyUnreachable,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<Result<bool>> ExecuteAsync(
        string sql,
        string key,
        Action<NpgsqlCommand> parameters,
        Func<NpgsqlException, Error> onFailure,
        CancellationToken cancellationToken)
    {
        try
        {
            var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            await using var closing = connection.ConfigureAwait(false);

            using var command = connection.CreateCommand();

            command.CommandText = sql;
            command.Parameters.Add(Db.Text("key", key));

            parameters(command);

            var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            return Result.Ok(affected > 0);
        }
        catch (NpgsqlException failure)
        {
            return onFailure(failure);
        }
    }
}

/// <summary>
/// A long-window budget shared by every node that consults it, in PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A fixed window with a stored counter, which is the difference from the token bucket
/// above.</strong> The window a call falls in is <c>now()</c> floored to a multiple of the
/// declared period, so two nodes agree about where the boundary is without agreeing about
/// anything else — the same reason every statement in this file reads the server's clock and
/// never the caller's. A caller arriving in a later window writes the new boundary and resets
/// the count in the same statement, so the reset costs nothing and there is no sweep to run.
/// </para>
/// <para>
/// <strong>One statement, for <see cref="PostgresRateLimiterStore"/>'s reason.</strong> The
/// read, the roll and the increment are three things about one count, and a second connection
/// between any two of them would decide against a count the first had already spent. The
/// upsert's row lock is the exclusion, and <c>RETURNING</c> carries the decision out.
/// </para>
/// <para>
/// <strong>The refused branch does not increment.</strong> A month of refusals that each
/// counted would drive <c>spent</c> arbitrarily far past the budget, which changes nothing
/// about the answer and makes the column stop meaning "calls admitted this period" — the one
/// thing an operator or a billing export would read it for.
/// </para>
/// </remarks>
public sealed class PostgresQuotaStore : IQuotaStore
{
    /// <summary>
    /// Rolls the window to <c>now()</c>, spends a unit if the budget has one, and reports.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The window boundary is computed with <c>to_timestamp(floor(extract(epoch from now()) /
    /// s) * s)</c> — the epoch second floored to a multiple of the period. It is an absolute
    /// grid rather than "the period since this key was first seen", which is what makes the
    /// boundary the same for every key and for every node, and what lets an operator say when
    /// a budget comes back without reading the row.
    /// </para>
    /// <para>
    /// <c>spent</c> is compared before it is written, so the admitted branch is "fewer than
    /// budget calls have been made in this window". The refused branch leaves the count where
    /// it was; see this class's remarks.
    /// </para>
    /// </remarks>
    private const string TryConsume =
        """
        INSERT INTO quota_counter AS q (quota_key, window_start, spent, admitted)
        VALUES (
            @key,
            to_timestamp(floor(extract(epoch from now()) / @period_seconds::double precision)
                         * @period_seconds::double precision),
            1,
            true)
        ON CONFLICT (quota_key) DO UPDATE
           SET window_start = to_timestamp(
                   floor(extract(epoch from now()) / @period_seconds::double precision)
                   * @period_seconds::double precision),
               spent = CASE
                   WHEN q.window_start < to_timestamp(
                            floor(extract(epoch from now()) / @period_seconds::double precision)
                            * @period_seconds::double precision)
                   THEN 1
                   WHEN q.spent < @budget::bigint
                   THEN q.spent + 1
                   ELSE q.spent
                   END,
               admitted = q.window_start < to_timestamp(
                              floor(extract(epoch from now()) / @period_seconds::double precision)
                              * @period_seconds::double precision)
                          OR q.spent < @budget::bigint
        RETURNING admitted, spent, window_start, now() AS server_now
        """;

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates a quota store over a data source.</summary>
    /// <param name="dataSource">The data source; its connection string selects the schema.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> is null.</exception>
    public PostgresQuotaStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _dataSource = dataSource;
    }

    /// <inheritdoc />
    public async ValueTask<Result<QuotaVerdict>> TryConsumeAsync(
        string key,
        int budget,
        TimeSpan period,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budget);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(period, TimeSpan.Zero);

        try
        {
            var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            await using var closing = connection.ConfigureAwait(false);

            using var command = connection.CreateCommand();

            command.CommandText = TryConsume;
            command.Parameters.Add(Db.Text("key", key));
            command.Parameters.Add(Db.Long("budget", budget));
            command.Parameters.Add(Db.Double("period_seconds", period.TotalSeconds));

            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            await using var closingReader = reader.ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return PostgresPolicyErrors.QuotaAnsweredNothing();
            }

            var admitted = await reader.GetFieldValueAsync<bool>(0, cancellationToken).ConfigureAwait(false);
            var spent = await reader.GetFieldValueAsync<long>(1, cancellationToken).ConfigureAwait(false);
            var start = await reader.GetFieldValueAsync<DateTime>(2, cancellationToken).ConfigureAwait(false);
            var now = await reader.GetFieldValueAsync<DateTime>(3, cancellationToken).ConfigureAwait(false);

            // What is left of the window the refusal happened in, from the server's own clock
            // on both sides — never the caller's, which would make the wait depend on NTP.
            var retryAfter = admitted ? TimeSpan.Zero : Remaining(start, now, period);

            return Result.Ok(new QuotaVerdict(admitted, Math.Max(0L, budget - spent), retryAfter));
        }
        catch (NpgsqlException failure)
        {
            return PostgresPolicyErrors.QuotaUnreachable(failure);
        }
    }

    /// <summary>How long until the window turns over and the budget is granted again.</summary>
    /// <remarks>
    /// Floored at one millisecond so a refusal never says "come back now", which is a refusal
    /// that produces a hot loop against the store that just refused — the same floor
    /// <see cref="PostgresRateLimiterStore"/> applies, for the same reason.
    /// </remarks>
    private static TimeSpan Remaining(DateTime windowStart, DateTime now, TimeSpan period)
    {
        var left = windowStart + period - now;

        return left <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : left;
    }
}

/// <summary>What the PostgreSQL policy stores report when the database does not answer.</summary>
/// <remarks>
/// An <see cref="Error"/> rather than an exception, for <c>RedisRateLimiterStore</c>'s reason:
/// the engine turns it into a refusal and can only do that if it is a value.
/// </remarks>
internal static class PostgresPolicyErrors
{
    public static Error RateLimiterUnreachable(Exception failure) => new(
        "postgres.ratelimit_unavailable",
        $"The PostgreSQL rate limiter did not answer: {failure.Message}",
        ErrorCategory.Unavailable);

    public static Error IdempotencyUnreachable(Exception failure) => new(
        "postgres.idempotency_unavailable",
        $"The PostgreSQL idempotency store did not answer: {failure.Message}",
        ErrorCategory.Unavailable);

    /// <summary>
    /// The statement ran and returned no row, which no correct schema produces.
    /// </summary>
    /// <remarks>
    /// Reported rather than defaulted, because both defaults are wrong in the dangerous
    /// direction: defaulting to admitted would over-admit silently, and defaulting to a free key
    /// would dispatch a step that may already have run.
    /// </remarks>
    public static Error RateLimiterAnsweredNothing() => new(
        "postgres.ratelimit_unavailable",
        "The rate-limit statement returned no row. The bucket table is missing or has a shape " +
        "this build does not write — run the migrations.",
        ErrorCategory.Unavailable);

    public static Error QuotaUnreachable(Exception failure) => new(
        "postgres.quota_unavailable",
        $"The PostgreSQL quota store did not answer: {failure.Message}",
        ErrorCategory.Unavailable);

    public static Error QuotaAnsweredNothing() => new(
        "postgres.quota_unavailable",
        "The quota statement returned no row. The counter table is missing or has a shape this " +
        "build does not write — run the migrations.",
        ErrorCategory.Unavailable);

    public static Error IdempotencyAnsweredNothing() => new(
        "postgres.idempotency_unavailable",
        "The idempotency statement returned no row. The record table is missing or has a shape " +
        "this build does not write — run the migrations.",
        ErrorCategory.Unavailable);
}
