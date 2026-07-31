using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Security.Claims;

namespace FlowX.Runtime;

/// <summary>
/// The collection a <see cref="StepKind.ForEach"/> is about to walk, as the engine sees
/// it: a count, and a handle it never looks inside.
/// </summary>
/// <remarks>
/// <para>
/// <strong><see cref="Items"/> is <c>object</c> on purpose.</strong> The engine owns
/// control flow and knows no contract types — that is the whole basis of
/// <see cref="IStepDispatcher"/> — so it cannot hold an
/// <c>IReadOnlyList&lt;TItem&gt;</c>. It holds the reference, hands it back to the
/// generated dispatcher for each element, and the dispatcher, which does know
/// <c>TItem</c>, casts it back. Nothing is boxed: the handle is already a reference.
/// </para>
/// <para>
/// <strong><see cref="Count"/> is read once, before the first iteration.</strong> That is
/// what bounds the loop, and therefore what replaces the forward-target rule as the
/// termination argument for the one kind whose block runs more than once. A selector
/// returning a lazily-growing sequence could otherwise iterate forever, which is why the
/// DSL types the selector as <c>IReadOnlyList&lt;TItem&gt;</c> rather than
/// <c>IEnumerable&lt;TItem&gt;</c>.
/// </para>
/// <para>
/// A struct, so asking a flow what it is about to iterate costs nothing.
/// </para>
/// </remarks>
public readonly struct IterationSource : IEquatable<IterationSource>
{
    /// <summary>Creates a source over an already-materialised collection.</summary>
    /// <param name="items">The collection, opaque to the engine.</param>
    /// <param name="count">How many elements it holds.</param>
    public IterationSource(object? items, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        Items = items;
        Count = count;
    }

    /// <summary>An empty collection: nothing to iterate, and no handle to hold.</summary>
    public static IterationSource Empty => default;

    /// <summary>The collection, opaque to the engine and cast back by the dispatcher.</summary>
    public object? Items { get; }

    /// <summary>How many elements the body will run for.</summary>
    public int Count { get; }

    /// <inheritdoc />
    public bool Equals(IterationSource other) => ReferenceEquals(Items, other.Items) && Count == other.Count;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is IterationSource other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Items, Count);

    /// <summary>Compares two sources.</summary>
    public static bool operator ==(IterationSource left, IterationSource right) => left.Equals(right);

    /// <summary>Compares two sources.</summary>
    public static bool operator !=(IterationSource left, IterationSource right) => !left.Equals(right);
}

/// <summary>Builds the per-iteration view of a flow context.</summary>
/// <remarks>
/// Called from the generated dispatcher, which is the only code that knows what a
/// <c>TItem</c> is. The engine receives a plain <see cref="FlowContext"/> back and never
/// learns the element type — the same division of labour that keeps the step loop
/// reflection-free.
/// </remarks>
public static class IterationScope
{
    /// <summary>Creates the view an iteration's body runs under.</summary>
    /// <typeparam name="TItem">The element type, inferred at the call site.</typeparam>
    /// <param name="outer">The context the loop itself is running under.</param>
    /// <param name="item">The element this iteration is for.</param>
    public static FlowContext For<TItem>(FlowContext outer, TItem item)
    {
        ArgumentNullException.ThrowIfNull(outer);

        return new IterationScope<TItem>(outer, item);
    }
}

/// <summary>
/// A flow context with one extra slot: the element the current iteration is processing.
/// </summary>
/// <typeparam name="TItem">The element type.</typeparam>
/// <remarks>
/// <para>
/// <strong>This is the answer to "where does the current item live".</strong> It cannot
/// live in the flow's state bag, because that bag is keyed by <c>typeof(T)</c> and every
/// iteration would write the same key — with
/// <c>MaxDegreeOfParallelism &gt; 1</c> that is a race whose winner is arbitrary, and even
/// at 1 it leaves the last element visible to every step after the loop, which reads like
/// a value the flow produced when it is really a leftover. So the item does not go in the
/// bag at all: it goes in a scope that shadows it, for exactly the duration of one
/// iteration, on exactly the one type it is about.
/// </para>
/// <para>
/// <strong>Reads shadow, writes fall through.</strong> A body step reading its input gets
/// the element; a body step's <em>output</em> is written to the shared bag, because it is
/// a value the flow produced and the code after the loop may legitimately want it. The
/// consequence is worth stating plainly: with a concurrency bound above one, two
/// iterations writing the same output type still race, and the winner is whichever
/// finished last. That is the same modelling question FLOWX1013 asks of a
/// <c>Parallel</c>'s branches, and the runtime's job is only to make the failure mode a
/// wrong value rather than a corrupted dictionary — which the guarded state bag does.
/// </para>
/// <para>
/// <strong>Nesting works by chaining.</strong> An inner loop's scope wraps the outer's, so
/// a body two levels deep sees both elements, each on its own type. A collection of the
/// same element type nested inside itself resolves to the innermost, which is what the
/// same code written with two C# <c>foreach</c> loops would do.
/// </para>
/// <para>
/// One small object per iteration, and it is the bulk of what a <c>ForEach</c> costs.
/// <c>EngineAllocationTests</c> records the figure rather than pretending it is zero.
/// </para>
/// </remarks>
internal sealed class IterationScope<TItem>(FlowContext outer, TItem item) : FlowContext
{
    /// <inheritdoc />
    public override string CorrelationId => outer.CorrelationId;

    /// <inheritdoc />
    public override string? FlowInstanceId => outer.FlowInstanceId;

    /// <inheritdoc />
    public override string CapabilityId => outer.CapabilityId;

    /// <inheritdoc />
    public override string? TenantId => outer.TenantId;

    /// <inheritdoc />
    public override string IdempotencyKey => outer.IdempotencyKey;

    /// <inheritdoc />
    public override DateTimeOffset Deadline => outer.Deadline;

    /// <inheritdoc />
    public override DateTimeOffset UtcNow => outer.UtcNow;

    /// <inheritdoc />
    public override Random Random => outer.Random;

    /// <inheritdoc />
    public override string FlowId => outer.FlowId;

    /// <inheritdoc />
    public override string FlowVersion => outer.FlowVersion;

    /// <inheritdoc />
    public override ClaimsPrincipal? Principal => outer.Principal;

    /// <inheritdoc />
    public override TriggerEnvelope Trigger => outer.Trigger;

    /// <inheritdoc />
    public override Error? Error => outer.Error;

    /// <inheritdoc />
    public override Guid NewId() => outer.NewId();

    /// <inheritdoc />
    public override T Get<T>() => TryGet<T>(out var value)
        ? value
        : throw new InvalidOperationException(
            $"No step in flow '{FlowId}' produced a {typeof(T).Name}, and it is not the " +
            "element this iteration is processing. Step bindings are resolved at build " +
            "time (FLOWX1020), so reaching this at run time means the value was written " +
            "dynamically rather than returned by a step.");

    /// <inheritdoc />
    /// <remarks>
    /// The type test is against a type parameter on both sides, so the JIT folds it to a
    /// constant. The reinterpret is sound precisely because of that test, and it is used
    /// rather than <c>(T)(object)item</c> so that a struct element is not boxed on every
    /// read — which would put an allocation inside the loop rather than per iteration.
    /// </remarks>
    public override bool TryGet<T>([MaybeNullWhen(false)] out T value)
    {
        if (typeof(T) == typeof(TItem) && item is not null)
        {
            var element = item;
            value = Unsafe.As<TItem, T>(ref element);
            return true;
        }

        return outer.TryGet(out value);
    }

    /// <inheritdoc />
    public override void Set<T>(T value) => outer.Set(value);
}
