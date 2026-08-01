namespace FlowX.Runtime;

/// <summary>
/// One external signal, delivered into an instance that is waiting for it at a
/// <see cref="StepKind.AwaitSignal"/> step.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Identity plus a typed payload, and both halves are load-bearing.</strong> The
/// identity is what the plan publishes — <c>StepNode.SignalType</c>, and the
/// <c>signal</c> field of an <c>AwaitSignal</c> step in <c>flowx.manifest.json</c> — so a
/// transport that receives <c>POST {flow route}/{instanceId}/signals/{identity}</c> can name
/// the step being satisfied without knowing a single contract type. <em>This sentence said
/// "the <c>signals[]</c> entry" and there has never been one; the manifest publishes the
/// identity on the step that waits for it, which is where its position gives it meaning
/// (ADR-0021), and the route is the flow's own rather than a <c>/flows/{id}</c> namespace
/// nothing serves (ADR-0022).</em> The payload is what the steps <em>after</em> the
/// suspension point bind to, and it is seeded into the state bag under
/// <see cref="Contract"/> rather than under its runtime type, so a signal declared as a base
/// contract and delivered as a derived one still resolves the way the flow wrote it.
/// </para>
/// <para>
/// <strong>There is no signal table, and this is why there does not need to be one.</strong>
/// A delivered signal is journaled as the <c>AwaitSignal</c> step's own row, through the
/// same <c>CommitAsync</c> every other step boundary uses, carrying the same state-bag
/// snapshot. So resumption after a second crash reads the signal back through
/// <c>IStepDispatcher.RestoreState</c> exactly as it reads back any step's output — one
/// mechanism, not two (ADR-0015).
/// </para>
/// </remarks>
public sealed class FlowSignal
{
    private FlowSignal(string signalType, Type contract, object payload)
    {
        SignalType = signalType;
        Contract = contract;
        Payload = payload;
    }

    /// <summary>The signal's identity, as the plan and the manifest publish it.</summary>
    public string SignalType { get; }

    /// <summary>The contract the payload is seeded into the state bag under.</summary>
    public Type Contract { get; }

    /// <summary>The value itself.</summary>
    public object Payload { get; }

    /// <summary>Builds a signal from a typed payload.</summary>
    /// <typeparam name="TSignal">
    /// The contract the flow declared in <c>.AwaitSignal&lt;TSignal&gt;(...)</c>. Captured
    /// statically, so what reaches the bag is what the flow's later steps ask for.
    /// </typeparam>
    /// <param name="signalType">The identity the plan carries, e.g. <c>offer.countersigned</c>.</param>
    /// <param name="payload">The value to deliver.</param>
    /// <exception cref="ArgumentException"><paramref name="signalType"/> is null or blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="payload"/> is null.</exception>
    public static FlowSignal Of<TSignal>(string signalType, TSignal payload)
        where TSignal : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signalType);
        ArgumentNullException.ThrowIfNull(payload);

        return new FlowSignal(signalType, typeof(TSignal), payload);
    }

    /// <inheritdoc />
    public override string ToString() => SignalType + " (" + Contract.Name + ")";
}
