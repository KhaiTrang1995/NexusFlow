using System.Globalization;
using System.Text;

namespace FlowX.Testing;

/// <summary>What a <see cref="FlowTestEntry"/> records.</summary>
public enum FlowTestEntryKind
{
    /// <summary>A step the engine dispatched: a capability, an emit, a signal or a <c>Fail</c>.</summary>
    Step = 0,

    /// <summary>A compensation the engine ran while unwinding.</summary>
    Compensation = 1,

    /// <summary>A <c>When</c>/<c>Otherwise</c> predicate and the answer it gave.</summary>
    Branch = 2,

    /// <summary>A <c>Switch</c> selector and the arm it chose.</summary>
    Switch = 3,

    /// <summary>A <c>ForEach</c> collection and how many elements it produced.</summary>
    Iteration = 4,

    /// <summary>A <c>SubFlow</c> composition, naming the child and its mode.</summary>
    SubFlow = 5,

    /// <summary>A capability fallback the engine asked after a step had finished failing.</summary>
    /// <remarks>
    /// Its own kind rather than a second <see cref="Step"/>, and for the reason the trace
    /// exists: a test asserting control flow has to be able to tell "the step answered" from
    /// "something else answered for it". Recorded under the *fallback's* capability id, so the
    /// trace names what actually ran — the same rule <c>docs/06 §7</c> rule 6 sets for a
    /// compensation.
    /// </remarks>
    Fallback = 6,
}

/// <summary>One thing the engine asked the flow under test to do.</summary>
/// <param name="Kind">Which question the engine asked.</param>
/// <param name="FlowId">
/// The flow the step belongs to. Not always the flow under test: a composed child's
/// steps are recorded under the child's id, which is what lets a test tell a parent's
/// undo from a child's when both use the same capability.
/// </param>
/// <param name="StepIndex">Position in that flow's compiled graph.</param>
/// <param name="Name">
/// A stable, greppable name. A capability step is the capability id verbatim
/// (<c>inventory.reserve</c>); everything else carries a prefix — <c>emit:</c>,
/// <c>signal:</c>, <c>fail</c>, <c>branch:</c>, <c>switch:</c>, <c>foreach:</c>,
/// <c>subflow:</c> — so no prefixed name can collide with a capability id, which the
/// <c>&lt;domain&gt;.&lt;verb&gt;</c> form forbids from containing a colon.
/// </param>
public sealed record FlowTestEntry(FlowTestEntryKind Kind, string FlowId, int StepIndex, string Name)
{
    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{FlowId}[{StepIndex}] {Kind}: {Name}");
}

/// <summary>
/// What one run of a <see cref="FlowTestHost"/> did, in the order it did it.
/// </summary>
/// <remarks>
/// <para>
/// The trace is what makes a flow test assert on <em>control flow</em> rather than only
/// on the answer. A flow expresses order, condition and recovery; a test that checks
/// only the returned error cannot tell a saga that compensated correctly from one that
/// leaked a reservation, and cannot tell a branch that was taken from a branch that was
/// skipped because the step before it silently did nothing.
/// </para>
/// <para>
/// <strong>Entries hold strings and integers only.</strong> Nothing here references a
/// <see cref="FlowContext"/>. The engine's contexts are pooled and reset the instant a
/// flow returns, so a trace that captured one would read as empty at best and as the
/// next test's data at worst — which is the contamination this host exists to prevent.
/// </para>
/// <para>
/// <strong>Order is exact for sequential control flow and arbitrary inside a fork.</strong>
/// A <c>Parallel</c>'s branches and a <c>ForEach</c> with a concurrency bound above one
/// genuinely interleave, so the relative order of their entries is whatever the thread
/// pool chose. Assert on <see cref="TimesExecuted"/> or <see cref="DidExecute"/> there,
/// and on <see cref="Executed"/> or <see cref="Compensated"/> where the flow is
/// sequential. Compensation is always sequential and strictly reverse, so
/// <see cref="Compensated"/> is exact even for a flow that forked.
/// </para>
/// </remarks>
public sealed class FlowTestTrace
{
    private readonly List<FlowTestEntry> _entries = [];
    private readonly Lock _sync = new();

    private string[]? _executed;
    private string[]? _compensated;
    private bool _sealed;

    /// <summary>Everything the engine asked, in the order it asked.</summary>
    public IReadOnlyList<FlowTestEntry> Entries
    {
        get
        {
            RequireSealed();
            return _entries;
        }
    }

    /// <summary>The names of the steps that ran, in order.</summary>
    /// <remarks>
    /// Includes the step that failed: it was dispatched, and a compensation ordering is
    /// only readable next to the step that triggered it. Excludes compensations, which
    /// are <see cref="Compensated"/>.
    /// </remarks>
    public IReadOnlyList<string> Executed
    {
        get
        {
            RequireSealed();
            return _executed ??= Names(FlowTestEntryKind.Step);
        }
    }

    /// <summary>The names of the compensations that ran, in unwind order.</summary>
    /// <remarks>
    /// Reverse order of completion, across sub-flow boundaries. A child that completed
    /// steps and returned successfully still appears here when the <em>parent</em> later
    /// failed — the child's undo runs at the point the child's work sits in the parent's
    /// unwind, not at the end.
    /// </remarks>
    public IReadOnlyList<string> Compensated
    {
        get
        {
            RequireSealed();
            return _compensated ??= Names(FlowTestEntryKind.Compensation);
        }
    }

    /// <summary>Whether a capability ran at least once.</summary>
    /// <param name="capabilityId">The capability id, exactly as the plan carries it.</param>
    public bool DidExecute(string capabilityId) => TimesExecuted(capabilityId) > 0;

    /// <summary>Whether a compensation ran at least once.</summary>
    /// <param name="capabilityId">The compensating capability's id.</param>
    public bool DidCompensate(string capabilityId) => TimesCompensated(capabilityId) > 0;

    /// <summary>How many times a capability ran.</summary>
    /// <param name="capabilityId">The capability id, exactly as the plan carries it.</param>
    /// <remarks>
    /// The question a <c>ForEach</c> or a <c>Parallel</c> test actually has, since the
    /// order of those entries is not deterministic but the count is.
    /// </remarks>
    public int TimesExecuted(string capabilityId) => Count(FlowTestEntryKind.Step, capabilityId);

    /// <summary>How many times a compensation ran.</summary>
    /// <param name="capabilityId">The compensating capability's id.</param>
    public int TimesCompensated(string capabilityId) => Count(FlowTestEntryKind.Compensation, capabilityId);

    /// <summary>The whole trace, one entry per line.</summary>
    /// <remarks>
    /// Written for an assertion message. A failed ordering assertion whose message is
    /// two collections is a failure the reader has to reconstruct; one that prints the
    /// run is a failure they can read.
    /// </remarks>
    public override string ToString()
    {
        RequireSealed();

        if (_entries.Count == 0)
        {
            return "(nothing ran)";
        }

        var text = new StringBuilder();

        foreach (var entry in _entries)
        {
            text.Append(entry).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>Appends an entry. Called from several threads when a flow forks.</summary>
    internal void Record(FlowTestEntryKind kind, string flowId, int stepIndex, string name)
    {
        lock (_sync)
        {
            if (_sealed)
            {
                // A step still writing after the run returned is the same defect as a
                // branch still writing to a pooled context: whatever it reports belongs
                // to no run the caller can see. Loud beats silent.
                throw new InvalidOperationException(
                    $"Flow '{flowId}' recorded {kind} '{name}' after its run had finished. " +
                    "Something the engine started is still running — a detached sub-flow " +
                    "that outlived the drain, or a step that ignored cancellation.");
            }

            _entries.Add(new FlowTestEntry(kind, flowId, stepIndex, name));
        }
    }

    /// <summary>Closes the trace. Nothing may be recorded afterwards.</summary>
    internal void Seal()
    {
        lock (_sync)
        {
            _sealed = true;
        }
    }

    private void RequireSealed()
    {
        lock (_sync)
        {
            if (!_sealed)
            {
                throw new InvalidOperationException(
                    "This trace belongs to a run that has not finished. Await the run " +
                    "before reading its trace.");
            }
        }
    }

    private string[] Names(FlowTestEntryKind kind)
    {
        var names = new List<string>(_entries.Count);

        foreach (var entry in _entries)
        {
            if (entry.Kind == kind)
            {
                names.Add(entry.Name);
            }
        }

        return [.. names];
    }

    private int Count(FlowTestEntryKind kind, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        RequireSealed();

        var count = 0;

        foreach (var entry in _entries)
        {
            if (entry.Kind == kind && string.Equals(entry.Name, name, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }
}
