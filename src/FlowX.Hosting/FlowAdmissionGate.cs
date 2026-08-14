namespace FlowX.Hosting;

/// <summary>
/// The ceiling on admitted-and-not-yet-finished work, consulted by every transport's admission
/// seam so that one bound covers all of them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nothing shed before this existed.</strong> A burst was admitted at whatever rate the
/// sweeps and the push hosts could read it, so the journal saturated and latency collapsed
/// rather than degrading. A ceiling turns that into a decision: work over the bound is refused
/// at the seam, in the shape the transport that offered it can act on — the broker keeps its
/// backlog, a window keeps its checkpoint, a caller gets a <c>429</c> — and the platform stays
/// responsive for what it did admit.
/// </para>
/// <para>
/// <strong>Unlimited by default, and that is the whole of the compatibility argument.</strong> A
/// hosting option must never change a running deployment's behaviour by being added — the rule
/// <see cref="HostSweeps.All"/> is the default for — so an unset
/// <see cref="FlowXOptions.MaxInFlightAdmissions"/> leaves <see cref="TryAcquire"/> returning an
/// admitted slot over no state at all. Budget <strong>B6</strong> reaches this far: the unlimited
/// path reads one <see langword="int"/> field, allocates nothing, and takes neither a lock nor an
/// interlocked operation, so a deployment that does not want a ceiling does not pay for one.
/// </para>
/// <para>
/// <strong><see cref="Interlocked"/> rather than a <see cref="SemaphoreSlim"/>, because this
/// never waits.</strong> A saturated node sheds — <c>docs/16 §4</c>'s "shed early, do not queue",
/// and <c>TenantErrors.Saturated</c>'s argument one scope up: a caller parked on a slot is a
/// thread and a socket held open for work the platform has already decided not to do. A
/// semaphore's whole apparatus is the waiting this gate refuses to do, and
/// <c>SemaphoreSlim.Wait(0)</c> would still take the semaphore's internal lock on the path that
/// admits. A compare-exchange loop over one counter is lock-free, allocation-free, and races
/// correctly: two threads at the ceiling both re-read and both refuse.
/// </para>
/// <para>
/// <strong>This is not <c>FlowHost.InFlight</c>, and the difference is deliberate.</strong> That
/// counter exists to know when a drain has finished, so it counts every execution including a
/// recovery resume and a delivered signal — neither of which is an <em>admission</em>: the first
/// is finishing work this node already accepted, and refusing the second would strand an
/// instance that is already journalled and waiting. Bounding those would shed work that has
/// nowhere else to go.
/// </para>
/// </remarks>
public sealed class FlowAdmissionGate
{
    /// <summary>The status a shed HTTP request is answered with.</summary>
    /// <remarks>
    /// Named here rather than derived from <see cref="ErrorCategory.Unavailable"/>, which
    /// <c>ToHttpStatusCode</c> maps to <c>503</c>. Both are true of a shed — the service is
    /// temporarily unavailable <em>to this caller</em> — and <c>429</c> is the more useful half,
    /// because it says the deployment is healthy and the request rate is what is over. Adding a
    /// category for it would re-map every existing <see cref="ErrorCategory.Unavailable"/>
    /// refusal in the repository, so the one place the distinction is wanted names it instead.
    /// </remarks>
    public const int TooManyRequests = 429;

    /// <summary>The code <see cref="Shed"/> raises.</summary>
    /// <remarks>
    /// <c>TenantErrors.SaturatedCode</c>'s noun one scope up: the same bulkhead decision, taken
    /// over the node rather than over one tenant's share of it. Spelled with the <c>host.</c>
    /// prefix that <c>host.draining</c> established, so an operator grepping a log sees which of
    /// the two refusals a node is issuing — a drain ends when the pod does, a shed ends when the
    /// burst does, and they are repaired differently.
    /// </remarks>
    public const string SaturatedCode = "host.saturated";

    /// <summary>How long a shed caller is told to wait.</summary>
    /// <remarks>
    /// A constant, because there is nothing honest to derive one from: a slot frees when any
    /// in-flight flow finishes, and the gate does not know what those flows are waiting on. One
    /// second is the smallest value <c>Retry-After</c> can carry that is not zero, and zero is a
    /// hot retry loop aimed at a node that has just said it is full.
    /// </remarks>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(1);

    private readonly int _ceiling;
    private int _inFlight;

    /// <summary>Builds the gate this host admits through.</summary>
    /// <param name="ceiling">
    /// How many items may be admitted and not yet finished at once, or null for no bound.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="ceiling"/> is present and not positive. A ceiling of zero admits nothing
    /// ever, which is a deployment that does no work rather than one that sheds; the startup
    /// validator refuses it there too, and this is the guard for a host built by hand.
    /// </exception>
    public FlowAdmissionGate(int? ceiling = null)
    {
        if (ceiling is { } bound)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bound);
        }

        _ceiling = ceiling ?? 0;
    }

    /// <summary>The ceiling, or null when this host admits without bound.</summary>
    public int? Ceiling => _ceiling == 0 ? null : _ceiling;

    /// <summary>Whether a ceiling is set at all.</summary>
    public bool IsBounded => _ceiling != 0;

    /// <summary>How many admitted items have not finished yet. Always zero when unbounded.</summary>
    /// <remarks>
    /// Zero rather than a count on an unbounded host because nothing is counted there — see the
    /// B6 paragraph on the type. A deployment that wants the number sets a ceiling.
    /// </remarks>
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>Takes a slot if there is one, and says whether it got it.</summary>
    /// <returns>
    /// A slot to hold for the duration of the run. <see cref="FlowAdmissionSlot.Admitted"/> is
    /// false when the ceiling is reached, and the caller must then shed rather than run.
    /// </returns>
    /// <remarks>
    /// The returned value is a <see langword="struct"/> and <c>using</c> binds
    /// <see cref="FlowAdmissionSlot.Dispose"/> without boxing it, so the bounded path allocates
    /// nothing either. Release is unconditional in that <c>Dispose</c>: a run that threw still
    /// finished, and a slot leaked once is a ceiling that is one lower for the life of the
    /// process.
    /// </remarks>
    public FlowAdmissionSlot TryAcquire()
    {
        if (_ceiling == 0)
        {
            return FlowAdmissionSlot.Unbounded;
        }

        var seen = Volatile.Read(ref _inFlight);

        while (seen < _ceiling)
        {
            var found = Interlocked.CompareExchange(ref _inFlight, seen + 1, seen);

            if (found == seen)
            {
                return new FlowAdmissionSlot(this);
            }

            // Somebody else moved it between the read and the exchange. Re-test against the
            // value they left rather than against the stale one, so a loser at the ceiling
            // sheds instead of retrying its way past it.
            seen = found;
        }

        return FlowAdmissionSlot.Shed;
    }

    /// <summary>The refusal a shed item carries.</summary>
    /// <param name="ceiling">The bound that was reached, so the message names the setting to raise.</param>
    /// <returns>The error.</returns>
    /// <remarks>
    /// <see cref="ErrorCategory.Unavailable"/>, for <c>FlowErrors.RateLimited</c>'s reason and
    /// <c>TenantErrors.Saturated</c>'s: the deployment is healthy and this caller is simply not
    /// getting in right now, so waiting and asking again is the correct response and the category
    /// is the half of the error that says so. <c>retryAfter</c> travels in the structured detail,
    /// which is what puts it in the problem document's extensions beside the header.
    /// </remarks>
    public static Error Shed(int ceiling) =>
        new Error(
            SaturatedCode,
            $"This node already has {ceiling.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            "admitted flow(s) that have not finished, which is its " +
            $"{nameof(FlowXOptions.MaxInFlightAdmissions)}. The work was shed rather than queued: " +
            "admitting past the ceiling is how a burst becomes a saturated journal and a latency " +
            $"collapse. Retry after {RetryAfter}.",
            ErrorCategory.Unavailable)
            .With("maxInFlightAdmissions", ceiling)
            .With("retryAfter", RetryAfter);

    /// <summary>Gives a slot back. Called only by <see cref="FlowAdmissionSlot.Dispose"/>.</summary>
    internal void Release() => Interlocked.Decrement(ref _inFlight);
}

/// <summary>
/// One admission's claim on the ceiling, held for as long as the run it admitted.
/// </summary>
/// <remarks>
/// A <see langword="struct"/> so that neither the unbounded path nor the bounded one allocates
/// (budget <strong>B6</strong>), and <see cref="IDisposable"/> so that <c>using</c> releases it
/// on every exit including a thrown one. A slot that was never taken — the unbounded case, and
/// the shed — holds no gate, so its <see cref="Dispose"/> is a null check.
/// </remarks>
public readonly struct FlowAdmissionSlot : IDisposable, IEquatable<FlowAdmissionSlot>
{
    private readonly FlowAdmissionGate? _gate;

    internal FlowAdmissionSlot(FlowAdmissionGate gate)
    {
        _gate = gate;
        Admitted = true;
    }

    private FlowAdmissionSlot(bool admitted)
    {
        _gate = null;
        Admitted = admitted;
    }

    /// <summary>The slot a host with no ceiling hands out: admitted, and counting nothing.</summary>
    internal static FlowAdmissionSlot Unbounded { get; } = new(admitted: true);

    /// <summary>The answer at the ceiling: not admitted, and holding nothing to release.</summary>
    internal static FlowAdmissionSlot Shed { get; } = new(admitted: false);

    /// <summary>Whether the item may run. False means the caller must shed it.</summary>
    public bool Admitted { get; }

    /// <summary>Returns the slot to the gate, if one was taken.</summary>
    public void Dispose() => _gate?.Release();

    /// <inheritdoc/>
    public bool Equals(FlowAdmissionSlot other) =>
        Admitted == other.Admitted && ReferenceEquals(_gate, other._gate);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is FlowAdmissionSlot other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Admitted, _gate);

    /// <summary>Whether two slots are the same claim.</summary>
    /// <param name="left">One slot.</param>
    /// <param name="right">The other.</param>
    /// <returns>True when they are.</returns>
    public static bool operator ==(FlowAdmissionSlot left, FlowAdmissionSlot right) => left.Equals(right);

    /// <summary>Whether two slots are different claims.</summary>
    /// <param name="left">One slot.</param>
    /// <param name="right">The other.</param>
    /// <returns>True when they are.</returns>
    public static bool operator !=(FlowAdmissionSlot left, FlowAdmissionSlot right) => !left.Equals(right);
}
