using FlowX.Runtime;

namespace FlowX.Hosting;

/// <summary>One stream subscription this node serves, and the flow a closed window starts.</summary>
/// <param name="Subscription">Which stream, under whose group.</param>
/// <param name="Window">The declared window shape, already read and validated.</param>
/// <param name="Flow">The compiled plan and its dispatcher.</param>
public sealed record StreamRegistration(
    StreamSubscription Subscription, StreamWindowSpec Window, FlowRegistration Flow)
{
    /// <summary>The id the instance for one closed window is started under.</summary>
    /// <param name="windowStart">The window's inclusive lower bound.</param>
    /// <param name="windowEnd">The window's exclusive upper bound.</param>
    public Guid InstanceIdFor(DateTimeOffset windowStart, DateTimeOffset windowEnd) =>
        StreamIdentity.InstanceIdFor(
            Subscription.FlowId,
            Subscription.FlowVersion,
            Subscription.Source,
            Subscription.Group,
            windowStart,
            windowEnd);

    /// <summary>The lease that makes this node the only one reading this stream.</summary>
    public Guid SubscriptionLease => StreamIdentity.SubscriptionLeaseIdFor(
        Subscription.FlowId, Subscription.FlowVersion, Subscription.Source, Subscription.Group);
}

/// <summary>
/// Which stream subscriptions this node serves, and the plans behind them.
/// </summary>
/// <remarks>
/// <see cref="FlowChangeCatalog"/>'s shape and its reasons, including registration rather than
/// discovery: reflecting over loaded assemblies to find <c>[StreamTrigger]</c> would be a
/// trim-time dependency on types nothing statically references, which constraint C2 forbids.
/// </remarks>
public sealed class FlowStreamCatalog
{
    private readonly Lock _gate = new();
    private readonly Dictionary<SubscriptionKey, StreamRegistration> _subscriptions = [];

    /// <summary>How many stream subscriptions this node serves.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _subscriptions.Count;
            }
        }
    }

    /// <summary>Every registered subscription, ordered so a pass is deterministic.</summary>
    public IReadOnlyList<StreamRegistration> Registrations
    {
        get
        {
            lock (_gate)
            {
                return [.. _subscriptions
                    .OrderBy(static entry => entry.Key.FlowId, StringComparer.Ordinal)
                    .ThenBy(static entry => entry.Key.FlowVersion, StringComparer.Ordinal)
                    .ThenBy(static entry => entry.Key.Source, StringComparer.Ordinal)
                    .ThenBy(static entry => entry.Key.Group, StringComparer.Ordinal)
                    .Select(static entry => entry.Value)];
            }
        }
    }

    /// <summary>Makes a stream subscription readable on this node.</summary>
    /// <param name="subscription">Which stream, under whose group.</param>
    /// <param name="window">The <c>Window</c> argument, e.g. <c>tumbling:1m</c>.</param>
    /// <param name="lateness">The <c>Lateness</c> argument, an ISO-8601 duration.</param>
    /// <param name="checkpoint">The <c>Checkpoint</c> argument, an ISO-8601 duration.</param>
    /// <param name="parallelism">The <c>Parallelism</c> argument.</param>
    /// <param name="plan">The compiled flow.</param>
    /// <param name="dispatcher">Invokes the capability behind each step index.</param>
    /// <returns>The same catalogue, so registrations chain.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// The plan is not the flow the subscription names, it does not declare
    /// <see cref="ExecutionProfile.Streaming"/>, or the declared window is a shape this engine
    /// does not implement.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <strong>A flow that does not declare <c>Streaming</c> is refused</strong>, for
    /// <see cref="FlowChangeCatalog.Add"/>'s reason with one term changed. The checkpoint is
    /// committed after a window's flow has run, so every crash in between re-reads that window's
    /// records and rebuilds it; what turns the second run into a refusal is the derived instance
    /// id meeting the journal's primary key, and an <c>Ephemeral</c> flow journals nothing. A
    /// <c>Durable</c> flow would journal — and is refused anyway, because a stream flow's input
    /// is a window rather than a request and the profile is the one line that says so. The
    /// runtime journals <c>Streaming</c> exactly as it journals <c>Durable</c>; the profiles
    /// differ in what starts them, not in what they cost.
    /// </para>
    /// <para>
    /// <strong>The window shape is refused here as well as by <c>FLOWX1042</c>.</strong> The
    /// diagnostic reads an attribute and this reads the values a host was handed, which are not
    /// always the same values: a registration written by hand, or generated by an older build,
    /// reaches here without passing the analyzer.
    /// </para>
    /// <para>
    /// Last registration wins for a given <c>(id, version, source, group)</c>, for
    /// <see cref="FlowCatalog.Add"/>'s reason.
    /// </para>
    /// </remarks>
    public FlowStreamCatalog Add(
        StreamSubscription subscription,
        string window,
        string lateness,
        string checkpoint,
        int parallelism,
        ExecutionPlan plan,
        IStepDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(dispatcher);

        if (!string.Equals(plan.Flow.Id, subscription.FlowId, StringComparison.Ordinal) ||
            !string.Equals(plan.Flow.Version, subscription.FlowVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The subscription names '{subscription.FlowId}@{subscription.FlowVersion}' and " +
                $"the plan is '{plan.Flow.Id}@{plan.Flow.Version}'. The identity is what the " +
                "window's instance id and the checkpoint are both keyed on, so a mismatch would " +
                "start one flow under another's windows and read another's position.",
                nameof(plan));
        }

        if (plan.Flow.Profile != ExecutionProfile.Streaming)
        {
            throw new ArgumentException(
                $"Flow '{subscription.FlowId}' reads '{subscription.Source}' and declares the " +
                $"profile '{plan.Flow.Profile}'. A stream-triggered flow must declare Streaming: " +
                "the checkpoint is committed after a window's flow has run, so a crash in " +
                "between rebuilds that window from the source, and only a journaled instance has " +
                "a primary key to refuse the second run. Declare " +
                "Profile = ExecutionProfile.Streaming.",
                nameof(plan));
        }

        var spec = StreamWindowSpec.Read(window, lateness, checkpoint, parallelism);

        if (spec.IsFailure)
        {
            throw new ArgumentException(
                $"Flow '{subscription.FlowId}' reads '{subscription.Source}': {spec.Error.Message}",
                nameof(window));
        }

        var key = new SubscriptionKey(
            subscription.FlowId,
            subscription.FlowVersion,
            subscription.Source,
            subscription.Group);

        lock (_gate)
        {
            _subscriptions[key] = new StreamRegistration(
                subscription, spec.Value, new FlowRegistration(plan, dispatcher));
        }

        return this;
    }

    /// <summary>What makes two registrations the same subscription.</summary>
    /// <remarks>
    /// The four terms the instance id and the checkpoint are keyed on, for
    /// <c>FlowChangeCatalog</c>'s reason: a catalogue keyed on anything narrower would let two
    /// registrations share a checkpoint.
    /// </remarks>
    private readonly record struct SubscriptionKey(
        string FlowId, string FlowVersion, string Source, string Group);
}
