using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;

namespace FlowX;

/// <summary>
/// Execution state of one flow instance, threaded through every step. Pooled and
/// reset by the runtime.
/// </summary>
/// <remarks>
/// In a <see cref="ExecutionProfile.Durable"/> flow the state bag is serialised into
/// the journal at every checkpoint, so anything placed in it must be serialisable by
/// a generated <c>System.Text.Json</c> context — enforced by FLOWX1006.
/// </remarks>
public abstract class FlowContext : CapabilityContext
{
    /// <summary>The flow's declared identity, e.g. <c>order.place</c>.</summary>
    public abstract string FlowId { get; }

    /// <summary>
    /// The flow version this instance started on. A durable instance keeps executing
    /// the version it started with, even across a deployment
    /// (docs/11-Distributed-Runtime.md §7).
    /// </summary>
    public abstract string FlowVersion { get; }

    /// <summary>The authenticated principal, or <c>null</c> for an anonymous trigger.</summary>
    public abstract ClaimsPrincipal? Principal { get; }

    /// <summary>
    /// How this instance was activated. Available for diagnostics; branching business
    /// logic on it breaks transport agnosticism and is reported as FLOWX1003.
    /// </summary>
    public abstract TriggerEnvelope Trigger { get; }

    /// <summary>
    /// The error that ended the flow, available inside <c>EmitOnFailure</c> and
    /// compensation steps. <c>null</c> on the success path.
    /// </summary>
    public abstract Error? Error { get; }

    /// <summary>
    /// Reads a value produced by an earlier step.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No step produced a <typeparamref name="T"/>. This is a defect — the compiler
    /// resolves step bindings at build time (FLOWX1020), so reaching this at run time
    /// means the value was written dynamically rather than returned by a step.
    /// </exception>
    public abstract T Get<T>();

    /// <summary>Non-throwing counterpart of <see cref="Get{T}"/>.</summary>
    public abstract bool TryGet<T>([NotNullWhen(true)] out T? value);

    /// <summary>
    /// Writes a value into the state bag. Step outputs are stored automatically; call
    /// this only for values a step cannot return.
    /// </summary>
    public abstract void Set<T>(T value);
}

/// <summary>
/// Typed flow context exposing the flow's own input contract.
/// </summary>
/// <typeparam name="TIn">The flow's input contract.</typeparam>
public abstract class FlowContext<TIn> : FlowContext
{
    /// <summary>The input this instance was triggered with. Immutable for the flow's lifetime.</summary>
    public abstract TIn Input { get; }
}
