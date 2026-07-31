// The corpus's own scaffolding: the ground-truth marker, the shared contracts every
// specimen uses, and the one conventional error factory that lives in a *different file
// from the capabilities* — which is the case docs/07-Capability-Model.md §4 describes and
// samples/ecommerce does not exercise, because its errors and capabilities share a file.
//
// Nothing in this directory is compiled into the test assembly. It is read as text,
// parsed into a compilation of its own, and handed to ErrorCatalogueReader. See
// ErrorCatalogueCorpusTests.cs, and docs/benchmarks/B13-error-catalogue-resolution.md for
// why these specimens and not others.

using System;
using System.Threading;
using System.Threading.Tasks;
using FlowX;

namespace Corpus;

/// <summary>What outcome a specimen is asserted to produce.</summary>
public enum Expect
{
    /// <summary>A catalogue is published and it matches <c>Truth</c>.</summary>
    Resolved,

    /// <summary>A catalogue is published, it is empty, and the capability really cannot fail.</summary>
    ResolvedEmpty,

    /// <summary>No catalogue is published, and the capability really can fail.</summary>
    Withheld,

    /// <summary>
    /// A catalogue is published and it disagrees with <c>Truth</c> — the state
    /// ADR-0014 §8 says this field cannot reach.
    /// </summary>
    /// <remarks>
    /// No specimen declares this any more, and the corpus asserts that none reaches it.
    /// The member stays because it is the classification the measurement computes, and a
    /// vocabulary that cannot express the defect cannot report its return.
    /// </remarks>
    FalseComplete,
}

/// <summary>
/// Ground truth for one specimen, supplied by the corpus author and machine-checked.
/// </summary>
/// <remarks>
/// <c>Truth</c> is what the capability can actually return, read off the source by a
/// human. It is the corpus's only unverifiable input and therefore the place to argue
/// with it: every number in the report is a comparison against these strings.
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class SpecimenAttribute : Attribute
{
    /// <summary>Marks a capability as a corpus specimen.</summary>
    /// <param name="pattern">The real-world shape this specimen stands for.</param>
    public SpecimenAttribute(string pattern) => Pattern = pattern;

    /// <summary>The real-world shape this specimen stands for.</summary>
    public string Pattern { get; }

    /// <summary>The outcome the reader is asserted to produce.</summary>
    public Expect Expect { get; set; }

    /// <summary>Every error this capability can return, as <c>code/Category</c>.</summary>
    public string[] Truth { get; set; } = [];

    /// <summary>Why the reader can or cannot follow it.</summary>
    public string Why { get; set; } = string.Empty;
}

/// <summary>What a caller asks for.</summary>
public sealed record Order(string Sku, int Quantity);

/// <summary>What a caller gets back.</summary>
public sealed record Receipt(string Id);

/// <summary>Where an order can be in its life.</summary>
public enum OrderState
{
    /// <summary>Not yet accepted.</summary>
    Draft,

    /// <summary>Accepted and awaiting payment.</summary>
    Pending,

    /// <summary>Cancelled by the customer.</summary>
    Cancelled,
}

/// <summary>Errors declared the way docs/07-Capability-Model.md §4 requires.</summary>
/// <remarks>
/// In a file of its own, and reached from capabilities declared elsewhere in the same
/// assembly. That is the convention as written, minus the part §4 also says — that
/// contracts live in a separate assembly — which <c>Shared</c> covers instead.
/// </remarks>
public static class OrderErrors
{
    /// <summary>Not enough stock.</summary>
    public static Error OutOfStock(string sku) =>
        new Error("inventory.out_of_stock", $"'{sku}' is out of stock.", ErrorCategory.Conflict)
            .With("sku", sku);

    /// <summary>The payment was refused.</summary>
    public static Error Declined(string reason) =>
        new("payment.declined", $"Declined: {reason}.", ErrorCategory.Unavailable);

    /// <summary>The order is not in a state that permits this.</summary>
    public static Error WrongState(OrderState state) =>
        new("order.wrong_state", $"Order is {state}.", ErrorCategory.Validation);

    /// <summary>No such order.</summary>
    public static Error NotFound(string id) =>
        new("order.not_found", $"No order '{id}'.", ErrorCategory.NotFound);
}

/// <summary>A domain service a capability delegates to, as most real capabilities do.</summary>
public interface IOrderService
{
    /// <summary>Places the order, or fails.</summary>
    ValueTask<Result<Receipt>> PlaceAsync(Order order, CancellationToken ct);
}

/// <summary>Something that turns an arbitrary condition into an error.</summary>
public interface IErrorTranslator
{
    /// <summary>Translates.</summary>
    Error Translate(string condition);
}

/// <summary>A base class capabilities in this corpus share, as a real hierarchy would.</summary>
public abstract class OrderCapabilityBase
{
    /// <summary>An error every subclass can raise, declared once.</summary>
    protected static Error Forbidden() =>
        new("order.forbidden", "Not permitted.", ErrorCategory.Forbidden);

    /// <summary>Subclasses decide what "invalid" means for them.</summary>
    protected abstract Error Invalid(Order order);

    /// <summary>The template method: shared plumbing over the subclass's hook.</summary>
    protected Result<T> Reject<T>(Order order) => Result.Fail<T>(Invalid(order));
}
