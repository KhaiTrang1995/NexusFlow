namespace FlowX.Compiler.Model;

/// <summary>What one call in a <c>Define</c> chain declared.</summary>
/// <remarks>
/// Only the kinds a linear P0 flow can contain. Branching kinds arrive with the DSL
/// surface that can express them, in P1 — modelling them now would be shapes
/// nothing can produce and no test can exercise.
/// </remarks>
public enum StepKindModel
{
    /// <summary><c>.Step&lt;TCapability&gt;()</c></summary>
    Capability = 0,

    /// <summary><c>.Emit&lt;TEvent&gt;(...)</c></summary>
    Emit = 1,

    /// <summary><c>.AwaitSignal&lt;TSignal&gt;(...)</c></summary>
    AwaitSignal = 2,
}

/// <summary>One step of a declared flow, expressed without Roslyn types.</summary>
/// <remarks>
/// <para>
/// A <c>record</c> specifically so <see cref="WithCompensation"/> and
/// <see cref="WithPolicy"/> can use <c>with</c> instead of copying every property by
/// hand. The hand-written versions listed a dozen properties each, and adding a field
/// meant remembering all three places — which failed the first time it was tried, in
/// exactly the way that kind of duplication always fails.
/// </para>
/// <para>
/// Immutable from the emitter's point of view: the analysis layer builds a step, then
/// <c>.CompensateWith</c> produces a new one carrying the compensation. That is what
/// makes emitter output reproducible from a model alone.
/// </para>
/// </remarks>
public sealed record StepModel
{
    private StepModel(int index, StepKindModel kind)
    {
        Index = index;
        Kind = kind;
    }

    /// <summary>Position in the chain, contiguous from zero.</summary>
    public int Index { get; }

    /// <summary>What the step does.</summary>
    public StepKindModel Kind { get; }

    /// <summary>Fully-qualified capability type, or <c>null</c> for non-capability kinds.</summary>
    public string? CapabilityTypeName { get; private init; }

    /// <summary>Business identity read from the capability's <c>[Capability]</c> attribute.</summary>
    public string? CapabilityId { get; private init; }

    /// <summary>Contract version from <c>[Capability]</c>.</summary>
    public string? CapabilityVersion { get; private init; }

    /// <summary>Whether the capability declared itself idempotent. Gates retry policies.</summary>
    public bool IsIdempotent { get; private init; }

    /// <summary>Declared side effects, in declaration order.</summary>
    public string[] SideEffects { get; private init; } = System.Array.Empty<string>();

    /// <summary>The capability's declared authorisation stance. Reaches the manifest.</summary>
    public string? AuthorizationMode { get; private init; }

    /// <summary>The capability's input contract, fully qualified. Required by the manifest schema.</summary>
    public string? CapabilityInput { get; private init; }

    /// <summary>The capability's output contract, fully qualified.</summary>
    public string? CapabilityOutput { get; private init; }

    /// <summary>
    /// The compensation declared by <c>.CompensateWith&lt;T&gt;()</c>, or <c>null</c>.
    /// </summary>
    /// <remarks>
    /// A whole <see cref="StepModel"/>, not three loose strings. It used to be the loose
    /// strings — type, id, version — and the consequence was that the manifest listed the
    /// compensation as a reference on the step and never as a capability in its own right.
    /// Its authorisation stance, side effects and idempotency were invisible, so
    /// <c>flowx diff</c> could not see a breaking change to one. Carrying the full model
    /// makes the compensation the same kind of thing as any other capability, which is
    /// what it always was.
    /// </remarks>
    public StepModel? Compensation { get; private init; }

    /// <summary>Fully-qualified compensation type, or <c>null</c>.</summary>
    public string? CompensationTypeName => Compensation?.CapabilityTypeName;

    /// <summary>Business identity of the compensation.</summary>
    public string? CompensationId => Compensation?.CapabilityId;

    /// <summary>Contract version of the compensation.</summary>
    public string? CompensationVersion => Compensation?.CapabilityVersion;

    /// <summary>Event identity for an <see cref="StepKindModel.Emit"/> step.</summary>
    public string? EventType { get; private init; }

    /// <summary>Signal identity for an <see cref="StepKindModel.AwaitSignal"/> step.</summary>
    public string? SignalType { get; private init; }

    /// <summary>Named policy set applied via <c>.WithPolicy(...)</c>.</summary>
    public string? PolicySetName { get; private init; }

    /// <summary><c>file:line</c> of the call, so a diagnostic points at the right chain link.</summary>
    public string? Location { get; private init; }

    /// <summary>True when the step declared a compensation.</summary>
    public bool IsCompensable => CompensationTypeName != null;

    /// <summary>Models a <c>.Step&lt;TCapability&gt;()</c> call.</summary>
    public static StepModel Capability(
        int index,
        string capabilityTypeName,
        string capabilityId,
        string capabilityVersion,
        bool isIdempotent,
        string[]? sideEffects = null,
        string? location = null,
        string? authorizationMode = null,
        string? capabilityInput = null,
        string? capabilityOutput = null)
    {
        return new StepModel(index, StepKindModel.Capability)
        {
            CapabilityTypeName = capabilityTypeName,
            CapabilityId = capabilityId,
            CapabilityVersion = capabilityVersion,
            IsIdempotent = isIdempotent,
            SideEffects = sideEffects ?? System.Array.Empty<string>(),
            Location = location,
            AuthorizationMode = authorizationMode,
            CapabilityInput = capabilityInput,
            CapabilityOutput = capabilityOutput,
        };
    }

    /// <summary>Models an <c>.Emit&lt;TEvent&gt;(...)</c> call.</summary>
    public static StepModel Emit(int index, string eventType, string? location = null)
    {
        return new StepModel(index, StepKindModel.Emit)
        {
            EventType = eventType,
            Location = location,
        };
    }

    /// <summary>Models an <c>.AwaitSignal&lt;TSignal&gt;(...)</c> call.</summary>
    public static StepModel AwaitSignal(int index, string signalType, string? location = null)
    {
        return new StepModel(index, StepKindModel.AwaitSignal)
        {
            SignalType = signalType,
            Location = location,
        };
    }

    /// <summary>Returns a copy carrying a compensation.</summary>
    /// <param name="compensation">
    /// The compensating capability, modelled exactly as a step is — build it with
    /// <see cref="Capability"/> so it reaches the manifest with its full metadata.
    /// </param>
    public StepModel WithCompensation(StepModel compensation) => this with
    {
        Compensation = compensation,
    };

    /// <summary>Returns a copy carrying a named policy set.</summary>
    public StepModel WithPolicy(string policySetName) => this with
    {
        PolicySetName = policySetName,
    };
}
