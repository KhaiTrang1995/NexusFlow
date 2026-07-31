// Specimens where the code string itself is assembled rather than written out.
//
// The reader's rule is not "the code must be a literal", it is "the code must be a
// compile-time constant", and C# folds more than people expect: nameof and const
// concatenation both survive, a static readonly string does not. That line is invisible at
// the call site — two spellings that look identical to an author land on opposite sides —
// which is why this group exists as its own set rather than as one representative case.

using System.Threading;
using System.Threading.Tasks;
using FlowX;

namespace Corpus.Composition;

/// <summary>Codes assembled from constants.</summary>
public static class Codes
{
    /// <summary>The domain prefix every code in this area shares.</summary>
    public const string Domain = "catalogue.";

    /// <summary>A whole code, as a constant.</summary>
    public const string Discontinued = "catalogue.discontinued";

    /// <summary>The same idea, but not a constant.</summary>
    public static readonly string Withdrawn = "catalogue.withdrawn";
}

/// <summary>The code interpolates a runtime value.</summary>
[Specimen(
    "code built by string interpolation over a runtime value",
    Expect = Expect.Withheld,
    Truth = ["(one code per SKU, none of them knowable)"],
    Why = "GetConstantValue has nothing to return. This is the reader working as designed: "
          + "a code nobody can branch on is not a contract.")]
[Capability("corpus.interpolated_code", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
public sealed class InterpolatedCode : ICapability<Order, Receipt>
{
    /// <inheritdoc />
    public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(Result.Fail<Receipt>(
            new Error($"catalogue.{input.Sku}", "Bad SKU.", ErrorCategory.Validation)));
}

/// <summary>The code is two constants concatenated.</summary>
[Specimen(
    "code composed from a const prefix and a const suffix",
    Expect = Expect.Resolved,
    Truth = ["catalogue.out_of_range/Validation"],
    Why = "C# folds constant concatenation, so GetConstantValue returns the whole string. "
          + "Visually this is the same move as interpolation and it resolves; the "
          + "difference is const-ness, which nothing tells the author about.")]
[Capability("corpus.const_concat", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
public sealed class ConstConcatenation : ICapability<Order, Receipt>
{
    /// <inheritdoc />
    public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(Result.Fail<Receipt>(
            new Error(Codes.Domain + "out_of_range", "Out of range.", ErrorCategory.Validation)));
}

/// <summary>The code is a <c>const</c> field.</summary>
[Specimen(
    "code held in a const field",
    Expect = Expect.Resolved,
    Truth = ["catalogue.discontinued/NotFound"],
    Why = "A const reference is a constant expression.")]
[Capability("corpus.const_field", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
public sealed class ConstFieldCode : ICapability<Order, Receipt>
{
    /// <inheritdoc />
    public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(Result.Fail<Receipt>(
            new Error(Codes.Discontinued, "Discontinued.", ErrorCategory.NotFound)));
}

/// <summary>The code is a <c>static readonly</c> field.</summary>
[Specimen(
    "code held in a static readonly field instead of a const",
    Expect = Expect.Withheld,
    Truth = ["catalogue.withdrawn/NotFound"],
    Why = "static readonly is not a constant expression, so the same code one keyword away "
          + "from the specimen above is unreadable. This is the sharpest instance of the "
          + "reader's cliff edge: nothing at the call site distinguishes them.")]
[Capability("corpus.static_readonly_code", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
public sealed class StaticReadonlyCode : ICapability<Order, Receipt>
{
    /// <inheritdoc />
    public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(Result.Fail<Receipt>(
            new Error(Codes.Withdrawn, "Withdrawn.", ErrorCategory.NotFound)));
}

/// <summary>The code is derived with <c>nameof</c>.</summary>
[Specimen(
    "code derived from nameof",
    Expect = Expect.Resolved,
    Truth = ["Discontinued/Conflict"],
    Why = "nameof is a constant expression, so it resolves — and publishes a code that "
          + "breaks the <domain>.<snake_case> convention the manifest promises. The reader "
          + "is not the thing that would catch that.")]
[Capability("corpus.nameof_code", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
public sealed class NameofCode : ICapability<Order, Receipt>
{
    /// <inheritdoc />
    public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(Result.Fail<Receipt>(
            new Error(nameof(Codes.Discontinued), "Discontinued.", ErrorCategory.Conflict)));
}

/// <summary>The category is decided at run time.</summary>
[Specimen(
    "category chosen by a runtime condition",
    Expect = Expect.Withheld,
    Truth = ["order.rejected/Conflict", "order.rejected/Validation"],
    Why = "The category is what a transport maps to a status code, so a guess is a wrong "
          + "wire contract. CategoryName returns null and the scan refuses.")]
[Capability("corpus.runtime_category", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
public sealed class RuntimeCategory : ICapability<Order, Receipt>
{
    /// <inheritdoc />
    public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct)
    {
        var category = input.Quantity < 0 ? ErrorCategory.Validation : ErrorCategory.Conflict;

        return ValueTask.FromResult(Result.Fail<Receipt>(
            new Error("order.rejected", "Rejected.", category)));
    }
}
