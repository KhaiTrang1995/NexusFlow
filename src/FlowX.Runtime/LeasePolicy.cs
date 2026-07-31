namespace FlowX.Runtime;

/// <summary>
/// How long a lease lives and how often its holder says so.
/// </summary>
/// <remarks>
/// <para>
/// The two numbers in <c>docs/11-Distributed-Runtime.md §3</c>'s table, and its trade-off
/// stated once rather than at every call site: <see cref="Ttl"/> bounds how long a crashed
/// node's instance sits unowned — a latency question — while correctness is bounded by the
/// fencing token, which no setting here can weaken.
/// </para>
/// <para>
/// <see cref="RenewalInterval"/> defaults to a third of the TTL, which is the margin the same
/// table asks for. Two renewals may be lost — to a GC pause, to clock skew, to a slow store —
/// before the lease lapses, and a node that has missed two in a row has something wrong with
/// it that another node is better placed to work around.
/// </para>
/// </remarks>
public sealed record LeasePolicy
{
    /// <summary>The defaults from <c>docs/11-Distributed-Runtime.md §3</c>: 30 s, renewed every 10 s.</summary>
    public static LeasePolicy Default { get; } = new();

    /// <summary>How long ownership lasts without a renewal.</summary>
    public TimeSpan Ttl { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How often the holder renews. A third of the TTL by default.</summary>
    public TimeSpan RenewalInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>A policy with this TTL, renewed at a third of it.</summary>
    /// <param name="ttl">How long ownership lasts without a renewal.</param>
    public static LeasePolicy WithTtl(TimeSpan ttl)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ttl, TimeSpan.Zero);

        return new LeasePolicy { Ttl = ttl, RenewalInterval = ttl / 3 };
    }

    /// <summary>Throws if either number is one no lease could be run on.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is not positive, or renewal is not shorter than the TTL.</exception>
    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Ttl, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(RenewalInterval, TimeSpan.Zero);

        // Renewing exactly at the TTL means every renewal races the expiry it is meant to
        // prevent, and a lease that expires while its holder still believes it is held is
        // the split brain the whole mechanism exists to avoid.
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(RenewalInterval, Ttl);
    }
}
