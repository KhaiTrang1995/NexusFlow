namespace FlowX.Hosting;

/// <summary>
/// The background sweeps a host performs, so a deployment can give one host a job rather
/// than every host every job.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this exists to make possible.</strong> Six sweeps run continuously, and each
/// decides for itself whether it <em>can</em> run — a recovery scan needs a journal, a bus
/// scan needs a subscription. None of them could be told whether it <em>should</em>. So a
/// host configured with a journal ran every sweep it was capable of, and the three-role
/// topology <c>docs/18-Cloud-Native.md §1</c> and <c>docs/28-Azure-Hosting.md §3.1</c> both
/// describe — an <c>api</c> that serves requests, a <c>worker</c> that consumes, a
/// <c>scheduler</c> that fires — was a picture with nothing behind it. Those documents named
/// an environment variable, <c>FLOWX_TRIGGERS</c>, that appears nowhere in the source.
/// </para>
/// <para>
/// <strong>Capability and choice are kept apart on purpose.</strong> A scan's
/// <c>IsEnabled</c> still answers "is there anything here for me to do", which is a fact
/// about the store and the catalogue. This answers "is this host the one that does it",
/// which is a fact about the deployment. Folding the second into the first would make a
/// missing journal and a deliberate opt-out indistinguishable in a log.
/// </para>
/// <para>
/// <strong>Defaults to <see cref="All"/>, and that is not laziness.</strong> Every existing
/// deployment runs every sweep it can; a default of anything else would silently stop work a
/// running system depends on, which is the one change a hosting option must never make.
/// </para>
/// <para>
/// <strong>Why an HTTP role is absent.</strong> Serving requests is not a sweep — generated
/// endpoints are mapped by the application, not by a loop here — so an <c>api</c> host is
/// simply one with <see cref="None"/>. Naming a flag for it would imply this type could turn
/// endpoints off, which it cannot.
/// </para>
/// </remarks>
[Flags]
public enum HostSweeps
{
    /// <summary>No background sweep. The shape of an API-only host.</summary>
    None = 0,

    /// <summary>Take over instances a dead node abandoned.</summary>
    Recovery = 1 << 0,

    /// <summary>Wake instances whose parked wait has come due.</summary>
    Timer = 1 << 1,

    /// <summary>Fire schedules whose occurrence has arrived.</summary>
    Schedule = 1 << 2,

    /// <summary>Consume from subscribed broker destinations.</summary>
    Bus = 1 << 3,

    /// <summary>Advance the change feed over the outbox.</summary>
    Change = 1 << 4,

    /// <summary>Advance stream subscriptions.</summary>
    Stream = 1 << 5,

    /// <summary>
    /// Everything a durable node keeps moving on its own: recovery, timers and schedules.
    /// </summary>
    /// <remarks>
    /// The <c>scheduler</c> role of the three in 18 §1. Not autoscaled — scaling a sweeper
    /// duplicates work — so it is a fixed pair for availability rather than throughput.
    /// </remarks>
    Durability = Recovery | Timer | Schedule,

    /// <summary>Everything driven by something arriving: bus, change feed and streams.</summary>
    /// <remarks>The <c>worker</c> role, which scales on backlog rather than on latency.</remarks>
    Ingestion = Bus | Change | Stream,

    /// <summary>Every sweep. The default, and what every host did before this type existed.</summary>
    All = Durability | Ingestion,
}
