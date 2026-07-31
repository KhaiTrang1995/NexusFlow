namespace FlowX;

/// <summary>
/// A monotonically increasing ownership token, issued by <see cref="ILeaseStore"/> at every
/// acquisition and checked on every <see cref="IFlowJournal"/> write.
/// </summary>
/// <remarks>
/// <para>
/// This is the type ADR-0006 rests on. A lease with a TTL and no token is the classic
/// split-brain bug: a node that pauses past its TTL — a GC pause, a partition, a stopped
/// container — wakes up believing it still owns the instance and writes stale state. The
/// token makes that impossible without reference to any clock: the journal remembers the
/// highest token it has been shown for an instance and refuses anything below it, so a
/// zombie's write is rejected no matter how long it was gone.
/// </para>
/// <para>
/// A <see cref="long"/> rather than a <see cref="Guid"/> or a timestamp, because the whole
/// value of the token is that it is <em>ordered</em>. Two tokens must be comparable by a
/// store that has never seen either before, which a random id cannot offer and a clock can
/// only offer as far as clock skew allows.
/// </para>
/// </remarks>
/// <seealso cref="IFlowJournal.FenceAsync"/>
public readonly record struct FencingToken : IComparable<FencingToken>
{
    /// <summary>Creates a token from the value a lease store issued.</summary>
    /// <param name="value">The issued value. Must be positive; zero is <see cref="None"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public FencingToken(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);

        Value = value;
    }

    /// <summary>The issued value. Strictly increasing per instance.</summary>
    public long Value { get; }

    /// <summary>
    /// The token an instance has before anyone has acquired a lease on it.
    /// </summary>
    /// <remarks>
    /// Zero rather than a nullable, so a journal's fence comparison is a plain
    /// <c>&gt;=</c> against a value that is always present. Every issued token is at least
    /// one, so <see cref="None"/> can never win a comparison against a real one.
    /// </remarks>
    public static FencingToken None => default;

    /// <summary>Whether this is the <see cref="None"/> token.</summary>
    public bool IsNone => Value == 0;

    /// <summary>The next token in the sequence.</summary>
    public FencingToken Next() => new(Value + 1);

    /// <inheritdoc />
    public int CompareTo(FencingToken other) => Value.CompareTo(other.Value);

    /// <summary>Whether the left token was issued before the right.</summary>
    public static bool operator <(FencingToken left, FencingToken right) => left.Value < right.Value;

    /// <summary>Whether the left token was issued after the right.</summary>
    public static bool operator >(FencingToken left, FencingToken right) => left.Value > right.Value;

    /// <summary>Whether the left token was issued no later than the right.</summary>
    public static bool operator <=(FencingToken left, FencingToken right) => left.Value <= right.Value;

    /// <summary>Whether the left token was issued no earlier than the right.</summary>
    public static bool operator >=(FencingToken left, FencingToken right) => left.Value >= right.Value;

    /// <inheritdoc />
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
