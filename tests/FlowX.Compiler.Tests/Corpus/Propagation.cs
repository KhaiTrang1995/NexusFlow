// Specimens where a failure travels through Result<T> rather than through an Error.
//
// These are the shapes where a capability returns a declared error and no expression of
// type Error occurs in its source — the failure is carried inside a Result<T> the whole
// way. They used to be the reader's blind spot: it identified a failure path by finding an
// expression of type Error, found none, found nothing it could not follow either, and
// published `errors: []` — which the schema and ADR-0014 §3 B(1) both define as the
// positive statement "analysed, and returns no declared error". Three of the five were
// confidently wrong.
//
// The reader now follows the Result<T> itself, so the question it asks at each of these
// sites is "where did this result come from" rather than "is there an Error here". Two of
// the three resolve to a correct catalogue — the code was in the source all along, one
// hop away — and the third, where the result comes from an injected service, is withheld.
//
// WP-37. See docs/benchmarks/B13-error-catalogue-resolution.md §5.1.

using System.Threading;
using System.Threading.Tasks;
using FlowX;

namespace Corpus.Propagation;

/// <summary>The three-argument <c>Result.Fail</c> overload.</summary>
[Specimen(
    "Result.Fail<T>(code, message, category) — the parts overload",
    Expect = Expect.Resolved,
    Truth = ["order.rejected/Conflict"],
    Why = "No expression here has type Error — the arguments are two strings and an enum, "
          + "and the call's type is Result<Receipt> — which is why this used to publish a "
          + "complete empty catalogue for a capability that returns a code. The reader now "
          + "reads the overload, and this is the best case of the three: the code and the "
          + "category are literals at the call site, so the answer is a correct catalogue "
          + "rather than a withheld one.")]
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
    Expect = Expect.Withheld,
    Truth = ["(whatever the service returns)"],
    Why = "The failure never takes the shape of an Error inside this file, so for as long "
          + "as the reader looked only for Error-typed expressions there was nothing to "
          + "follow and nothing to refuse, and a capability that is one line of delegation "
          + "— a very common shape — published errors: []. The result itself is now the "
          + "trail, and it leads to an interface member with no body. Withheld is the only "
          + "true answer: which implementation is registered is not a compile-time fact.")]
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
    Expect = Expect.Resolved,
    Truth = ["order.invalid_quantity/Validation"],
    Why = "The helper's return type is Result<Receipt>, so the call site holds no "
          + "Error-typed expression, and the construction inside the helper used to be "
          + "unreachable for a second reason: the scan only walked the capability's own "
          + "declaration and the helper is on a different class. Following the result "
          + "answers both — the guard is an ordinary static method in this compilation, "
          + "and one hop inside it the Error is constructed from literals.")]
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
    Why = "Two trails, both ending off the edge of the compilation: result.Error is an "
          + "Error-typed expression that leads to Result<T>.Error in FlowX.Abstractions, "
          + "and the awaited call leads to the service interface. Refused on either "
          + "count. This specimen and the delegating one above are the same situation, "
          + "and they used to produce opposite manifests; they now agree.")]
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
