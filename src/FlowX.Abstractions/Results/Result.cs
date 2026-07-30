using System.Diagnostics.CodeAnalysis;

namespace FlowX;

/// <summary>
/// The outcome of a capability: either a value or an <see cref="FlowX.Error"/>.
/// A <c>readonly struct</c> so the failure path allocates nothing — quality goal Q1
/// holds when things go wrong, which is when latency matters most (ADR-0007).
/// </summary>
/// <typeparam name="T">The success value type.</typeparam>
public readonly struct Result<T>
{
    private readonly T? _value;
    private readonly Error? _error;

    private Result(T value)
    {
        _value = value;
        _error = null;
    }

    private Result(Error error)
    {
        _value = default;
        _error = error;
    }

    /// <summary>True when the capability produced a value.</summary>
    [MemberNotNullWhen(false, nameof(_error))]
    public bool IsSuccess => _error is null;

    /// <summary>True when the capability produced an error.</summary>
    [MemberNotNullWhen(true, nameof(_error))]
    public bool IsFailure => _error is not null;

    /// <summary>
    /// The success value. Reading it on a failed result is a defect in the caller,
    /// not a business error, so it throws.
    /// </summary>
    /// <exception cref="InvalidOperationException">The result is a failure.</exception>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException(
            $"Cannot read Value of a failed Result<{typeof(T).Name}>: {_error}");

    /// <summary>
    /// The error. Reading it on a successful result is a defect in the caller.
    /// </summary>
    /// <exception cref="InvalidOperationException">The result is a success.</exception>
    public Error Error => _error
        ?? throw new InvalidOperationException(
            $"Cannot read Error of a successful Result<{typeof(T).Name}>.");

    /// <summary>Non-throwing accessor for both outcomes.</summary>
    public bool TryGetValue([NotNullWhen(true)] out T? value, [NotNullWhen(false)] out Error? error)
    {
        value = _value;
        error = _error;
        return _error is null;
    }

    /// <summary>Projects the success value, propagating any error unchanged.</summary>
    public Result<TNext> Map<TNext>(Func<T, TNext> projection)
    {
        ArgumentNullException.ThrowIfNull(projection);

        if (_error is not null)
        {
            return Result<TNext>.Fail(_error);
        }

        return Result<TNext>.Ok(projection(_value!));
    }

    /// <summary>Collapses both outcomes into a single value.</summary>
    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<Error, TOut> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);

        return _error is not null ? onFailure(_error) : onSuccess(_value!);
    }

    /// <summary>
    /// Lifts a value into a successful result, so a capability can <c>return</c> the thing
    /// it computed rather than restating the type in a factory call.
    /// </summary>
    /// <param name="value">The success value.</param>
    public static implicit operator Result<T>(T value) => new(value);

    /// <summary>
    /// Lifts an error into a failed result, so a capability can <c>return</c> the error it
    /// found rather than restating the value type in <see cref="Result.Fail{T}(Error)"/>.
    /// </summary>
    /// <remarks>
    /// For the single instantiation <c>Result&lt;Error&gt;</c> — a capability whose success
    /// value is itself an <see cref="FlowX.Error"/> — this conversion and the one from
    /// <typeparamref name="T"/> collide, and any implicit conversion to that type is a
    /// <c>CS0457</c> at the call site. That case is diagnosed loudly at compile time, never
    /// silently mis-resolved, and <see cref="Result.Ok{T}(T)"/> / <see cref="Result.Fail{T}(Error)"/>
    /// remain unambiguous escapes. The ergonomics of every other instantiation are worth
    /// that one explicit call.
    /// </remarks>
    /// <param name="error">The failure.</param>
    public static implicit operator Result<T>(Error error) => new(error);

    internal static Result<T> Ok(T value) => new(value);

    internal static Result<T> Fail(Error error) => new(error);
}

/// <summary>Factory methods for <see cref="Result{T}"/>.</summary>
public static class Result
{
    /// <summary>A successful result carrying <paramref name="value"/>.</summary>
    public static Result<T> Ok<T>(T value) => Result<T>.Ok(value);

    /// <summary>A failed result carrying <paramref name="error"/>.</summary>
    public static Result<T> Fail<T>(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return Result<T>.Fail(error);
    }

    /// <summary>
    /// A failed result assembled from its parts, for call sites that do not have a
    /// shared error factory.
    /// </summary>
    public static Result<T> Fail<T>(string code, string message, ErrorCategory category)
        => Result<T>.Fail(new Error(code, message, category));
}
