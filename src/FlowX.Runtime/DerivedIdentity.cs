using System.Security.Cryptography;
using System.Text;

namespace FlowX.Runtime;

/// <summary>
/// Turns a tuple of terms every node agrees on into the <see cref="Guid"/> they all derive.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Extracted from <see cref="ScheduleOccurrence"/> when a second trigger kind needed the
/// same derivation</strong>, which is the condition
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0031-an-occurrence-names-the-instance-it-starts.md">ADR-0031</a>
/// named in advance: <em>"a second transport needs occurrence-derived identity, at which point
/// the derivation belongs to the abstraction rather than to the sweep."</em> The bytes are
/// unchanged — <c>ScheduleOccurrence</c> builds the same material string it always did and hands
/// it here — so no id any journal already holds moves.
/// </para>
/// <para>
/// <strong>Why the terms are joined on a NUL.</strong> A flow id, a version, a cron expression,
/// an IANA zone, a topic and a consumer group cannot contain one, so concatenating on it is
/// injective — where concatenating on nothing would make <c>("ab", "c")</c> and
/// <c>("a", "bc")</c> the same subject.
/// </para>
/// </remarks>
public static class DerivedIdentity
{
    /// <summary>Separates the terms so that no two different tuples can produce one string.</summary>
    public const char Separator = '\0';

    /// <summary>The id a tuple of terms derives.</summary>
    /// <param name="terms">The terms, in a fixed order. None may be null.</param>
    /// <returns>
    /// A UUID version 8 — RFC 9562's slot for a derived id. Not a version 4, which would claim
    /// the bytes were random, and not the version 7 <c>FlowHost</c> mints for a request-started
    /// instance, which would claim the leading bits were the instant it was created. An operator
    /// reading a journal row is entitled to tell a derived key from a minted one.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="terms"/>, or a term, is null.</exception>
    public static Guid From(params string[] terms)
    {
        ArgumentNullException.ThrowIfNull(terms);

        var material = new StringBuilder();

        foreach (var term in terms)
        {
            ArgumentNullException.ThrowIfNull(term);

            material.Append(term).Append(Separator);
        }

        return FromMaterial(material.ToString());
    }

    /// <summary>The id one already-assembled material string derives.</summary>
    /// <param name="material">The terms, joined and separated by the caller.</param>
    /// <returns>The id.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="material"/> is null.</exception>
    /// <remarks>
    /// Exposed beside <see cref="From"/> for a caller whose last term is not a string — a
    /// schedule's occurrence is an instant, formatted with an explicit pattern so that a node in
    /// a different culture derives the same id.
    /// </remarks>
    public static Guid FromMaterial(string material)
    {
        ArgumentNullException.ThrowIfNull(material);

        return AsVersion8(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    /// <summary>Lays the first sixteen bytes of a digest out as a UUID version 8.</summary>
    /// <remarks>
    /// Byte order is fixed here rather than left to <c>new Guid(byte[])</c>'s little-endian
    /// reading of the first three groups, so the id a Postgres <c>uuid</c> column shows is a
    /// prefix of the digest an operator can recompute. The version and variant nibbles are set
    /// per RFC 9562 §4.2, which costs six of the digest's bits — the remaining 122 are far more
    /// than the birthday bound any subscription will reach.
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
