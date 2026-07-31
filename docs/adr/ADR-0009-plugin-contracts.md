# ADR-0009: Plugins depend only on FlowX.Abstractions and must pass a conformance suite

**Status:** Accepted
**Date:** 2026-07-30
**Deciders:** Platform architecture, Plugin team

## Context

Quality goal Q6 requires that a new transport can be added with zero changes to
`FlowX.Runtime`. Two failure modes are common in extensible platforms: first-party
extensions quietly use internal APIs that third parties cannot reach, so the
extension point is a fiction; and contracts stay signature-compatible while
behaviour drifts, so "implements the interface" stops meaning "works".

Options considered:

- **A. Plugins reference `FlowX.Runtime`.** *Rejected:* couples every plugin to
  runtime internals, so runtime refactoring becomes an ecosystem-breaking event,
  and plugins drag the runtime's dependency tree into consumers.
- **B. Plugins reference `FlowX.Abstractions` only; first-party plugins may use
  internals.** *Rejected:* creates a two-tier ecosystem where third-party plugins
  are structurally second-class.
- **C. Plugins reference `FlowX.Abstractions` only, first-party included, with a
  published conformance suite as the semantic contract.** Chosen.

## Decision

We will require **every** plugin — first-party and third-party alike — to depend
only on `FlowX.Abstractions` (a package with **zero** dependencies), and we will
publish `FlowX.Conformance.Tests` as a NuGet package that defines the *semantic*
contract for each extension point, because a syntactic interface is not a
sufficient specification for behaviour like backpressure, drain-on-shutdown and
fencing-token rejection.

Extension points are chosen deliberately, not sprinkled: each one is a
compatibility obligation held forever.

> [!IMPORTANT]
> **One half of this decision is enforced; the other is unwritten.** The
> dependency rule holds by gate: `AbstractionsHasNoDependencies` fails the build
> if `FlowX.Abstractions` gains any package or project reference, and
> `RuntimeDoesNotReferenceAnyPlugin` holds the opposite direction. The
> **conformance suite does not exist** — there is no `FlowX.Conformance.Tests`
> project or package — and there is one plugin, `plugins/FlowX.Http`, so the rule
> that "no abstraction ships with fewer than two real implementations" has not
> been tested against anything. Publishing the suite is a **P3** deliverable and
> the named mitigation for risks R3 and R8 in
> [05 §11](../05-Architecture.md#11-risks-and-technical-debt).

## Consequences

**Positive**
- First-party plugins are the proof that the extension points are sufficient —
  if Kafka needs an internal API, that is a design bug we discover ourselves.
  *Unproven: `FlowX.Http` is the only plugin, and the trigger extension point it
  would implement (`ITriggerSource`) is not declared.*
- Third parties can self-certify by running `dotnet test`; no gatekeeping
  committee, and the standard is machine-checkable. *There is nothing to run
  yet.*
- Runtime internals stay refactorable, because nothing outside depends on them.
- Consumers do not inherit a plugin's dependency tree through abstractions.
- Behavioural drift is caught: the conformance suite is versioned alongside the
  contracts.

**Negative / accepted trade-offs**
- **Some abstractions are more general than one implementation needs**, which is
  a mild form of speculative generality. Mitigated by the rule that no
  abstraction ships with fewer than two real implementations.
- **The conformance suite is real work** to write and maintain, and it must be
  extended whenever a contract's semantics are clarified.
- **In-process plugins are trusted with process-level access.** Permission
  declaration narrows this, but true isolation (out-of-process plugins) is
  deferred to v2 and stated as a known limitation in
  [15 §11](../15-Security.md#11-known-limitations).
- Adding a member to a contract requires a default implementation to avoid
  breaking existing plugins, which constrains contract design.

**Revisit when:** never expected for the dependency rule. The conformance-suite
scope is reviewed each phase gate as new failure modes are discovered in the
field.
