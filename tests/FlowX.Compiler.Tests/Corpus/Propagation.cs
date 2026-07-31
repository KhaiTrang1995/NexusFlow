// Specimens where a failure travels through Result<T> rather than through an Error.
//
// The reader's premise is that "an expression's type is the only thing that identifies a
// failure path". These are the shapes where a capability returns a declared error and no
// expression of type Error occurs in its source — the failure is carried inside a
// Result<T> the whole way. Three of the five are therefore not withheld: they are
// published as `errors: []`, which the schema and ADR-0014 §3 B(1) both define as the
// positive statement "analysed, and returns no declared error".
//
// The first of them, ResultFailFromParts, uses Result.Fail<T>(code, message, category) —
// a first-party overload in FlowX.Abstractions whose own summary says it is "for call
// sites that do not have a shared error factory".

using System.Threading;
using System.Threading.Tasks;
using FlowX;

namespace Corpus.Propagation;

/// <summary>The three-argument <c>Result.Fail</c> overload.</summary>
[Specimen(
    "Result.Fail<T>(code, message, category) — the parts overload",
    Expect = Expect.FalseComplete,
    Truth = ["order.rejected/Conflict"],
    Why = "No expression here has type Error: the arguments are two strings and an enum, "
          + "and the call's type is Result<Receipt>. The scan finds nothing, finds nothing "
          + "it could not follow either, and publishes a complete empty catalogue for a "
          + "capability that returns a code.")]
[Capability("corpus.fail_from_parts", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
public sealed class ResultFailFromParts : ICapability<Order, Receipt>
{
    /// <inheritdoc />
    public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(input.Quantity > 0
            ? Result.Ok(new Receipt(input.Sku))
            : Result.Fail<Receipt>("order.rejected", "Rejected.", ErrorCategory.Conflict));
}

/// <summary>A thin capability over a domain service.</summary>
[Specimen(
    "capability delegates wholesale to an injected service returning Result<T>",
    Expect = Expect.FalseComplete,
    Truth = ["(whatever the service returns)"],
    Why = "The failure never takes the shape of an Error inside this file, so there is "
          + "nothing to follow and nothing to refuse. A capability that is one line of "
          + "delegation — a very common shape — publishes errors: [].")]
[Capability("corpus.delegating", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
public sealed class DelegatingCapability : ICapability<Order, Receipt>
{
    private readonly IOrderService _service;

    /// <summary>Creates the capability.</summary>
    public DelegatingCapability(IOrderService service) => _service = service;

    /// <inheritdoc />
    public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct) =>
        _service.PlaceAsync(input, ct);
}

/// <summary>A guard clause that returns a failed result built elsewhere.</summary>
[Specimen(
    "private helper returning Result<T>, not Error",
    Expect = Expect.FalseComplete,
    Truth = ["order.invalid_quantity/Validation"],
    Why = "The helper's return type is Result<Receipt>, so the call site holds no "
          + "Error-typed expression. The construction inside the helper is not reached "
          + "either: Roots only walks the capability's own declaration, and the helper is "
          + "declared on a different class.")]
[Capability("corpus.result_helper", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
public sealed class ResultReturningHelper : ICapability<Order, Receipt>
{
    /// <inheritdoc />
    public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(input.Quantity > 0
            ? Result.Ok(new Receipt(input.Sku))
            : OrderGuards.InvalidQuantity<Receipt>());
}

/// <summary>Guards that return a failed result rather than an error.</summary>
public static class OrderGuards
{
    /// <summary>The quantity is not positive.</summary>
    public static Result<T> InvalidQuantity<T>() =>
        Result.Fail<T>(new Error("order.invalid_quantity", "Not positive.", ErrorCategory.Validation));
}

/// <summary>An inner failure passed straight through.</summary>
[Specimen(
    "propagating an inner Result's Error",
    Expect = Expect.Withheld,
    Truth = ["(whatever the service returns)"],
    Why = "result.Error IS an Error-typed expression, so the reader sees the failure path "
          + "and follows it to Result<T>.Error, declared in FlowX.Abstractions. Off the "
          + "edge of the compilation, and refused — the correct answer, and the opposite "
          + "of what the delegating specimen above produces for the same situation.")]
[Capability("corpus.propagated", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
public sealed class PropagatedError : ICapability<Order, Receipt>
{
    private readonly IOrderService _service;

    /// <summary>Creates the capability.</summary>
    public PropagatedError(IOrderService service) => _service = service;

    /// <inheritdoc />
    public async ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct)
    {
        var placed = await _service.PlaceAsync(input, ct).ConfigureAwait(false);

        if (placed.IsFailure)
        {
            return Result.Fail<Receipt>(placed.Error);
        }

        return Result.Ok(placed.Value);
    }
}

/// <summary>A conventional error routed through a one-line wrapper.</summary>
[Specimen(
    "error passed as a parameter to a private wrapper",
    Expect = Expect.Withheld,
    Truth = ["payment.declined/Unavailable"],
    Why = "The call site resolves perfectly. What loses the catalogue is the wrapper's own "
          + "body: `error` is an Error-typed expression whose symbol is a parameter, and a "
          + "parameter's declaration holds no expression to follow, so the scan refuses. "
          + "The obstacle is a helper the author added for readability, not the error.")]
[Capability("corpus.error_parameter", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
public sealed class ErrorAsParameter : ICapability<Order, Receipt>
{
    /// <inheritdoc />
    public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(input.Quantity > 0
            ? Result.Ok(new Receipt(input.Sku))
            : Wrap(OrderErrors.Declined("empty order")));

    private static Result<Receipt> Wrap(Error error) => Result.Fail<Receipt>(error);
}
