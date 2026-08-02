using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FlowX.Postgres;

/// <summary>
/// Derives the schema a tenant's rows live in, from the tenant id and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Derived rather than looked up, and that is a safety property rather than a
/// performance one.</strong> A registry mapping tenant to schema is kept — the sweeps need a
/// set to fan out over — but nothing on the execution path consults it. A corrupted, stale or
/// half-written registry row can therefore cause a sweep to miss a tenant; it cannot cause one
/// tenant's execution to be pointed at another tenant's schema, which is the failure that would
/// matter. The lookup that cannot be wrong is the one that does not happen.
/// </para>
/// <para>
/// <strong>Injective, because a collision is a cross-tenant read.</strong> Two tenants sharing a
/// schema would share every row in it, and no policy below would notice — they would each be
/// reading their own schema as far as the connection string was concerned. So the name is not
/// the tenant id cleaned up: it is a readable slug <em>and</em> a fingerprint of the exact,
/// unmodified tenant id, and the fingerprint is what carries the distinction. <c>Acme</c>,
/// <c>acme</c> and <c>a-c-m-e</c> all slug to <c>acme</c> and none of them share a schema.
/// </para>
/// <para>
/// <strong>Sixty-three bytes is the whole budget.</strong> PostgreSQL truncates a longer
/// identifier silently, which would collapse two schemas into one at exactly the length where
/// nobody is looking — so the slug is capped rather than the result trimmed, and the prefix is
/// validated against the same budget when it is configured.
/// </para>
/// </remarks>
internal static class TenantSchemaName
{
    /// <summary>How many characters of the tenant id survive into the readable half.</summary>
    public const int SlugLength = 24;

    /// <summary>How many hex characters of the digest are kept.</summary>
    /// <remarks>
    /// Sixteen, which is sixty-four bits. Enough that a deployment would need on the order of
    /// four billion tenants before a collision became likely, and short enough that a slug and
    /// a prefix still fit beside it. Eight would have been a birthday collision at about sixty
    /// thousand tenants, which is a real number of tenants.
    /// </remarks>
    public const int FingerprintLength = 16;

    /// <summary>The longest prefix that leaves room for a full slug and fingerprint.</summary>
    public const int MaximumPrefixLength = 63 - SlugLength - FingerprintLength - 1;

    /// <summary>Derives the schema name for one tenant.</summary>
    /// <param name="prefix">The configured prefix, already validated.</param>
    /// <param name="tenantId">The tenant, exactly as it was resolved.</param>
    /// <returns>A bare lower-case identifier of at most sixty-three characters.</returns>
    public static string For(string prefix, string tenantId)
    {
        var slug = Slug(tenantId);
        var fingerprint = Fingerprint(tenantId);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{prefix}{slug}_{fingerprint}");
    }

    /// <summary>
    /// Rejects a prefix that is not a usable start for a schema name.
    /// </summary>
    /// <param name="prefix">The configured prefix.</param>
    /// <returns>The prefix.</returns>
    /// <exception cref="ArgumentException">It is not usable.</exception>
    /// <remarks>
    /// The prefix is validated as a schema name in its own right — so it cannot start with a
    /// digit, and cannot smuggle a quote — and then again for length, because a prefix that
    /// leaves no room for a fingerprint would produce names that PostgreSQL truncates back into
    /// each other.
    /// </remarks>
    public static string RequirePrefix(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        Identifiers.RequireSchemaName(prefix);

        if (prefix.Length > MaximumPrefixLength)
        {
            throw new ArgumentException(
                $"'{prefix}' is too long a tenant schema prefix: at most " +
                $"{MaximumPrefixLength} characters, so that a {SlugLength}-character slug and " +
                $"a {FingerprintLength}-character fingerprint still fit inside PostgreSQL's " +
                "63-byte identifier limit. A longer one would be truncated by the server, and " +
                "two tenants truncated to the same name share every row they own.",
                nameof(prefix));
        }

        return prefix;
    }

    /// <summary>The readable half: what a DBA sees in <c>\dn</c>.</summary>
    /// <remarks>
    /// Lower-cased and reduced to <c>[a-z0-9_]</c> for <see cref="Identifiers"/>'s reason — an
    /// unquoted identifier is folded by the server and a quoted one is not, so a name that means
    /// two things depending on how it is written is refused rather than handled. This half
    /// carries no distinguishing weight at all; the fingerprint does.
    /// </remarks>
    private static string Slug(string tenantId)
    {
        var length = Math.Min(tenantId.Length, SlugLength);
        var slug = new char[length];

        for (var i = 0; i < length; i++)
        {
            var character = char.ToLowerInvariant(tenantId[i]);

            slug[i] = character is >= 'a' and <= 'z' or >= '0' and <= '9' ? character : '_';
        }

        return new string(slug);
    }

    /// <summary>The distinguishing half: a digest of the tenant id, byte for byte.</summary>
    private static string Fingerprint(string tenantId)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];

        SHA256.HashData(Encoding.UTF8.GetBytes(tenantId), digest);

        return Convert.ToHexStringLower(digest[..(FingerprintLength / 2)]);
    }
}
