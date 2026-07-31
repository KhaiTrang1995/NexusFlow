# ADR-0008: Use source-generated System.Text.Json with versioned schemas

**Status:** Accepted
**Date:** 2026-07-30
**Deciders:** Runtime team

## Context

FlowX serialises in four places: trigger payloads, journal state, outbox events
and telemetry attributes. Constraint C2 requires NativeAOT support, which
excludes reflection-based serialisation. Journal payloads additionally need to be
readable years later, potentially by a different code version.

Options considered:

- **A. Reflection-based `System.Text.Json`.** *Rejected:* incompatible with AOT
  and trimming (C2).
- **B. Newtonsoft.Json.** *Rejected:* same AOT problem, plus a heavier dependency
  in `FlowX.Abstractions`, which must have none.
- **C. Protobuf or MessagePack everywhere.** *Rejected as the default:* faster
  and smaller, but journals stop being human-readable — which destroys the
  debugging and incident-analysis value of replay, one of the platform's main
  selling points.
- **D. Source-generated `System.Text.Json` by default, with pluggable binary
  serialisers via `IPayloadSerializer`.** Chosen.

## Decision

We will use **source-generated `System.Text.Json` contexts** as the default
serialiser for all four surfaces, with a versioned schema stamp on every
persisted payload, and allow binary serialisers as an opt-in plugin — because AOT
compatibility is mandatory and journal readability is worth more than the ~20 %
commit-time saving a binary format would provide.

Every persisted payload carries `schemaVersion`. Contracts are immutable records;
schema evolution follows the additive-only rules in
[07 §5](../07-Capability-Model.md#5-versioning).

> [!IMPORTANT]
> **Accepted; almost none of it is built.** There is no generated STJ context, no
> `schemaVersion` stamp, no `IPayloadSerializer` interface and no binary plugin —
> and no journal, outbox or replay to serialise into. `[Sensitive]` redaction is
> **not** applied by a generated serialiser: the compiler records the members in
> the manifest, and exactly one sink consumes that list, the RFC 7807 body
> (`ProblemDetailsMapper`). `FLOWX1006`, cited below as the build-time failure
> for an unannotated contract, does not exist.
>
> What holds today is the decision's core: contracts are immutable records, no
> serialisation path uses reflection, and `EveryShippedRuntimeProjectIsAotAnalyzed`
> plus the *NativeAOT smoke test* job keep the AOT constraint honest. The
> `flowx.manifest.json` writer is hand-written and AOT-clean. The rest arrives
> with **P2** (journal payloads) and **P5** (redaction across every sink).

## Consequences

**Positive**
- NativeAOT and trimming work; cold start budget B10 is achievable.
- No reflection, consistent with ADR-0002.
- Journals and outbox rows are human-readable — `flowx replay --mode inspect`
  shows real values, and an operator can read a row directly in an incident.
- The same generated JSON Schema drives OpenAPI, AsyncAPI, MCP tool descriptors
  and validation. One schema, four consumers.
- `[Sensitive]` redaction is applied inside the generated serialiser, so no code
  path can bypass it.

**Negative / accepted trade-offs**
- **JSON is larger and slower than binary**: roughly 2–3× the bytes and ~20 %
  more commit time than MessagePack. Accepted for readability; the binary plugin
  exists for workloads that measure the difference and prefer the trade.
- **Contract types must be annotated** for the generated context; a type outside
  the context fails at build time (`FLOWX1006`) rather than at run time. This is
  friction, but it is friction at the right moment.
- **Schema evolution discipline is required.** Old journal rows must remain
  deserialisable for the retention window, so removing a field is a breaking
  change even when no live code uses it. Enforced by `flowx diff`.
- Polymorphic contracts need explicit type discriminators; implicit polymorphism
  is not supported.

**Revisit when:** journal serialisation is measured as the dominant cost in B7 on
production-like hardware — at which point the binary plugin becomes the default
for the journal only, keeping JSON for events and triggers.
