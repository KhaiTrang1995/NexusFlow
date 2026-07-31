// Specimens written the way the convention prescribes.
//
// A corpus of nothing but hard cases would be as misleading as samples/ecommerce is, in
// the opposite direction. These eight are here to be resolved, and they establish that the
// reader does what it claims on the shapes it was built for — including two the existing
// tests never cover: a factory reached across a file boundary, and the implicit
// Error-to-Result conversion, which is how the synthetic scale project spells every one of
// its 262 capabilities.

using System.Threading;
using System.Threading.Tasks;
using FlowX;

namespace Corpus.Conventional;

/// <summary>An error constructed where it is returned.</summary>
[Specimen(
    "new Error(...) inline in the capability body",
    Expect = Expect.Resolved,
    Truth = ["order.invalid_quantity/Validation"],
    Why = "The construction is right there; nothing has to be followed.")]
[Capability("corpus.inline", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
public sealed class InlineError : ICapability<Order, Receipt>
{
    /// <inheritdoc />
    public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct)
    {
        if (input.Quantity <= 0)
        {
            return ValueTask.FromResult(Result.Fail<Receipt>(
                new Error("order.invalid_quantity", $"Got {input.Quantity}.", ErrorCategory.Validation)));
        }

        return ValueTask.FromResult(Result.Ok(new Receipt(input.Sku)));
    }
}

/// <summary>The documented convention, with the factory in another file of this assembly.</summary>
[Specimen(
    "static factory class per domain, in a different file of the same assembly",
    Expect = Expect.Resolved,
    Truth = ["inventory.out_of_stock/Conflict"],
    Why = "Read() is given the whole Compilation, so a factory in another tree still has syntax.")]
[Capability("corpus.cross_file_factory", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
public sealed class CrossFileFactory : ICapability<Order, Receipt>
{
    /// <inheritdoc />
    public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(input.Quantity > 0
            ? Result.Ok(new Receipt(input.Sku))
            : Result.Fail<Receipt>(OrderErrors.OutOfStock(input.Sku)));
}

/// <summary>The implicit conversion from <c>Error</c> to <c>Result&lt;T&gt;</c>.</summary>
[Specimen(
    "return the Error directly, relying on the implicit Result<T> conversion",
    Expect = Expect.Resolved,
    Truth = ["payment.declined/Unavailable"],
    Why = "IsErrorExpression asks for the expression's Type, not its ConvertedType, so the "
          + "conversion to Result<T> does not hide it.")]
[Capability("corpus.implicit_conversion", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
public sealed class ImplicitConversion : ICapability<Order, Receipt>
{
    /// <inheritdoc />
    public async ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct)
    {
        await Task.Yield();

        if (input.Quantity > 100)
        {
            return OrderErrors.Declined("too large");
        }

        return new Receipt(input.Sku);
    }
}

/// <summary>An error decorated with structured detail.</summary>
[Specimen(
    "factory result decorated with .With(...)",
    Expect = Expect.Resolved,
    Truth = ["order.not_found/NotFound"],
    Why = "An instance method on Error unwraps to its receiver; the chain is one failure.")]
[Capability("corpus.decorated", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
public sealed class DecoratedError : ICapability<Order, Receipt>
{
    /// <inheritdoc />
    public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(Result.Fail<Receipt>(
            OrderErrors.NotFound(input.Sku).With("sku", input.Sku).With("quantity", input.Quantity)));
}

/// <summary>Three distinct failures from one body.</summary>
[Specimen(
    "several independent failure paths in one capability",
    Expect = Expect.Resolved,
    Truth = ["order.invalid_quantity/Validation", "inventory.out_of_stock/Conflict", "payment.declined/Unavailable"],
    Why = "Each is an independent root; the scan accumulates them.")]
[Capability("corpus.several", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
public sealed class SeveralFailures : ICapability<Order, Receipt>
{
    /// <inheritdoc />
    public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct)
    {
        if (input.Quantity <= 0)
        {
            return ValueTask.FromResult(Result.Fail<Receipt>(
                new Error("order.invalid_quantity", "Not positive.", ErrorCategory.Validation)));
        }

        if (input.Quantity > 10)
        {
            return ValueTask.FromResult(Result.Fail<Receipt>(OrderErrors.OutOfStock(input.Sku)));
        }

        if (input.Sku.Length == 0)
        {
            return ValueTask.FromResult(Result.Fail<Receipt>(OrderErrors.Declined("no sku")));
        }

        return ValueTask.FromResult(Result.Ok(new Receipt(input.Sku)));
    }
}

/// <summary>Constructor arguments given by name and out of order.</summary>
[Specimen(
    "named and reordered constructor arguments",
    Expect = Expect.Resolved,
    Truth = ["order.rejected/Conflict"],
    Why = "Arguments are matched to parameter names, not to positions.")]
[Capability("corpus.named_arguments", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
public sealed class NamedArguments : ICapability<Order, Receipt>
{
    /// <inheritdoc />
    public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(Result.Fail<Receipt>(new Error(
            Category: ErrorCategory.Conflict,
            Message: "Rejected.",
            Code: "order.rejected")));
}

/// <summary>A capability with no declared failure at all.</summary>
[Specimen(
    "a capability that cannot fail",
    Expect = Expect.ResolvedEmpty,
    Truth = [],
    Why = "No Error-typed expression anywhere, and nothing that would have made one "
          + "unreadable. The empty array is a positive statement here.")]
[Capability("corpus.infallible", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
public sealed class Infallible : ICapability<Order, Receipt>
{
    /// <inheritdoc />
    public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(Result.Ok(new Receipt(input.Sku)));
}

/// <summary>A private helper in the capability's own class.</summary>
[Specimen(
    "private static helper on the capability itself",
    Expect = Expect.Resolved,
    Truth = ["order.wrong_state/Validation"],
    Why = "The helper is inside the class declaration the scan already walks, so its "
          + "Error-typed expression is a root even before anything is followed.")]
[Capability("corpus.private_helper", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
public sealed class PrivateHelper : ICapability<Order, Receipt>
{
    /// <inheritdoc />
    public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(input.Quantity > 0
            ? Result.Ok(new Receipt(input.Sku))
            : Result.Fail<Receipt>(Rejected()));

    private static Error Rejected() =>
        new("order.wrong_state", "Not orderable.", ErrorCategory.Validation);
}
