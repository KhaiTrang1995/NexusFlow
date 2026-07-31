// Specimens where which error is returned is decided by control flow.
//
// The reader handles the conditional operator and the switch expression explicitly, so
// most of this group is expected to resolve. It is here for the two shapes that are just
// as ordinary and are not handled: an exhaustive switch with a defensive throw arm, and
// the null-coalescing operator.

using System;
using System.Threading;
using System.Threading.Tasks;
using FlowX;

namespace Corpus.Branching;

/// <summary>Both arms of a conditional expression.</summary>
[Specimen(
    "ternary choosing between two errors",
    Expect = Expect.Resolved,
    Truth = ["payment.declined/Unavailable", "inventory.out_of_stock/Conflict"],
    Why = "Both arms are failure paths and the reader descends into both.")]
[Capability("corpus.ternary", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
public sealed class TernaryArms : ICapability<Order, Receipt>
{
    /// <inheritdoc />
    public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(Result.Fail<Receipt>(
            input.Quantity <= 0 ? OrderErrors.Declined("none") : OrderErrors.OutOfStock(input.Sku)));
}

/// <summary>A switch expression over a domain enum.</summary>
[Specimen(
    "switch expression mapping a state to an error",
    Expect = Expect.Resolved,
    Truth = ["order.wrong_state/Validation", "order.not_found/NotFound", "payment.declined/Unavailable"],
    Why = "Every arm is resolved; the reader has a case for SwitchExpressionSyntax.")]
[Capability("corpus.switch_arms", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
public sealed class SwitchArms : ICapability<Order, Receipt>
{
    /// <inheritdoc />
    public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct)
    {
        var state = input.Quantity switch
        {
            <= 0 => OrderState.Draft,
            > 100 => OrderState.Cancelled,
            _ => OrderState.Pending,
        };

        if (state == OrderState.Pending)
        {
            return ValueTask.FromResult(Result.Ok(new Receipt(input.Sku)));
        }

        var error = state switch
        {
            OrderState.Draft => OrderErrors.WrongState(state),
            OrderState.Cancelled => OrderErrors.NotFound(input.Sku),
            _ => OrderErrors.Declined("unknown state"),
        };

        return ValueTask.FromResult(Result.Fail<Receipt>(error));
    }
}

/// <summary>An exhaustive switch with a defensive throw for the impossible arm.</summary>
[Specimen(
    "switch expression whose default arm throws instead of returning an error",
    Expect = Expect.Withheld,
    Truth = ["order.wrong_state/Validation", "order.not_found/NotFound"],
    Why = "The throw arm is a ThrowExpressionSyntax, which reaches the reader's default "
          + "case and marks the scan incomplete. The two real arms resolve and are then "
          + "discarded with the rest of the catalogue.")]
[Capability("corpus.switch_with_throw", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
public sealed class SwitchWithThrowArm : ICapability<Order, Receipt>
{
    /// <inheritdoc />
    public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct)
    {
        if (input.Quantity > 0)
        {
            return ValueTask.FromResult(Result.Ok(new Receipt(input.Sku)));
        }

        var state = (OrderState)(-input.Quantity);

        var error = state switch
        {
            OrderState.Draft => OrderErrors.WrongState(OrderState.Draft),
            OrderState.Cancelled => OrderErrors.NotFound(input.Sku),
            _ => throw new InvalidOperationException("Unreachable."),
        };

        return ValueTask.FromResult(Result.Fail<Receipt>(error));
    }
}

/// <summary>A fallback error chosen with <c>??</c>.</summary>
[Specimen(
    "null-coalescing between a looked-up error and a default",
    Expect = Expect.Withheld,
    Truth = ["order.not_found/NotFound", "payment.declined/Unavailable"],
    Why = "?? is a BinaryExpressionSyntax and the reader has no case for it, so the scan "
          + "reaches its default and refuses. The same two errors written as a ternary "
          + "would resolve.")]
[Capability("corpus.coalesce", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
public sealed class CoalescedFallback : ICapability<Order, Receipt>
{
    /// <inheritdoc />
    public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct)
    {
        if (input.Quantity > 0)
        {
            return ValueTask.FromResult(Result.Ok(new Receipt(input.Sku)));
        }

        var error = Lookup(input.Sku) ?? OrderErrors.Declined("no rule");

        return ValueTask.FromResult(Result.Fail<Receipt>(error));
    }

    private static Error? Lookup(string sku) =>
        sku.Length == 0 ? OrderErrors.NotFound(sku) : null;
}

/// <summary>A local function that produces the error.</summary>
[Specimen(
    "local function inside the capability body",
    Expect = Expect.Resolved,
    Truth = ["order.forbidden/Forbidden"],
    Why = "A local function's body is inside the class declaration the scan walks, so it "
          + "needs no following at all.")]
[Capability("corpus.local_function", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
public sealed class LocalFunctionError : ICapability<Order, Receipt>
{
    /// <inheritdoc />
    public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct)
    {
        if (input.Quantity > 0)
        {
            return ValueTask.FromResult(Result.Ok(new Receipt(input.Sku)));
        }

        return ValueTask.FromResult(Result.Fail<Receipt>(Denied()));

        static Error Denied() => new("order.forbidden", "Not permitted.", ErrorCategory.Forbidden);
    }
}
