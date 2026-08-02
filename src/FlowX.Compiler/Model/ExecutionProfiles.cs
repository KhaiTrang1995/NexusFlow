namespace FlowX.Compiler.Model;

/// <summary>
/// What a declared execution profile means to the parts of this compiler that emit journal code.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This exists because "is this flow journaled?" was written as
/// <c>Profile == "Durable"</c> in four places, and a third profile made all four wrong.</strong>
/// <c>ExecutionProfile.Streaming</c> is journaled exactly as <c>Durable</c> is — its own
/// documentation says so, <c>FlowStreamCatalog.Add</c> refuses a stream-triggered flow that
/// declares anything else precisely <em>because</em> only a journaled instance has a primary key
/// to refuse a rebuilt window, and <c>FlowHost</c> runs it down the same path
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0055-a-window-names-the-instance-it-starts.md">ADR-0055</a>).
/// </para>
/// <para>
/// <strong>What the four literals cost while they stood</strong>, all of it silent: a
/// <c>Streaming</c> flow's generated dispatcher described no journal payloads, so a resumed
/// window re-entered with an empty state bag; its <c>.Emit</c> steps staged nothing and reported
/// <c>FLOWX1024</c> saying "the flow declares Profile = Ephemeral", which it did not;
/// <c>FLOWX1006</c> never fired for it, so the missing serialiser contexts that would have
/// explained the empty bag were never named.
/// </para>
/// <para>
/// The names are compared as strings because that is what
/// <c>FlowModel.Profile</c> holds — attribute metadata read as a name — and turning it into an
/// enum here would mean parsing a value the analyzer already validated.
/// </para>
/// </remarks>
internal static class ExecutionProfiles
{
    /// <summary>The profile whose instances are journaled and which nothing else is measured against.</summary>
    public const string Durable = "Durable";

    /// <summary>The profile a closed window starts, journaled for <see cref="Durable"/>'s reason.</summary>
    public const string Streaming = "Streaming";

    /// <summary>
    /// Whether a flow declaring this profile journals its instances, its state bag and its
    /// step results.
    /// </summary>
    /// <param name="profile">The profile name, as read from <c>[Flow]</c>.</param>
    /// <returns>
    /// <c>true</c> for <see cref="Durable"/> and <see cref="Streaming"/>; <c>false</c> for
    /// <c>Ephemeral</c>, for an unreadable declaration, and for a profile this build does not
    /// know — which is the safe direction, because the consequence of answering <c>false</c> is
    /// a diagnostic that is not raised rather than generated code that does not compile.
    /// </returns>
    public static bool Journals(string? profile) =>
        string.Equals(profile, Durable, System.StringComparison.Ordinal) ||
        string.Equals(profile, Streaming, System.StringComparison.Ordinal);
}
