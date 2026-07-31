namespace FlowX.Runtime;

/// <summary>
/// What each element of a <c>ForEach</c> declared with
/// <c>ContinueOnError = true</c> produced.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart of <see cref="ParallelOutcome"/>, and for the same reason. A loop that
/// keeps going after a failure has to say <em>something</em> about what failed, or the
/// flow silently proceeds as though every element worked — which is the one outcome
/// nobody would choose deliberately. So the engine writes one of these into the state bag
/// when such a loop finishes, and the step after the loop reads
/// <c>ctx.Get&lt;ForEachOutcome&gt;()</c> and decides what a partial success means. That
/// is a business question the engine has no standing to answer.
/// </para>
/// <para>
/// <strong>Only <c>ContinueOnError = true</c> writes one.</strong> The default stops at
/// the first failing element and fails the flow with that element's own error, so there is
/// no later step to read an outcome and the allocation would be waste on a path that has
/// already failed.
/// </para>
/// <para>
/// <strong>Two such loops in one flow keep the later one</strong>, exactly as two
/// <c>AllSettled</c> forks do: the state bag is keyed by type, so the second write replaces
/// the first. <see cref="StepIndex"/> is here rather than implied so a reader can tell
/// which loop it is holding.
/// </para>
/// </remarks>
public sealed class ForEachOutcome
{
    private readonly Error?[] _elements;

    internal ForEachOutcome(int stepIndex, Error?[] elements)
    {
        StepIndex = stepIndex;
        _elements = elements;
    }

    /// <summary>The flat index of the loop that produced this, matching the plan and traces.</summary>
    public int StepIndex { get; }

    /// <summary>How many elements the loop ran for.</summary>
    public int ElementCount => _elements.Length;

    /// <summary>How many elements completed every step of the body.</summary>
    public int SucceededCount
    {
        get
        {
            var count = 0;

            foreach (var error in _elements)
            {
                if (error is null)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>True when every element completed.</summary>
    public bool AllSucceeded => SucceededCount == _elements.Length;

    /// <summary>True when no element completed.</summary>
    public bool AllFailed => _elements.Length > 0 && SucceededCount == 0;

    /// <summary>The error element <paramref name="element"/> ended with, or <c>null</c> when it completed.</summary>
    /// <param name="element">Zero-based position in the collection the selector produced.</param>
    /// <exception cref="ArgumentOutOfRangeException">There is no such element.</exception>
    public Error? ErrorFor(int element)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(element);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(element, _elements.Length);

        return _elements[element];
    }

    /// <summary>The errors, in element order, skipping the elements that succeeded.</summary>
    public IEnumerable<Error> Errors
    {
        get
        {
            foreach (var error in _elements)
            {
                if (error is not null)
                {
                    yield return error;
                }
            }
        }
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"foreach step {StepIndex}: {SucceededCount}/{_elements.Length} element(s) succeeded";
}
