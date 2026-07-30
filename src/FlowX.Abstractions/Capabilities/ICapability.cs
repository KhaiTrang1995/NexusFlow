namespace FlowX;

/// <summary>
/// One unit of business work. This is the entire contract — retries, telemetry,
/// authorisation, caching, idempotency and transaction scope are applied
/// <em>around</em> it by the platform, never inside it.
/// </summary>
/// <remarks>
/// <para>Compiler-enforced rules (docs/07-Capability-Model.md §3):</para>
/// <list type="number">
///   <item>Exactly one input type and one output type — no overloads (FLOWX1015).</item>
///   <item>Expected failures are <see cref="Result{T}"/> values, not exceptions (FLOWX1016).</item>
///   <item>A capability never invokes another capability (FLOWX1004). Composition is the flow's job.</item>
///   <item>A capability never references a transport or plugin assembly (FLOWX1003).</item>
///   <item>A capability declares an authorisation stance (FLOWX1010).</item>
///   <item>A capability is stateless: no mutable instance or static fields (FLOWX1009).</item>
///   <item>Time, identifiers and randomness come from <see cref="CapabilityContext"/> only (FLOWX1007/1008).</item>
/// </list>
/// <para>
/// Rule 3 is the load-bearing one: because capabilities cannot call each other, the
/// capability set is a <em>set</em>, not a graph. That is what makes the architecture
/// analysable, replayable and testable without a host.
/// </para>
/// </remarks>
/// <typeparam name="TIn">Immutable input contract.</typeparam>
/// <typeparam name="TOut">Immutable output contract.</typeparam>
public interface ICapability<in TIn, TOut>
{
    /// <summary>Performs the work.</summary>
    /// <param name="input">The input contract instance.</param>
    /// <param name="ctx">
    /// Ambient execution state: clock, identifiers, tenant, principal, idempotency key.
    /// Never read <see cref="DateTime.UtcNow"/> or call <see cref="Guid.NewGuid"/> directly —
    /// replay determinism depends on going through the context.
    /// </param>
    /// <param name="ct">Cancellation linked to the flow's deadline.</param>
    /// <returns>
    /// A value on success, or an <see cref="Error"/> for any outcome a caller could
    /// reasonably handle. Throwing signals a defect and is reported as one
    /// (<c>flowx_capability_unhandled_total</c>).
    /// </returns>
    ValueTask<Result<TOut>> ExecuteAsync(TIn input, CapabilityContext ctx, CancellationToken ct);
}
