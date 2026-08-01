using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FlowX.Runtime;

/// <summary>
/// The derivation that turns one firing of one schedule into the id of the instance it starts.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the whole of the multi-node answer, and there is deliberately nothing
/// else.</strong> Ten nodes evaluating one <see cref="CronSchedule"/> arrive at the same
/// occurrence, because the evaluation is a pure function of the expression and the clock. Ten
/// nodes handing that occurrence to <see cref="InstanceIdFor"/> arrive at the same
/// <see cref="Guid"/>. So ten nodes then race to start <em>one</em> instance, and the two
/// primitives the runtime already has settle it: <c>ILeaseStore.AcquireAsync</c> refuses nine
/// of them while the winner holds the lease, and <c>IFlowJournal.StartAsync</c> refuses them
/// with <c>journal.instance_exists</c> for ever afterwards
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0031-an-occurrence-names-the-instance-it-starts.md">ADR-0026</a>).
/// </para>
/// <para>
/// <strong>The lease is the fast answer and the journal is the true one.</strong> A lease has
/// a TTL, so it says nothing about a node that comes back an hour later; the primary key is
/// what makes "this occurrence has fired" a permanent fact. That is why the derivation has to
/// be stable across processes, deployments and machines, and why nothing in it is a clock
/// reading, a machine name or a hash-code.
/// </para>
/// <para>
/// <strong>Every term is in the id because leaving it out would fold two schedules into
/// one.</strong> The flow version in particular: an instance is pinned to the version it
/// started with (<c>docs/11-Distributed-Runtime.md §7</c>), so two versions deployed side by
/// side are two schedules, and a shared id would let the older one's fire suppress the newer
/// one's for the whole of a canary.
/// </para>
/// </remarks>
public static class ScheduleOccurrence
{
    /// <summary>
    /// Separates the terms so that no two different tuples can produce one string.
    /// </summary>
    /// <remarks>
    /// A NUL cannot occur in a flow id, a version, a cron expression or an IANA zone, so
    /// concatenating on it is injective — where concatenating on nothing would make
    /// <c>("ab", "c")</c> and <c>("a", "bc")</c> the same schedule.
    /// </remarks>
    private const char Separator = '\0';

    /// <summary>The id the instance for one firing is started under.</summary>
    /// <param name="flowId">The flow's business identity.</param>
    /// <param name="flowVersion">The exact version this node would run it at.</param>
    /// <param name="cron">The expression, verbatim as declared and published.</param>
    /// <param name="timeZone">The IANA zone the expression was evaluated in.</param>
    /// <param name="occurrence">The instant the expression named.</param>
    /// <returns>
    /// A UUID version 8 — RFC 9562's slot for a derived id. Not a version 4, which would claim
    /// the bytes were random, and not the version 7 <c>FlowHost</c> mints for a request-started
    /// instance, which would claim the leading bits were the instant it was created. An
    /// operator reading a journal row is entitled to tell a derived key from a minted one.
    /// </returns>
    public static Guid InstanceIdFor(
        string flowId,
        string flowVersion,
        string cron,
        string timeZone,
        DateTimeOffset occurrence)
    {
        ArgumentNullException.ThrowIfNull(flowId);
        ArgumentNullException.ThrowIfNull(flowVersion);
        ArgumentNullException.ThrowIfNull(cron);
        ArgumentNullException.ThrowIfNull(timeZone);

        var material = new StringBuilder()
            .Append(flowId).Append(Separator)
            .Append(flowVersion).Append(Separator)
            .Append(cron).Append(Separator)
            .Append(timeZone).Append(Separator)

            // The instant, normalised to UTC and to the minute cron resolves to. Written with
            // an explicit format rather than through the current culture, because a node with
            // a different culture must derive the same id.
            .Append(occurrence.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))
            .ToString();

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(material));

        return AsVersion8(digest);
    }

    /// <summary>Lays the first sixteen bytes of a digest out as a UUID version 8.</summary>
    /// <remarks>
    /// <para>
    /// Byte order is fixed here rather than left to <c>new Guid(byte[])</c>'s little-endian
    /// reading of the first three groups, so the id a Postgres <c>uuid</c> column shows is a
    /// prefix of the digest an operator can recompute. The version and variant nibbles are set
    /// per RFC 9562 §4.2, which costs six of the digest's bits — the remaining 122 are far more
    /// than the birthday bound any schedule will reach.
    /// </para>
    /// </remarks>
    private static Guid AsVersion8(ReadOnlySpan<byte> digest)
    {
        Span<byte> bytes = stackalloc byte[16];

        digest[..16].CopyTo(bytes);

        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return new Guid(bytes, bigEndian: true);
    }
}
