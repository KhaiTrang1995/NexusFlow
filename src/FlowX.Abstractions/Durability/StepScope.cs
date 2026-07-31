using System.Globalization;

namespace FlowX;

/// <summary>
/// Which iteration of which loop a step ran in — the part of a journal key that a straight
/// line of steps does not need and a <c>ForEach</c> cannot do without.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This exists because <c>(instance, step)</c> is not unique.</strong> A
/// <c>ForEach</c> re-enters one range of the flat step array once per element, so a
/// 500-element loop writes step 7 five hundred times. An append-only table cannot overwrite
/// a row, so without the scope in the key the second element is a primary-key violation and
/// the flow cannot be journaled at all.
/// </para>
/// <para>
/// <c>CompensationStack</c> met the same problem first and answered it the same way: its
/// duplicate check moved from <c>index</c> to <c>(index, scope)</c>, because keying on the
/// index alone threw on the second element. The journal is not allowed to be less precise
/// than the compensation stack that has to undo it.
/// </para>
/// <para>
/// Rendered as text — the empty string for the flow body, <c>7</c> for the eighth element,
/// <c>7/2</c> for an element of a loop nested inside it — so a store persists one column and
/// an operator reading a row can see where a step ran without joining anything.
/// </para>
/// </remarks>
public readonly record struct StepScope
{
    private const char Separator = '/';

    /// <summary>Null for the flow body; otherwise a non-empty canonical path.</summary>
    private readonly string? _text;

    private StepScope(string? text) => _text = text;

    /// <summary>The flow's own scope: not inside any iteration.</summary>
    public static StepScope Root => default;

    /// <summary>The canonical text, empty for <see cref="Root"/>.</summary>
    public string Text => _text ?? string.Empty;

    /// <summary>Whether this is the flow body rather than an iteration.</summary>
    public bool IsRoot => _text is null;

    /// <summary>How many loops are open around a step in this scope.</summary>
    public int Depth
    {
        get
        {
            if (_text is null)
            {
                return 0;
            }

            var depth = 1;

            foreach (var character in _text)
            {
                if (character == Separator)
                {
                    depth++;
                }
            }

            return depth;
        }
    }

    /// <summary>The scope of one element of a loop entered from this one.</summary>
    /// <param name="index">The zero-based element index.</param>
    /// <exception cref="ArgumentOutOfRangeException">The index is negative.</exception>
    public StepScope Element(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        var element = index.ToString(CultureInfo.InvariantCulture);

        return new StepScope(_text is null ? element : _text + Separator + element);
    }

    /// <summary>
    /// Reconstructs a scope from the text a store persisted.
    /// </summary>
    /// <param name="text">The stored value. Null or empty is <see cref="Root"/>.</param>
    /// <remarks>
    /// The read half of the round trip, and the reason <see cref="Text"/> is a single column
    /// rather than a structure: a store persists what it was given and hands the same string
    /// back, and neither end has to agree on a nesting representation.
    /// </remarks>
    /// <exception cref="ArgumentException">The text is not a canonical scope path.</exception>
    public static StepScope Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Root;
        }

        foreach (var segment in text.Split(Separator))
        {
            if (segment.Length == 0 || !segment.All(char.IsAsciiDigit))
            {
                throw new ArgumentException(
                    $"'{text}' is not a scope path. Expected the empty string for the flow " +
                    "body, or slash-separated element indices such as '7' or '7/2'.",
                    nameof(text));
            }
        }

        return new StepScope(text);
    }

    /// <inheritdoc />
    public override string ToString() => Text;
}
