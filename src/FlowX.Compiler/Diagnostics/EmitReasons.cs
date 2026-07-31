namespace FlowX.Compiler.Diagnostics;

/// <summary>
/// The reasons an <c>.Emit&lt;TEvent&gt;(...)</c> step can still stage nothing, and the
/// property keys that carry one from analysis to the pipeline that decides which applies.
/// </summary>
/// <remarks>
/// <para>
/// <strong>FLOWX1024 is now two narrow rules wearing one id, and both are fixable in user
/// code.</strong> Until WP-56's follow-on it was one broad one — "nothing anywhere publishes
/// an emitted event" — whose only honest advice was to delete the step. The engine stages the
/// event now and <c>PostgresOutboxPublisher</c> drains it, so what is left is the two
/// conditions under which the chain still cannot start.
/// </para>
/// <para>
/// One descriptor rather than two ids because the question a reader is asking is the same in
/// both cases — <em>will my consumer receive this?</em> — and the answer is no for a reason
/// the message names. Two ids would also mean two suppression decisions for one gap.
/// </para>
/// </remarks>
internal static class EmitReasons
{
    /// <summary>Property key carrying the event contract's fully qualified name.</summary>
    public const string ContractProperty = "flowx.event.contract";

    /// <summary>Property key carrying the event contract's simple name, for the message.</summary>
    public const string NameProperty = "flowx.event.name";

    /// <summary>
    /// The placeholder on the diagnostic analysis raises before the decision is made.
    /// </summary>
    /// <remarks>
    /// Never reported: <c>FlowPlanGenerator.Produce</c> either drops the provisional
    /// diagnostic or replaces it with one carrying a real reason. It reads as what it is if
    /// it ever escapes, which is the point of not leaving it empty.
    /// </remarks>
    public const string Provisional = "the build has not yet decided whether it can be staged";

    /// <summary>The flow is <c>Ephemeral</c>, so there is no transaction to stage into.</summary>
    public const string Ephemeral =
        "the flow declares Profile = Ephemeral, so it keeps no journal and there is no " +
        "transaction for the event to be staged in — declare Profile = Durable to publish it";

    /// <summary>No single serialiser context in the compilation declares the contract.</summary>
    /// <remarks>
    /// <c>{0}</c> is the contract's fully qualified name. "No single" rather than "none":
    /// two contexts declaring the same contract is the same answer as none, for the reason
    /// <c>EndpointEmitter</c> gives — picking the first would make the wire format depend on
    /// file order.
    /// </remarks>
    public const string NoSerializerContextFormat =
        "no single source-generated JsonSerializerContext in this compilation declares " +
        "[JsonSerializable(typeof({0}))], so the event body cannot be written without " +
        "reflection — add it to one";
}
