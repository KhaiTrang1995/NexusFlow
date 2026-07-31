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
    /// <remarks>
    /// Uses <c>[MaybeNullWhen(false)] out T</c> rather than <c>out T?</c>: on a type
    /// parameter whose constraints are inherited, an overriding method cannot
    /// disambiguate <c>T?</c> from <c>Nullable&lt;T&gt;</c>, so the latter form is not
    /// overridable. This is the same shape <c>Dictionary.TryGetValue</c> uses.
    /// </remarks>
    public abstract bool TryGet<T>([MaybeNullWhen(false)] out T value);

    /// <summary>
    /// Writes a value into the state bag. Step outputs are stored automatically; call
    /// this only for values a step cannot return.
    /// </summary>
    public abstract void Set<T>(T value);

    /// <summary>
    /// Adapts a delegate written against <see cref="FlowContext{TIn}"/> into one over the
    /// plain context.
    /// </summary>
    /// <typeparam name="TIn">The flow's input contract.</typeparam>
    /// <typeparam name="TResult">What the delegate produces.</typeparam>
    /// <param name="projection">The delegate to adapt.</param>
    /// <remarks>
    /// <para>
    /// Exists for one caller: the generated <c>Projection</c> field. An author's
    /// <c>.Return(...)</c> is written against <see cref="FlowContext{TIn}"/> — that is the
    /// parameter type the DSL declares, and the only one on which
    /// <see cref="FlowContext{TIn}.Input"/> resolves — while
    /// <c>FlowEngine.ExecuteAsync</c> and <c>MapFlow</c> both take a
    /// <c>Func&lt;FlowContext, TOut&gt;</c>. One call adapts the first shape to the
    /// second; retyping the engine would push a generic parameter through two packages to
    /// save it.
    /// </para>
    /// <para>
    /// Here rather than on <see cref="FlowContext{TIn}"/> itself, where it would read more
    /// naturally, because a static member on a generic type is CA1000 — and the rule has a
    /// point: <c>FlowContext&lt;TIn&gt;.Untyped</c> would have to be spelled with the type
    /// argument the compiler cannot infer from a lambda anyway.
    /// </para>
    /// <para>
    /// The adapter is built once, into a <c>static readonly</c> field on the generated
    /// partial class, so a flow pays for it at type initialisation and never per
    /// execution.
    /// </para>
    /// </remarks>
    public static Func<FlowContext, TResult> Untyped<TIn, TResult>(
        Func<FlowContext<TIn>, TResult> projection)
    {
        ArgumentNullException.ThrowIfNull(projection);

        return context => projection(new FlowContext<TIn>(context));
    }
}

/// <summary>
/// The typed view of a flow's context that every <see cref="IFlowBuilder{TIn, TOut}"/>
/// delegate receives: a <see cref="FlowContext"/> plus the flow's own input.
/// </summary>
/// <typeparam name="TIn">The flow's input contract.</typeparam>
/// <remarks>
/// <para>
/// <strong>A value-typed view rather than a context in its own right, and the change is
/// what makes <c>ctx.Input</c> compile.</strong> This used to be an <c>abstract class
/// FlowContext&lt;TIn&gt; : FlowContext</c> — and nothing anywhere derived from it. There
/// was no such object at run time: the engine's context is pooled and shared by every
/// flow, and a pool of one type cannot be a <c>FlowContext&lt;PlaceOrder&gt;</c> and a
/// <c>FlowContext&lt;FulfilOrder&gt;</c> at once. So the generator emitted every copied
/// lambda into a field typed <c>Func&lt;FlowContext, …&gt;</c>, the parameter lost
/// <see cref="Input"/> on the way, and a predicate that read it failed to compile with
/// CS1061 — in generated code, against three documentation pages that all said it worked.
/// </para>
/// <para>
/// A view fixes that without touching pooling. It is one reference wide, so passing it
/// costs a register and allocates nothing — budget B2 is a hard zero for the linear,
/// conditional and switch paths and a per-branch allocation would have lost it. It wraps
/// <em>any</em> <see cref="FlowContext"/>, which matters more than it looks: inside a
/// <c>ForEach</c> body the context is the iteration's scope and inside a sub-flow it is
/// the child's, and a cast to a class would have failed on both.
/// </para>
/// <para>
/// <strong><see cref="Input"/> reads the state bag rather than a field of its own.</strong>
/// The engine seeds the flow's input under its own type before the first step — that is
/// what lets a generated dispatcher write <c>ctx.Get&lt;PlaceOrder&gt;()</c> — so the input
/// is already there, and holding a second copy would be a chance for the two to disagree.
/// </para>
/// <para>
/// <c>default(FlowContext&lt;TIn&gt;)</c> wraps nothing and is not usable. The DSL never
/// produces one: every value reaching a builder delegate is built by the generated
/// dispatcher from a real context.
/// </para>
/// </remarks>
public readonly struct FlowContext<TIn> : IEquatable<FlowContext<TIn>>
{
    private readonly FlowContext _context;

    /// <summary>Creates the typed view of a running flow's context.</summary>
    /// <param name="context">The context to view. Never copied — this holds the reference.</param>
    /// <remarks>
    /// Public so a predicate or a projection can be unit-tested directly against a
    /// <c>TestFlowContext</c>, which is the same reason the generated <c>Projection</c>
    /// field is public.
    /// </remarks>
    public FlowContext(FlowContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _context = context;
    }

    /// <summary>The input this instance was triggered with. Immutable for the flow's lifetime.</summary>
    /// <exception cref="InvalidOperationException">
    /// The flow was started without an input. Every trigger supplies one; a plan executed
    /// through the engine's input-less overload has nothing for this to read.
    /// </exception>
    public TIn Input
    {
        get
        {
            if (_context.TryGet<TIn>(out var input))
            {
                return input;
            }

            throw new InvalidOperationException(
                $"Flow '{_context.FlowId}' was started without a {typeof(TIn).Name}, so " +
                "ctx.Input has nothing to read. The input is seeded into the context by " +
                "the engine at the start of the flow; reaching this means the plan was " +
                "executed through an overload that takes no input.");
        }
    }

    /// <summary>The context this is a view of.</summary>
    public FlowContext Context => _context;

    /// <inheritdoc cref="CapabilityContext.CorrelationId" />
    public string CorrelationId => _context.CorrelationId;

    /// <inheritdoc cref="CapabilityContext.FlowInstanceId" />
    public string? FlowInstanceId => _context.FlowInstanceId;

    /// <inheritdoc cref="CapabilityContext.CapabilityId" />
    public string CapabilityId => _context.CapabilityId;

    /// <inheritdoc cref="CapabilityContext.TenantId" />
    public string? TenantId => _context.TenantId;

    /// <inheritdoc cref="CapabilityContext.IdempotencyKey" />
    public string IdempotencyKey => _context.IdempotencyKey;

    /// <inheritdoc cref="CapabilityContext.Deadline" />
    public DateTimeOffset Deadline => _context.Deadline;

    /// <inheritdoc cref="CapabilityContext.UtcNow" />
    public DateTimeOffset UtcNow => _context.UtcNow;

    /// <inheritdoc cref="CapabilityContext.Random" />
    public Random Random => _context.Random;

    /// <inheritdoc cref="CapabilityContext.TimeRemaining" />
    public TimeSpan TimeRemaining => _context.TimeRemaining;

    /// <inheritdoc cref="FlowContext.FlowId" />
    public string FlowId => _context.FlowId;

    /// <inheritdoc cref="FlowContext.FlowVersion" />
    public string FlowVersion => _context.FlowVersion;

    /// <inheritdoc cref="FlowContext.Principal" />
    public ClaimsPrincipal? Principal => _context.Principal;

    /// <inheritdoc cref="FlowContext.Trigger" />
    public TriggerEnvelope Trigger => _context.Trigger;

    /// <inheritdoc cref="FlowContext.Error" />
    public Error? Error => _context.Error;

    /// <inheritdoc cref="CapabilityContext.NewId" />
    public Guid NewId() => _context.NewId();

    /// <inheritdoc cref="FlowContext.Get{T}" />
    public T Get<T>() => _context.Get<T>();

    /// <inheritdoc cref="FlowContext.TryGet{T}" />
    public bool TryGet<T>([MaybeNullWhen(false)] out T value) => _context.TryGet(out value);

    /// <inheritdoc cref="FlowContext.Set{T}" />
    public void Set<T>(T value) => _context.Set(value);

    /// <summary>Unwraps the view, so a helper that takes a <see cref="FlowContext"/> still binds.</summary>
    public static implicit operator FlowContext(FlowContext<TIn> context) => context._context;

    /// <summary>Named alternative to the implicit conversion.</summary>
    public FlowContext ToFlowContext() => _context;

    /// <inheritdoc />
    public bool Equals(FlowContext<TIn> other) => ReferenceEquals(_context, other._context);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is FlowContext<TIn> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _context?.GetHashCode() ?? 0;

    /// <summary>Compares two views.</summary>
    public static bool operator ==(FlowContext<TIn> left, FlowContext<TIn> right) => left.Equals(right);

    /// <summary>Compares two views.</summary>
    public static bool operator !=(FlowContext<TIn> left, FlowContext<TIn> right) => !left.Equals(right);
}
