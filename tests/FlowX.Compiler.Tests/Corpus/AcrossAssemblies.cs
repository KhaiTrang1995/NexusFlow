// Specimens whose error factory lives in a referenced assembly.
//
// This group is the one that matters most, and it is not an edge case: it is the layout
// docs/07-Capability-Model.md §4 prescribes. The same block that mandates the static error
// class opens with "Contracts live in a dedicated assembly with no dependencies", and its
// rule table repeats "Contracts live in <App>.Contracts". PaymentErrors, the example the
// section uses to justify the mandate on the grounds that "error codes are enumerable —
// they appear in the manifest", is declared inside that block.
//
// ErrorCatalogueReader can only follow a symbol that has DeclaringSyntaxReferences, which a
// symbol from a referenced assembly does not. So a team that follows §4 exactly gets no
// catalogue at all, and the field's own documentation is the instruction that empties it.
//
// samples/ecommerce does not hit this because it is one project: its Contracts.cs and its
// Capabilities.cs are two files in one assembly, and the errors are in Capabilities.cs.

using System.Threading;
using System.Threading.Tasks;
using FlowX;
using Corpus.Shared;

namespace Corpus.AcrossAssemblies;

/// <summary>The documented contracts-assembly layout.</summary>
[Specimen(
    "error factory in a referenced contracts assembly, as docs/07 §4 prescribes",
    Expect = Expect.Withheld,
    Truth = ["payment.gateway_unavailable/Unavailable"],
    Why = "The factory symbol has no DeclaringSyntaxReferences in this compilation, so the "
          + "trail ends. Refusing is correct; what is notable is that the convention the "
          + "reader was built to read puts the factory here.")]
[Capability("corpus.referenced_factory", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
public sealed class ReferencedFactory : ICapability<Order, Receipt>
{
    /// <inheritdoc />
    public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(input.Quantity > 0
            ? Result.Ok(new Receipt(input.Sku))
            : Result.Fail<Receipt>(SharedErrors.GatewayUnavailable()));
}

/// <summary>A pre-built error value in a referenced assembly.</summary>
[Specimen(
    "error held as a static readonly field in a referenced assembly",
    Expect = Expect.Withheld,
    Truth = ["payment.timeout/Unavailable"],
    Why = "Same boundary, reached through a field rather than a method.")]
[Capability("corpus.referenced_field", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
public sealed class ReferencedField : ICapability<Order, Receipt>
{
    /// <inheritdoc />
    public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(input.Quantity > 0
            ? Result.Ok(new Receipt(input.Sku))
            : Result.Fail<Receipt>(SharedErrors.Timeout));
}

/// <summary>One local error and one from across the boundary.</summary>
[Specimen(
    "a capability with one local failure and one from a shared library",
    Expect = Expect.Withheld,
    Truth = ["inventory.out_of_stock/Conflict", "billing.quota_exceeded/Forbidden"],
    Why = "The catalogue is all or nothing. The local code resolves and is discarded with "
          + "the rest, which is the design working — but it means one shared-library call "
          + "anywhere in a capability erases everything else it declares.")]
[Capability("corpus.mixed_sources", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
public sealed class MixedSources : ICapability<Order, Receipt>
{
    /// <inheritdoc />
    public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext ctx, CancellationToken ct)
    {
        if (input.Quantity <= 0)
        {
            return ValueTask.FromResult(Result.Fail<Receipt>(OrderErrors.OutOfStock(input.Sku)));
        }

        if (input.Quantity > 1000)
        {
            return ValueTask.FromResult(Result.Fail<Receipt>(SharedErrors.QuotaExceeded(input.Sku)));
        }

        return ValueTask.FromResult(Result.Ok(new Receipt(input.Sku)));
    }
}
