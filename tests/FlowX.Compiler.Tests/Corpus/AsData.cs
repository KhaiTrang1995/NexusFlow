// Specimens where errors are held as data rather than produced by a call.
//
// An Error is a record, so teams that have more than a handful of them stop writing a
// factory method per code and start writing a table. Every shape here is ordinary C#, and
// the reader's behaviour splits on something the author has no reason to think about:
// whether the error is reached by naming a member or by indexing one.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FlowX;

namespace Corpus.AsData;

/// <summary>Errors kept as values instead of behind factory methods.</summary>
public static class ShippingErrors
{
    /// <summary>No carrier serves the address.</summary>
    public static readonly Error NoCarrier =
        new("shipping.no_carrier", "No carrier serves this address.", ErrorCategory.Unavailable);

    /// <summary>The parcel is too heavy.</summary>
    public static Error TooHeavy { get; } =
        new("shipping.too_heavy", "Over the weight limit.", ErrorCategory.Validation);

    /// <summary>The same errors, reachable by index.</summary>
    public static readonly Error[] Table =
    [
        new Error("shipping.no_carrier", "No carrier serves this address.", ErrorCategory.Unavailable),
        new Error("shipping.too_heavy", "Over the weight limit.", ErrorCategory.Validation),
    ];

    /// <summary>And by key.</summary>
    public static readonly Dictionary<string, Error> ByReason = new(StringComparer.Ordinal)
    {
        ["no_carrier"] = new Error("shipping.no_carrier", "No carrier.", ErrorCategory.Unavailable),
    };

    /// <summary>Built by a stored function rather than a method.</summary>
    public static readonly Func<string, Error> Build =
        reason => new Error("shipping.refused", reason, ErrorCategory.Conflict);
}

/// <summary>An error held in a static readonly field.</summary>
[Specimen(
    "error held in a static readonly field and returned by name",
    Expect = Expect.Resolved,
    Truth = ["shipping.no_carrier/Unavailable"],
    Why = "A bare name is an IdentifierNameSyntax whose symbol is the field; Follow reaches "
          + "the declarator and its initializer. The reader's own remarks call this case "
          + "out as the reason it does not simply exclude every TypeSyntax.")]
[Capability("corpus.error_field", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
public sealed class ErrorField : ICapability<Order, Receipt>
{
    /// <inheritdoc />
    public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(input.Quantity > 0
            ? Result.Ok(new Receipt(input.Sku))
            : Result.Fail<Receipt>(ShippingErrors.NoCarrier));
}

/// <summary>An error behind a get-only property.</summary>
[Specimen(
    "error exposed as a static get-only property",
    Expect = Expect.Resolved,
    Truth = ["shipping.too_heavy/Validation"],
    Why = "The property's declaring syntax carries the initializer, and Follow walks it "
          + "like any other member.")]
[Capability("corpus.error_property", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
public sealed class ErrorProperty : ICapability<Order, Receipt>
{
    /// <inheritdoc />
    public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(input.Quantity < 100
            ? Result.Ok(new Receipt(input.Sku))
            : Result.Fail<Receipt>(ShippingErrors.TooHeavy));
}

/// <summary>An error taken out of an array.</summary>
[Specimen(
    "error selected from a static readonly array by index",
    Expect = Expect.Withheld,
    Truth = ["shipping.no_carrier/Unavailable", "shipping.too_heavy/Validation"],
    Why = "An element access is not a name, an invocation or a construction, so it falls "
          + "to the reader's default case. The table it indexes is in the same file and "
          + "fully literal.")]
[Capability("corpus.error_array", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
public sealed class ErrorFromArray : ICapability<Order, Receipt>
{
    /// <inheritdoc />
    public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(input.Quantity > 0
            ? Result.Ok(new Receipt(input.Sku))
            : Result.Fail<Receipt>(ShippingErrors.Table[input.Sku.Length % 2]));
}

/// <summary>An error looked up by key.</summary>
[Specimen(
    "error looked up in a dictionary keyed by reason",
    Expect = Expect.Withheld,
    Truth = ["shipping.no_carrier/Unavailable"],
    Why = "Same default case as the array, for the same reason.")]
[Capability("corpus.error_dictionary", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
public sealed class ErrorFromDictionary : ICapability<Order, Receipt>
{
    /// <inheritdoc />
    public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(input.Quantity > 0
            ? Result.Ok(new Receipt(input.Sku))
            : Result.Fail<Receipt>(ShippingErrors.ByReason["no_carrier"]));
}

/// <summary>An error built by a stored delegate.</summary>
[Specimen(
    "error built by invoking a Func<string, Error> field",
    Expect = Expect.Withheld,
    Truth = ["shipping.refused/Conflict"],
    Why = "The invocation binds to Func<,>.Invoke, which is declared in the framework and "
          + "has no syntax here. Which delegate is stored is not a compile-time fact, so "
          + "refusing is right.")]
[Capability("corpus.error_delegate", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
public sealed class ErrorFromDelegate : ICapability<Order, Receipt>
{
    /// <inheritdoc />
    public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(input.Quantity > 0
            ? Result.Ok(new Receipt(input.Sku))
            : Result.Fail<Receipt>(ShippingErrors.Build("no reason")));
}
