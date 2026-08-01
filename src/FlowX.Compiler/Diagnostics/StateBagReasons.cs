namespace FlowX.Compiler.Diagnostics;

/// <summary>
/// Why a state-bag contract cannot be journaled, and the property keys that carry one from
/// analysis to the pipeline that decides which applies.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The same two-stage shape <c>FLOWX1024</c> uses, and for the identical reason.</strong>
/// Whether a contract can be journaled turns on two facts. The flow's profile is one, and
/// <c>FlowAnalyzer</c> knows it. The other — whether exactly one <c>JsonSerializerContext</c>
/// in this compilation declares the contract — is a question about every tree in the build,
/// and a per-flow transform that walked them all to answer it would trade the generator's
/// incrementality for a diagnostic. So analysis raises a provisional carrying the contract in
/// its properties, and <c>FlowPlanGenerator.Produce</c> drops it or restates it.
/// </para>
/// <para>
/// The provisional is never reported. It reads as what it is if it ever escapes, which is the
/// point of not leaving the reason empty.
/// </para>
/// </remarks>
internal static class StateBagReasons
{
    /// <summary>Property key carrying the state-bag contract's fully qualified name.</summary>
    public const string ContractProperty = "flowx.state.contract";

    /// <summary>Property key carrying the contract's simple name, for the message.</summary>
    public const string NameProperty = "flowx.state.name";

    /// <summary>Property key carrying the flow id the contract belongs to.</summary>
    /// <remarks>
    /// A <c>Diagnostic</c>'s message arguments are fixed at creation and cannot be read back,
    /// so a restated diagnostic has to be handed every argument again. Carrying the id here is
    /// what stops the pipeline recovering it by parsing the provisional's own message.
    /// </remarks>
    public const string FlowProperty = "flowx.state.flow";

    /// <summary>The placeholder analysis raises before the decision is made.</summary>
    public const string Provisional = "the build has not yet decided whether it can be journaled";

    /// <summary>No single serialiser context in the compilation declares the contract.</summary>
    /// <remarks>
    /// <c>{0}</c> is the contract's fully qualified name. "No single" rather than "none": two
    /// contexts declaring one contract is the same answer as none, because picking the first
    /// of several would make the stored shape depend on file order — the rule
    /// <c>EndpointEmitter</c> and <c>FLOWX1024</c> already follow.
    /// </remarks>
    public const string NoSerializerContextFormat =
        "no single source-generated JsonSerializerContext in this compilation declares " +
        "[JsonSerializable(typeof({0}))], so the journal cannot record it without " +
        "reflection — add it to one";
}
