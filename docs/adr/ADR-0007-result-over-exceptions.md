# ADR-0007: Return `Result<T>` for business outcomes; reserve exceptions for defects

**Status:** Accepted
**Date:** 2026-07-30
**Deciders:** Platform architecture

## Context

"Out of stock", "payment declined" and "order not cancellable" are ordinary
business outcomes, not failures of the program. Modelling them as exceptions
causes four concrete problems: they are invisible in a method signature; they
cost 5–20 µs each on the throw path, which is catastrophic against a 5 µs
platform budget (Q1); they are indistinguishable from genuine defects in
telemetry; and they cannot be enumerated for the manifest, so the error catalogue
cannot be generated (Q3).

Options considered:

- **A. Exceptions for everything.** Idiomatic in older .NET code. *Rejected:*
  fails Q1 and Q3, and conflates business outcomes with defects.
- **B. Nullable returns / `TryX` patterns.** *Rejected:* cannot carry an error
  code, category or data.
- **C. `Result<T>` as a readonly struct with a typed `Error`.** Chosen.
- **D. A full functional Either/monad library.** *Rejected:* higher learning
  cost, heavier syntax, and pulls a dependency into `FlowX.Abstractions`, which
  must have none.

## Decision

We will represent expected business outcomes as `Result<T>` — a `readonly struct`
carrying either a value or an `Error(Code, Message, Category, Data)` — and treat
exceptions as **defect or infrastructure-fault signals only**, because business
outcomes must be enumerable, allocation-free and distinguishable from bugs.

`ErrorCategory` is a closed set (`Validation`, `NotFound`, `Conflict`,
`Forbidden`, `Unavailable`, `Internal`) and is the single mapping point to every
transport: HTTP status, gRPC status, retryability, dead-lettering.

## Consequences

**Positive**
- Failure paths are visible in signatures and enumerable in the manifest, so
  error catalogues, OpenAPI responses and client SDKs are generated.
- No allocation and no throw cost on the failure path — Q1 holds even when things
  go wrong, which is when latency matters most.
- Retryability is a property of the category, so retry policy is correct by
  default instead of by convention.
- Unhandled exceptions become a **defect signal**
  (`flowx_capability_unhandled_total`) rather than being lost among expected
  failures — a genuinely useful alert.

**Negative / accepted trade-offs**
- **It is not idiomatic .NET**, and it is the second-most-common early complaint
  after the no-capability-calls-capability rule. Mitigated by analyzer
  `FLOWX1016`, code fixes, and templates that show the pattern immediately.
- **Result propagation is verbose** in capabilities with several failure modes.
  Mitigated by `Result.Try`, pattern matching and error factory classes; not
  fully solved.
- **Boundary discipline is required**: adapters throw (ADO.NET, HttpClient), so
  capabilities must translate. The runtime catches at the capability boundary as
  a safety net, but a capability that leaks exceptions is a bug and is reported
  as one.
- Mixing the two models inside a capability is possible and confusing; review
  guidance and the analyzer both push toward `Result` for anything a caller might
  reasonably handle.

**Revisit when:** C# gains first-class discriminated unions, which would make a
language-native representation strictly better than a hand-rolled struct.
