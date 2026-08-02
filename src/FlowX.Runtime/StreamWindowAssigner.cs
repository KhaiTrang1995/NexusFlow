namespace FlowX.Runtime;

/// <summary>What admitting one record did.</summary>
public enum StreamAdmission
{
    /// <summary>The record joined an open window.</summary>
    Windowed = 0,

    /// <summary>Its window had already closed. It belongs to the side output.</summary>
    Late = 1,

    /// <summary>The engine is already holding as many records as it is allowed to.</summary>
    Overflowed = 2,
}

/// <summary>One window the watermark has closed, and everything that was assigned to it.</summary>
/// <param name="Start">Inclusive lower bound, in event time.</param>
/// <param name="End">Exclusive upper bound, in event time.</param>
/// <param name="Records">The records, in arrival order.</param>
public sealed record ClosedWindow(
    DateTimeOffset Start, DateTimeOffset End, IReadOnlyList<StreamRecord> Records);

/// <summary>
/// The whole of this engine's stream state: which windows are open, where the watermark is, and
/// which position is safe to checkpoint.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Deliberately a plain object with no I/O.</strong> Everything decided here is decided
/// from records and their event times, so it is a pure state machine that a test can drive one
/// record at a time — and, more importantly, one that a restart reconstructs exactly by replaying
/// the same records from the checkpoint
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0055-a-window-names-the-instance-it-starts.md">ADR-0055</a>).
/// If any decision here depended on wall-clock time, arrival order across passes, or a value read
/// from a store, that reconstruction would not hold and window state would have to be journaled.
/// </para>
/// <para>
/// <strong>The safe checkpoint is a prefix, computed in arrival order.</strong> Positions are
/// opaque (<see cref="StreamPosition"/>), so the engine never takes a minimum over open windows.
/// It keeps every admitted record's position in the order the source served them, marks each
/// settled when the window it went to has run or the side output has taken it, and checkpoints
/// the last position of the longest settled prefix. That is <c>FlowChangeScan</c>'s progress rule
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0048-a-change-feed-advances-a-cursor.md">ADR-0048</a>)
/// applied to a structure that reorders: a window that is still open holds the prefix exactly
/// where its oldest record is, which is precisely the position a restart must resume from to
/// rebuild it.
/// </para>
/// <para>
/// <strong>Memory is bounded by construction, and the bound refuses rather than grows.</strong>
/// A window is held until the watermark closes it, so the resident record count is the one thing
/// here that a hostile stream could make unbounded — a wide window and a fast producer. It is
/// capped, and the cap is a refusal (<see cref="StreamErrors.WindowOverflow"/>) rather than an
/// eviction: evicting records would emit a window computed from part of its input, which is the
/// silent wrongness this engine exists to avoid.
/// </para>
/// </remarks>
public sealed class StreamWindowAssigner
{
    private readonly StreamWindowSpec _spec;
    private readonly int _maxResident;
    private readonly SortedDictionary<DateTimeOffset, List<StreamRecord>> _open = [];
    private readonly Queue<Admitted> _ledger = new();

    private DateTimeOffset _watermark = DateTimeOffset.MinValue;
    private int _resident;

    /// <summary>Builds the state machine for one subscription.</summary>
    /// <param name="spec">The declared window shape.</param>
    /// <param name="maxResident">How many records may sit in open windows at once.</param>
    /// <exception cref="ArgumentNullException"><paramref name="spec"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxResident"/> is not positive.</exception>
    public StreamWindowAssigner(StreamWindowSpec spec, int maxResident)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxResident, 1);

        _spec = spec;
        _maxResident = maxResident;
    }

    /// <summary>The highest event time observed, less the declared lateness. Monotonic.</summary>
    public DateTimeOffset Watermark => _watermark;

    /// <summary>How many records are sitting in open windows.</summary>
    public int Resident => _resident;

    /// <summary>How many admitted records have not yet been dealt with.</summary>
    /// <remarks>
    /// The ledger's depth, which is what the checkpoint prefix is computed over. Larger than
    /// <see cref="Resident"/> when a settled record is still behind an unsettled one.
    /// </remarks>
    public int Pending => _ledger.Count;

    /// <summary>How many windows are open.</summary>
    public int OpenWindows => _open.Count;

    /// <summary>Offers one record, in the order the source served it.</summary>
    /// <param name="record">The record.</param>
    /// <returns>What happened to it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="record"/> is null.</exception>
    /// <remarks>
    /// <strong>The watermark moves before the record is placed, not after.</strong> A record whose
    /// event time is the highest yet advances the watermark past every window below it, and then
    /// asks whether its own window is one of them — which it is not, because a window's upper
    /// bound is exclusive and the watermark trails by the declared lateness. Placing first and
    /// advancing after would let a record land in a window that the same record then closes,
    /// making the window's contents depend on how the source happened to batch it.
    /// </remarks>
    public StreamAdmission Admit(StreamRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var candidate = record.EventTime.ToUniversalTime() - _spec.Lateness;

        if (candidate > _watermark)
        {
            _watermark = candidate;
        }

        var start = _spec.WindowStartFor(record.EventTime);

        if (_spec.WindowEndFor(start) <= _watermark)
        {
            _ledger.Enqueue(new Admitted(record.Position, WindowStart: null));

            return StreamAdmission.Late;
        }

        if (_resident >= _maxResident)
        {
            return StreamAdmission.Overflowed;
        }

        if (!_open.TryGetValue(start, out var window))
        {
            window = [];
            _open[start] = window;
        }

        window.Add(record);
        _resident++;
        _ledger.Enqueue(new Admitted(record.Position, start));

        return StreamAdmission.Windowed;
    }

    /// <summary>Takes every window the watermark has closed, oldest first.</summary>
    /// <returns>
    /// The closed windows. They leave this object: their records stop being resident, and the
    /// ledger entries pointing at them stay unsettled until <see cref="Settle(DateTimeOffset)"/> is called.
    /// </returns>
    /// <remarks>
    /// Removing the records here and settling separately (<c>Settle</c>) is what makes a failed
    /// window flow hold the checkpoint without also holding the memory: the records are the
    /// caller's now, and the caller drops them, but the position they came from is still in the
    /// ledger and still unsettled, so a restart re-reads them.
    /// </remarks>
    public IReadOnlyList<ClosedWindow> TakeClosed()
    {
        List<ClosedWindow>? closed = null;

        foreach (var start in _open.Keys.ToArray())
        {
            var end = _spec.WindowEndFor(start);

            if (end > _watermark)
            {
                // SortedDictionary is in ascending key order, so nothing after this one can be
                // closed either.
                break;
            }

            var records = _open[start];

            _open.Remove(start);
            _resident -= records.Count;

            (closed ??= []).Add(new ClosedWindow(start, end, records));
        }

        return closed ?? (IReadOnlyList<ClosedWindow>)[];
    }

    /// <summary>Records that everything a window held has been dealt with.</summary>
    /// <param name="windowStart">The window's inclusive lower bound.</param>
    public void Settle(DateTimeOffset windowStart) => Settle(entry => entry.WindowStart == windowStart);

    /// <summary>Records that one late record has been taken by the side output.</summary>
    /// <param name="position">The record's position.</param>
    public void SettleLate(StreamPosition position) =>
        Settle(entry => entry.WindowStart is null && entry.Position == position);

    /// <summary>
    /// The last position of the longest settled prefix, or null when the oldest admitted record
    /// has not been dealt with.
    /// </summary>
    /// <returns>The position to checkpoint, and the ledger is consumed up to it.</returns>
    public StreamPosition? TakeCheckpoint()
    {
        StreamPosition? reached = null;

        while (_ledger.Count > 0 && _ledger.Peek().Settled)
        {
            reached = _ledger.Dequeue().Position;
        }

        return reached;
    }

    private void Settle(Func<Admitted, bool> match)
    {
        // The ledger is a queue because the prefix is read from its head; settling is the one
        // operation that touches the middle, and it is O(pending) rather than O(1). Pending is
        // bounded by the resident cap plus one pass's reads, and a dictionary from window to
        // ledger entries would be a second structure to keep in step for a constant this engine
        // never notices.
        for (var index = 0; index < _ledger.Count; index++)
        {
            var entry = _ledger.Dequeue();

            _ledger.Enqueue(match(entry) ? entry with { Settled = true } : entry);
        }
    }

    /// <summary>One admitted record's place in arrival order, and whether it is dealt with.</summary>
    /// <param name="Position">Where the source served it from.</param>
    /// <param name="WindowStart">The window it joined, or null when it was late.</param>
    /// <param name="Settled">Whether the checkpoint may move past it.</param>
    private sealed record Admitted(
        StreamPosition Position, DateTimeOffset? WindowStart, bool Settled = false);
}
