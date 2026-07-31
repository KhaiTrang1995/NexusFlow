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
> **Accepted; most of it is still unbuilt, and two clauses of this box expired at WP-52.**
> FlowX emits no STJ context of its own, there is no `IPayloadSerializer` interface and no
> binary plugin. `FLOWX1006`, cited below as the build-time failure for an unannotated
> contract, does not exist — it is blocked on the generated payload writer (**WP-59**).
>
> *This box said there was "no journal, outbox or replay to serialise into".* **There is a
> journal.** Since WP-52 `FlowX.Runtime` commits one row per step boundary for a `Durable`
> flow, and a value reaches a store only as a `JournalPayload`
> (`src/FlowX.Abstractions/Durability/`), whose `Of<T>` requires the generated
> `JsonTypeInfo<T>` — there is no overload that reflects over a type, so a contract outside a
> generated context cannot reach the journal at all. The AOT-safe serialisation this record
> chose is therefore a compile error to bypass on that path rather than a convention, which
> is more than the decision asked for and by a route it did not name. The outbox and replay
> are still absent: `OutboxWrite` and an `outbox_event` table exist
> ([ADR-0016](ADR-0016-postgres-journal-adapter.md)), nothing in the runtime stages a row
> ([FLOWX1024](../diagnostics/FLOWX1024.md)), and no replay command has been written. The
> `schemaVersion` stamp this decision requires on **every** persisted payload is on neither:
> `OutboxWrite.SchemaVersion` is declared on a contract nothing writes, and a journal row
> carries the flow's version and the capability's, not the payload's.
>
> *The outbox half of that paragraph is spent.* A `Durable` flow's `.Emit<T>()` stages an
> `OutboxWrite` in the step's own transaction, and its `Payload` is a `JournalPayload` built
> through the compilation's own generated context — so an **event body** is now AOT-safe by
> the same compile-time route a journal payload is, and `[Sensitive]` redaction reaches it
> without a second implementation. `OutboxWrite.SchemaVersion` is written, from the same
> constant the manifest's `events` array stamps, so the two cannot drift; it is still a
> constant rather than something a contract declares. Replay is still absent.
>
> *It also said `[Sensitive]` redaction has "exactly one sink, the RFC 7807 body".* **There
> are two since WP-52**, and the second holds the property by a different mechanism than
> this record predicted. `ProblemDetailsMapper` (`plugins/FlowX.Http`) still consumes the
> compiler's `SensitiveMembers` list for the RFC 7807 body. The journal's redaction is
> **structural rather than remembered**: `JournalPayload` exposes no accessor for the value,
> its only exit is `ToJson()`, and `ToJson()` replaces every declared member by name,
> case-insensitively, at every depth — so a store has no route to the object graph and
> therefore no route to serialise it unredacted. The Positive consequence below reads
> *"applied inside the generated serialiser, so no code path can bypass it"*: the property
> holds on this sink and the stated mechanism is not what holds it. The generated serialiser
> is still WP-59, and `RedactionCannotBeBypassed` stays blocked until **P5**, because it is
> blocked on every sink at once rather than on any one of them.
>
> What holds today is the decision's core: contracts are immutable records, no
> serialisation path uses reflection, and `EveryShippedRuntimeProjectIsAotAnalyzed`
> plus the *NativeAOT smoke test* job keep the AOT constraint honest. The
> `flowx.manifest.json` writer is hand-written and AOT-clean. Journal payloads arrived with
> **P2**; the generated writer and `FLOWX1006` are **WP-59**, and redaction across every sink
> is **P5**.

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
