namespace FlowX;

/// <summary>
/// One unit of business work. This is the entire contract — retries, telemetry,
/// authorisation, caching, idempotency and transaction scope are applied
/// <em>around</em> it by the platform, never inside it.
/// </summary>
/// <remarks>
/// <para>
/// The rules a capability obeys (docs/07-Capability-Model.md §3). <strong>One of them is
/// still not enforced</strong>, and this list once claimed all of them were — it was headed
/// "Compiler-enforced rules" while citing three diagnostics that had no descriptor.
/// `FlowXDiagnostics` deliberately does not stub a reserved code, on the grounds that
/// "a descriptor nothing raises is a promise the compiler is not keeping"; a doc comment
/// naming one is the same promise made somewhere the compiler cannot see it. Three of those
/// four have since been built, and the fourth at WP-59 — which is the reason the split below
/// is now eight-to-nothing on enforcement, and why the one remaining gap is half a rule
/// rather than a whole one.
/// </para>
/// <para><strong>Enforced at build time:</strong></para>
/// <list type="number">
///   <item>Exactly one input type and one output type — no overloads (FLOWX1015).</item>
///   <item>Expected failures are <see cref="Result{T}"/> values, not exceptions (FLOWX1016).</item>
///   <item>A capability never invokes another capability (FLOWX1004). Composition is the flow's job.</item>
///   <item>A capability never references a transport or plugin assembly (FLOWX1003).</item>
///   <item>A capability declares an authorisation stance (FLOWX1010).</item>
///   <item>A capability is stateless: no mutable instance or static fields (FLOWX1009).</item>
///   <item>Time, identifiers and randomness come from <see cref="CapabilityContext"/> only
///     (FLOWX1007, FLOWX1008).</item>
///   <item>A contract a <c>Durable</c> flow's journal must write is declared by a generated
///     <c>System.Text.Json</c> context (FLOWX1006).</item>
/// </list>
/// <para>
/// The last three shipped at WP-58, after being blocked on severity rather than on
/// analysis: they are <c>Warning</c> by default and <c>Error</c> where the compilation can
/// prove the code is on a durable flow's replay path. Under the old stance they would have
/// been informational under <c>Ephemeral</c>, which is the default profile — so they would
/// have shipped saying nothing in nearly every build.
/// </para>
/// <para>
/// <strong>Required, and not enforced — half of one rule.</strong>
/// </para>
/// <list type="number">
///   <item>Contract types are <em>immutable</em> records. The serialisable half of this rule
///     is FLOWX1006, raised at WP-59 once the generated payload writer gave it something to
///     check; nothing refuses a mutable contract, and the two halves were only ever one rule
///     because a record with init-only members usually satisfies both at once.</item>
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
