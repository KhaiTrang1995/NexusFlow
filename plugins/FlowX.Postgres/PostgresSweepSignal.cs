using System.Globalization;
using Npgsql;

namespace FlowX.Postgres;

/// <summary>
/// Turns the announcements migrations <c>0013</c> and <c>0014</c> make into the wake a host's
/// sweeps wait on.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One session, held open, doing nothing but listening.</strong> <c>LISTEN</c> is a
/// property of a connection rather than of a statement: the session that issued it is the session
/// notifications are delivered to, so this holds one connection for the life of the process and
/// takes it from nobody. It is opened on the first wait rather than at construction, for
/// <see cref="PostgresOutboxPublisher"/>'s reason — registering a store must not start a loop —
/// and a host that resolves this and never waits on it never connects.
/// </para>
/// <para>
/// <strong>It is an accelerator, and every failure mode is chosen to keep it one.</strong> The
/// connection drops, the server restarts, an announcement lands while this node was between
/// sessions: in each case the sweep waits out its interval and finds the same rows it would have
/// found anyway. Nothing here is allowed to throw into a sweep loop and nothing here is allowed
/// to make a pass wait longer than the interval it was given.
/// </para>
/// <para>
/// <strong>The channels are part of the contract with the migrations</strong>, which is why they
/// are named here as constants rather than assembled: the trigger functions call
/// <c>pg_notify</c> with these exact three names, and an operator poking a stuck node by hand can
/// use them — <c>NOTIFY flowx_sweep_change, 'flowx'</c> is a legitimate way to make a node sweep
/// now, and can do no harm because the pass it causes reads the cursor like every other pass.
/// </para>
/// </remarks>
public sealed class PostgresSweepSignal : ISweepSignal, IAsyncDisposable
{
    /// <summary>The channel a staged outbox event is announced on.</summary>
    public const string ChangeChannel = "flowx_sweep_change";

    /// <summary>The channel a parked wake is announced on.</summary>
    public const string TimerChannel = "flowx_sweep_timer";

    /// <summary>The channel the earliest live lease expiry is announced on.</summary>
    public const string RecoveryChannel = "flowx_sweep_recovery";

    /// <summary>
    /// All three channels, subscribed to in one round trip.
    /// </summary>
    /// <remarks>
    /// A literal rather than an interpolation over the three constants above, because
    /// <c>SqlFitnessTests</c> requires every statement to be fixed when the assembly is built and
    /// "it is only const holes" is a claim a scan cannot check. What holds them in step is
    /// behavioural: a listener that subscribed to a channel nothing announces on hears nothing,
    /// which is what <c>SweepSignalTests</c> asserts against a real server.
    /// </remarks>
    private const string ListenToAll =
        "LISTEN flowx_sweep_change; LISTEN flowx_sweep_timer; LISTEN flowx_sweep_recovery";

    /// <summary>
    /// How long after an announced lease expiry the recovery sweep is woken.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A lease is taken before the row it protects is written, and the sweep tests
    /// both.</strong> <c>FlowRecoveryScan</c> asks for instances whose <c>updated_at</c> is a
    /// lease TTL old and then acquires; the announcement is about the lease. On the ordinary path
    /// the lease is acquired and the instance row is written a journal round trip later, so the
    /// two thresholds are the same TTL measured from instants a few milliseconds apart — and a
    /// wake armed at exactly the announced instant would land on the earlier of them, every time,
    /// finding nothing and sleeping a full interval. That is the one way this hint can be
    /// reliably useless, and this is the whole of the defence against it.
    /// </para>
    /// <para>
    /// A second rather than a millisecond because it also absorbs the fast steps an instance may
    /// have committed after its lease was taken, and it is small against every interval it
    /// competes with — the sweep's own is ten seconds and the TTL it rides on is thirty. It
    /// cannot absorb a step that outlives a renewal interval; that instance's wake is early, its
    /// pass finds nothing, and the interval takes it, which is where this started.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(1);

    /// <summary>How long the first reconnection waits, and the ceiling it doubles towards.</summary>
    /// <remarks>
    /// Short at the start because the ordinary cause is a server that bounced and is already back,
    /// and bounded because the cost of being disconnected is latency rather than loss — there is
    /// nothing here worth hammering a recovering server for.
    /// </remarks>
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromMilliseconds(200);

    private static readonly TimeSpan LongestBackoff = TimeSpan.FromSeconds(10);

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _schema;
    private readonly string? _tenantPrefix;
    private readonly Gate _change = new();
    private readonly Gate _timer = new();
    private readonly Gate _recovery = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _sync = new();

    private Task? _listening;
    private TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _connections;
    private long _horizonTicks;
    private long _sweepsRecovery;
    private Timer? _due;
    private DateTimeOffset _dueAt;
    private Timer? _lapse;
    private DateTimeOffset _lapseAt;
    private string? _lapseSchema;

    /// <summary>Creates a listener over a connection string of its own.</summary>
    /// <param name="directConnectionString">
    /// How to reach PostgreSQL <em>directly</em>. See
    /// <c>ServiceCollectionExtensions.AddFlowXPostgresSweepSignal</c> for why this is a second
    /// connection string rather than the journal's.
    /// </param>
    /// <param name="options">Where the tables live, which is what a notification is filtered by.</param>
    /// <exception cref="ArgumentException"><paramref name="directConnectionString"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public PostgresSweepSignal(string directConnectionString, PostgresJournalOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directConnectionString);

        var settings = options ?? new PostgresJournalOptions();

        _schema = settings.Schema;
        _tenantPrefix = settings.TenantSchemas.IsEnabled ? settings.TenantSchemas.Prefix : null;
        _dataSource = BuildListener(directConnectionString, _schema);
    }

    /// <summary>Whether this listener currently holds a session that is subscribed.</summary>
    /// <remarks>
    /// For a probe and for a test that has to announce something <em>after</em> the subscription
    /// exists. Nothing in the sweep path reads it: a sweep that asked whether the listener was
    /// connected before deciding what to do would have made the connection load-bearing, which is
    /// the one thing this class must not become.
    /// </remarks>
    public bool IsListening
    {
        get
        {
            lock (_sync)
            {
                return _connected.Task.IsCompleted;
            }
        }
    }

    /// <inheritdoc />
    public async Task WaitAsync(
        SweepKind sweep, TimeSpan interval, CancellationToken cancellationToken)
    {
        // What the timer sweep's own interval is, learned from the sweep rather than configured
        // twice. It is the horizon a parked wake has to fall inside to be worth arming a local
        // timer for: anything further out is reached by the sweep first, and a host that never
        // waits on the timer sweep leaves this zero and arms nothing at all.
        if (sweep is SweepKind.Timer)
        {
            Volatile.Write(ref _horizonTicks, interval.Ticks);
        }

        // The same question the horizon answers for timers — is there anybody in this process to
        // wake? — with no horizon to go with it. A lease announcement's instant is a TTL out and
        // a TTL is longer than the sweep interval by construction, so the rule that ignores a
        // parked wake beyond the interval would ignore every lease there has ever been. What that
        // rule is really protecting against is a process-resident object per row, and there is
        // one armed instant for the node here whatever the instant is.
        if (sweep is SweepKind.Recovery)
        {
            Volatile.Write(ref _sweepsRecovery, 1);
        }

        EnsureListening();

        var gate = sweep switch
        {
            SweepKind.Timer => _timer,
            SweepKind.Recovery => _recovery,
            _ => _change,
        };
        var raised = gate.Pending;

        using var elapsing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var elapsed = Task.Delay(interval, elapsing.Token);

        if (ReferenceEquals(await Task.WhenAny(raised, elapsed).ConfigureAwait(false), raised))
        {
            gate.Consume(raised);

            // The interval this pass did not need. Cancelled rather than left to fire, or a node
            // woken a hundred times a second would hold a hundred pending timers per second.
            await elapsing.CancelAsync().ConfigureAwait(false);
        }

        // The wait ends the way Task.Delay ends it, because this call stands where that one did
        // and the services' shutdown path is written against that shape.
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Stops listening and closes the session.</summary>
    /// <returns>A task that completes when the listening loop has stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);

        if (_listening is { } listening)
        {
            // Its own cancellation, and nothing else can come out of it: the loop swallows every
            // store failure by design, so awaiting it here cannot surface one.
            await listening.ConfigureAwait(false);
        }

        Timer? due;
        Timer? lapse;

        lock (_sync)
        {
            due = _due;
            lapse = _lapse;
            _due = null;
            _lapse = null;
        }

        if (due is not null)
        {
            await due.DisposeAsync().ConfigureAwait(false);
        }

        if (lapse is not null)
        {
            await lapse.DisposeAsync().ConfigureAwait(false);
        }

        await _dataSource.DisposeAsync().ConfigureAwait(false);

        _stopping.Dispose();
    }

    /// <summary>
    /// A data source for one connection that is never handed back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Pooling off, because a pooled connection is returned.</strong> A subscription lives
    /// on the session that made it, so a connection that goes back to a pool between waits is a
    /// subscription that has silently stopped existing while the object holding it still looks
    /// healthy.
    /// </para>
    /// <para>
    /// <strong>A keepalive, because the failure this has to survive is silent.</strong> A
    /// listening session sends nothing and receives nothing for hours at a time, so a connection
    /// dropped by a firewall, a load balancer or a server that went away reads exactly like a
    /// quiet one — the read never returns and nothing reconnects. The keepalive query is what
    /// turns that into an exception the loop below can act on.
    /// </para>
    /// <para>
    /// <strong>Named after what it does</strong>, so the one idle connection this adds to a
    /// deployment's budget answers for itself in <c>pg_stat_activity</c> rather than looking like
    /// a leak.
    /// </para>
    /// </remarks>
    private static NpgsqlDataSource BuildListener(string connectionString, string schema)
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            KeepAlive = 30,
            ApplicationName = $"flowx-listen-{schema}",
        };

        return new NpgsqlDataSourceBuilder(settings.ConnectionString).Build();
    }

    private void EnsureListening()
    {
        if (_listening is not null)
        {
            return;
        }

        lock (_sync)
        {
            _listening ??= Task.Run(
                () => ListenAsync(_stopping.Token), CancellationToken.None);
        }
    }

    /// <summary>Holds a subscribed session, reopening it for as long as the host is running.</summary>
    private async Task ListenAsync(CancellationToken ct)
    {
        var backoff = FirstBackoff;

        while (!ct.IsCancellationRequested)
        {
            var before = Interlocked.Read(ref _connections);

            try
            {
                await ListenOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A session that ended is the ordinary case here, and every way
            catch (Exception)  //   it can end -- a server bounce, a firewall, an administrator
            {                  //   with pg_terminate_backend -- arrives as some driver's
                // Reconnect.  //   exception. Not reconnecting would leave a host on its poll
            }                  //   interval for ever, silently, which is the one outcome worse
#pragma warning restore CA1031 //   than the disconnection itself.

            Disconnected();

            backoff = Interlocked.Read(ref _connections) > before ? FirstBackoff : Next(backoff);

            try
            {
                await Task.Delay(backoff, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Opens one session, subscribes, and stays on it until it ends.</summary>
    private async Task ListenOnceAsync(CancellationToken ct)
    {
        var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

        await using var closing = connection.ConfigureAwait(false);

        connection.Notification += OnNotification;

        using (var listen = connection.CreateCommand())
        {
            listen.CommandText = ListenToAll;

            await listen.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (Interlocked.Increment(ref _connections) > 1)
        {
            // A reconnection, so this node was not listening for some window and cannot know what
            // was announced in it. The only safe reading of "cannot know" is that there is work:
            // one pass of each sweep, which finds whatever is there and finds nothing when there
            // is nothing. The first connection deliberately does not do this -- the services
            // sleep before their first pass on purpose, and a node that swept the instant it
            // started would be sweeping while its own subscriptions are still registering.
            _change.Raise();
            _timer.Raise();
            _recovery.Raise();
        }

        Connected();

        while (!ct.IsCancellationRequested)
        {
            // Returns when an announcement arrives -- the event above is what carries it -- or
            // throws when the session is gone, which is the loop above's cue.
            await connection.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private void OnNotification(object? sender, NpgsqlNotificationEventArgs args)
    {
        if (!IsOurs(args.Payload))
        {
            return;
        }

        if (string.Equals(args.Channel, TimerChannel, StringComparison.Ordinal))
        {
            ArmDue(args.Payload);

            return;
        }

        if (string.Equals(args.Channel, RecoveryChannel, StringComparison.Ordinal))
        {
            ArmLapse(args.Payload);

            return;
        }

        _change.Raise();
    }

    /// <summary>Whether an announcement came from a schema this node's sweeps read.</summary>
    /// <remarks>
    /// A channel name is database-wide, so a second deployment sharing the database announces on
    /// the same three channels. Ignoring the ones that are not ours costs nothing and saves a pass
    /// that could only ever find nothing; being wrong about it in either direction costs one pass
    /// or one interval, which is why the payload is allowed to decide this and nothing else.
    /// </remarks>
    private bool IsOurs(string payload)
    {
        var schema = SchemaOf(payload);

        return schema.Equals(_schema, StringComparison.Ordinal) ||
            (_tenantPrefix is not null && schema.StartsWith(_tenantPrefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// Wakes the timer sweep when the announced wait ends, rather than when it was written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A wake is written before it is due, so the write is not the event.</strong> Waking
    /// the sweep at write time would sweep, find nothing due, and sleep a full interval — the poll
    /// it started with, plus a query. What the sweep is waiting for is the instant, so that is
    /// what is waited on.
    /// </para>
    /// <para>
    /// <strong>One armed instant for the node, not one per row.</strong> The earliest wins and
    /// later ones are dropped: everything behind an overdue instance is overdue too, so the pass
    /// the earliest instant causes is the pass that finds the rest, and the interval is what
    /// catches anything this dropped. That is the whole difference between this and the
    /// per-instance timer <c>FlowTimerService</c> exists to avoid — this object is lost on a
    /// restart and costs nothing when it is, because the row is still the truth.
    /// </para>
    /// <para>
    /// <strong>Beyond the sweep's own interval, nothing is armed.</strong> A week-long wait is
    /// reached by the sweep long before it is due, and holding a timer for it would be a process
    /// resident object per parked instance — which is the design this one is not.
    /// </para>
    /// </remarks>
    private void ArmDue(string payload)
    {
        var horizon = Volatile.Read(ref _horizonTicks);

        if (horizon == 0)
        {
            // Nothing in this process sweeps timers, so there is nobody to wake.
            return;
        }

        if (!TryReadDue(payload, out var at))
        {
            // An announcement whose instant cannot be read still says a row was parked. Waking now
            // costs one pass that may find nothing; ignoring it would cost the latency this
            // migration exists to remove, on the release where the payload's shape moves.
            _timer.Raise();

            return;
        }

        var delay = at - DateTimeOffset.UtcNow;

        if (delay <= TimeSpan.Zero)
        {
            _timer.Raise();

            return;
        }

        if (delay.Ticks > horizon)
        {
            return;
        }

        lock (_sync)
        {
            if (_due is not null && _dueAt <= at)
            {
                return;
            }

            _due?.Dispose();
            _dueAt = at;
            _due = new Timer(
                static state => ((PostgresSweepSignal)state!).Due(),
                this,
                delay,
                Timeout.InfiniteTimeSpan);
        }
    }

    private void Due()
    {
        lock (_sync)
        {
            _due?.Dispose();
            _due = null;
        }

        _timer.Raise();
    }

    /// <summary>
    /// Wakes the recovery sweep when the next lease lapses, rather than on the interval after it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The announcement is already the earliest live expiry, so this arms what it was
    /// told.</strong> <c>0014</c> computes the minimum over the schema rather than reporting the
    /// row it fired on, which is what makes an armed instant replaceable in both directions: a
    /// renewal extends an expiry and the announcement that carries it moves the armed instant
    /// <em>later</em>, so the superseded instant does not fire a pass that could only find a
    /// healthy lease. A listener that kept the earliest instant it had heard could not do that,
    /// and one that simply replaced on every announcement would follow whichever lease was
    /// written last — never the dead node's, which is the only one that is not being written.
    /// The migration's own remarks are the long form of this paragraph.
    /// </para>
    /// <para>
    /// <strong>Which leaves this with one armed instant for the node and no per-lease
    /// state.</strong> The same object <see cref="ArmDue"/> holds, for the same reasons: it is
    /// lost on a restart and costs nothing when it is, because the row is still the truth and the
    /// interval still fires.
    /// </para>
    /// <para>
    /// <strong>Across tenant schemas the earliest wins and the schema that armed it can move
    /// it.</strong> Each tenant schema announces its own minimum, and they are separate facts —
    /// replacing on an announcement from another schema would let a quiet tenant's distant expiry
    /// bury an imminent one. Keeping the earliest of them is the same trade the rest of this
    /// class makes: what is dropped costs an interval and never an instance.
    /// </para>
    /// </remarks>
    private void ArmLapse(string payload)
    {
        if (Volatile.Read(ref _sweepsRecovery) == 0)
        {
            // Nothing in this process sweeps for abandoned instances, so there is nobody to wake.
            return;
        }

        if (!TryReadDue(payload, out var at))
        {
            // An announcement whose instant cannot be read still says a lease was written. Waking
            // now costs one pass that may find nothing; ignoring it would cost the latency this
            // migration exists to remove, on the release where the payload's shape moves.
            _recovery.Raise();

            return;
        }

        var delay = at - DateTimeOffset.UtcNow + Settle;

        if (delay <= TimeSpan.Zero)
        {
            _recovery.Raise();

            return;
        }

        var schema = SchemaOf(payload);

        lock (_sync)
        {
            // The common announcement under load is a renewal that leaves the earliest expiry
            // exactly where it was — every lease but one reports its neighbour's instant — so the
            // first thing this does is notice that there is nothing to change. A node renewing a
            // thousand leases a second allocates nothing here.
            var armed = _lapse is not null && _lapseSchema is not null;
            var ours = armed && schema.Equals(_lapseSchema, StringComparison.Ordinal);

            if (armed && (ours ? _lapseAt == at : _lapseAt <= at))
            {
                return;
            }

            _lapse?.Dispose();
            _lapseAt = at;
            _lapseSchema = ours ? _lapseSchema : new string(schema);
            _lapse = new Timer(
                static state => ((PostgresSweepSignal)state!).Lapsed(),
                this,
                delay,
                Timeout.InfiniteTimeSpan);
        }
    }

    private void Lapsed()
    {
        lock (_sync)
        {
            _lapse?.Dispose();
            _lapse = null;
            _lapseSchema = null;
        }

        _recovery.Raise();
    }

    private void Connected()
    {
        lock (_sync)
        {
            _connected.TrySetResult();
        }
    }

    private void Disconnected()
    {
        lock (_sync)
        {
            if (_connected.Task.IsCompleted)
            {
                _connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    /// <summary>The schema an announcement came from, which is everything up to the separator.</summary>
    private static ReadOnlySpan<char> SchemaOf(string payload)
    {
        var separator = payload.IndexOf('|', StringComparison.Ordinal);

        return separator < 0 ? payload : payload.AsSpan(0, separator);
    }

    /// <summary>The wake instant a timer announcement carries, in epoch milliseconds.</summary>
    private static bool TryReadDue(string payload, out DateTimeOffset at)
    {
        at = default;

        var separator = payload.IndexOf('|', StringComparison.Ordinal);

        if (separator < 0 ||
            !long.TryParse(
                payload.AsSpan(separator + 1),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var milliseconds))
        {
            return false;
        }

        at = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);

        return true;
    }

    private static TimeSpan Next(TimeSpan backoff) =>
        backoff >= LongestBackoff ? LongestBackoff : backoff * 2;

    /// <summary>
    /// One sweep's wake: a task that is already complete when a wake arrived while nobody was
    /// waiting, and a fresh one once the waiter has taken it.
    /// </summary>
    /// <remarks>
    /// <strong>A latch and not a count</strong>, for the reason <see cref="ISweepSignal"/> gives:
    /// two announcements and one announcement both mean "there is work", and the pass that answers
    /// either reads everything. <see cref="Consume"/> re-arms only the task the waiter actually
    /// observed, so an announcement that lands in the moment between the interval elapsing and the
    /// waiter looking is kept rather than discarded — it costs one extra pass and never a missed
    /// one.
    /// </remarks>
    private sealed class Gate
    {
        private readonly Lock _sync = new();

        private TaskCompletionSource _raised = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Pending
        {
            get
            {
                lock (_sync)
                {
                    return _raised.Task;
                }
            }
        }

        public void Raise()
        {
            lock (_sync)
            {
                _raised.TrySetResult();
            }
        }

        public void Consume(Task observed)
        {
            lock (_sync)
            {
                if (ReferenceEquals(_raised.Task, observed))
                {
                    _raised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
        }
    }
}
