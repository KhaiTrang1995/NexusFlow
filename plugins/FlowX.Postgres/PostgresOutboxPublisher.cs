using System.Data.Common;
using Npgsql;

namespace FlowX.Postgres;

/// <summary>
/// Drains <c>outbox_event</c> to an <see cref="IEventPublisher"/>, and marks what arrived.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this closes.</strong> <see cref="PostgresFlowJournal"/> already stages events
/// in the same transaction as the step that emitted them, which is the half of
/// <c>docs/11-Distributed-Runtime.md §5</c> that removes the dual-write problem. Nothing read
/// them back out. Constraint <c>C4</c> — "no 2-phase commit; consistency is saga-based,
/// outbox for atomic publish" — named the outbox as the mechanism that makes atomic publish
/// true, and until there was a publisher it named a table.
/// </para>
/// <para>
/// <strong>The ordering offered, exactly.</strong> Events with the same
/// <c>partition_key</c> reach the broker in the order they were staged, including when more
/// than one publisher is running — <see cref="OutboxSql.ClaimPending"/> holds an event back
/// when its key has an older pending sibling this claim did not take. <strong>Global
/// ordering is not offered.</strong> Two events with different keys can arrive in either
/// order, and no configuration changes that: a total order across keys serialises the whole
/// stream to one publisher, and per-key order is what a consumer that partitions by key can
/// actually use. An event with a null key asks for no order and is given none.
/// </para>
/// <para>
/// <strong>At-least-once, and never zero.</strong> A pass claims, publishes, marks and
/// commits in one transaction. A crash anywhere before the commit — including after the
/// broker acknowledged and before <c>published_at</c> was written — rolls the mark back and
/// leaves the rows pending, so the next pass republishes them. The event is delivered twice
/// rather than lost once, which is the composition <c>§4</c> calls effectively-once
/// processing and which requires consumers to be idempotent on
/// <see cref="OutboxRecord.EventId"/>.
/// </para>
/// <para>
/// <strong>The broker call happens inside the transaction, and that is the cost of the
/// guarantee.</strong> The rows stay locked while the publisher is on the network, so a slow
/// broker holds a claim open. That is what <c>FOR UPDATE SKIP LOCKED</c> is for: a second
/// publisher steps over the locked rows instead of waiting. Publishing outside the
/// transaction and marking after would either lose the mark on a crash — same guarantee, no
/// benefit — or need a claim column and a reaper for a publisher that died holding one.
/// </para>
/// <para>
/// <strong>One event nobody can publish stops the ones behind it.</strong> The batch is a
/// prefix contract: the caller marks the first <c>n</c> the publisher acknowledged and offers
/// the rest again, so an event a broker permanently refuses is retried forever and holds up
/// the outbox behind it. There is no dead-letter path here; <c>docs/17-Plugin-System.md §4</c>
/// lists DLQ as part of the <c>PublisherConformance</c> suite that has not been written, and
/// it is not part of this package.
/// </para>
/// <para>
/// <strong>At <see cref="TenantIsolation.Schema"/> it is the same loop asked once per
/// tenant.</strong> Every tenant has an <c>outbox_event</c> of its own, so the claim, the
/// <c>SKIP LOCKED</c> and the per-key hold are all evaluated inside one tenant's schema and
/// nothing about any of them changes — a smaller table asked the same question. What the
/// fan-out adds is three properties the single-schema loop had for free: the registry is
/// re-read every pass, so a tenant provisioned by another node a second ago is drained by this
/// one; the visiting order rotates, so a tenant with a permanent backlog cannot hold the batch
/// budget of the tenant behind it; and a tenant whose database call fails is recorded and
/// stepped over rather than ending the pass, because one tenant being down must not stop the
/// other nine publishing.
/// </para>
/// <para>
/// <strong>Ordering across tenants is not offered, and never was.</strong> ADR-0018 promises
/// staging order within one <c>partition_key</c> and nothing else. Two tenants that happen to
/// choose the same key string are two streams rather than one — at
/// <see cref="TenantIsolation.Row"/> they shared a table and the <c>NOT EXISTS</c> hold made one
/// wait behind the other, which was an accident of colocation rather than a guarantee. Here they
/// do not meet.
/// </para>
/// </remarks>
public sealed class PostgresOutboxPublisher
{
    private readonly NpgsqlDataSource? _dataSource;
    private readonly PostgresTenantStores? _stores;
    private readonly IEventPublisher _publisher;
    private readonly PostgresOutboxOptions _options;

    /// <summary>Where the next fan-out starts, so that no tenant is always visited last.</summary>
    private int _rotation;

    /// <summary>Creates a publisher over a data source and a broker adapter.</summary>
    /// <param name="dataSource">
    /// The data source. Its connection string selects the schema, and the publisher does not
    /// own it — a data source is pooled and shared with the journal.
    /// </param>
    /// <param name="publisher">Where acknowledged events go.</param>
    /// <param name="options">Batch size and poll interval. Defaults to §5's numbers.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public PostgresOutboxPublisher(
        NpgsqlDataSource dataSource,
        IEventPublisher publisher,
        PostgresOutboxOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(publisher);

        _dataSource = dataSource;
        _publisher = publisher;
        _options = options ?? new PostgresOutboxOptions();
    }

    /// <summary>Creates a publisher that drains every registered tenant's outbox in turn.</summary>
    /// <param name="stores">The per-tenant pools, and the registry that lists them.</param>
    /// <param name="publisher">Where acknowledged events go.</param>
    /// <param name="options">Batch size and poll interval, applied per tenant per pass.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// <see cref="PostgresOutboxOptions.BatchSize"/> becomes each tenant's own batch rather than
    /// the pass's, which is what <see cref="PostgresTenantRecoveryIndex"/> does with
    /// <c>PerTenantLimit</c> and for the same reason: the limit bounds how many rows one claim
    /// holds locks over, and a claim is per schema.
    /// </remarks>
    public PostgresOutboxPublisher(
        PostgresTenantStores stores,
        IEventPublisher publisher,
        PostgresOutboxOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stores);
        ArgumentNullException.ThrowIfNull(publisher);

        _stores = stores;
        _publisher = publisher;
        _options = options ?? new PostgresOutboxOptions();
    }

    /// <summary>
    /// Claims one batch, publishes it and marks what the broker acknowledged — once per
    /// tenant, where the deployment gives each tenant an outbox of its own.
    /// </summary>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>What the pass claimed and published, summed over the tenants it visited.</returns>
    /// <remarks>
    /// The whole pass is one transaction, and every early return leaves it to be rolled back
    /// by its disposal. That is the property the crash test exercises: there is no path
    /// through this method that marks an event published without the claim that produced it
    /// committing at the same instant, and no path that loses an event by marking one that
    /// did not arrive. One transaction <em>per tenant</em> under the fan-out, which is the same
    /// sentence: two tenants' rows were never in one claim to begin with.
    /// </remarks>
    public async ValueTask<OutboxPass> PublishPendingAsync(
        CancellationToken cancellationToken = default) =>
        (await SweepAsync(cancellationToken).ConfigureAwait(false)).Pass;

    /// <summary>One pass, and whether any schema it visited filled its batch.</summary>
    private ValueTask<Sweep> SweepAsync(CancellationToken cancellationToken) =>
        _stores is null
            ? PassAsync(_dataSource!, tenantId: null, cancellationToken)
            : FanOutAsync(_stores, cancellationToken);

    /// <summary>
    /// Every registered tenant's outbox, in a rotating order, with a failing tenant stepped over.
    /// </summary>
    private async ValueTask<Sweep> FanOutAsync(
        PostgresTenantStores stores,
        CancellationToken cancellationToken)
    {
        // Re-read every pass rather than cached, for KnownTenantsAsync's own stated reason: a
        // tenant another node provisioned five seconds ago has events this node must drain.
        var tenants = await stores.KnownTenantsAsync(cancellationToken).ConfigureAwait(false);

        if (tenants.Count == 0)
        {
            return Sweep.Nothing;
        }

        var offset = (int)((uint)Interlocked.Increment(ref _rotation) % (uint)tenants.Count);

        var claimed = 0;
        var published = 0;
        var saturated = false;
        Error? failure = null;

        for (var i = 0; i < tenants.Count; i++)
        {
            var tenant = tenants[(i + offset) % tenants.Count];

            try
            {
                var store = await stores.ForAsync(tenant, cancellationToken).ConfigureAwait(false);
                var sweep = await PassAsync(store, tenant, cancellationToken).ConfigureAwait(false);

                claimed += sweep.Pass.Claimed;
                published += sweep.Pass.Published;
                saturated |= sweep.Saturated;
                failure ??= sweep.Pass.Failure;
            }
            catch (Exception unreachable)
                when (unreachable is DbException or InvalidOperationException or TimeoutException)
            {
                // The tenant fails, not the pass. A schema that was dropped, a pool that is
                // exhausted or a database that stopped answering is one tenant's problem, and
                // letting it out of here would make every tenant after it in the rotation wait
                // for that repair. A broker that throws is deliberately not caught: that is not
                // a tenant failing, it is the publisher this loop exists to feed, and the
                // at-least-once guarantee is built on the transaction unwinding with it.
                failure ??= PostgresOutboxErrors.TenantUnreachable(tenant, unreachable);
            }
        }

        return new Sweep(new OutboxPass(claimed, published, failure), saturated);
    }

    /// <summary>One schema's claim, publish and mark, in one transaction.</summary>
    private async ValueTask<Sweep> PassAsync(
        NpgsqlDataSource dataSource,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var closing = connection.ConfigureAwait(false);

        var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var closingTransaction = transaction.ConfigureAwait(false);

        var claimed = await ClaimAsync(connection, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (claimed.Count == 0)
        {
            return Sweep.Nothing;
        }

        var published = await _publisher.PublishAsync(claimed, cancellationToken)
            .ConfigureAwait(false);

        if (published.IsFailure)
        {
            // The broker refused the batch. Nothing is marked and the transaction rolls back,
            // so every claimed row is pending again the moment the lock is released. The
            // error is the publisher's to report — a caller that wants it can ask the
            // publisher, and a caller that wants throughput wants the next pass.
            return new Sweep(new OutboxPass(claimed.Count, 0, published.Error), Saturated: false);
        }

        var acknowledged = Prefix(claimed, published.Value);

        if (acknowledged.Length == 0)
        {
            return new Sweep(new OutboxPass(claimed.Count, 0, null), Saturated: false);
        }

        await MarkAsync(connection, acknowledged, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new Sweep(
            new OutboxPass(claimed.Count, acknowledged.Length, null),
            acknowledged.Length >= _options.BatchSize);
    }

    /// <summary>
    /// Polls until cancelled, draining the outbox as fast as it fills.
    /// </summary>
    /// <param name="cancellationToken">Stops the loop.</param>
    /// <returns>A task that completes when the loop is cancelled.</returns>
    /// <remarks>
    /// <para>
    /// A plain loop rather than a hosted service. This package depends on
    /// <c>FlowX.Abstractions</c> and Npgsql and nothing else (ADR-0009), and a
    /// <c>BackgroundService</c> would add a hosting dependency to a plugin for the sake of
    /// one <c>while</c>. A host wires this the way it wires any long-running task.
    /// </para>
    /// <para>
    /// The loop keys its wait on what was <em>published</em>, not on what was claimed. A pass
    /// that drained a full batch is followed immediately by the next one, so the poll
    /// interval bounds idle latency without throttling a backlog; a pass that claimed a full
    /// batch and published none — a refusing broker, or a batch entirely held behind older
    /// siblings — waits, because retrying that at full speed is a hot loop against a
    /// condition that needs time to change. Under the fan-out the same question is asked of
    /// each tenant separately and answered <c>true</c> if any of them filled its batch, because
    /// a sum across ten idle tenants and one saturated one is neither number.
    /// </para>
    /// <para>
    /// <strong>Cancellation ends the loop, it does not fault it.</strong> A shutdown that
    /// arrives while a pass is on the network cancels the database call, and the transaction
    /// that call was in rolls back — the claimed events stay pending and the next process to
    /// run publishes them, which is the same at-least-once outcome as a crash. Letting the
    /// <c>OperationCanceledException</c> escape would put a stack trace in every host's log at
    /// every deployment, which is how a component teaches operators to ignore its logs.
    /// </para>
    /// </remarks>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var sweep = await SweepAsync(cancellationToken).ConfigureAwait(false);

                if (sweep.Saturated)
                {
                    continue;
                }

                await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Only when this token is the one that was cancelled. A cancellation from
            // anywhere else — a command timeout surfacing as one, say — is a fault and stays
            // one; swallowing every OperationCanceledException would hide a store that has
            // stopped answering behind a loop that looks like it shut down politely.
        }
    }

    /// <summary>Takes the leading events the publisher acknowledged.</summary>
    /// <remarks>
    /// A count above the batch is clamped rather than trusted. <see cref="IEventPublisher"/>
    /// is an extension point, so this number arrives from code this package did not write,
    /// and reading past the end of the claim would be the publisher's defect becoming this
    /// one.
    /// </remarks>
    private static Guid[] Prefix(IReadOnlyList<OutboxRecord> claimed, int published)
    {
        var count = Math.Clamp(published, 0, claimed.Count);
        var events = new Guid[count];

        for (var i = 0; i < count; i++)
        {
            events[i] = claimed[i].EventId;
        }

        return events;
    }

    private async ValueTask<IReadOnlyList<OutboxRecord>> ClaimAsync(
        NpgsqlConnection connection,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();

        command.CommandText = OutboxSql.ClaimPending;
        command.Parameters.Add(Db.Int("batch", _options.BatchSize));

        var claimed = new List<OutboxRecord>();

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            claimed.Add(JournalRows.PendingOutbox(reader, tenantId));
        }

        return claimed;
    }

    private static async ValueTask MarkAsync(
        NpgsqlConnection connection,
        Guid[] events,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();

        command.CommandText = OutboxSql.MarkPublished;
        command.Parameters.Add(Db.UuidArray("events", events));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A pass, plus the one fact the polling loop needs and a caller does not.</summary>
    /// <param name="Pass">What the pass claimed and published.</param>
    /// <param name="Saturated">
    /// Whether some schema filled its batch, which is <see cref="RunAsync"/>'s signal to come
    /// straight back rather than wait. Not on <see cref="OutboxPass"/>, because summing
    /// <see cref="OutboxPass.Published"/> across tenants and comparing it to a per-tenant batch
    /// size is the comparison this field exists to stop anybody making.
    /// </param>
    private readonly record struct Sweep(OutboxPass Pass, bool Saturated)
    {
        /// <summary>A pass that found nothing, in any schema it looked at.</summary>
        public static Sweep Nothing => new(OutboxPass.Empty, Saturated: false);
    }
}

/// <summary>What the fan-out reports when one tenant's outbox could not be reached.</summary>
/// <remarks>
/// A value rather than a throw, because the throw is what the fan-out is catching: the pass
/// carries on to the next tenant, and this is how the one it stepped over is not lost. Named
/// separately from <c>PostgresPolicyErrors</c> so that an operator grepping a log for a tenant
/// that stopped publishing finds one code.
/// </remarks>
internal static class PostgresOutboxErrors
{
    public static Error TenantUnreachable(string tenantId, Exception failure) => new Error(
        "postgres.outbox_tenant_unavailable",
        $"Tenant '{tenantId}' outbox could not be drained on this pass: {failure.Message}. " +
        "Every other tenant was still visited; this one is retried on the next pass.",
        ErrorCategory.Unavailable)
        .With("tenantId", tenantId);
}

/// <summary>What one pass of the outbox publisher did.</summary>
/// <param name="Claimed">
/// How many pending events the pass took. Below the batch size means the outbox is drained,
/// or that the rest is held behind an older sibling or another publisher's lock.
/// </param>
/// <param name="Published">
/// How many of them the broker acknowledged and the pass marked. Always a prefix of
/// <paramref name="Claimed"/>, and always committed together with the mark.
/// </param>
/// <param name="Failure">
/// Why the broker took nothing, when it took nothing because it refused — or, under the
/// per-tenant fan-out, the first tenant whose outbox could not be reached, whose other tenants
/// were still drained. Null when the publisher answered — including when it answered with a
/// count of zero, which is a publisher declining rather than failing.
/// </param>
public readonly record struct OutboxPass(int Claimed, int Published, Error? Failure)
{
    /// <summary>A pass that found nothing pending.</summary>
    public static OutboxPass Empty => new(0, 0, null);
}
