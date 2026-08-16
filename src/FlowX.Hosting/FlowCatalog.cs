using System.Diagnostics.CodeAnalysis;
using FlowX.Runtime;

namespace FlowX.Hosting;

/// <summary>
/// Which compiled plans this node can run, keyed by the identity a journal row carries.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A recovery scan is the reason this exists.</strong> An instance a trigger starts
/// arrives with its plan and its dispatcher already in hand; an instance a scan finds is a
/// row with a <c>flow_id</c> and a <c>flow_version</c> on it and nothing else. Something has
/// to turn those two strings back into an <see cref="ExecutionPlan"/> and an
/// <see cref="IStepDispatcher"/>, and a host is the only layer that knows what this process
/// was deployed with.
/// </para>
/// <para>
/// <strong>The version is part of the key, and that is the point rather than a detail.</strong>
/// A durable instance keeps the version it started with until it completes
/// (<c>docs/11-Distributed-Runtime.md §7</c>), so a node carrying only <c>1.3.0</c> must not
/// resume an instance pinned to <c>1.2.0</c>: the steps would be a different flow's, and the
/// journal's committed prefix would be replayed against a plan that never produced it.
/// Looking up by id alone would make a mid-flight deployment silently change what an
/// instance means, which is the failure version pinning exists to prevent. A node that does
/// not carry the pinned version simply leaves the instance for one that does.
/// </para>
/// <para>
/// Registered rather than discovered. Reflecting over loaded assemblies to find plans would
/// be a trim-time dependency on types nothing statically references, which constraint C2
/// forbids; the generated registration is a call per flow and is legible in a stack trace.
/// </para>
/// </remarks>
public sealed class FlowCatalog
{
    private readonly Lock _gate = new();
    private readonly Dictionary<FlowKey, FlowRegistration> _flows = [];

    /// <summary>How many flows this node can run.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _flows.Count;
            }
        }
    }

    /// <summary>Makes a flow resumable on this node.</summary>
    /// <param name="plan">The compiled flow.</param>
    /// <param name="dispatcher">Invokes the capability behind each step index.</param>
    /// <returns>The same catalogue, so registrations chain.</returns>
    /// <remarks>
    /// Last registration wins for a given <c>(id, version)</c>. A duplicate is a host
    /// registering its flows twice rather than two different flows colliding — the version is
    /// in the key — and throwing would turn an idempotent composition root into a startup
    /// crash.
    /// </remarks>
    public FlowCatalog Add(ExecutionPlan plan, IStepDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(dispatcher);

        lock (_gate)
        {
            _flows[new FlowKey(plan.Flow.Id, plan.Flow.Version)] = new FlowRegistration(plan, dispatcher);
        }

        return this;
    }

    /// <summary>Every flow registered on this node, in no particular order.</summary>
    /// <remarks>
    /// A snapshot taken under the gate, for the reason <see cref="Count"/> takes one: a caller
    /// enumerating the dictionary itself would be reading it while a composition root that
    /// registers from more than one place is still writing. It is read at start-up — by the
    /// check that refuses a node whose plans declare a store nobody registered — rather than on
    /// any execution path, so copying the list costs nothing that matters.
    /// </remarks>
    public IReadOnlyList<FlowRegistration> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _flows.Values];
            }
        }
    }

    /// <summary>Finds the plan an instance is pinned to, or fails to.</summary>
    /// <param name="flowId">The flow's business identity.</param>
    /// <param name="flowVersion">The exact version the instance is pinned to.</param>
    /// <param name="registration">The plan and its dispatcher, when this node carries them.</param>
    /// <returns>Whether this node can run that flow at that version.</returns>
    public bool TryGet(
        string flowId,
        string flowVersion,
        [NotNullWhen(true)] out FlowRegistration? registration)
    {
        lock (_gate)
        {
            return _flows.TryGetValue(new FlowKey(flowId, flowVersion), out registration);
        }
    }

    /// <summary>Ordinal by construction: a flow id and a version are identifiers, not prose.</summary>
    private readonly record struct FlowKey(string FlowId, string FlowVersion);
}

/// <summary>One flow this node can run: its compiled plan and the dispatcher behind it.</summary>
/// <param name="Plan">The compiled flow.</param>
/// <param name="Dispatcher">Invokes the capability behind each step index.</param>
public sealed record FlowRegistration(ExecutionPlan Plan, IStepDispatcher Dispatcher);
