namespace FlowX.Redis;

/// <summary>
/// The key space this package occupies in Redis, in one place.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Public because a key space is an operational contract.</strong> A relational
/// adapter's layout is discoverable — an operator runs <c>\dt</c> and sees the tables. A
/// Redis adapter's layout exists only in the code that builds the strings, so an operator
/// sizing a keyspace, writing a <c>SCAN</c> pattern or setting up a cluster hash tag has
/// nowhere else to read it. Building the keys anywhere else in the package would make this
/// type a description of the key space rather than its definition.
/// </para>
/// <para>
/// <strong>Every key carries a cluster hash tag around the instance id.</strong> Redis
/// Cluster routes by the substring inside braces, so every key belonging to one flow instance
/// lands in one slot, and a future multi-key operation over an instance stays a single-slot
/// operation. Nothing here needs that today — the lease is one key — but a key layout is the
/// hardest thing in a Redis deployment to change afterwards, because changing it means
/// migrating live data rather than shipping a new binary.
/// </para>
/// </remarks>
public static class RedisKeys
{
    /// <summary>
    /// The sentinel that stands for <see cref="StepScope.Root"/> in a key or a field name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the mapping ADR-0015 and ADR-0016 both told the second adapter to
    /// make.</strong> Both records note that commitment 1 — <c>(instance, scope, step,
    /// attempt)</c> as a key — holds in PostgreSQL <em>partly by luck of dialect</em>:
    /// <see cref="StepScope.Root"/> renders as the empty string, PostgreSQL treats <c>''</c>
    /// as a value distinct from <c>NULL</c>, and so the flow body is a legal key component
    /// there without anybody having decided that it should be. A store that folds the empty
    /// string into "absent" rejects every root-scope row instead, and both ADRs say the same
    /// thing about it: any adapter after the first has to map <c>Root</c> explicitly.
    /// </para>
    /// <para>
    /// <strong>Redis has that fold, and it is at the client boundary rather than in the
    /// server.</strong> Redis itself distinguishes an empty string from a missing key. What
    /// does not distinguish them is the value type every read goes through: a missing hash
    /// field and a stored empty string arrive as two <c>RedisValue</c>s that
    /// <c>IsNullOrEmpty</c> reports identically, and <c>(string?)value ?? fallback</c> — the
    /// idiom for reading one — collapses them by construction. An adapter that stored
    /// <c>Root</c> as <c>""</c> would therefore work until the first read site was written the
    /// ordinary way, and would then lose the flow body's rows to a null check that looks
    /// correct.
    /// </para>
    /// <para>
    /// A hyphen rather than a word, because it cannot collide with a rendered scope: every
    /// non-root scope is slash-separated ASCII digits (<see cref="StepScope.Parse"/> enforces
    /// it), so no element index can ever render as <c>-</c>.
    /// </para>
    /// </remarks>
    public const string RootScope = "-";

    private const char Separator = ':';

    /// <summary>The key holding one instance's lease and its fencing-token counter.</summary>
    /// <param name="keyPrefix">The configured prefix, from <see cref="RedisLeaseOptions"/>.</param>
    /// <param name="instanceId">The instance the lease covers.</param>
    /// <returns>The key.</returns>
    /// <exception cref="ArgumentException"><paramref name="keyPrefix"/> is null or empty.</exception>
    public static string Lease(string keyPrefix, Guid instanceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyPrefix);

        return string.Concat(keyPrefix, Separator, "{", instanceId.ToString("n"), "}", Separator, "lease");
    }

    /// <summary>Renders a scope as a key component that is never empty.</summary>
    /// <param name="scope">The scope to render.</param>
    /// <returns><see cref="RootScope"/> for the flow body, otherwise the canonical path.</returns>
    /// <remarks>
    /// The inverse of <see cref="ParseScope"/>, and the only place in this package that turns a
    /// <see cref="StepScope"/> into text bound for Redis.
    /// </remarks>
    public static string Scope(StepScope scope) => scope.IsRoot ? RootScope : scope.Text;

    /// <summary>Reads back a scope this package wrote.</summary>
    /// <param name="text">The stored component. <see cref="RootScope"/> is the flow body.</param>
    /// <returns>The scope.</returns>
    /// <exception cref="ArgumentException">
    /// The text is neither <see cref="RootScope"/> nor a canonical scope path — which
    /// includes the empty string, because this package never writes one and reading one back
    /// means something other than this package wrote the value.
    /// </exception>
    /// <remarks>
    /// <strong>The empty string is rejected rather than quietly read as <c>Root</c>.</strong>
    /// Accepting it would restore exactly the fold this mapping exists to remove: a read site
    /// that lost the distinction would be indistinguishable from one that never had it, and
    /// the resulting rows would be attributed to the flow body silently.
    /// </remarks>
    public static StepScope ParseScope(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text == RootScope)
        {
            return StepScope.Root;
        }

        if (text.Length == 0)
        {
            throw new ArgumentException(
                $"An empty scope component was read back. This package writes '{RootScope}' " +
                "for the flow body and never writes an empty string, so an empty value here " +
                "is a missing value that something folded into one — the failure mode " +
                "ADR-0016's portability note names.",
                nameof(text));
        }

        return StepScope.Parse(text);
    }
}
