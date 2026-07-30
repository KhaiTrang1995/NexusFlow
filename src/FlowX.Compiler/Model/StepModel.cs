using System.Collections.Generic;

namespace FlowX.Compiler.Model;

/// <summary>What one call in a <c>Define</c> chain declared.</summary>
/// <remarks>
/// The remaining branching kinds — switch, parallel, for-each, sub-flow — arrive with
/// the DSL surface that can express them. Modelling them now would be shapes nothing can
/// produce and no test can exercise.
/// </remarks>
public enum StepKindModel
{
    /// <summary><c>.Step&lt;TCapability&gt;()</c></summary>
    Capability = 0,

    /// <summary><c>.Emit&lt;TEvent&gt;(...)</c></summary>
    Emit = 1,

    /// <summary><c>.AwaitSignal&lt;TSignal&gt;(...)</c></summary>
    AwaitSignal = 2,

    /// <summary><c>.When(predicate, then)</c>, with the <c>.Otherwise(...)</c> that may follow it.</summary>
    Condition = 3,
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

    /// <summary>
    /// The policy kinds that set declares — <c>Retry</c>, <c>Cache</c>, and so on —
    /// ordinally sorted.
    /// </summary>
    /// <remarks>
    /// The name alone said nothing about the contents, which is why FLOWX1014 and
    /// FLOWX1018 could not fire: both ask a question about what is in the set, not what
    /// it is called. Empty when the set could not be resolved — one built at run time
    /// cannot be inspected at compile time, and a guess would produce a diagnostic
    /// nobody could act on.
    /// </remarks>
    public string[] PolicyKinds { get; private init; } = System.Array.Empty<string>();

    /// <summary><c>file:line</c> of the call, so a diagnostic points at the right chain link.</summary>
    public string? Location { get; private init; }

    /// <summary>
    /// Source text of the <c>.When(...)</c> predicate, copied verbatim, or <c>null</c> for
    /// every other kind.
    /// </summary>
    /// <remarks>
    /// Verbatim for the same reason the <c>.Return(...)</c> projection is — see
    /// <c>FlowModel.ReturnProjection</c>. Reconstructing an arbitrary C# expression means
    /// re-rendering every form the language has, and being wrong on the first one nobody
    /// thought of.
    /// </remarks>
    public string? Predicate { get; private init; }

    /// <summary><c>file:line</c> of the predicate expression, for its <c>#line</c> directive.</summary>
    public string? PredicateLocation { get; private init; }

    /// <summary>Steps declared in the <c>then</c> block, in declaration order.</summary>
    public IReadOnlyList<StepModel> Then { get; private init; } = System.Array.Empty<StepModel>();

    /// <summary>Steps declared in the <c>.Otherwise(...)</c> block. Empty when there is none.</summary>
    public IReadOnlyList<StepModel> Otherwise { get; private init; } = System.Array.Empty<StepModel>();

    /// <summary>
    /// Where control continues when the predicate does not hold.
    /// </summary>
    /// <remarks>
    /// The flat layout a conditional compiles to is
    /// <c>branch · then… · [jump] · otherwise…</c>, so the true path is the branch's own
    /// index plus one and only the false path needs to be recorded. See
    /// <c>StepNode.Target</c>, which is what this becomes.
    /// </remarks>
    public int FalseTarget { get; private init; }

    /// <summary>
    /// Index of the jump that closes the <c>then</c> block, or <c>null</c> when there is
    /// no <c>Otherwise</c> to skip over.
    /// </summary>
    public int? JumpIndex { get; private init; }

    /// <summary>
    /// Index the whole conditional joins at: the first step after both blocks, which is
    /// also the jump's target.
    /// </summary>
    /// <remarks>
    /// May be one past the last step of the flow, when the conditional is the last thing
    /// the chain declares. That is the layout <c>StepGraph</c> deliberately permits.
    /// </remarks>
    public int JoinIndex { get; private init; }

    /// <summary>
    /// The flat index immediately after everything this step occupies.
    /// </summary>
    /// <remarks>
    /// One past its own index for a plain step; the join index for a conditional, which
    /// occupies its branch, both blocks and possibly a jump. This is what lets a nested
    /// conditional be laid out without the enclosing block having to know how deep it goes.
    /// </remarks>
    public int NextIndex => Kind == StepKindModel.Condition ? JoinIndex : Index + 1;

    /// <summary>True when the step declared a compensation.</summary>
    public bool IsCompensable => CompensationTypeName != null;

    /// <summary>
    /// This step and every step nested inside it, in flat-layout order.
    /// </summary>
    /// <remarks>
    /// Pre-order, which for this shape <em>is</em> the order the flat step array runs in:
    /// a conditional comes before its <c>then</c> block, which comes before its
    /// <c>Otherwise</c> block. Everything that used to read <c>flow.Steps</c> as the whole
    /// flow — the descriptors, the dispatcher's switch, the manifest's capability list —
    /// has to read this instead, or a capability invoked inside a branch is invisible to it.
    /// </remarks>
    public IEnumerable<StepModel> SelfAndNested
    {
        get
        {
            yield return this;

            foreach (var nested in Then)
            {
                foreach (var step in nested.SelfAndNested)
                {
                    yield return step;
                }
            }

            foreach (var nested in Otherwise)
            {
                foreach (var step in nested.SelfAndNested)
                {
                    yield return step;
                }
            }
        }
    }

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

    /// <summary>Models a <c>.When(predicate, then)</c> and the <c>.Otherwise(...)</c> that may follow.</summary>
    /// <param name="index">Flat index of the branch itself.</param>
    /// <param name="predicate">The predicate's source text, copied verbatim.</param>
    /// <param name="then">Steps of the <c>then</c> block, already carrying their flat indices.</param>
    /// <param name="otherwise">Steps of the <c>Otherwise</c> block, or empty when there is none.</param>
    /// <param name="predicateLocation"><c>file:line</c> of the predicate expression.</param>
    /// <param name="location"><c>file:line</c> of the <c>.When</c> call.</param>
    /// <remarks>
    /// The three layout numbers — <see cref="FalseTarget"/>, <see cref="JumpIndex"/>,
    /// <see cref="JoinIndex"/> — are derived here from the blocks rather than passed in.
    /// They are a consequence of the layout, not an independent decision, and a caller
    /// able to supply a target that disagreed with the blocks it also supplied could
    /// produce a plan that skips or repeats real steps.
    /// </remarks>
    public static StepModel Condition(
        int index,
        string predicate,
        IReadOnlyList<StepModel> then,
        IReadOnlyList<StepModel>? otherwise = null,
        string? predicateLocation = null,
        string? location = null)
    {
        var thenSteps = then ?? (IReadOnlyList<StepModel>)System.Array.Empty<StepModel>();
        var otherwiseSteps = otherwise ?? (IReadOnlyList<StepModel>)System.Array.Empty<StepModel>();

        var thenEnd = thenSteps.Count == 0 ? index + 1 : thenSteps[thenSteps.Count - 1].NextIndex;

        // With no alternative there is nothing to skip, so no jump is emitted and the
        // false path lands exactly where the `then` block ended. Emitting a jump anyway
        // would leave a step in the graph that does nothing but cost an index.
        var hasOtherwise = otherwiseSteps.Count > 0;

        var join = hasOtherwise
            ? otherwiseSteps[otherwiseSteps.Count - 1].NextIndex
            : thenEnd;

        return new StepModel(index, StepKindModel.Condition)
        {
            Predicate = predicate,
            PredicateLocation = predicateLocation,
            Then = thenSteps,
            Otherwise = otherwiseSteps,
            JumpIndex = hasOtherwise ? thenEnd : (int?)null,
            FalseTarget = hasOtherwise ? thenEnd + 1 : thenEnd,
            JoinIndex = join,
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
    /// <param name="policySetName">The set as it was written at the call site.</param>
    /// <param name="policyKinds">What the set declares, or empty when it could not be read.</param>
    public StepModel WithPolicy(string policySetName, string[]? policyKinds = null) => this with
    {
        PolicySetName = policySetName,
        PolicyKinds = policyKinds ?? System.Array.Empty<string>(),
    };
}
