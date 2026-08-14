using System.Diagnostics;
using System.Globalization;
using FlowX.Hosting;
using Shouldly;
using Xunit;

namespace FlowX.Postgres.Tests;

/// <summary>
/// The listener against a real server: what wakes a sweep, when, and what happens when the
/// session it is holding is taken away from it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every assertion here is a wait on the signal rather than a sleep and a look.</strong>
/// The interval passed to <see cref="ISweepSignal.WaitAsync"/> is thirty seconds and the test's
/// patience is ten, so a wait that completes at all could not have completed because the interval
/// elapsed — there is no threshold to tune, and a slow machine makes the test slower rather than
/// flakier. The one measurement that is a duration, in
/// <see cref="AParkedWakeWakesTheTimerSweepWhenTheWaitEndsRatherThanWhenItIsWritten"/>, is
/// compared against a second and ten seconds, which is three orders of magnitude of headroom
/// either side of the thing being distinguished.
/// </para>
/// <para>
/// <strong>Against PostgreSQL rather than a double, because none of this is C#.</strong> Whether
/// a staged event announces itself is a trigger in migration <c>0013</c>; whether the
/// announcement survives a commit boundary is <c>pg_notify</c>'s transactional behaviour; whether
/// a killed session comes back is Npgsql's and this class's between them. A double would assert
/// that the object calls its own method.
/// </para>
/// <para>
/// The skip behaviour is <see cref="PostgresTestDatabase"/>'s and is inherited.
/// </para>
/// </remarks>
public sealed class SweepSignalTests
{
    /// <summary>
    /// An interval no test here can reach, so that a completed wait is evidence of an
    /// announcement and of nothing else.
    /// </summary>
    private static readonly TimeSpan Unreachable = TimeSpan.FromSeconds(30);

    /// <summary>How long a test waits for a wake before calling it a failure.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>
    /// Staging an event wakes the change sweep far inside the interval it would have polled on.
    /// </summary>
    /// <remarks>
    /// The defect this package exists for, measured: <c>FlowXOptions.ChangeScanInterval</c> is one
    /// second, so a chain of a flow emitting and a flow consuming pays up to that per link. The
    /// wake arrives in the time a round trip takes.
    /// </remarks>
    [Fact]
    public async Task AStagedEventWakesTheChangeSweepWellInsideThePollInterval()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);
        await using var signal = Listener(schema);

        await ListeningAsync(signal);

        var woken = signal.WaitAsync(SweepKind.Change, Unreachable, Cancellation);
        var since = Stopwatch.StartNew();

        await StageAsync(schema, Guid.NewGuid());

        await woken.WaitAsync(Patience, Cancellation);

        since.Elapsed.ShouldBeLessThan(
            new FlowXOptions().ChangeScanInterval,
            "the wake has to beat the poll it replaces, or it has bought nothing. A wait that " +
            "took longer than the interval means the announcement was missed and the interval " +
            "is what ended the wait — which is safe, and is the defect.");
    }

    /// <summary>
    /// A parked wake wakes the timer sweep when the wait ends, not when the row is written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Both halves are the assertion, and the first one is the easier mistake.</strong> A
    /// listener that woke the sweep at write time would look like it worked — a wake arrives,
    /// well inside the interval — and would have bought nothing at all: the sweep would find
    /// nothing due, sleep a full interval, and fire the timer exactly as late as before, having
    /// spent an extra query. So the lower bound is checked as hard as the upper one.
    /// </para>
    /// <para>
    /// The instant is a second out and the sweep's own interval is ten, which is the case
    /// <c>PLAN §6c</c> names.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AParkedWakeWakesTheTimerSweepWhenTheWaitEndsRatherThanWhenItIsWritten()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);
        await using var signal = Listener(schema);

        await ListeningAsync(signal);

        var woken = signal.WaitAsync(SweepKind.Timer, Unreachable, Cancellation);
        var since = Stopwatch.StartNew();

        await ParkAsync(schema, Guid.NewGuid(), TimeSpan.FromSeconds(1));

        await woken.WaitAsync(Patience, Cancellation);

        since.Elapsed.ShouldBeGreaterThan(
            TimeSpan.FromMilliseconds(500),
            "the sweep was woken when the row was written rather than when the wait it records " +
            "ends. That pass can only find nothing, and the timer still fires an interval late.");

        since.Elapsed.ShouldBeLessThan(
            new FlowXOptions().TimerScanInterval,
            "and it has to beat the sweep interval, which is the ten seconds a due .Delay(1s) " +
            "waits without this.");
    }

    /// <summary>
    /// A lease wakes the recovery sweep when it lapses, not when it is taken.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same two-sided assertion the parked wake gets, and the same easier mistake: a listener
    /// woken when the lease was <em>written</em> would sweep at once, find an instance whose owner
    /// is alive and well, and take over exactly as late as before.
    /// </para>
    /// <para>
    /// The TTL here is two seconds against a sweep interval of ten, which is the shape
    /// <c>PLAN §6c</c> names at thirty and ten. The wake is expected a beat after the expiry
    /// rather than on it — <c>PostgresSweepSignal.Settle</c> says why the beat is there.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ALeaseWakesTheRecoverySweepWhenItLapsesRatherThanWhenItIsTaken()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);
        await using var signal = Listener(schema);

        await ListeningAsync(signal);

        var woken = signal.WaitAsync(SweepKind.Recovery, Unreachable, Cancellation);
        var since = Stopwatch.StartNew();

        _ = await AcquireAsync(schema, Guid.NewGuid(), TimeSpan.FromSeconds(2));

        await woken.WaitAsync(Patience, Cancellation);

        since.Elapsed.ShouldBeGreaterThan(
            TimeSpan.FromMilliseconds(1500),
            "the sweep was woken when the lease was taken rather than when it lapses. That pass " +
            "finds an instance whose owner still holds it, and the takeover is still an interval " +
            "late — which is the defect this migration exists to remove.");

        since.Elapsed.ShouldBeLessThan(
            new FlowXOptions().RecoveryScanInterval,
            "and it has to beat the sweep interval, which is what a dead node's instances wait " +
            "on top of the lease TTL without this.");
    }

    /// <summary>
    /// A renewal moves the wake, so the instant it superseded fires nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the case that decides the shape of the whole mechanism.</strong> A holder
    /// renews every <c>LeaseRenewalInterval</c>, so under load the announcements are mostly
    /// renewals — and a renewal <em>extends</em> an expiry. An armed instant that could only ever
    /// be kept or lowered would fire at the expiry the renewal replaced, on a lease whose holder
    /// is alive, once per TTL per instance. Announcing the earliest live expiry rather than the
    /// row's own is what lets a renewal move the armed instant later, and this is where that is
    /// checked rather than asserted in a comment.
    /// </para>
    /// <para>
    /// Two seconds of lease, renewed for thirty a beat later, then five seconds of silence
    /// demanded: the superseded instant and the beat after it are both inside that window.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ARenewedLeaseDoesNotWakeTheSweepAtTheInstantItSuperseded()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);
        await using var signal = Listener(schema);

        await ListeningAsync(signal);

        var woken = signal.WaitAsync(SweepKind.Recovery, Unreachable, Cancellation);

        var lease = await AcquireAsync(schema, Guid.NewGuid(), TimeSpan.FromSeconds(2));

        await Task.Delay(TimeSpan.FromMilliseconds(500), Cancellation);

        await RenewAsync(schema, lease, TimeSpan.FromSeconds(30));

        await Should.ThrowAsync<TimeoutException>(
            async () => await woken.WaitAsync(TimeSpan.FromSeconds(5), Cancellation),
            "the sweep was woken at an expiry the renewal had already moved. The pass it causes " +
            "can only find a live lease, and a node renewing a thousand of them would pay for " +
            "one such pass per lease per TTL.");
    }

    /// <summary>Releasing a lease wakes nothing, because a finished instance is not recovery.</summary>
    /// <remarks>
    /// A release moves <c>expires_at</c> into the past rather than deleting the row, so every
    /// completing instance writes the lease table. Announcing an instant in the past would put a
    /// recovery pass behind every completed flow in the deployment — a stampede built out of the
    /// ordinary case — which is why <c>0014</c> announces the earliest expiry that is still in
    /// the future and stays silent when there is none.
    /// </remarks>
    [Fact]
    public async Task ReleasingTheOnlyLeaseWakesNothing()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);
        await using var signal = Listener(schema);

        await ListeningAsync(signal);

        var lease = await AcquireAsync(schema, Guid.NewGuid(), TimeSpan.FromSeconds(30));

        var woken = signal.WaitAsync(SweepKind.Recovery, Unreachable, Cancellation);

        await ReleaseAsync(schema, lease);

        await Should.ThrowAsync<TimeoutException>(
            async () => await woken.WaitAsync(TimeSpan.FromSeconds(3), Cancellation),
            "a completed instance woke the recovery sweep. There is nothing to take over from " +
            "an instance that finished, and the announcement that says so is the one every flow " +
            "in the deployment makes.");
    }

    /// <summary>
    /// A session killed under the listener comes back, and announcements land on the new one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The reconnection is the whole reliability argument for a held session.</strong> A
    /// listener that stopped at the first dropped connection would leave a node on its poll
    /// interval for ever, silently — the sweeps would keep working, at the latency this package
    /// exists to remove, and nothing anywhere would say why.
    /// </para>
    /// <para>
    /// <strong>Two things are asserted and the second is the one that matters.</strong> That the
    /// reconnection announces itself is deliberate — a listener that was away cannot know what it
    /// missed, so it sweeps once — and it is <em>not</em> evidence that listening was restored.
    /// The evidence is the wake after it, which can only come from a <c>LISTEN</c> issued on the
    /// new session by a trigger that fired after the kill.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheListenerReconnectsWhenItsSessionIsKilled()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);
        await using var signal = Listener(schema);

        await ListeningAsync(signal);

        var killed = await SessionAsync(schema);

        killed.ShouldNotBeNull("the listener holds one session, named after the schema it serves.");

        await schema.ExecuteAsync(
            $"SELECT pg_terminate_backend({killed})", Cancellation);

        // The reconnection's own announcement, consumed so that the wake asserted below can only
        // have come from the trigger.
        await signal.WaitAsync(SweepKind.Change, Unreachable, Cancellation).WaitAsync(
            Patience, Cancellation);

        await ListeningAsync(signal);

        (await SessionAsync(schema)).ShouldNotBe(
            killed, "the listener is holding a new session, not the one that was terminated.");

        var woken = signal.WaitAsync(SweepKind.Change, Unreachable, Cancellation);

        await StageAsync(schema, Guid.NewGuid());

        await woken.WaitAsync(
            Patience,
            Cancellation);
    }

    /// <summary>An announcement from another schema in the same database is not this node's.</summary>
    /// <remarks>
    /// A channel name is database-wide, which is the whole reason the payload carries a schema.
    /// Without the filter every FlowX deployment sharing a database would wake every other one's
    /// sweeps — passes that can only find nothing, on somebody else's write rate.
    /// </remarks>
    [Fact]
    public async Task AnAnnouncementFromAnotherSchemaIsIgnored()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);
        await using var other = await PostgresTestSchema.CreateAsync(Cancellation);
        await using var signal = Listener(schema);

        await ListeningAsync(signal);

        var woken = signal.WaitAsync(SweepKind.Change, TimeSpan.FromSeconds(2), Cancellation);
        var since = Stopwatch.StartNew();

        await StageAsync(other, Guid.NewGuid());

        await woken;

        since.Elapsed.ShouldBeGreaterThan(
            TimeSpan.FromSeconds(1),
            "the wait ended early, so another schema's staged event woke this node's sweep.");
    }

    /// <summary>The listener over the fixture's schema, on the same server the tests use.</summary>
    /// <remarks>
    /// The connection string is the fixture's, which is a direct one. A deployment behind a
    /// transaction pooler does not register this at all —
    /// <c>ServiceCollectionExtensions.AddFlowXPostgresSweepSignal</c> says why.
    /// </remarks>
    private static PostgresSweepSignal Listener(PostgresTestSchema schema) =>
        new(PostgresTestDatabase.ConnectionString!, schema.Options);

    /// <summary>
    /// Waits until the listener holds a subscribed session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Arrangement, not assertion.</strong> An announcement made before <c>LISTEN</c> is
    /// issued is delivered to nobody — that is what <c>LISTEN</c> means — so a test that staged
    /// its event first would be asserting the interval. Polling here is what makes every
    /// assertion afterwards a deterministic wait rather than a race.
    /// </para>
    /// <para>
    /// The first wait is what opens the session: the listener connects on demand, so that a host
    /// which resolves it and never sweeps never holds a connection.
    /// </para>
    /// </remarks>
    private static async Task ListeningAsync(PostgresSweepSignal signal)
    {
        await signal.WaitAsync(SweepKind.Timer, TimeSpan.FromMilliseconds(1), Cancellation);

        var since = Stopwatch.StartNew();

        while (!signal.IsListening && since.Elapsed < Patience)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), Cancellation);
        }

        signal.IsListening.ShouldBeTrue(
            "the listener never reached a subscribed session, so nothing below could have been " +
            "announced to it.");
    }

    /// <summary>The backend holding this schema's listening session, or null when there is none.</summary>
    /// <remarks>
    /// Found by application name, which the listener sets so that the one idle connection it adds
    /// to a deployment's budget answers for itself rather than looking like a leak.
    /// </remarks>
    private static async Task<int?> SessionAsync(PostgresTestSchema schema)
    {
        var pid = await schema.ScalarAsync(
            $"""
             SELECT pid
               FROM pg_stat_activity
              WHERE application_name = 'flowx-listen-{schema.Options.Schema}'
              ORDER BY backend_start DESC
              LIMIT 1
             """,
            Cancellation);

        return pid is int found ? found : null;
    }

    /// <summary>Stages one outbox event, which is what the change trigger announces.</summary>
    private static async Task StageAsync(PostgresTestSchema schema, Guid instance)
    {
        await schema.ExecuteAsync(
            $$"""
              INSERT INTO flow_instance (instance_id, flow_id, flow_version, state, fence)
              VALUES ('{{instance}}', 'order.place', '1.2.0', 'Running', 1);
              INSERT INTO outbox_event (event_id, instance_id, sequence, ordinal, type,
                                        schema_version, partition_key, payload)
              VALUES (gen_random_uuid(), '{{instance}}', 1, 0, 'order.placed',
                      '1.0.0', 'order-7', '{"a":1}');
              """,
            Cancellation);
    }

    /// <summary>
    /// Takes a lease, which is what the recovery trigger announces the consequence of.
    /// </summary>
    /// <remarks>
    /// The shipped store rather than the raw SQL the two helpers above use, because half of what
    /// is under test is that <c>PostgresLeaseStore</c>'s own statements write the column the
    /// trigger watches. A hand-written <c>UPDATE</c> would keep announcing after the store had
    /// stopped writing it, which is the regression worth catching.
    /// </remarks>
    private static async Task<FlowLease> AcquireAsync(
        PostgresTestSchema schema, Guid instance, TimeSpan ttl)
    {
        var acquired = await schema.Leases.AcquireAsync(instance, "node-1", ttl, Cancellation);

        acquired.IsSuccess.ShouldBeTrue("nobody else has this brand-new instance's lease.");

        return acquired.Value;
    }

    /// <summary>Extends a lease, which is the announcement this schema makes most of.</summary>
    private static async Task<FlowLease> RenewAsync(
        PostgresTestSchema schema, FlowLease lease, TimeSpan ttl)
    {
        var renewed = await schema.Leases.RenewAsync(lease, ttl, Cancellation);

        renewed.IsSuccess.ShouldBeTrue("the lease is still held, so it renews.");

        return renewed.Value;
    }

    /// <summary>Gives a lease up, which moves its expiry into the past rather than deleting it.</summary>
    private static async Task ReleaseAsync(PostgresTestSchema schema, FlowLease lease)
    {
        var released = await schema.Leases.ReleaseAsync(lease, Cancellation);

        released.IsSuccess.ShouldBeTrue("the holder releases what it holds.");
    }

    /// <summary>Parks an instance on a wake instant, which is what the timer trigger announces.</summary>
    private static async Task ParkAsync(PostgresTestSchema schema, Guid instance, TimeSpan dueIn)
    {
        var at = DateTimeOffset.UtcNow.Add(dueIn).ToString("O", CultureInfo.InvariantCulture);

        await schema.ExecuteAsync(
            $"""
             INSERT INTO flow_instance (instance_id, flow_id, flow_version, state, fence,
                                        wake_at, wake_step_id, wake_scope)
             VALUES ('{instance}', 'order.place', '1.2.0', 'Suspended', 1,
                     '{at}', 1, '')
             """,
            Cancellation);
    }
}
