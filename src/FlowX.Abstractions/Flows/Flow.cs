namespace FlowX;

/// <summary>
/// Declares a class as a flow. Everything here reaches <c>flowx.manifest.json</c>.
/// </summary>
/// <param name="id">Business identity in <c>&lt;domain&gt;.&lt;verb&gt;</c> form, e.g. <c>order.place</c>.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class FlowAttribute(string id) : Attribute
{
    /// <summary>Business identity.</summary>
    public string Id { get; } = id;

    /// <summary>SemVer of the flow. A durable instance keeps executing the version it started on.</summary>
    public string Version { get; init; } = "1.0.0";

    /// <summary>
    /// The cost/reliability trade-off. Defaults to <see cref="ExecutionProfile.Ephemeral"/> —
    /// durability is opt-in (ADR-0003).
    /// </summary>
    public ExecutionProfile Profile { get; init; } = ExecutionProfile.Ephemeral;

    /// <summary>Owning team, surfaced in the manifest for impact analysis and access review.</summary>
    public string? Owner { get; init; }
}

/// <summary>
/// The flow's absolute budget, set at trigger time and never reset by a retry.
/// </summary>
/// <param name="duration">ISO-8601 duration, e.g. <c>PT30S</c> or <c>P30D</c>.</param>
/// <remarks>
/// The analyzer (FLOWX1019) warns when step timeouts multiplied by retry attempts
/// exceed this value — the classic "3 retries × 30&#160;s timeout inside a 10&#160;s SLA"
/// incoherence.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class FlowDeadlineAttribute(string duration) : Attribute
{
    /// <summary>ISO-8601 duration.</summary>
    public string Duration { get; } = duration;
}

/// <summary>
/// The non-generic root of every flow.
/// </summary>
/// <remarks>
/// <para>
/// Exists for exactly one reason: <c>IFlowBuilder.SubFlow&lt;TFlow, TSubIn&gt;</c> has to
/// say <em>which kinds of type may be composed</em>, and it cannot name
/// <see cref="Flow{TIn, TOut}"/> — the child's own contracts are not the parent's and are
/// not inferable from the call. Without a constraint, <c>.SubFlow&lt;PaymentCapture,
/// …&gt;()</c> — a capability, not a flow — compiles, and the mistake surfaces as a
/// missing <c>Plan</c> member in generated code. With it, the mistake is an ordinary C#
/// error on the author's own line, which is the better diagnostic and costs no rule.
/// </para>
/// <para>
/// The constructor is <c>private protected</c>, so nothing outside this assembly can
/// derive from it directly. A flow is still declared by deriving from
/// <see cref="Flow{TIn, TOut}"/>; this type never appears in user code.
/// </para>
/// </remarks>
public abstract class Flow
{
    private protected Flow()
    {
    }
}

/// <summary>
/// Base class for every flow. A flow expresses <em>order, condition and recovery</em> —
/// nothing else. Business rules live in capabilities.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Define"/> is a <em>declaration</em>, not a script. It runs at most once,
/// and often zero times, because the compiler resolves the graph statically. It must be
/// pure: no I/O, no clock, no randomness.
/// </para>
/// <para>
/// Inheriting one flow from another is a build error (FLOWX1005) — it hides control
/// flow from the graph. Extract shared steps into a sub-flow instead.
/// </para>
/// </remarks>
/// <typeparam name="TIn">Input contract.</typeparam>
/// <typeparam name="TOut">Output contract.</typeparam>
public abstract class Flow<TIn, TOut> : Flow
{
    /// <summary>Declares the flow's graph. Called by the compiler and, at most once, by the runtime.</summary>
    /// <param name="flow">The builder to declare against.</param>
    protected abstract void Define(IFlowBuilder<TIn, TOut> flow);
}
