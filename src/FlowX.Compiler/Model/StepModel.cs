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
public sealed class StepModel
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
    public string? CapabilityTypeName { get; private set; }

    /// <summary>Business identity read from the capability's <c>[Capability]</c> attribute.</summary>
    public string? CapabilityId { get; private set; }

    /// <summary>Contract version from <c>[Capability]</c>.</summary>
    public string? CapabilityVersion { get; private set; }

    /// <summary>Whether the capability declared itself idempotent. Gates retry policies.</summary>
    public bool IsIdempotent { get; private set; }

    /// <summary>Declared side effects, in declaration order.</summary>
    public string[] SideEffects { get; private set; } = System.Array.Empty<string>();

    /// <summary>Fully-qualified compensation type from <c>.CompensateWith&lt;T&gt;()</c>.</summary>
    public string? CompensationTypeName { get; private set; }

    /// <summary>Business identity of the compensation.</summary>
    public string? CompensationId { get; private set; }

    /// <summary>Contract version of the compensation.</summary>
    public string? CompensationVersion { get; private set; }

    /// <summary>Event identity for an <see cref="StepKindModel.Emit"/> step.</summary>
    public string? EventType { get; private set; }

    /// <summary>Signal identity for an <see cref="StepKindModel.AwaitSignal"/> step.</summary>
    public string? SignalType { get; private set; }

    /// <summary>Named policy set applied via <c>.WithPolicy(...)</c>.</summary>
    public string? PolicySetName { get; private set; }

    /// <summary><c>file:line</c> of the call, so a diagnostic points at the right chain link.</summary>
    public string? Location { get; private set; }

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
        string? location = null)
    {
        return new StepModel(index, StepKindModel.Capability)
        {
            CapabilityTypeName = capabilityTypeName,
            CapabilityId = capabilityId,
            CapabilityVersion = capabilityVersion,
            IsIdempotent = isIdempotent,
            SideEffects = sideEffects ?? System.Array.Empty<string>(),
            Location = location,
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
    /// <remarks>
    /// A copy rather than a mutation: the analysis layer builds steps as it walks
    /// the chain, and <c>.CompensateWith</c> attaches to the step already built.
    /// Returning a new instance keeps the model immutable from the emitter's point
    /// of view, which is what makes emitter tests reproducible.
    /// </remarks>
    public StepModel WithCompensation(string typeName, string id, string version)
    {
        return new StepModel(Index, Kind)
        {
            CapabilityTypeName = CapabilityTypeName,
            CapabilityId = CapabilityId,
            CapabilityVersion = CapabilityVersion,
            IsIdempotent = IsIdempotent,
            SideEffects = SideEffects,
            EventType = EventType,
            SignalType = SignalType,
            PolicySetName = PolicySetName,
            Location = Location,
            CompensationTypeName = typeName,
            CompensationId = id,
            CompensationVersion = version,
        };
    }

    /// <summary>Returns a copy carrying a named policy set.</summary>
    public StepModel WithPolicy(string policySetName)
    {
        return new StepModel(Index, Kind)
        {
            CapabilityTypeName = CapabilityTypeName,
            CapabilityId = CapabilityId,
            CapabilityVersion = CapabilityVersion,
            IsIdempotent = IsIdempotent,
            SideEffects = SideEffects,
            EventType = EventType,
            SignalType = SignalType,
            Location = Location,
            CompensationTypeName = CompensationTypeName,
            CompensationId = CompensationId,
            CompensationVersion = CompensationVersion,
            PolicySetName = policySetName,
        };
    }
}
