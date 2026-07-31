# ADR-0017: What must be true before the manifest schema is frozen at v1.0

**Status:** Accepted
**Date:** 2026-07-31
**Deciders:** Repository owner · Platform architecture
**Amends:** [ADR-0005](ADR-0005-manifest-as-build-artifact.md) ·
[13-AI-Native §3](../13-AI-Native.md#3-the-manifest-schema)

> **The freeze was a deadline three documents used and none defined.**
> [13-AI-Native](../13-AI-Native.md) gates its whole second half behind it and warns that
> *"adding a field after the freeze is expensive"*.
> [ADR-0014 §8](ADR-0014-derived-error-catalogue-vs-build-budget.md#8-consequences) makes it
> the point after which its own decision *"is materially harder to reverse"*, and uses
> *"P8 approaches manifest v1.0 freeze"* as one of its four revisit triggers.
> [20-Roadmap §3](../20-Roadmap.md#3-increment-detail) lists *"manifest v1.0 frozen"* first
> among P8's Must items. **None of them says what has to be true for it to happen**, and
> [ADR-0005](ADR-0005-manifest-as-build-artifact.md) — the record that created the artifact
> — does not mention freezing at all. This record writes the criteria down so the date can
> be read off the repository rather than guessed at.
>
> **Every criterion below is checkable against something that already exists**: the
> committed schema, `ManifestSchemaTests`, `ManifestIsComplete`, `flowx diff`'s
> classification rules, and the diagnostics the compiler raises today. Eight of them, and
> **not one holds as this is written** — seven name a gap that is in the repository right
> now, and the eighth is the bump itself. That is the point: a criterion nothing can fail on
> the day it is written is [PLAN §1.1](../../PLAN.md#11-what-this-plan-is-held-to)'s
> "satisfied but unenforced" row wearing a different hat.
>
> **One thing this record cannot fix, and names instead.**
> [ADR-0014](ADR-0014-derived-error-catalogue-vs-build-budget.md) is **Proposed**, and its
> decision has been deliberately left unmade. It is the record that governs the `errors`
> field, and freezing v1.0 is what makes that field unremovable. So the criteria below are
> now the mechanism that will eventually force it — see [F8](#f8--no-field-whose-record-is-still-proposed-is-frozen).
> **A deadline that arrives with an undecided ADR behind it decides that ADR by default**,
> in the direction of whatever shipped, and nobody has to choose for that to happen. That is
> worth saying a phase in advance rather than on the day.

---

## 1. Context

`flowx.manifest.json` is emitted on every build as a compiled-in constant, validated against
`schemas/flowx.manifest.schema.json` by `ManifestSchemaTests`, checked for completeness by
`ManifestIsComplete`, scanned by `ManifestContainsNoSecrets`, and diffed for compatibility by
`flowx diff` ([22-CLI §3](../22-CLI.md#3-the-classification-rules)). That much is real and is
what [ADR-0005](ADR-0005-manifest-as-build-artifact.md) delivered.

Two facts decide what a freeze has to mean.

* **The schema is a public contract.** ADR-0005's first negative is that it *"must be
  versioned and supported forever"*, and its root is `additionalProperties: false`, so a
  consumer cannot add anything to a manifest that validates — `extensions` is the one
  declared escape hatch and exists precisely so *"a one-off need never forces a breaking
  change"*.
* **The schema is not frozen yet, and the version says so.**
  `ManifestWriter.SchemaVersion` is **`0.1.0`**, and
  [20-Roadmap §4](../20-Roadmap.md#4-version-policy) allows breaking changes across the whole
  of `0.x`. Until the bump, removing a field costs nothing but a baseline update. After it,
  removing a field is `FLOWX-DIFF-200` at best and a broken consumer at worst.

So the freeze is not a ceremony. It is the moment the cheap direction of change closes, and
the only criteria worth writing are the ones that ask **what would still be cheap today and
expensive tomorrow**.

### What is wrong with the schema right now

Thirteen fields the committed schema declares are written by **nothing** — fourteen counting
`extensions`, which is the consumer's to write and is listed here because
[F6](#f6--the-escape-hatch-is-exercised-before-it-is-needed) turns on it. This was derived by
walking every `properties` block in `schemas/flowx.manifest.schema.json` against every name
`src/FlowX.Compiler/Emit/ManifestWriter.cs` writes, then confirmed against
`samples/ecommerce/flowx.manifest.baseline.json` — which is the only manifest in the
repository produced by a real compilation.

| Field | The fact exists in | Cost to close |
|---|---|---|
| `application.commit`, `application.builtAt` | the build | small; both are ignored by `flowx diff` ([22-CLI §2.2](../22-CLI.md#22-what-is-never-reported)) |
| `flow.owner`, `capability.owner` | nothing — there is no owner attribute | a DSL addition |
| `capability.source` | the symbol's location; the flow's `source` is already emitted from it | small |
| `capability.deprecated` | nothing declares deprecation | a DSL addition; `FLOWX-DIFF-204` already classifies the change |
| `capability.authorization.value` | `CapabilityAttribute.Permission` / `.Policy` | **small, and see [F5](#f5--flowx-diff-can-see-every-field-the-freeze-makes-permanent)** |
| `capability.authorization.approvedBy` | `[ApprovedBy]`, read from source by `PublicCapabilitiesAreReviewed` | small |
| `typeRef.schema`, top-level `schemas` | nothing generates JSON Schema for contracts | large; it is what OpenAPI, AsyncAPI and MCP descriptors are generated from |
| `event.partitionKey` | nothing declares one | a DSL addition |
| `event.producedBy` | the `Emit` steps the compiler already walks | small |
| `event.consumedBy` | nothing — there is no subscriber concept | needs a transport and a registry |
| top-level `extensions` | by design nobody's but the consumer's | see [F6](#f6--the-escape-hatch-is-exercised-before-it-is-needed) |

`samples/ecommerce` is the sharpest illustration: `payment.capture` declares
`Authorization.Permission`, and its manifest entry is `{"mode": "Permission"}` with **no
permission name** — while [13-AI-Native §3](../13-AI-Native.md#3-the-manifest-schema)'s
worked example shows `"value": "payment:capture"`, and the Guarantees list under it claims
*"every node carries `source` (file:line) and `owner`"*. No capability entry carries either.

*None of this is a defect in `ManifestWriter`. A field with no producer is what
[WP-22](../../PLAN.md#4-p1--compiler-hardening) closed for `triggers` and per-capability
`errors`, and the reason it was worth a work package is the same reason it is worth a
criterion here: a consumer reading the schema cannot tell "this application has no owner
recorded" from "the compiler never looked".*

---

## 2. Why this is a new record and not an amendment to ADR-0005

The obvious alternative is a section inside ADR-0005, and it was rejected for four reasons.

1. **It is a different decision.** ADR-0005 decides *that* the graph is emitted as a build
   artifact. This decides *when the contract around it stops being changeable*. The index's
   own rule is *"one decision per record — never bundled"*.
2. **Their revisit conditions are incompatible.** ADR-0005's is *"never expected — this
   decision is foundational"*. A freeze checklist is the opposite kind of clause: it is meant
   to be re-read every time a phase gate approaches, and it goes stale by design as producers
   land. Putting a list that must move inside a record that must not is how the two end up
   disagreeing.
3. **Precedent.** [ADR-0016](ADR-0016-postgres-journal-adapter.md) amended
   [ADR-0015](ADR-0015-journal-schema-and-durable-execution.md) from a new record rather than
   by editing it, and ADR-0015 carries an `Amended by:` header pointing back. The same
   arrangement is used here, for the same reason: the amended record keeps saying what it
   said, and the reader can see which clause moved and when.
4. **A freeze is a dated event.** It needs a record whose status can carry it — this one is
   amended on the day the bump lands, with the checklist state attached (see
   [§5](#5-how-the-freeze-itself-is-recorded)). ADR-0005 has nothing to hang that on.

---

## 3. Decision

**The manifest schema is frozen at v1.0 when, and only when, all eight criteria below hold.**
Each names what must be true, what checks it, and where it stands today. Where a criterion has
no check today, closing it includes writing one — a criterion verified by reading is a
criterion that will be read optimistically at a phase gate.

### F1 — Every field the schema declares has a producer, or the schema loses it

**Requires:** each of the thirteen fields tabled in [§1](#what-is-wrong-with-the-schema-right-now)
is either emitted by the compiler, or **deleted from the committed schema** before the bump.
Deleting is a legitimate outcome and is free today; after the freeze it is a breaking change
to the contract.

**Checked by:** a both-directions property-coverage test in `ManifestSchemaTests`, which walks
the committed schema's `properties` and asserts every path appears in at least one manifest of
a fixture corpus, or is named in an exemption list *with a reason in the test*. The shape is
`DiffCodeDocumentationTests`'s, which asserts the diff-code set in both directions and says
why the second direction is not padding: *"a documented code that nothing emits is a promise
in a table with no behaviour behind it."*

**Today:** unmet. Thirteen fields, no such test.

### F2 — No field is emitted as a constant standing in for a fact

**Requires:** every emitted value is derived from the compilation. `ManifestWriter` writes
`event.schemaVersion` as the literal `"1.0.0"` for every event, so a consumer pinning an event
major — which is what `FLOWX-DIFF-020` is about — reads a number the compiler invented.

**Why it is separate from F1:** an absent field is visible and a fabricated one is not. F1's
test passes on `event.schemaVersion` today, and that is the argument for this criterion
existing rather than being folded in.

**Checked by:** a test in which two events declared at different versions produce different
`schemaVersion` values — one that cannot pass while the value is a literal.

**Today:** unmet, for `event.schemaVersion`. No other emitted field is known to be constant,
and F1's corpus test is what would find the next one.

### F3 — `ManifestIsComplete` covers all four of Q3's nouns

**Requires:** the fitness function fails the build when a declared **policy** or **event** is
absent from the manifest, not only a flow or a capability. That is criterion **V7**'s unmet
half ([PLAN §1.1](../../PLAN.md#11-what-this-plan-is-held-to)) and quality goal **Q3**'s.

**Checked by:** `ManifestIsComplete` in `PublishedContractTests`, extended — and shown to fail
on a manifest with a policy or an event removed. The second half is not ceremony:
`performance.yml`'s two self-test jobs exist on the stated principle that *"a gate nobody has
seen fail is an assumption, not a gate"*, and this is the fitness function
[05 §12](../05-Architecture.md#12-architecture-fitness-functions) already records as written
*"as though it checked everything"*.

**Today:** unmet, and *vacuously* so, which is the trap. 05 §12 states it exactly: nothing in
this repository declares a policy, no policy executes, and `.Emit<T>()` publishes nothing — so
a completeness check over either would pass while proving nothing. **A green vacuous check is
worse than the gap it hides**, so F3 cannot be closed before F4.

### F4 — The two absent producers have landed

**Requires:** the **transactional outbox** (`WP-56`,
[11 §5](../11-Distributed-Runtime.md#5-the-transactional-outbox)) and the **policy engine**
(P4) exist. Both fields the freeze would make permanent describe run-time structure that
nothing in the repository produces:

* **Events.** [`FLOWX1024`](../diagnostics/FLOWX1024.md) is raised on **every** `.Emit<T>()`
  in the repository, at `Warning`, and says why: *"the manifest promises the event is
  published. In this release it is not."* Freezing `event.producedBy` and `event.consumedBy`
  while that diagnostic still fires freezes a topology against a promise the process does not
  keep.
* **Policies.** `.WithPolicy(...)` reaches the manifest as a per-step `policies` array with
  each policy's fixed stage, and `FlowX.Runtime` contains no policy engine at all, so a
  declared `Retry` *"is a manifest entry and nothing more"* (05 §12). The schema's **top-level**
  `policies` — named policy sets — is written by nothing, because named sets are not a thing
  the DSL has.

**Checked by:** `FLOWX1024` no longer being raised for a published emit, and F3's
non-vacuous completeness check, which cannot be written honestly until these land.

**Today:** unmet. WP-56 is P2 and not started; P4 has not started.

### F5 — `flowx diff` can see every field the freeze makes permanent

**Requires:** for every field in the committed schema, `flowx diff` either classifies a change
to it under a `FLOWX-DIFF-nnn` rule, or the field is listed in
[22-CLI §2.2](../22-CLI.md#22-what-is-never-reported)'s never-reported table with its reason.
A frozen field whose change nobody can see is a contract with no enforcement, and `flowx diff`
is the only enforcement ADR-0005 claims.

**The defect this criterion is written from runs the other way, and it is live.**
`FLOWX-DIFF-015` — **Breaking**, *"authorisation tightened, or the named permission changed"*
— compares `authorization.value` on both sides. `ManifestWriter` never writes that field, so
that half of a Breaking rule **cannot fire on any manifest FlowX produces**. It is the mirror
image of the six undocumented codes `DiffCodeDocumentationTests` was written for, and neither
that test nor `ManifestSchemaTests` can see it: one knows codes and documentation, the other
knows schema and instance. Nothing today knows fields and rules.

**Checked by:** extending `DiffCodeDocumentationTests` — or a sibling in the same project — to
a field↔rule map asserted in both directions.

**Today:** unmet, with one known counterexample and no instrument that would find a second.

### F6 — The escape hatch is exercised before it is needed

**Requires:** a manifest carrying `extensions` survives every verb the CLI has —
`flowx graph`, `flowx diff`, `flowx verify --cost` — and a change inside `extensions` is
classified, whether by a rule or by a row in 22-CLI §2.2.

**Why this is a freeze criterion and not a nice-to-have:** `extensions` is the *reason*
ADR-0005 was willing to accept "a public contract we must support forever" as a trade. If the
hatch does not work, the freeze has no relief valve, and the first one-off need after v1.0
becomes a schema change — the exact outcome ADR-0005 says the field exists to prevent.

**Today:** unmet, and untested in either direction. Nothing writes `extensions`, nothing reads
it, no fixture carries it, and 22-CLI says nothing about what a diff does with one.

### F7 — The bump is one atomic change, and a partial one fails the build

**Requires:** `0.1.0 → 1.0.0` lands in a single commit that moves `ManifestWriter.SchemaVersion`,
the committed schema, `samples/ecommerce/flowx.manifest.baseline.json` and the fixtures in
`ManifestSchemaTests` together; and `flowx diff` from the last `0.x` manifest to the first
`1.0.0` one reports `FLOWX-DIFF-200` — *"manifest schema major differs"*, Neutral — and **no
Breaking finding attributable to the bump itself**.

**Checked by:** the tests that already exist. `ManifestSchemaTests` validates emitted manifests
against the *committed* schema, so a half-done bump fails; `EveryPublicContractIsVersioned`
holds the manifest version to SemVer; `flowx diff` exits 1 on any Breaking finding, so the
second half is a command with an exit code and not a review.

**Today:** unmet by definition — the bump has not happened. Listed because it is the criterion
that says the freeze is a *build-verified* event rather than an announcement.

### F8 — No field whose record is still Proposed is frozen

**Requires:** every ADR that governs a field in the manifest is **Accepted** (or the field is
gone) at the moment of the bump.

**Today there is exactly one such field, and one such record.**
[ADR-0014](ADR-0014-derived-error-catalogue-vs-build-budget.md) governs per-capability
`errors`; it is **Proposed**; its
[§8](ADR-0014-derived-error-catalogue-vs-build-budget.md#8-consequences) records that once
v1.0 is frozen *"removing `errors` is a breaking change, so this decision is materially harder
to reverse after P8 than before it"*; its
[§4](ADR-0014-derived-error-catalogue-vs-build-budget.md#4-decision)(5) commits to
re-measuring the derivation on a non-synthetic project *before* the freeze; and its fourth
revisit trigger is the freeze itself. **The decision has been left unmade on purpose**, and
this record does not touch it.

What this criterion adds is only this: **the freeze cannot be the thing that makes the
decision.** If the bump lands with ADR-0014 still Proposed, the field is kept — not because
anyone weighed the argument, but because it was there — and the record's own §4(5) commitment
expires unnoticed on the same day. That is
[PLAN §9](../../PLAN.md#9-open-items-blocking-the-plan)'s stated failure mode — *"decisions
that will otherwise be taken by accident"* — arriving on a date this record has just made
readable.

**Checked by:** reading the ADR index. `docs/adr/README.md`'s Status column is the check, and
it is the one criterion here whose instrument is a human — which is why it is stated as a
precondition on a dated event rather than as a habit.

---

## 4. What these criteria deliberately do not require

Stated so that a later reader knows the omissions were chosen.

* **The eleven P8 consumers.** `flowx query`, MCP descriptors, OpenAPI/AsyncAPI generation,
  alerts, dashboards, test scaffolds, Studio — one of the eleven exists (`flowx graph`).
  Requiring them would make the freeze impossible, and it has the dependency backwards:
  13-AI-Native's argument is that each consumer is *"a constraint on what the manifest must
  contain"*, not that each must be built first. F1 and F5 are how that constraint is
  discharged — the field exists and its changes are visible — without the consumer existing.
  **The exception is `schemas`**, which is not a consumer's convenience but the input every
  contract-shaped consumer needs, and it is inside F1.
* **An admissible withheld rate for the error catalogue.**
  [B13 §9](../benchmarks/B13-error-catalogue-resolution.md#9-what-a-real-measurement-would-need)
  names the cheap substitute, and it is worth doing — but it changes what `errors` is *worth*,
  not what the schema must *contain*. It belongs to ADR-0014's revisit conditions, not to this
  checklist, and folding it in here would be this record deciding that one by the back door.
* **Any build-performance criterion.** What the manifest costs to produce is
  [ADR-0014](ADR-0014-derived-error-catalogue-vs-build-budget.md)'s subject and budget B12's.
  A freeze criterion that also gated build time would bundle two decisions in one record.
* **A stable `application.version`, `commit` or `builtAt`.** These change on every build by
  design and `flowx diff` ignores all three (22-CLI §2.2). F1 covers whether they are
  *emitted*; nothing here asks them to be stable.

---

## 5. How the freeze itself is recorded

When the bump lands, **this record is amended in place** — not superseded — with the date, the
commit, and the state of all eight criteria as verified on that day, in the way
[ADR-0016](ADR-0016-postgres-journal-adapter.md) records what ADR-0015 looked like against a
real database. A criterion waived rather than met is recorded as waived, with who waived it.

Anything discovered *after* the freeze that one of these criteria should have caught is
recorded here too, in italics beside the criterion it belongs to, rather than by rewriting the
criterion. The list is only useful if the next freeze — the schema will have a v2.0 eventually
— can read what this one missed.

---

## 6. Consequences

**Positive**
- **The deadline is datable.** Three documents used "the manifest v1.0 freeze" as a point in
  time and none could resolve it; it is now eight conditions, seven of which name a gap that
  exists today and can be pointed at.
- **Thirteen unproduced fields became a list with costs beside them**, most of them small, and
  four of them (`capability.source`, `authorization.value`, `authorization.approvedBy`,
  `event.producedBy`) facts the compiler already has and does not write down.
- **One live defect surfaced from asking the question**: a Breaking `flowx diff` rule that
  cannot fire because the field it compares is never emitted. It was found by writing F5, not
  by a test, which is F5's own argument for existing.
- **P8's first Must acquires an entry gate** instead of being the phase where the freeze
  happens because the phase started.

**Negative / accepted trade-offs**
- **This lengthens the road to P8.** F4 puts the outbox (P2) and the policy engine (P4) in
  front of the freeze, which is most of two phases. That is the honest consequence of refusing
  to freeze fields whose producers do not exist, and it can be waived under
  [§5](#5-how-the-freeze-itself-is-recorded) — visibly, by a named person, rather than by
  nobody noticing.
- **Four of the eight have no instrument yet.** F1, F2, F5 and F6 each need a check written;
  F3 extends `ManifestIsComplete`, which exists; F7's checks all exist already; and F8's
  instrument is a person reading the ADR index. A checklist whose checks are themselves
  unwritten is one phase away from being aspirational, which is the accusation this record
  exists to answer — and the four that are missing are named rather than assumed.
- **F8 names a coupling it cannot resolve.** ADR-0014 is Proposed by choice. This record can
  only ensure the freeze does not silently decide it; it cannot make the decision happen, and
  if the two collide at P8 the freeze is what has to wait.
- **The criteria are about the schema's *shape*, not its *fitness for eleven unbuilt
  consumers*.** A field can satisfy every criterion here and still turn out to be the wrong
  field once something reads it. `extensions` (F6) is the whole of the mitigation, which is why
  it is a criterion rather than a footnote.

**Revisit when:** any one of —
- a criterion is closed by a check that **could not have failed** — the vacuous-green failure
  F3 is written against, and the one this list is most likely to die of; or
- **P8 starts with F3 or F4 still open**, at which point the roadmap's phase order and this
  checklist disagree and one of them must move, deliberately; or
- the schema gains a field before the freeze — every addition re-opens F1 and F5 for that
  field, and adding one is cheap only while `schemaVersion` is `0.x`; or
- the freeze happens. This record is then amended per [§5](#5-how-the-freeze-itself-is-recorded)
  and its Revisit-when becomes the v2.0 question.

---

**Back to:** [ADR index](README.md) · [ADR-0005](ADR-0005-manifest-as-build-artifact.md) ·
[ADR-0014](ADR-0014-derived-error-catalogue-vs-build-budget.md) ·
[13-AI-Native](../13-AI-Native.md) · [22-CLI](../22-CLI.md) · [Roadmap](../20-Roadmap.md)
