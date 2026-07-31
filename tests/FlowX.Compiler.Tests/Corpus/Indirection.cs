// Specimens where the error is produced somewhere other than the capability.
//
// This is where most real code lives. A capability that has grown past a page pushes its
// failure construction into a base class, an injected translator, an extension method or a
// second factory, and each of those is a different kind of hop for the reader. Four of the
// six are in-assembly and resolve; the rest end at a declaration that contains no
// expression that could carry a failure, which the reader treats as "whatever it does, I
// do not understand it" — the roots.Count == 0 branch.

using System.Threading;
using System.Threading.Tasks;
using FlowX;

namespace Corpus.Indirection;

/// <summary>A protected helper inherited from a base class in the same assembly.</summary>
[Specimen(
    "error factory inherited from a base class, alongside an unreachable override",
    Expect = Expect.Resolved,
    Truth = ["order.forbidden/Forbidden"],
    Why = "Forbidden() resolves — the base class is in this compilation. The Invalid "
          + "override below is the point of the specimen: nothing in this capability calls "
          + "it, and the catalogue used to carry its order.invalid anyway, because the scan "
          + "walked the whole class declaration lexically and never asked what was "
          + "reachable. It now starts at ExecuteAsync and follows values, so an Error "
          + "constructed in a member nothing reaches is not published.")]
[Capability("corpus.base_helper", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
public sealed class InheritedHelper : OrderCapabilityBase, ICapability<Order, Receipt>
{
    /// <inheritdoc />
    public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(input.Quantity > 0
            ? Result.Ok(new Receipt(input.Sku))
            : Result.Fail<Receipt>(Forbidden()));

    /// <inheritdoc />
    protected override Error Invalid(Order order) =>
        new("order.invalid", "Invalid.", ErrorCategory.Validation);
}

/// <summary>A template method on the base calling an abstract hook the subclass fills in.</summary>
[Specimen(
    "template method on a base class over an abstract error hook",
    Expect = Expect.Resolved,
    Truth = ["order.rejected_here/Conflict"],
    Why = "Right answer, and now for the reason it looks like. Reject<T> used not to be "
          + "followed at all — its type is Result<Receipt>, which the reader could not see "
          + "as a failure path — so the abstract hook was never reached and the catalogue "
          + "was correct only because the override happens to be declared inside this "
          + "class, where a lexical walk found its construction. The result is now the "
          + "trail, and the hook is dispatched to this capability's override, which is a "
          + "compile-time fact for a concrete capability. Move that one line into a factory "
          + "in a contracts assembly and the same capability withholds — that part stands.")]
[Capability("corpus.abstract_hook", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
public sealed class AbstractHook : OrderCapabilityBase, ICapability<Order, Receipt>
{
    /// <inheritdoc />
    public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(input.Quantity > 0
            ? Result.Ok(new Receipt(input.Sku))
            : Reject<Receipt>(input));

    /// <inheritdoc />
    protected override Error Invalid(Order order) =>
        new("order.rejected_here", "Rejected.", ErrorCategory.Conflict);
}

/// <summary>An injected collaborator that maps a condition to an error.</summary>
[Specimen(
    "error produced by an injected collaborator, called through its interface",
    Expect = Expect.Withheld,
    Truth = ["(whatever the registered implementation returns)"],
    Why = "The call binds to the interface member, which declares no body. This is the "
          + "correct answer and the reader cannot do better: which implementation is "
          + "registered is not a compile-time fact.")]
[Capability("corpus.injected_translator", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
public sealed class InjectedTranslator : ICapability<Order, Receipt>
{
    private readonly IErrorTranslator _translator;

    /// <summary>Creates the capability.</summary>
    public InjectedTranslator(IErrorTranslator translator) => _translator = translator;

    /// <inheritdoc />
    public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(input.Quantity > 0
            ? Result.Ok(new Receipt(input.Sku))
            : Result.Fail<Receipt>(_translator.Translate("quantity")));
}

/// <summary>An extension method on a domain type.</summary>
[Specimen(
    "extension method producing the error",
    Expect = Expect.Resolved,
    Truth = ["order.too_large/Validation"],
    Why = "An extension call is an ordinary static invocation once bound, and the static "
          + "class is in this compilation.")]
[Capability("corpus.extension_method", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
public sealed class ExtensionMethodError : ICapability<Order, Receipt>
{
    /// <inheritdoc />
    public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(input.Quantity <= 10
            ? Result.Ok(new Receipt(input.Sku))
            : Result.Fail<Receipt>(input.TooLarge()));
}

/// <summary>Extensions the specimen above uses.</summary>
public static class OrderExtensions
{
    /// <summary>The order exceeds the per-order cap.</summary>
    public static Error TooLarge(this Order order) =>
        new("order.too_large", $"{order.Quantity} exceeds the cap.", ErrorCategory.Validation);
}

/// <summary>A domain factory that forwards to another domain factory.</summary>
[Specimen(
    "two-hop factory forwarding whole",
    Expect = Expect.Resolved,
    Truth = ["catalogue.discontinued_line/NotFound"],
    Why = "Each hop is another Follow, and the Visited set stops it looping.")]
[Capability("corpus.two_hop", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
public sealed class TwoHopFactory : ICapability<Order, Receipt>
{
    /// <inheritdoc />
    public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(Result.Fail<Receipt>(CatalogueErrors.DiscontinuedLine(input.Sku)));
}

/// <summary>A domain factory layered over a shared builder that takes the code.</summary>
[Specimen(
    "domain factory over a shared builder that receives the code as an argument",
    Expect = Expect.Withheld,
    Truth = ["catalogue.unknown_sku/NotFound"],
    Why = "The trail reaches the construction, and the construction's code argument is the "
          + "builder's parameter rather than a constant. This is the shape a team reaches "
          + "for the moment it has more than a handful of codes, and the code is a literal "
          + "one frame up.")]
[Capability("corpus.shared_builder", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
public sealed class SharedBuilder : ICapability<Order, Receipt>
{
    /// <inheritdoc />
    public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(Result.Fail<Receipt>(CatalogueErrors.UnknownSku(input.Sku)));
}

/// <summary>Domain factories, one forwarding and one parameterised.</summary>
public static class CatalogueErrors
{
    /// <summary>The whole product line is gone.</summary>
    public static Error DiscontinuedLine(string sku) => LineGone(sku);

    /// <summary>No such SKU.</summary>
    public static Error UnknownSku(string sku) => NotFound("catalogue.unknown_sku", sku);

    private static Error LineGone(string sku) =>
        new("catalogue.discontinued_line", $"'{sku}' is discontinued.", ErrorCategory.NotFound);

    private static Error NotFound(string code, string what) =>
        new(code, $"'{what}' does not exist.", ErrorCategory.NotFound);
}
