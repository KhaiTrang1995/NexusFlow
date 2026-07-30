using System.Collections.Frozen;

namespace FlowX;

/// <summary>
/// An expected business failure. Errors are values, not exceptions (ADR-0007):
/// they appear in signatures, are enumerable into the manifest, and cost nothing
/// to produce on the failure path.
/// </summary>
/// <param name="Code">
/// Stable, greppable identifier in <c>&lt;domain&gt;.&lt;snake_case_reason&gt;</c> form,
/// e.g. <c>inventory.out_of_stock</c>. It is a contract: it appears in the manifest,
/// in generated OpenAPI responses and in RFC 7807 <c>type</c> URIs, so renaming it is
/// a breaking change.
/// </param>
/// <param name="Message">Human-readable description. Never place secrets or PII here.</param>
/// <param name="Category">Drives retryability and transport mapping.</param>
/// <param name="Data">Optional structured detail, surfaced in Problem Details extensions.</param>
public sealed record Error(
    string Code,
    string Message,
    ErrorCategory Category,
    FrozenDictionary<string, object?>? Data = null)
{
    /// <summary>Returns a copy carrying one additional structured detail.</summary>
    public Error With(string key, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var next = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (Data is not null)
        {
            foreach (var pair in Data)
            {
                next[pair.Key] = pair.Value;
            }
        }

        next[key] = value;
        return this with { Data = next.ToFrozenDictionary(StringComparer.Ordinal) };
    }

    /// <inheritdoc />
    public override string ToString() => $"{Code} ({Category}): {Message}";
}
