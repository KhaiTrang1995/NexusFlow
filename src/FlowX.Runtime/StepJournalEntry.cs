namespace FlowX.Runtime;

/// <summary>
/// What the journal records about one finished step: the value it produced, the flow's state
/// bag as it now stands, and the event it emitted — each wrapped in the type that redacts.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The dispatcher supplies this, not the engine, and that is the same division of
/// labour every other typed thing follows.</strong> The step loop holds a
/// <c>Dictionary&lt;Type, object&gt;</c> and knows no contract types — that is the whole
/// basis of <see cref="IStepDispatcher"/> — so it cannot name a
/// <c>JsonTypeInfo&lt;T&gt;</c> for anything in it. Generated code can, and
/// <c>JournalPayload.Of</c> requires one, which is what keeps the write path
/// reflection-free, trim-safe and NativeAOT-safe (constraint C2) and what makes membership
/// of the generated JSON context a compile-time fact rather than a convention
/// (ADR-0015 commitment 5).
/// </para>
/// <para>
/// <strong>Every payload carries its flow's <c>SensitiveMembers</c>.</strong> The journal is
/// a new sink for <c>[Sensitive]</c> values and it lands three phases before the fitness
/// function that guards sinks, so the redaction is structural rather than remembered: there
/// is no accessor for the value on <see cref="JournalPayload"/>, and its only exit —
/// <see cref="JournalPayload.ToJson"/> — replaces every declared member with
/// <see cref="JournalPayload.Redacted"/>. A store is handed the payload, never a blob.
/// </para>
/// <para>
/// <strong>The emitted event is on this type for exactly that reason.</strong> An event body
/// is a second sink — a broker fans it out to every consumer, where a journal row at least
/// stays in one table — so it travels as an <see cref="OutboxWrite"/> whose
/// <see cref="OutboxWrite.Payload"/> is a <see cref="JournalPayload"/>, and inherits the
/// control rather than re-implementing it. There is no overload here that takes a string.
/// </para>
/// <para>
/// A <c>readonly struct</c>, so describing a step costs nothing beyond the payloads
/// themselves, and <c>default</c> is the honest answer for a dispatcher with nothing to say.
/// </para>
/// </remarks>
public readonly struct StepJournalEntry : IEquatable<StepJournalEntry>
{
    private readonly JournalPayload? _result;
    private readonly JournalPayload? _stateBag;
    private readonly OutboxWrite? _event;

    private StepJournalEntry(JournalPayload? result, JournalPayload? stateBag, OutboxWrite? emitted)
    {
        _result = result;
        _stateBag = stateBag;
        _event = emitted;
    }

    /// <summary>The step produced nothing the journal records.</summary>
    public static StepJournalEntry Nothing => default;

    /// <summary>Describes a finished step.</summary>
    /// <param name="result">
    /// What the step produced, wrapped with the generated <c>JsonTypeInfo</c> and the flow's
    /// <c>SensitiveMembers</c>.
    /// </param>
    /// <param name="stateBag">
    /// The flow's state bag after the step — the snapshot that bounds a resume scan, and
    /// what a resumed instance is rehydrated from.
    /// </param>
    public static StepJournalEntry Of(JournalPayload? result, JournalPayload? stateBag) =>
        new(result, stateBag, null);

    /// <summary>Describes a finished step that also emitted an event.</summary>
    /// <param name="result">What the step produced.</param>
    /// <param name="stateBag">The flow's state bag after the step.</param>
    /// <param name="emitted">
    /// The event to stage in the same transaction as the step row. Its
    /// <see cref="OutboxWrite.Payload"/> carries the flow's <c>SensitiveMembers</c>, like
    /// every other payload here.
    /// </param>
    public static StepJournalEntry Of(
        JournalPayload? result, JournalPayload? stateBag, OutboxWrite? emitted) =>
        new(result, stateBag, emitted);

    /// <summary>
    /// Describes an <c>Emit</c> step: an event and nothing else.
    /// </summary>
    /// <param name="emitted">The event to stage with the step row.</param>
    /// <remarks>
    /// An <c>Emit</c> step invokes no capability, so it produces no result and writes nothing
    /// into the state bag. Spelling that as its own factory keeps the generated dispatcher
    /// from passing two nulls to say so.
    /// </remarks>
    public static StepJournalEntry OfEvent(OutboxWrite emitted) => new(null, null, emitted);

    /// <summary>The step's result as the journal should store it.</summary>
    public JournalPayload Result => _result ?? JournalPayload.Empty;

    /// <summary>The state bag as the journal should store it.</summary>
    public JournalPayload StateBag => _stateBag ?? JournalPayload.Empty;

    /// <summary>
    /// The event this step emitted, or <c>null</c> when it emitted none.
    /// </summary>
    /// <remarks>
    /// Read by the engine only for a plan whose <see cref="ExecutionPlan.HasEmit"/> is true,
    /// so a flow that emits nothing never pays for the ones that do.
    /// </remarks>
    public OutboxWrite? Event => _event;

    /// <summary>Whether there is nothing to write for this step.</summary>
    public bool IsEmpty => Result.IsEmpty && StateBag.IsEmpty && _event is null;

    /// <inheritdoc />
    public bool Equals(StepJournalEntry other) =>
        ReferenceEquals(_result, other._result) &&
        ReferenceEquals(_stateBag, other._stateBag) &&
        ReferenceEquals(_event, other._event);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is StepJournalEntry other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_result, _stateBag, _event);

    /// <summary>Compares two entries by the payloads they carry.</summary>
    public static bool operator ==(StepJournalEntry left, StepJournalEntry right) => left.Equals(right);

    /// <summary>Compares two entries by the payloads they carry.</summary>
    public static bool operator !=(StepJournalEntry left, StepJournalEntry right) => !left.Equals(right);
}
