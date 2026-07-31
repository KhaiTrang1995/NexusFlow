// Compiled into a SEPARATE assembly and referenced, not added to the corpus compilation.
//
// This file is the whole reason the corpus needs two assemblies. docs/07-Capability-Model.md
// §4 says, in the same block that mandates the static error class, that "contracts live in
// a dedicated assembly with no dependencies" and are "referenced by nothing else" — so a
// team following the documented layout puts its error factories exactly here, one assembly
// away from the capabilities that call them, where ErrorCatalogueReader cannot see their
// bodies.

using FlowX;

namespace Corpus.Shared;

/// <summary>Errors declared in a contracts assembly, as docs/07 §4 lays out.</summary>
public static class SharedErrors
{
    /// <summary>The gateway is down.</summary>
    public static Error GatewayUnavailable() =>
        new("payment.gateway_unavailable", "Payment gateway unavailable.", ErrorCategory.Unavailable);

    /// <summary>The tenant is over quota.</summary>
    public static Error QuotaExceeded(string tenant) =>
        new("billing.quota_exceeded", $"Tenant '{tenant}' is over quota.", ErrorCategory.Forbidden);

    /// <summary>A pre-built error held as data, not produced by a call.</summary>
    public static readonly Error Timeout =
        new("payment.timeout", "The gateway did not answer.", ErrorCategory.Unavailable);
}
