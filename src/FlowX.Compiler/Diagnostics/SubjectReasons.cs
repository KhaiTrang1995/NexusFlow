namespace FlowX.Compiler.Diagnostics;

/// <summary>
/// The four shapes of <c>[Subject]</c> declaration <c>FLOWX1047</c> refuses, written as the
/// clause that completes its message.
/// </summary>
/// <remarks>
/// <para>
/// One diagnostic with four reasons rather than four diagnostics, unlike <c>FLOWX1038</c> and
/// its neighbours, which is a decision and not an economy. Those four are about four different
/// trigger attributes and a suppression of one must not silence another. These four are one
/// rule about one attribute — <em>the runtime cannot read the subject you declared</em> — and
/// a project that legitimately suppressed the rule for one of them would legitimately suppress
/// it for all of them, because the consequence is identical in every case: rows that no
/// erasure can ever find.
/// </para>
/// <para>
/// Every reason is decided inside <c>FlowAnalyzer</c> from the flow's own symbols, so unlike
/// <c>FLOWX1006</c> and <c>FLOWX1024</c> there is no provisional stage and nothing for the
/// pipeline to settle.
/// </para>
/// </remarks>
internal static class SubjectReasons
{
    /// <summary>Two members of one contract both claim to name the subject.</summary>
    /// <remarks><c>{0}</c> is the contract, <c>{1}</c> the members.</remarks>
    public const string AmbiguousFormat =
        "'{0}' declares [Subject] on more than one member ({1}), so there are two answers to " +
        "'whose record is this' and the runtime would record whichever the serialiser wrote " +
        "first. Mark exactly one";

    /// <summary>The marker is on the flow's output.</summary>
    /// <remarks><c>{0}</c> is the contract.</remarks>
    public const string OnOutputFormat =
        "'{0}' is this flow's output contract, and the subject's handle is written onto the " +
        "instance row before the first step runs — an output does not exist yet at that " +
        "moment, and by the time it does the row it would have identified has already been " +
        "written. Mark the member of the input contract that names the same person";

    /// <summary>The marked member is not text.</summary>
    /// <remarks><c>{0}</c> is the member, <c>{1}</c> its type.</remarks>
    public const string NotAStringFormat =
        "member '{0}' is of type '{1}', and a subject digest is computed over text. A " +
        "structured or numeric identifier has more than one faithful rendering, and two " +
        "renderings are two subjects — so the choice belongs to the application that knows " +
        "which one is canonical. Expose the canonical form as a string member and mark that";

    /// <summary>The flow keeps no journal for the handle to live on.</summary>
    /// <remarks><c>{0}</c> is the declared profile.</remarks>
    public const string NotJournaledFormat =
        "this flow declares Profile = ExecutionProfile.{0}, which opens no instance row, so " +
        "there is nowhere for the handle to be written and nothing for an erasure to find. " +
        "Declare Durable, or take the marker off — a subject marked on a flow that records " +
        "nothing is a right-to-erasure control over an empty set";
}
