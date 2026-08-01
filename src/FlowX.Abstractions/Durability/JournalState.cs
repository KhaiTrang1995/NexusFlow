using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace FlowX;

/// <summary>
/// The read side of a state-bag snapshot: one stored member, back as the typed value the
/// steps after the frontier bind to.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is not on <see cref="JournalPayload"/>.</strong> That type is the write
/// path, and its whole design is that a value goes in and only a redacted document comes out.
/// Reading is the opposite direction and carries none of the same risk: what is on disk has
/// already been through the redaction pass, so nothing here can leak a value the journal
/// never held.
/// </para>
/// <para>
/// <strong>Still no reflection.</strong> The metadata comes out of the generated context by
/// the same lookup <c>JournalPayload.Of</c>'s context overload uses — a switch over the types
/// the author's <c>[JsonSerializable]</c> attributes named — so the read path is trim- and
/// NativeAOT-safe too (constraint C2).
/// </para>
/// <para>
/// <strong>What comes back for a marked member.</strong> The placeholder, because that is what
/// was stored. A journal that could return the original would be a journal that had it, which
/// is the thing <c>[Sensitive]</c> exists to prevent. <c>IStepDispatcher.RestoreState</c>
/// states this on its own contract, and it is the reason a resumed flow must not treat a
/// rehydrated secret as usable.
/// </para>
/// </remarks>
public static class JournalState
{
    /// <summary>Reads one stored state-bag member back into its contract type.</summary>
    /// <typeparam name="T">The contract. Must be declared by <paramref name="context"/>.</typeparam>
    /// <param name="member">The stored member's document.</param>
    /// <param name="context">The generated context declaring <typeparamref name="T"/>.</param>
    /// <returns>The rehydrated value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The context does not declare <typeparamref name="T"/>, or the stored document does not
    /// deserialise into it.
    /// </exception>
    /// <remarks>
    /// A throw rather than a null, and the engine turns it into
    /// <c>flow.state_restore_failed</c>. Resuming with a member missing would run the rest of
    /// the flow against a value no step produced, which is worse than refusing the resume and
    /// leaving the instance for a node that can read the row.
    /// </remarks>
    public static T Read<T>(in JsonElement member, JsonSerializerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.GetTypeInfo(typeof(T)) is not JsonTypeInfo<T> typeInfo)
        {
            throw new InvalidOperationException(
                $"'{context.GetType().Name}' does not declare [JsonSerializable(typeof({typeof(T).Name}))], " +
                "so a state bag holding one cannot be restored. Add the attribute to the context.");
        }

        return member.Deserialize(typeInfo) ??
            throw new InvalidOperationException(
                $"The journal's state bag holds a null '{typeof(T).Name}'. A snapshot member " +
                "that deserialises to nothing would resume the flow with a value no step " +
                "produced.");
    }
}
