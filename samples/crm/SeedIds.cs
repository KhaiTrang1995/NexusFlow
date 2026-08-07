using System.Security.Cryptography;
using System.Text;

namespace Crm;

/// <summary>
/// Turns a tenant, a kind and an alias into the identifier that row will always have.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the id is derived rather than written down or generated.</strong> A seed file
/// that carried ids would make an operator responsible for keeping thirty UUIDs unique by hand,
/// and a seed that generated them would write a second copy of everything each time it ran.
/// Deriving the id from the alias makes applying the same file twice a no-op — the second run's
/// inserts collide with the first run's rows and do nothing — and it makes the same file
/// produce the same ids in every environment, which is what lets a screenshot, a support ticket
/// and a test all name the same record.
/// </para>
/// <para>
/// <strong>The tenant is inside the hash, not beside it.</strong> Two tenants seeded from one
/// file get different ids for the same alias. Sharing them would put one tenant's primary key
/// in another tenant's table — which row-level security would then hide rather than refuse, and
/// the symptom would be a row that exists and cannot be read.
/// </para>
/// <para>
/// <strong>UUIDv8 over SHA-256, and not v5.</strong> Version 5 is specified over SHA-1, which
/// this repository's analysers refuse and which nobody should be adding new uses of. RFC 9562
/// §5.8 leaves version 8 to the implementation precisely so a name-based identifier can be
/// built on a hash of one's choosing; the version and variant bits are set, so the result is a
/// well-formed UUID rather than sixteen bytes in a UUID-shaped hole.
/// </para>
/// </remarks>
public static class SeedIds
{
    // Distinguishes these ids from any other name-based id anybody derives later over the same
    // three strings. Without it, two schemes agreeing on inputs would agree on outputs.
    private const string Namespace = "flowx.crm.seed.v1";

    // A character an alias cannot contain, so the length prefixes below have a terminator.
    private const char Separator = '\u001f';

    /// <summary>The identifier an aliased item always has.</summary>
    /// <param name="tenant">The tenant the seed is written for.</param>
    /// <param name="kind">What sort of thing it is — <c>account</c>, <c>lead</c>, and so on.</param>
    /// <param name="alias">Its name in the file.</param>
    /// <returns>The identifier.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static Guid For(string tenant, string kind, string alias)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(alias);

        // Length-prefixed rather than joined by a separator: ("a", "bc") and ("ab", "c") join to
        // the same string under any separator the parts are allowed to contain, and an alias is
        // free text.
        var builder = new StringBuilder(Namespace).Append(Separator);

        foreach (var part in (string[])[tenant, kind, alias])
        {
            builder.Append(part.Length).Append(':').Append(part).Append(Separator);
        }

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()), hash);

        var bytes = hash[..16];

        // RFC 9562 §4.1-4.2: version 8 in the high nibble of octet 6, variant 10 in the top bits
        // of octet 8. Without these the value is a hash that happens to be sixteen bytes, and a
        // reader cannot tell it apart from a v4 that was generated randomly.
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return new Guid(bytes, bigEndian: true);
    }
}
