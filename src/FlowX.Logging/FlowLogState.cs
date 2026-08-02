using System.Collections;

namespace FlowX.Logging;

/// <summary>
/// A log record's fields, in the shape a structured sink reads them: an
/// <c>IReadOnlyList&lt;KeyValuePair&lt;string, object?&gt;&gt;</c> whose
/// <see cref="ToString"/> is the constant message.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This shape is what makes the record structured rather than formatted.</strong>
/// <c>Microsoft.Extensions.Logging</c> asks a state object for its fields through exactly this
/// interface — it is the same contract the compiler-generated state behind
/// <c>LogWarning("… {OrderId}", id)</c> implements — so a JSON sink emits one property per field
/// and a console sink falls back to <see cref="ToString"/>.
/// </para>
/// <para>
/// <strong><see cref="ToString"/> returns the message unchanged, and that is the point.</strong>
/// <a href="../../../docs/12-Observability.md">12-Observability</a> §4 requires "Data as fields,
/// never interpolated into the message", so there are no placeholders to fill: the message is a
/// constant from <c>FlowXLog</c> and formatting it can only return it. A state that rendered its
/// fields into the message here would defeat the whole discipline one layer below the sink,
/// where nobody would look for it.
/// </para>
/// </remarks>
/// <param name="Message">The constant message.</param>
/// <param name="Fields">The record's fields, keyed by §2's frozen attribute names.</param>
public readonly record struct FlowLogState(
    string Message, IReadOnlyList<KeyValuePair<string, object?>> Fields)
    : IReadOnlyList<KeyValuePair<string, object?>>
{
    /// <inheritdoc />
    public int Count => Fields.Count;

    /// <inheritdoc />
    public KeyValuePair<string, object?> this[int index] => Fields[index];

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => Fields.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>The constant message, never the fields rendered into it.</summary>
    /// <returns>The message.</returns>
    public override string ToString() => Message;
}
