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
> **One half of this decision is enforced; the other has a shape and no substance.**
> The dependency rule holds by gate: `AbstractionsHasNoDependencies` fails the
> build if `FlowX.Abstractions` gains any package or project reference, and
> `RuntimeDoesNotReferenceAnyPlugin` holds the opposite direction.
>
> **This box said the conformance suite does not exist. That is now wrong, and
> the correction is smaller than it looks.** WP-51 added
> `tests/FlowX.Conformance.Tests`, holding `JournalConformance` and
> `LeaseStoreConformance` — the suites for two of the extension points in
> [17 §2](../17-Plugin-System.md#2-extension-contracts) and the first place this
> ADR's "semantic contract" is written down as executable assertions rather than
> as an intention. The mechanism works and is proved to: a store claims
> conformance by deriving and supplying itself, and
> `TheSuiteRejectsAStoreThatIsWrongTests` runs deliberately broken stores through
> it and asserts each is caught by the assertion whose name says why.
>
> **Something real has met it, and three clauses of this box expired at WP-53 saying
> otherwise.** *It read: "the only implementation held to the suite is an in-memory
> reference in the same project", "no store has ever run against a real database", and
> "there is still one plugin".* `plugins/FlowX.Postgres` is a second plugin, it depends on
> `FlowX.Abstractions` and nothing else, and it runs `JournalConformance` and
> `LeaseStoreConformance` **unmodified, from a different assembly**, against **PostgreSQL
> 16.13** — 45 conformance assertions green. That is precisely the arrangement
> [17 §5](../17-Plugin-System.md) describes for a third party claiming conformance,
> performed for the first time, and it is the first evidence that this decision's central
> mechanism works on something other than the reference implementation.
> The suite also *disagreed* with a real store where a document was wrong: three clauses of
> [ADR-0015](ADR-0015-journal-schema-and-durable-execution.md) did not survive it, recorded
> in [ADR-0016](ADR-0016-postgres-journal-adapter.md). **A conformance suite that has never
> failed a real implementation has not been tested either**, which is why that matters as
> much as the passes.
>
> **What has not changed is most of it, and none of it is bookkeeping.** Four of the six
> suites are unwritten, including `TriggerSourceConformance` — the one `plugins/FlowX.Http`
> would take — and `ITriggerSource` is still undeclared, so the transport plugin extends
> FlowX without the interface that would formalise it. A third durability contract has since
> shipped with no suite at all: `IRecoveryIndex` has two implementations that agree on which
> states count as abandoned by reading two comments and no assertion, which ADR-0016 names
> rather than closes. The project is a test project and is **deliberately not packable**, so
> the "third parties self-certify by running `dotnet test`" clause of this decision remains
> unavailable to any third party — the cross-assembly proof above was performed *inside this
> repository*, which is a different thing from publishing.
> `PluginsPassConformance` is still blocked
> ([21 §2.4](../21-Quality-Gates.md#24-gates-named-here-but-not-yet-enforced)); what narrowed
> is the reason, from "there is no suite" to "there is no trigger suite", and a gate does not
> move because its excuse improved. **Two plugins are not two implementations of one
> abstraction.** `IFlowJournal` and `ILeaseStore` have one shipped implementation each beside
> an in-memory reference in a test project, so the rule that "no abstraction ships with fewer
> than two real implementations" is still tested against nothing, and one store is one data
> point about a suite that could have encoded it — Redis is **WP-54**. Publishing a trigger
> suite is a **P3** deliverable and the named mitigation for risks R3 and R8 in
> [05 §11](../05-Architecture.md#11-risks-and-technical-debt).
>
> **Where the contracts live is settled by this ADR and was got wrong in the
> documents.** `docs/05 §5.3` used to place `IFlowJournal` and `ILeaseStore` in
> `FlowX.Runtime.Durable`, which a plugin referencing only `FlowX.Abstractions`
> could not have implemented. They shipped in `FlowX.Abstractions/Durability/`;
> the source tree has been corrected.

## Consequences

**Positive**
- First-party plugins are the proof that the extension points are sufficient —
  if Kafka needs an internal API, that is a design bug we discover ourselves.
  *This read "Unproven: `FlowX.Http` is the only plugin". It is still the only **transport**
  plugin and `ITriggerSource` is still undeclared, so the half of the claim this bullet was
  written about is untested. The other half was tested at WP-53 and behaved as predicted:
  `plugins/FlowX.Postgres` needed nothing outside `FlowX.Abstractions`, and building it
  found three schema clauses wrong and one shipped extension point that no production
  implementation registered — defects discovered by us, in the shape this bullet says they
  arrive ([ADR-0016](ADR-0016-postgres-journal-adapter.md)).*
- Third parties can self-certify by running `dotnet test`; no gatekeeping
  committee, and the standard is machine-checkable. *Not yet available to a third
  party: the two suites that exist live in a test project that is not packable, so
  there is nothing to reference and nothing for an outside store to derive from.
  What is no longer unproven is the derivation itself — a store in another assembly
  inherited both suites without an edit — so what WP-70 is missing is the package, not the
  mechanism.*
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
