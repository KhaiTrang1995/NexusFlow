using FlowX.Runtime;

namespace FlowX.Hosting;

/// <summary>One change subscription this node serves, and the flow it starts.</summary>
/// <param name="Subscription">What it observes, and under whose group.</param>
/// <param name="Flow">The compiled plan and its dispatcher.</param>
public sealed record ChangeRegistration(ChangeSubscription Subscription, FlowRegistration Flow)
{
    /// <summary>The id the instance for one observed change is started under.</summary>
    /// <param name="changeId">The change's own identity.</param>
    public Guid InstanceIdFor(Guid changeId) => ChangeIdentity.InstanceIdFor(
        Subscription.FlowId,
        Subscription.FlowVersion,
        Subscription.Source,
        Subscription.Group,
        changeId);

    /// <summary>The lease that makes this node the only one reading this subscription.</summary>
    public Guid SubscriptionLease => ChangeIdentity.SubscriptionLeaseIdFor(
        Subscription.FlowId, Subscription.FlowVersion, Subscription.Source, Subscription.Group);
}

/// <summary>
/// Which change subscriptions this node serves, and the plans behind them.
/// </summary>
/// <remarks>
/// <see cref="FlowBusCatalog"/>'s shape and its reasons, including registration rather than
/// discovery: reflecting over loaded assemblies to find <c>[ChangeTrigger]</c> would be a
/// trim-time dependency on types nothing statically references, which constraint C2 forbids.
/// </remarks>
public sealed class FlowChangeCatalog
{
    private readonly Lock _gate = new();
    private readonly Dictionary<SubscriptionKey, ChangeRegistration> _subscriptions = [];

    /// <summary>How many change subscriptions this node serves.</summary>
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
    public IReadOnlyList<ChangeRegistration> Registrations
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

    /// <summary>Makes a change subscription observable on this node.</summary>
    /// <param name="subscription">What it observes, and under whose group.</param>
    /// <param name="plan">The compiled flow.</param>
    /// <param name="dispatcher">Invokes the capability behind each step index.</param>
    /// <returns>The same catalogue, so registrations chain.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// The plan is not the flow the subscription names, it does not declare
    /// <see cref="ExecutionProfile.Durable"/>, or it emits the very type the subscription
    /// observes.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <strong>An ephemeral flow is refused</strong>, for <see cref="FlowBusCatalog.Add"/>'s
    /// reason: nothing journals an ephemeral instance, so the id a change derives
    /// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0049-a-change-names-the-instance-it-starts.md">ADR-0049</a>)
    /// is inert and every re-read of an uncommitted cursor position runs the flow again with
    /// nothing recording that it had. <c>FLOWX1041</c> is the earlier half of the same rule.
    /// </para>
    /// <para>
    /// <strong>A flow that emits the type it observes is refused, and this check exists nowhere
    /// else</strong>
    /// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0047-a-change-trigger-observes-the-outbox.md">ADR-0047</a>
    /// decision 3). The staged event starts the flow, the flow stages another under a fresh
    /// identity, and the feed offers that one — for ever, with the outbox growing and every
    /// instance legitimately distinct, so nothing downstream can tell it from work. The emitted
    /// types are in the plan, which is why the answer is read from the artifact that will run
    /// rather than from syntax. An <em>indirect</em> cycle across two flows is not refused: this
    /// catalogue sees one registration at a time.
    /// </para>
    /// <para>
    /// Last registration wins for a given <c>(id, version, source, group)</c>, for
    /// <see cref="FlowCatalog.Add"/>'s reason.
    /// </para>
    /// </remarks>
    public FlowChangeCatalog Add(
        ChangeSubscription subscription, ExecutionPlan plan, IStepDispatcher dispatcher)
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
                "instance id and the cursor are both keyed on, so a mismatch would start one " +
                "flow under another's changes and read another's position.",
                nameof(plan));
        }

        if (plan.Flow.Profile != ExecutionProfile.Durable)
        {
            throw new ArgumentException(
                $"Flow '{subscription.FlowId}' observes '{subscription.Source}' and declares the " +
                $"profile '{plan.Flow.Profile}'. A change-triggered flow must declare Durable: " +
                "nothing journals an ephemeral instance, so the id a change derives is inert, " +
                "there is no primary key to refuse a re-read, and one change would start one " +
                "flow per pass until the cursor happened to commit — with no error, no duplicate " +
                "row and nothing anywhere to count.",
                nameof(plan));
        }

        if (Emits(plan, subscription.Source))
        {
            throw new ArgumentException(
                $"Flow '{subscription.FlowId}' observes '{subscription.Source}' and emits it. " +
                "The change would start the flow, the flow would stage another event of that " +
                "type under a fresh identity, and the feed would offer that one — for ever, " +
                "with every instance legitimately distinct so nothing refuses it and nothing " +
                "downstream can tell the loop from work. Observe a different type, or emit one.",
                nameof(plan));
        }

        lock (_gate)
        {
            _subscriptions[new SubscriptionKey(
                subscription.FlowId,
                subscription.FlowVersion,
                subscription.Source,
                subscription.Group)] =
                new ChangeRegistration(subscription, new FlowRegistration(plan, dispatcher));
        }

        return this;
    }

    /// <summary>Whether any <c>Emit</c> node in the plan stages the type given.</summary>
    /// <remarks>
    /// <see cref="ExecutionPlan.HasEmit"/> is checked first because it is a bool the plan already
    /// computed, and a plan that emits nothing is the common case.
    /// </remarks>
    private static bool Emits(ExecutionPlan plan, string type) =>
        plan.HasEmit &&
        plan.Graph.Steps.Any(step =>
            step.Kind == StepKind.Emit &&
            string.Equals(step.EventType, type, StringComparison.Ordinal));

    /// <summary>Ordinal by construction: an id, a version, a type and a group are identifiers.</summary>
    private readonly record struct SubscriptionKey(
        string FlowId, string FlowVersion, string Source, string Group);
}
