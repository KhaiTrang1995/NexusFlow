using System.Text.Json;
using System.Text.Json.Serialization;

namespace Crm;

/// <summary>
/// How a seed file is read, and it is stricter than how a request is.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A separate context from <c>CrmJsonContext</c>, because the two want opposite
/// things.</strong> A request body is read leniently on purpose — an old client sending a
/// property this build has dropped should still be served. A seed file is read by one person
/// who is about to redeploy, and a property they misspelt is a configuration they think they
/// made and did not. <c>UnmappedMemberHandling.Disallow</c> turns that into a refusal naming the
/// property, at the cost of nothing, because no client sends this document.
/// </para>
/// <para>
/// <strong>Source-generated, so the reader is trim- and AOT-safe.</strong> The reflection-based
/// overload is an analyser error in this repository, and rightly: a shape resolved at run time
/// is a shape a trimmed build can silently read as empty.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowTrailingCommas = false,
    ReadCommentHandling = JsonCommentHandling.Disallow)]
[JsonSerializable(typeof(SeedDocument))]
public sealed partial class SeedJsonContext : JsonSerializerContext
{
    /// <summary>The context the reader uses, with the document's depth bounded.</summary>
    /// <remarks>
    /// <c>MaxDepth</c> is a property of the options rather than of the generator's attribute, so
    /// it is applied by rebuilding the context around a copy. Depth is the one limit
    /// <see cref="SeedLimits.MaxBytes"/> does not already imply: a few hundred bytes of nothing
    /// but brackets is a deep stack, and the parser walks it before any of this file's own checks
    /// get a turn.
    /// </remarks>
    /// <para>
    /// Built on first use rather than in a field initialiser. <c>Default</c> is generated onto
    /// this same type in another file, so a field initialiser reading it depends on which of the
    /// two the compiler emits first — and the parameterless constructor is not a substitute: it
    /// passes <c>null</c> to the base and the instance ends up with framework defaults, which
    /// reads <c>"tenant"</c> as nothing at all.
    /// </para>
    public static SeedJsonContext Bounded => Lazily.Value;

    private static readonly Lazy<SeedJsonContext> Lazily =
        new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    private static SeedJsonContext Build() =>
        new(new JsonSerializerOptions(Default.Options) { MaxDepth = SeedLimits.MaxDepth });
}
