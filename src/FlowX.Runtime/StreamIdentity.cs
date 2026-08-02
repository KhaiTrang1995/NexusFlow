using System.Globalization;

namespace FlowX.Runtime;

/// <summary>
/// The derivation that turns one closed window into the id of the instance it starts — and the
/// one that makes a subscription readable by one node at a time.
/// </summary>
/// <remarks>
/// <para>
/// <strong><see cref="ChangeIdentity"/>'s derivation with a window in place of a change</strong>
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0055-a-window-names-the-instance-it-starts.md">ADR-0055</a>).
/// A stream engine is at-least-once for a reason of its own — the checkpoint is committed after
/// the windows' flows have run, so a crash in between re-reads every record since it — and the
/// answer is the one ADR-0031, ADR-0035 and ADR-0049 already reached: derive the id, and let
/// <c>ILeaseStore.AcquireAsync</c> refuse the second while the first is running and
/// <c>IFlowJournal.StartAsync</c> refuse it for ever afterwards.
/// </para>
/// <para>
/// <strong>This is what makes window state not worth journaling.</strong> A window is a pure
/// function of the records the source retains and their event times, so a node that dies mid
/// window rebuilds an identical one by re-reading from the checkpoint — and the window that
/// <em>had</em> already run derives the same id and is refused. Journaling the accumulating state
/// would buy nothing the source does not already hold, and would owe ADR-0015 a schema for a
/// value that changes on every record.
/// </para>
/// </remarks>
public static class StreamIdentity
{
    /// <summary>The id the instance for one closed window is started under.</summary>
    /// <param name="flowId">The windowing flow's business identity.</param>
    /// <param name="flowVersion">The exact version this node would run it at.</param>
    /// <param name="source">The stream, verbatim as declared and published.</param>
    /// <param name="group">The reader group, verbatim as declared and published.</param>
    /// <param name="windowStart">The window's inclusive lower bound, in event time.</param>
    /// <param name="windowEnd">The window's exclusive upper bound, in event time.</param>
    /// <returns>The instance id every node derives for this window.</returns>
    /// <exception cref="ArgumentNullException">A term is null.</exception>
    /// <remarks>
    /// <strong>The bounds are formatted with an explicit round-trip pattern</strong>, for
    /// <see cref="ScheduleOccurrence"/>'s reason: a node in another culture must derive the same
    /// id, and a default <c>ToString</c> does not promise that. They are normalised to UTC first,
    /// so two nodes configured with different offsets agree about which window this is.
    /// </remarks>
    public static Guid InstanceIdFor(
        string flowId,
        string flowVersion,
        string source,
        string group,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd)
    {
        ArgumentNullException.ThrowIfNull(flowId);
        ArgumentNullException.ThrowIfNull(flowVersion);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(group);

        return DerivedIdentity.From(
            WindowScope, flowId, flowVersion, source, group, Instant(windowStart), Instant(windowEnd));
    }

    /// <summary>
    /// The lease one node takes to be the only node reading one subscription's stream.
    /// </summary>
    /// <param name="flowId">The windowing flow's business identity.</param>
    /// <param name="flowVersion">The exact version this node would run it at.</param>
    /// <param name="source">The stream, verbatim as declared and published.</param>
    /// <param name="group">The reader group, verbatim as declared and published.</param>
    /// <returns>The lease id.</returns>
    /// <exception cref="ArgumentNullException">A term is null.</exception>
    /// <remarks>
    /// <strong>One subscription, one reader</strong>, for
    /// <see cref="ChangeIdentity.SubscriptionLeaseIdFor"/>'s reason and one that is stronger
    /// here: a window's records are accumulated in the reader's memory, so two readers would
    /// each hold half of every window and both would emit a partial aggregate under the same
    /// derived id — of which the journal would keep whichever committed first. A deployment
    /// scales by adding subscriptions, or by partitioning the stream and declaring one
    /// subscription per partition; it does not scale by adding nodes.
    /// </remarks>
    public static Guid SubscriptionLeaseIdFor(
        string flowId, string flowVersion, string source, string group)
    {
        ArgumentNullException.ThrowIfNull(flowId);
        ArgumentNullException.ThrowIfNull(flowVersion);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(group);

        return DerivedIdentity.From(SubscriptionLeaseScope, flowId, flowVersion, source, group);
    }

    private static string Instant(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>The term that keeps a window's instance id out of every other id space.</summary>
    private const string WindowScope = "flowx\0stream\0window";

    /// <summary>The term that keeps a subscription lease out of the instance id space.</summary>
    private const string SubscriptionLeaseScope = "flowx\0stream\0subscription";
}
