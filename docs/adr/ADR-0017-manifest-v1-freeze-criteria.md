# ADR-0017: What must be true before the manifest schema is frozen at v1.0

**Status:** Accepted
**Date:** 2026-07-31
**Deciders:** Repository owner · Platform architecture
**Amends:** [ADR-0005](ADR-0005-manifest-as-build-artifact.md) ·
[13-AI-Native §3](../13-AI-Native.md#3-the-manifest-schema)
**Amended by:** [ADR-0021](ADR-0021-manifest-publishes-the-wait.md)

> **The freeze was a deadline three documents used and none defined.**
> [13-AI-Native](../13-AI-Native.md) gates its whole second half behind it and warns that
> *"adding a field after the freeze is expensive"*.
> [ADR-0014 §8](ADR-0014-derived-error-catalogue-vs-build-budget.md)#8-consequences) makes it
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

Thirteen fields the committed schema declares were written by **nothing** — fourteen
counting `extensions`, which is the consumer's to write and is listed here because
[F6](#f6--the-escape-hatch-is-exercised-before-it-is-needed) turns on it. This was derived by
walking every `properties` block in `schemas/flowx.manifest.schema.json` against every name
`src/FlowX.Compiler/Emit/ManifestWriter.cs` writes, then confirmed against
`samples/ecommerce/flowx.manifest.baseline.json` — which is the only manifest in the
repository produced by a real compilation.

**All of them have since closed, and the table is struck rather than deleted** — this list
is the record of what the criterion was written from, and a reader comparing the two should
see which rows moved and how. `capability.authorization.value` went first, for the reason
[F5](#f5--flowx-diff-can-see-every-field-the-freeze-makes-permanent) gives: it was not merely
unwritten, it was the half of a Breaking rule that could not fire. **The remaining twelve
closed on 2026-08-15, four by gaining a producer and eight by leaving the schema.** Which of
the two a field got was decided one field at a time, on one question — whether the fact
exists at compile time — and the answers are in the right-hand column.

| Field | The fact exists in | Outcome |
|---|---|---|
| ~~`application.commit`, `application.builtAt`~~ | **neither, as it turns out** | **struck, 2026-08-15.** The row said "the build", and the build does not have them: no SourceLink package is referenced, so no commit reaches the assembly, and `Deterministic=true` means the PE header carries a content hash rather than a time. `ManifestWriter`'s own header refuses both anyway — byte-identical output for identical source is what `flowx diff` rests on. A publish-time stamp is a property of a *release*, not of the document the compiler emits |
| ~~`flow.owner`, `capability.owner`~~ | nothing — there is no owner attribute | **struck, 2026-08-15.** Still a DSL addition, and one nobody has asked for. Re-addable when an attribute exists to read |
| ~~`capability.source`~~ | the symbol's location | **produced, 2026-08-15.** `CapabilityReader.DeclarationOf` takes the first of the type's source locations by path and line — ordered, because a `partial` type has one per part and Roslyn promises no order — and `ManifestWriter` relativises it through the same `Relativise` the flow's `source` uses |
| ~~`capability.deprecated`~~ | ~~nothing declares deprecation~~ **`[Obsolete("...")]`** | **produced, 2026-08-15.** The row called this "a DSL addition"; it is not, because C# already spells deprecation and every tool understands the spelling. A bare `[Obsolete]` publishes nothing — the field carries the *notice*, and an attribute with no message has no sentence to publish. `FLOWX-DIFF-204` now has something to compare |
| ~~`capability.authorization.value`~~ | `CapabilityAttribute.Permission` / `.Policy` | **closed** — read by `CapabilityReader`, carried on `StepModel`, written by `ManifestWriter`. `FLOWX1030` now refuses the stance that would leave it empty |
| ~~`capability.authorization.approvedBy`~~ | `[ApprovedBy]`, read from source by `PublicCapabilitiesAreReviewed` | **produced, 2026-08-15.** The reviewer, not the date: the schema field is one string, and the date stays where the fitness function reads it rather than being copied into a second place that can disagree |
| ~~`typeRef.schema`, top-level `schemas`~~ | nothing generates JSON Schema for contracts | **struck, 2026-08-15.** The cost was "large" and nothing has paid it. [§4](#4-what-these-criteria-deliberately-do-not-require) held `schemas` out of the consumers it declined to require, on the grounds that it is the input every contract-shaped consumer needs — which is an argument for building it, and F1's terms are produce **or** delete. Deleting is what is honest today |
| ~~`event.partitionKey`~~ | nothing declares one | **struck, 2026-08-15.** A DSL addition, and ordering-per-key is a promise no transport in the repository is asked to keep yet |
| ~~`event.producedBy`~~ | the `Emit` steps the compiler already walks | **produced, 2026-08-15.** And it was not idle: `ManifestReview.ReviewEvents` had been reading this array since it was written, so its orphan-event finding could not fire on any manifest FlowX produced — the `FLOWX-DIFF-015` defect again, one consumer over |
| ~~`event.consumedBy`~~ | ~~nothing — there is no subscriber concept~~ **a `Bus` trigger's topic** | **produced, 2026-08-15.** There *is* a subscriber concept and the compiler already wrote it down: `[BusTrigger("invoice.requested")]` names the event a flow is started by, and `trigger.topic` has carried it since WP-22. Scoped to events this application also emits, because an event only consumed here belongs to another build's manifest and publishing an entry for it would mean inventing its `schemaVersion` — the fabrication [F2](#f2--no-field-is-emitted-as-a-constant-standing-in-for-a-fact) refuses |
| top-level `extensions` | by design nobody's but the consumer's | **kept and exercised** — see [F6](#f6--the-escape-hatch-is-exercised-before-it-is-needed) |

*Two fields not in this table also left the schema on 2026-08-15, and they were missed when
it was compiled: the **top-level `policies`** array and the `policySet` definition it points
at. [F4](#f4--the-two-absent-producers-have-landed) names them — "named sets are not a thing
the DSL has" — but they were never carried up into §1's list, so the count of unproduced
fields this record has quoted throughout was short by two. That is the arithmetic the F1
corpus test now does instead of a person.*

`samples/ecommerce` was the sharpest illustration: `payment.capture` declares
`Authorization.Permission, Permission = "payment.write"` and its manifest entry was
`{"mode": "Permission"}` with **no permission name** — while
[13-AI-Native §3](../13-AI-Native.md#3-the-manifest-schema)'s worked example shows
`"value": "payment:capture"`. Note where the loss was: the sample declared the permission
all along, and the compiler dropped it between `CapabilityReader` and `ManifestWriter`. The
baseline now carries `"value": "payment.write"`.

The Guarantees list under that same worked example still claims *"every node carries
`source` (file:line) and `owner`"*, and no capability entry carries either.

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

> **State on 2026-08-15: six of the eight hold.** F1, F3, F4, F5, F6 and F8. Each criterion's
> **Today** below carries the date it closed and what closed it, per
> [§5](#5-how-the-freeze-itself-is-recorded); the two that remain are:
>
> * **[F2](#f2--no-field-is-emitted-as-a-constant-standing-in-for-a-fact)** — `event.schemaVersion`
>   is still one constant for every event. Closing it needs a way to *declare* an event's
>   version, which is a DSL addition and
>   [ADR-0018](ADR-0018-outbox-publication-and-ordering.md)'s revisit. **This is the only
>   criterion blocking the bump on something nobody has decided**, and it takes
>   `FLOWX-DIFF-020`'s second half down with it.
> * **[F7](#f7--the-bump-is-one-atomic-change-and-a-partial-one-fails-the-build)** — unmet by
>   definition until the bump lands. Its checks all exist; there is no work owing.
>
> Read this as: *the schema is ready to be frozen except for one field whose fate is a
> decision, not a task.* Whether to take F2 as a waiver under §5, close it with an attribute,
> or strike `event.schemaVersion` and narrow `FLOWX-DIFF-020` is the repository owner's call,
> and F8 exists to stop the bump making it by arriving.

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

~~**Today:** unmet. Thirteen fields, no such test.~~
**Met, 2026-08-15.** `ManifestSchemaTests.EveryFieldTheSchemaDeclaresIsWritten` walks the
committed schema's `properties`, resolving `$ref` and stopping on the `step`→`branches` cycle,
and asserts every path appears in at least one manifest of a three-document corpus written
through the real writer. `EveryExemptionNamesAFieldTheSchemaStillDeclares` is the second
direction. **One exemption, with its reason in the test:** `extensions`, whose producer is
deliberately outside this repository and which [F6](#f6--the-escape-hatch-is-exercised-before-it-is-needed)
covers instead. The corpus needed a fixture that declares what the samples do not —
`Models.FullyDescribed` carries a reviewed `Public` capability, an obsolete one, a
permissioned one, a policied step, a fallback, sensitive members on both contracts and an
event whose identity is its own `Bus` trigger's topic. Falsified by nulling one producer:
dropping `approvedBy` from that fixture fails the test naming
`capabilities.authorization.approvedBy`.

*The schema has since gained two — an `AwaitSignal` step's `signal` and `timeout`
([ADR-0021](ADR-0021-manifest-publishes-the-wait.md)) — and **this count did not move**,
because both are written by `ManifestWriter` in the commit that declared them and both are
classified by a `flowx diff` rule in the same commit. That is what this criterion asks of an
addition, stated in advance rather than discovered at the bump. Note the shape the corpus test
now has to take: `timeout` is written **conditionally**, when the author's declared duration
folds at compile time, so a test asserting that every manifest carries it would fail on a
manifest with no wait in it and one asserting nothing would pass vacuously. The corpus needs a
fixture that waits.*

### F2 — No field is emitted as a constant standing in for a fact

**Requires:** every emitted value is derived from the compilation. `ManifestWriter` writes
`event.schemaVersion` as the literal `"1.0.0"` for every event, so a consumer pinning an event
major — which is what `FLOWX-DIFF-020` is about — reads a number the compiler invented.

**Why it is separate from F1:** an absent field is visible and a fabricated one is not. F1's
test passes on `event.schemaVersion` today, and that is the argument for this criterion
existing rather than being folded in.

**Checked by:** a test in which two events declared at different versions produce different
`schemaVersion` values — one that cannot pass while the value is a literal.

**Today:** **still unmet, and the reason is now exact.** Nothing declares an event's version:
an event contract is a plain record, its identity comes from the type name by convention, and
there is no attribute to read a version off. So the check this criterion asks for — two events
at different versions producing different values — **cannot be written**, because two events
cannot be declared at different versions. That is a DSL addition and it is
[ADR-0018](ADR-0018-outbox-publication-and-ordering.md)'s revisit, not this record's to take.

*Two things learned on 2026-08-15 that narrow it further.* The value is **not invented**, which
is softer than this criterion assumed: `ManifestWriter.EventSchemaVersion` is the same constant
`OutboxWrite.SchemaVersion` stamps on the wire, so a consumer pinning an event major reads the
number that will actually arrive. What it cannot do is *vary*. And the harm has a second face
this record did not name: **`FLOWX-DIFF-020`'s "schema major bumped" half cannot fire either**,
because every event in every manifest FlowX produces carries `1.0.0`. That is the
`FLOWX-DIFF-015` shape a third time — a Breaking rule with a dead half — and it is worth noting
that neither [F1](#f1--every-field-the-schema-declares-has-a-producer-or-the-schema-loses-it)'s
corpus test nor [F5](#f5--flowx-diff-can-see-every-field-the-freeze-makes-permanent)'s
field↔rule map would find it: the field is written and it is mapped. **F2 is the only criterion
that catches a constant, which is the argument its own "why it is separate from F1" was
making.** *F2 is therefore the one criterion whose closure the freeze is still waiting on, and
it is waiting on a decision rather than on work.*

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

~~**Today:** unmet, and *half* of it vacuously so, which is the trap.~~
**Met, 2026-08-15, and neither half is vacuous.** The trap this criterion was written against
was a `policies` check passing because nothing declared a policy; five samples now declare
sixteen between them and ten kinds execute, so the check has something to be wrong about.

* **Policies.** `PoliciesSurvived` counts `.WithPolicy(...)` call sites in each flow type's
  IL — through nested display classes, because a fork's block is a lambda and
  samples/banking declares two of its seven inside one — and asserts the manifest carries as
  many policied steps. The independent side is the call site, not another part of the same
  document. **Falsified**: making `WritePolicies` skip a single-policy step fails the gate
  with `Banking declares 7 … and its manifest carries 6`, plus Healthcare 4→3 and
  Workflow 2→1.
* **Events.** `EventsAreDescribed` adds the catalogue's own half — an entry naming nobody at
  either end, an end naming a flow the document does not describe, and the two indexes of one
  event disagreeing. **Falsified**: stopping `producedBy` being written fails the gate on
  seven sample events.

*What made the policy half writable was not this record.* `FLOWX1032` has been deleted —
every declarable kind now executes, so [F4](#f4--the-two-absent-producers-have-landed)'s
"five of the nine" is history, and the vacuity F3 refused to ship went with it.

### F4 — The two absent producers have landed

**Requires:** the **transactional outbox** (`WP-56`,
[11 §5](../11-Distributed-Runtime.md#5-the-transactional-outbox)) and the **policy engine**
(P4) exist. Both fields the freeze would make permanent describe run-time structure that
nothing in the repository produces:

* **Events.** *This bullet said `FLOWX1024` is raised on **every** `.Emit<T>()` in the
  repository. It is not, any more.* A `Durable` flow's emitted event is staged by the step's
  own commit and drained by `PostgresOutboxPublisher`, and the rule now fires only where the
  chain cannot start — an `Ephemeral` flow, or a contract no source-generated
  `JsonSerializerContext` declares. *This bullet then said a **broker plugin** was missing and
  that `IEventPublisher` had one recording test double behind it; that expired at WP-56b, when
  `RedisStreamEventPublisher` shipped and `PublisherConformance` began holding both to one
  contract.* The freeze argument survives it intact, because it was never about the publisher:
  nothing in the repository writes `event.consumedBy` at all — a **consumer** is what would
  populate it, and the manifest describes one application's production side. Freezing those two
  fields would still freeze a topology only half of which anything produces.
* **Policies.** `.WithPolicy(...)` reaches the manifest as a per-step `policies` array with
  each policy's fixed stage. *This bullet said `FlowX.Runtime` contains no policy engine at
  all, so a declared `Retry` "is a manifest entry and nothing more". That expired when the
  engine landed `PolicyStage.Resilience`:* a published `Timeout`, `Retry`, `CircuitBreaker`
  and `Bulkhead` now describe run-time structure something produces. **The criterion is not
  met, and what is left of it is narrower and specific.** Four declarable kinds —
  `RateLimit`, `Idempotency`, `Cache` and `Audit` — still reach the array and are applied by
  nothing (`FLOWX1032`,
  [ADR-0025](ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md)), so freezing the
  array would freeze a vocabulary the runtime honours five of the nine declarable kinds of.
  The schema's
  **top-level** `policies` — named policy sets — is still written by nothing, because named
  sets are not a thing the DSL has.

**Checked by:** `FLOWX1024` no longer being raised for a published emit, and F3's
non-vacuous completeness check, which cannot be written honestly until these land.

~~**Today:** unmet, and half of it for a new reason.~~
**Met, 2026-08-15.** Both producers landed, and both fields this criterion was protecting are
now settled — one by being produced and one by leaving the schema.

* **Events.** The topology objection was that nothing writes `event.consumedBy` because "a
  consumer is what would populate it". **The objection was wrong about the repository, not
  about the principle.** A `[BusTrigger("…")]` *is* a consumer, the compiler has read its
  topic since WP-22, and `trigger.topic` was already in the document — the only thing missing
  was the index by event. Scoped to this application, which is the same scope the manifest
  has always had.
* **Policies.** `FLOWX1032` is deleted and `PolicyStages` carries eleven kinds, so the "five
  of the nine" this bullet complained of is gone; `RateLimit`, `Idempotency`, `Cache` and
  `Audit` are applied, and `Hedge`, `Fallback` and `CompensationRetry` joined them. The
  **top-level** `policies` array — named sets, which the DSL still does not have — was struck
  from the schema on the same day, along with the `policySet` definition it pointed at.

### F5 — `flowx diff` can see every field the freeze makes permanent

**Requires:** for every field in the committed schema, `flowx diff` either classifies a change
to it under a `FLOWX-DIFF-nnn` rule, or the field is listed in
[22-CLI §2.2](../22-CLI.md#22-what-is-never-reported)'s never-reported table with its reason.
A frozen field whose change nobody can see is a contract with no enforcement, and `flowx diff`
is the only enforcement ADR-0005 claims.

**The defect this criterion was written from ran the other way, and it is now fixed.**
`FLOWX-DIFF-015` — **Breaking**, *"authorisation tightened, or the named permission changed"*
— compares `authorization.value` on both sides. `ManifestWriter` never wrote that field, so
that half of a Breaking rule **could not fire on any manifest FlowX produced**. It was the
mirror image of the six undocumented codes `DiffCodeDocumentationTests` was written for, and
neither that test nor `ManifestSchemaTests` could see it: one knows codes and documentation,
the other knows schema and instance. Nothing yet knows fields and rules.

The value is now carried — `CapabilityReader` reads `Permission` / `Policy`, `StepModel`
carries it beside `AuthorizationMode`, `ManifestWriter` writes `authorization.value` — and
the first thing that happened when it landed was the sample's own gate failing:

```text
BREAKING (1)
  FLOWX-DIFF-015  capability payment.capture@2
      authorization value changed: (none) -> payment.write
```

`FLOWX1030` closes the other end, refusing at compile time the `Permission` or `Policy`
stance that names nothing — because a rule that compares a field is worth only as much as
the field's being populated, and a stance with no name has no value to move.

**What that fix does not do is meet this criterion.** One counterexample is closed; the
instrument that would find the second still does not exist, and the argument for it is
unchanged. Note how this one was found — while writing the criterion, by reading the rule
and the writer side by side. That is not a method that scales to a frozen schema.

**Checked by:** extending `DiffCodeDocumentationTests` — or a sibling in the same project — to
a field↔rule map asserted in both directions.

~~**Today:** unmet. The one known counterexample is closed; no instrument would find a second.~~
**Met, 2026-08-15.** `ManifestFieldCoverageTests` in FlowX.Cli.Tests is the map, asserted four
ways: every field the schema declares has a row; every row names a field the schema still
declares; every code a row names is one `src/FlowX.Cli` emits; and every field a row calls
silent has its phrase in 22-CLI §2.2. The second direction is the one that catches this
criterion's own defect — a rule outliving the field it compares now fails a test instead of
sitting dead.

*It found two on the day it was written*, and neither was a rule with a dead half: **a
trigger's `group` and `description` were classified by nothing at all.** `Describe` keys a
trigger on `kind`, `method`, `route`, `transport`, `topic` and `cron`, so a consumer-group
rename changes a published field and produces no finding of any severity. Both are recorded
in 22-CLI §2.2 rather than graded — whether a consumer-group rename deserves a code is a
severity question, and answering it here would be this record deciding one by the back door,
which is [§4](#4-what-these-criteria-deliberately-do-not-require)'s own objection to folding
things in. **The gap is now published instead of invisible, which is all F5 ever claimed to
do.**

### F6 — The escape hatch is exercised before it is needed

**Requires:** a manifest carrying `extensions` survives every verb the CLI has —
`flowx graph`, `flowx diff`, `flowx verify --cost` — and a change inside `extensions` is
classified, whether by a rule or by a row in 22-CLI §2.2.

**Why this is a freeze criterion and not a nice-to-have:** `extensions` is the *reason*
ADR-0005 was willing to accept "a public contract we must support forever" as a trade. If the
hatch does not work, the freeze has no relief valve, and the first one-off need after v1.0
becomes a schema change — the exact outcome ADR-0005 says the field exists to prevent.

~~**Today:** unmet, and untested in either direction.~~
**Met, 2026-08-15, and the hatch is kept rather than struck.** It was the one field where
striking was on the table and the argument against it is ADR-0005's own: the escape hatch is
what that record traded for accepting a contract it must support forever, so removing it
would take the relief valve off the freeze in the same commit that tightens the contract —
and the first one-off need after v1.0 would then be the schema change the field exists to
prevent. **Nothing produces one, and nothing should**; the keys are the consumer's. So it is
made real from the consumer's side instead.

`ExtensionsEscapeHatchTests` feeds the tool a manifest whose `extensions` block is nested two
deep and carries an array, a number and a string, and asserts `flowx graph`, `flowx verify
--cost` and `flowx diff` all accept it. Two of the five are the classification half: a diff
whose *only* difference is inside `extensions` — values changed, an array shortened, a key
added — reports no finding, and neither does adding the block to a manifest that had none.
The round trip matters, because a parser that silently dropped unknown properties would pass
by comparing two documents with nothing in them. 22-CLI §2.2 carries the row, so the silence
is a published decision and not an omission.

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

**Today:** unmet by definition — the bump has not happened, and it is the repository owner's
to make. Listed because it is the criterion that says the freeze is a *build-verified* event
rather than an announcement.

*What the bump now has to move has changed, on 2026-08-15.* `ManifestSchemaTests` no longer
holds only fixtures: `EveryFieldTheSchemaDeclaresIsWritten` reads the committed schema at run
time, so a bump that edits the schema without the writer fails there as well as at
validation. And the corpus has a fourth file to keep in step — `Models.FullyDescribed`, whose
whole purpose is to carry the fields the samples do not.

### F8 — No field whose record is still Proposed is frozen

**Requires:** every ADR that governs a field in the manifest is **Accepted** (or the field is
gone) at the moment of the bump.

**Today there is exactly one such field, and one such record.**
[ADR-0014](ADR-0014-derived-error-catalogue-vs-build-budget.md) governs per-capability
`errors`; it is **Proposed**; its
[§8](ADR-0014-derived-error-catalogue-vs-build-budget.md)#8-consequences) records that once
v1.0 is frozen *"removing `errors` is a breaking change, so this decision is materially harder
to reverse after P8 than before it"*; its
[§4](ADR-0014-derived-error-catalogue-vs-build-budget.md)#4-decision)(5) commits to
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

~~**Today there is exactly one such field, and one such record.**~~
**Met, and not by this record's doing.** [ADR-0014](ADR-0014-derived-error-catalogue-vs-build-budget.md)
is **Accepted** — decided 2026-08-10, amended by
[ADR-0080](ADR-0080-the-build-overhead-trigger-fired-and-0014-governs-the-gate.md) — so
`errors` is now governed by a record somebody argued rather than by a deadline. Read on
2026-08-15, `docs/adr/README.md` carries no `Proposed` row at all, which is the whole of what
this criterion asks.

**The failure mode it was written against did not occur, and that is worth saying plainly.**
The fear was that a freeze arriving with 0014 open would keep `errors` "not because anyone
weighed the argument, but because it was there". The argument was weighed, on its own
schedule, four days before the criteria were re-checked. *What this record can claim is only
that the collision was made visible a phase in advance; who resolved it, and on what evidence,
is 0014's and 0080's to say.*

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
  `authorization.value` has since been written and struck from the list, leaving twelve.
- **One live defect surfaced from asking the question**: a Breaking `flowx diff` rule that
  could not fire because the field it compares was never emitted. It was found by writing F5,
  not by a test, which is F5's own argument for existing — and it is now fixed, along with
  `FLOWX1030` to stop the field being emitted empty. F5 itself remains unmet: the
  counterexample is closed, the instrument that would find the next one is not written.
- **P8's first Must acquires an entry gate** instead of being the phase where the freeze
  happens because the phase started.

**Negative / accepted trade-offs**
- **This lengthens the road to P8.** F4 puts the outbox (P2) and the policy engine (P4) in
  front of the freeze, which is most of two phases. That is the honest consequence of refusing
  to freeze fields whose producers do not exist, and it can be waived under
  [§5](#5-how-the-freeze-itself-is-recorded) — visibly, by a named person, rather than by
  nobody noticing.
- ~~**Four of the eight have no instrument yet.** F1, F2, F5 and F6 each need a check written~~
  **Three of the four were written on 2026-08-15** — F1 as a schema-versus-corpus test, F5 as
  a field↔rule map asserted four ways, F6 as an extensions block put through every verb — and
  F3's extension to `ManifestIsComplete` was falsified twice rather than assumed. **F2 is the
  one still without an instrument, and cannot have one**: its check is two events at different
  versions differing, and nothing lets an author declare an event's version. F7's checks all
  exist already; F8's instrument is a person reading the ADR index, and on that reading it now
  holds.
- ~~**F8 names a coupling it cannot resolve.** ADR-0014 is Proposed by choice.~~ **It
  resolved itself**, on 2026-08-10, four days before the criteria were next read. The clause
  stands for the next time: this record can ensure a freeze does not silently decide an open
  ADR, and cannot make the decision happen.
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
  field, and adding one is cheap only while `schemaVersion` is `0.x`.
  ***This one has fired**, on 2026-08-01, for an `AwaitSignal` step's `signal` and `timeout`.
  [ADR-0021](ADR-0021-manifest-publishes-the-wait.md) is the record it produced, and its §4
  answers for the addition against all eight criteria — which is what this clause was written
  to make somebody do*; or
- the freeze happens. This record is then amended per [§5](#5-how-the-freeze-itself-is-recorded)
  and its Revisit-when becomes the v2.0 question.

---

**Back to:** [ADR index](README.md) · [ADR-0005](ADR-0005-manifest-as-build-artifact.md) ·
[ADR-0014](ADR-0014-derived-error-catalogue-vs-build-budget.md) ·
[13-AI-Native](../13-AI-Native.md) · [22-CLI](../22-CLI.md) · [Roadmap](../20-Roadmap.md)
