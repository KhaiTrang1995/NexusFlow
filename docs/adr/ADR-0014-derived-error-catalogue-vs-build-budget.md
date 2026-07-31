# ADR-0014: Keep the derived error catalogue, and re-express the build-overhead budget it breaks

**Status:** Proposed
**Date:** 2026-07-31
**Deciders:** Repository owner · Platform architecture

> **Recommendation: keep the derivation ([option A](#a-keep-it-as-is-re-express-the-budget--recommended)), and re-express B12's budget per unit
> of work with a size-qualified P1 exit criterion — because deleting the feature does not
> buy the criterion either.** The pre-catalogue tree was measured at **+18.4 %** at 200
> flows against a **+8 %** budget. The catalogue takes that to **+77.1 %**, so it is what
> makes the miss enormous, but it is not what makes it a miss. Every option below leaves
> P1 failing its exit criterion as written; only this one keeps the one field a consumer
> can actually see.
>
> **The strongest argument against it is in [§5](#5-the-strongest-argument-against-the-recommendation)
> and it is a good one:** a budget renegotiated the first time it binds is not a budget,
> and R1's trigger exists precisely to stop this move.

---

## 1. What is being decided, in one page

The manifest now emits a per-capability `errors` catalogue — every error code and category
a capability can return, derived from the capability's own source by
`src/FlowX.Compiler/Analysis/ErrorCatalogueReader.cs`. Before it, the field was in the
schema and nothing wrote it: a consumer could not tell "this capability declares no
errors" from "the generator never looked".

**Deriving errors from code means binding every capability body.** 93 % of the reader is
`SemanticModel.GetTypeInfo`, asked of every expression in every capability, because an
expression's type is the only thing that identifies a failure path. So the generator's
cost is now a function of **how much capability implementation exists**, not of how many
flows there are.

| Measurement | Figure | Source |
|---|---:|---|
| `FlowPlanGenerator`, merge base before the feature (`1687072`) | **5.60 ms per flow** | [B12-scale §5.2](../benchmarks/B12-scale.md) |
| `FlowPlanGenerator`, the commit that added it (`c7ae70a`) | **27.28 ms per flow** | §5.2 |
| `dev`, with the catalogue read stubbed to `null` (the control) | **7.36 ms per flow** | §5.2 |
| `dev`, before → after the duplicated-bind fix (paired A/B) | **25.4 → 20.4 ms per flow** | §5.2 |
| Cost of the derivation itself | **~3 ms per capability type** | §5.2 |

*The last row is §5.2's own summary. The instrumented build it is drawn from — 50 flows,
262 capability types — recorded `ErrorCatalogueReader.Read` at **1 229 ms**, of which
1 118 ms is `SemanticModel.GetTypeInfo` across 39 964 calls, and 20 762 of those calls were
the duplicate since removed.*

| End-to-end build overhead | Overhead | 95 % CI |
|---|---:|---|
| 200 flows, **pre-catalogue** tree `a75c1f0` | **+18.4 %** | [+16.3, +19.9] |
| 200 flows, **with** the catalogue (before the 20 % fix) | **+77.1 %** | [+72.0, +80.6] |
| 50 flows, with the catalogue, **after** the 20 % fix | **+46.6 %** | [+42.8, +51.3] |
| **Budget** ([14-Performance §1](../14-Performance.md), B12) | **+8 %** | — |

*200 flows has not been re-measured since the 20 % fix. The 50-flow figure is the most
recent end-to-end number, and the two sizes are not comparable to each other: overhead is
a ratio whose denominator scales with something else.*

**What it costs, stated plainly:** roughly two thirds to four fifths of the generator —
and the generator is **97.6 %** of the generator-and-analyzer split, the other three
analyzers together accounting for 2.4 %. **What it buys:** the
only field in the manifest that answers "what can go wrong here" — which is what
[ADR-0007](ADR-0007-result-over-exceptions.md) promised when it chose `Result<T>` partly
so that failures "are enumerable in the manifest, so error catalogues, OpenAPI responses
and client SDKs are generated", and what
[13-AI-Native](../13-AI-Native.md) and P8's `flowx query` consume downstream.

Meanwhile [P1's exit criterion](../20-Roadmap.md) is *"a 200-flow synthetic solution
builds with ≤ 8 % overhead"*, and [R1's trigger](../20-Roadmap.md#6-standing-risk-review)
is *"build overhead > 8 %"* with the action *"freeze features; invest in the generator's
test harness and model layer"*. The criterion is not just a number on a dashboard; it is
wired to a risk whose remedy is a feature freeze.

---

## 2. The dilemma as posed is false, and that is the most important thing here

It is tempting to read this as "the error catalogue versus P1's exit criterion". It is
not, and the measurement that settles it is already recorded: **the pre-catalogue tree
`a75c1f0` measured +18.4 % at 200 flows** — 2.3× the budget, with the catalogue nowhere
in the build.

So:

* Deleting the feature outright moves 200 flows from roughly +77 % back to roughly
  +18 %. **It still fails.**
* Every option in §3 that reduces the derivation's cost lands somewhere between those two
  numbers. **None of them reaches +8 %.**
* Reaching +8 % at 200 flows requires the generator work B12-scale §8(1) already names,
  *in addition to* whatever is decided here.

The decision in front of the owner is therefore **not** "can we afford this feature".
It is **"is this feature worth being four fifths of a budget we are going to miss
anyway, and what should the budget say instead"**.

---

## 3. Options

Each is priced against the same question: what does it cost, what does it give up, who it
affects, and what it does to +8 %.

### A. Keep it as is; re-express the budget — *recommended*

**Cost:** ~3 ms per capability type, permanently, on every full build. 200 flows stays
far outside +8 % and P1 cannot exit against the criterion as written.

**Gives up:** the ability to say the budget was met without changing it. R1's trigger
fires and must be answered deliberately rather than by silence.

**Affects:** CI (the `scale-overhead` job stays advisory, and stays red); the roadmap
(P1's exit criterion must be restated, and honestly, or it becomes a criterion everyone
has agreed to ignore).

**What it does to +8 %:** nothing, directly. What it does is force the budget to become
falsifiable. B12-scale §8(2) already made this point before the catalogue existed: *"+8 %
is declared against no stated project size, which is how the same generator can pass at
one flow and fail at 25."* A budget expressed as **milliseconds per flow and milliseconds
per capability type** is testable at any size; a ratio is not, because its denominator is
the user's code.

### B. Make it opt-in (MSBuild property, or per capability)

**Cost to build:** approximately zero when off. This is the only option that genuinely
returns the pre-feature build time.

**What it gives up is larger than it looks, for three reasons.**

1. **Absence already means something else.** `ErrorCatalogueReader` marks a catalogue
   incomplete when a failure path cannot be resolved — a factory in a referenced assembly,
   a code composed at run time — and `ManifestWriter` then **omits the field entirely**.
   Its reason, in the reader's own words: *"a catalogue that is short by one is
   indistinguishable from one that is right, and a consumer cannot tell it is being lied
   to."* An empty **present** array is a positive statement: analysed, and returns no declared
   error. The `samples/ecommerce` baseline exercises exactly this — `inventory.release`
   ships `"errors": []`. An opt-out would give absence a second meaning, and the field's
   whole design is that it has three states, not two.
2. **A fourth state means a schema change**, and the schema is a public contract
   ([ADR-0005](ADR-0005-manifest-as-build-artifact.md)) that P8 freezes at v1.0. A
   manifest-level "error derivation was disabled" flag is the honest form of this option,
   and it is a breaking-shaped change made for a build-speed reason.
3. **`flowx diff` cannot see the distinction today, and would be actively harmed.**
   `ManifestCapability.Errors` deserialises a missing field to `[]`, so the CLI already
   collapses "withheld" into "resolved and empty". Comparing a withheld manifest against a
   populated one therefore emits `FLOWX-DIFF-017` **Breaking — "error code removed"** for
   every code, with the consequence text *"consumers branching on this code silently stop
   matching"*. Under today's always-on derivation that misfires only when a capability's
   completeness genuinely flips. Under opt-in it would misfire every time a local build is
   diffed against a CI build. **This is a latent defect regardless of the decision** — see
   [§7](#7-follow-ups-that-are-required-whichever-option-is-chosen).

**Affects:** every consumer, because a field that is sometimes there for a reason nobody
can read is worse than a field that is never there. The P8 AI surface is the sharpest
case: an agent deciding which failures it must handle cannot act on "maybe".

**What it does to +8 %:** returns builds that opt out to the pre-catalogue cost — about
**+18 % at 200 flows, which still fails**. So it does not buy the criterion either. It
buys a cheaper default and a field consumers cannot rely on.

### C. Make it declarative — an attribute or convention read cheaply

**Cost to build:** small. Reading an attribute does not bind a body.

**Cost to the author:** a second declaration of something the code already states. The
convention already exists and is documented — [07-Capability-Model §4](../07-Capability-Model.md)
requires that *"errors are declared in a static class per domain"*, for the stated reason
that *"error codes are enumerable — they appear in the manifest and in generated
OpenAPI"*. An `[Error]` attribute does not add a declaration mechanism; it adds a
**duplicate** one.

**Cost in truth, which is the real objection:** a declared list can be wrong, and a
derived one cannot. A derived catalogue has exactly two outcomes — correct, or absent.
A declared catalogue has a third — confidently wrong — and it is wrong in the direction
nobody notices, because the drift happens when someone adds a failure path and forgets
the attribute. That is the case where the catalogue matters most. This is the reason the
feature was built this way, argued in `c7ae70a`'s message and restated in
`ErrorCatalogueReader`'s own remarks.

**Affects:** authors (new obligation, enforced by nothing that is cheap to enforce — an
analyzer that checks a declared list against the code would have to bind the body, which
is the cost we were avoiding); consumers (a field that is *usually* right, which is the
worst kind).

**What it does to +8 %:** roughly the same as B, about +18 % at 200 flows. Still fails.

### D. Emit it in a separate, slower pass — `flowx manifest` in CI only

**This option does not exist in the shape the question assumes.** The manifest is not
written to `obj/`; `FlowPlanGenerator` emits it as a **compiled-in `const string`**
(`FlowXManifest.g.cs`), for a documented reason — writing files from a generator *"breaks
incrementality and races with the build"*. `flowx manifest --assembly` **extracts** that
constant; it does not compute a manifest. [22-CLI §1](../22-CLI.md) is explicit that the
CLI *"reads `flowx.manifest.json` and nothing else"* and has no project reference to any
FlowX assembly, held by the `CliDependsOnNothingButTheManifest` fitness test.

So this option means building a **second compiler front end** that re-analyses the source
outside the build. Then the manifest in the assembly and the manifest published to the
registry disagree by construction — which is [R7, *"manifest drift between build and
deploy"*](../05-Architecture.md), the risk whose whole mitigation is that
`flowx verify --runtime` can compare a hash.

**What it does to +8 %:** meets it, by moving the cost somewhere the budget does not
look. That is not the same as removing it, and CI time is not free either.

**Verdict: reject.** It trades a measured cost for an architectural one, and the
architecture explicitly refused this trade once already.

### E. Narrow the derivation

Three narrowings are available, and none is both safe and large.

* **By position** (skip binding in type-only syntactic positions). B12-scale §5.2 priced
  it at *perhaps 6 %* and refused it: *"get the list of positions wrong and the catalogue
  starts claiming to be complete when it is not, which is the one failure mode the whole
  design of this reader exists to prevent."*
* **By reachability** (only capabilities a flow has a step for). The generator reads every
  `[Capability]` in the compilation deliberately — *"including one no flow has a step for
  yet"* — and a capability library whose manifest describes nothing is the same gap this
  feature closed. Saves nothing on a project whose capabilities are all used.
* **By a syntactic pre-filter on names** (bind only invocations whose simple name could
  name an `Error`-producing member). 64 % of the reader's cost is asking invocations their
  type, and only 1 048 of 4 268 of them are `Error`-typed, so the headroom is real —
  perhaps a third of the reader. But a name-based filter fails on aliases and indirection
  by **silently omitting**, not by marking the catalogue incomplete, which is the same
  failure mode as the first narrowing.

**What it does to +8 %:** the safe subset is worth single-digit percentages of the
generator. It changes nothing about the decision.

---

## 4. Decision

**We will keep the derived error catalogue, and change what the budget says instead**,
because the alternative options each trade a fact a consumer can rely on for a build-speed
improvement that does not reach the criterion anyway.

Concretely, this decision commits to five things:

1. **B12's budget is re-expressed per unit of work.** A ratio against the user's own
   compilation cannot be met or missed at a stated size. The replacement is two numbers —
   **milliseconds per flow** and **milliseconds per capability type** — recorded with the
   project they were measured on, with `+8 %` retained as a *reporting* figure at a named
   size rather than as the gate.
2. **P1's exit criterion is restated to match**, with a size, and it must remain hard
   enough to fail. A criterion that this evidence would pass is not a criterion.
3. **R1 stays live and is answered, not silenced.** Its trigger has fired. The answer
   recorded here is: the overhead is a constant factor in a linear generator
   (`flows^0.91`, CI [0.82, 1.08]), which B12-scale §8 already argues is *"an optimisation
   backlog rather than an architectural defect"* — but the feature-freeze remedy is
   deliberately **not** invoked, and that is a decision the owner is making, not a
   conclusion the data forces.
4. **The `scale-overhead` CI job stays advisory** until a pass is recorded, per B12-scale
   §8's reasoning about gates people learn to ignore.
5. **The derivation is re-measured on a non-synthetic project before manifest v1.0 is
   frozen in P8.** See [§6](#6-what-we-are-not-certain-of) — the extrapolation to real
   projects is currently unmeasured in both directions.

---

## 5. The strongest argument against the recommendation

**A budget renegotiated the first time it binds is not a budget.**

This is the objection, and it should be recorded in its strongest form rather than
answered away:

* B12 and B12-scale exist *because* the +8 % clause had never been measured and therefore
  *"could never have fired"*. It has now fired, and the immediate response is to change
  the clause. That is the failure mode the whole benchmark programme was built to prevent.
* B12-scale documents the same pattern happening by accident: a headline split that
  *"survived four working packages and stopped being true during them, and nothing
  noticed"*. Rewriting a budget around the feature that broke it is that pattern happening
  on purpose.
* **We are being asked to set a new number without knowing what the new number should be.**
  The ~3 ms per capability type is measured on a synthetic project whose capability bodies
  are *deliberately minimal*. A per-capability budget calibrated on that is calibrated on
  the cheapest possible input.
* The manifest's other fields together cost a fraction of this one — the stub control puts
  everything else the generator does at 7.36 ms per flow against the catalogue's ~15. And
  the field's first consumers (`flowx query`, MCP descriptors, generated OpenAPI responses)
  are all **P8**, five phases away and not started. Paying now, in full, for a consumer
  that does not exist yet is a real objection and not a cheap one.

The honest weighing is this: the objection is about **process discipline**, and it is
right about the process. The counter is about **what the alternatives actually deliver** —
none of them meets the criterion, and each one that reduces the cost does so either by
making the field less trustworthy or by moving the cost somewhere the budget cannot see
it. If the owner weights process discipline above that, **option B with an explicit
manifest-level flag** is the defensible second choice, and it should be taken with the
schema change made honestly rather than by overloading absence.

---

## 6. What we are not certain of

Stated so the decision is made with the uncertainty visible, not after it.

* **One machine, one synthetic project.** 4 logical cores, load average 2–4, and a harness
  that refuses a verdict when the machine is too noisy. Reproduced across sessions, but not
  on other hardware, and B12-scale is explicit that it *"does not claim +18.4 % is the
  number on your machine"*.
* **"Real projects pay more" is an extrapolation, not a measurement**, and it has an
  unmeasured counterweight. Real capability bodies are larger, which costs more per
  capability. But the synthetic project has **262 capability types across 50 flows (5.2 per
  flow)** and real projects *reuse* capabilities across flows, which would give a real
  200-flow solution proportionally fewer capability types than the synthetic one. **The two
  effects point in opposite directions and neither has been measured.** Nobody currently
  knows whether a real 200-flow solution pays more or less than this measurement predicts.
* **Nobody knows the real withheld rate.** The only non-synthetic data point is
  `samples/ecommerce`: 4 capabilities, 4 complete catalogues — written by the same people
  who wrote the reader, against the exact factory convention it was built to read. If real
  projects resolve at, say, 60 %, the field is far less valuable than this ADR assumes and
  option B's objections weaken considerably. **This is the single measurement most likely
  to change the decision, and it is cheap to take.**
* **Incremental and IDE builds have never been measured.** Every figure here is
  `--no-incremental` full build. The catalogue is read through a per-capability incremental
  pipeline, so the inner loop may be far cheaper — or may not: the transform reads the
  whole `Compilation` to follow factories into other files, and there is no test in the
  repository covering whether an edit to an error factory correctly invalidates the
  catalogue of a capability declared in a different file. **That is a correctness question
  as much as a performance one, and it is currently open.**
* **200 flows has not been re-measured since the 20 % duplicated-bind fix.** The +77.1 %
  figure is from the tree before it.

---

## 7. Follow-ups that are required whichever option is chosen

1. **`flowx diff` collapses "withheld" into "resolved and empty".**
   `ManifestCapability.Errors` defaults to an empty list, so a manifest with the field
   absent compares as a manifest with no errors, and a capability whose catalogue becomes
   unreadable — a factory moved into a referenced assembly is enough — reports every code
   as `FLOWX-DIFF-017` **Breaking**. The compiler is careful about three states; the first
   consumer of the manifest sees two. This is a false-breaking-change generator in a gate
   that blocks merges.
2. **[14-Performance §1](../14-Performance.md)'s B12 row still reads `+0.4 %`** and links
   only [B12.md](../benchmarks/B12.md), which was recorded at WP-14 on a one-flow sample
   before this feature existed. B12-scale §7 says outright that the unqualified +8 %
   *"is not currently met by any project big enough to notice"*. The budget table should
   link both documents and carry the size qualification.
3. **`ErrorCatalogueReader` and `CapabilityErrorModel` cite
   `docs/07-Capability-Model.md §7`** for the static-factory rule. That rule is in **§4,
   Contract design**; §7 is Idempotency.

---

## 8. Consequences

**Positive**
- The manifest keeps a field that cannot be wrong: correct, or explicitly absent. Nothing
  else in the document has that property, and it is the one consumers act on when deciding
  what failures to handle.
- [ADR-0007](ADR-0007-result-over-exceptions.md)'s stated benefit — enumerable failures —
  stops being an aspiration. It was one for the whole of P0.
- The build-overhead budget becomes falsifiable at any project size instead of only at the
  size it happens to be measured on, which is a defect it had before this feature and
  independently of it.
- P8's `flowx query`, MCP descriptors and generated OpenAPI responses can be built against
  a field that is trustworthy, rather than one that is trustworthy in CI.

**Negative / accepted trade-offs**
- **P1 does not exit against its published criterion, and the criterion is being changed
  after it failed.** This is the cost of the decision and it should not be softened.
  Mitigated only by restating the criterion in a form that can still fail, and by leaving
  R1 open.
- **Build cost now tracks how much capability implementation a project contains.** A team
  that doubles its capability count doubles this cost, and nothing in the DSL warns them.
- **The remaining derivation cost is not optimisable without giving something up.** Three
  routes were considered and refused; the fourth (§3 E's name pre-filter) fails silently in
  the same way. The realistic ceiling on further optimisation is single-digit percentages.
- **A field with a real per-build price now sits in a schema that P8 freezes.** Once v1.0
  is frozen, removing `errors` is a breaking change, so this decision is materially harder
  to reverse after P8 than before it.
- **The measurement base is thin**: one machine, one synthetic project, minimal capability
  bodies, and no incremental-build or real-project data.

**Revisit when:** any one of —
- a **real** (non-synthetic) project is measured and the derivation costs more than
  **2× the ~3 ms per capability type** recorded here; or
- the withheld rate on real code exceeds **20 %** of capabilities, at which point the
  field is unreliable enough that a declared list is no longer obviously worse; or
- an incremental-build measurement shows the inner loop paying full derivation cost per
  edit; or
- **P8 approaches manifest v1.0 freeze** — this decision must be re-affirmed before the
  schema becomes unremovable.

---

## 9. Evidence recorded after this ADR was proposed

**Nothing above has been changed.** §4's decision and the recommendation at the top stand
exactly as written, and re-deciding them is the owner's to do. This section exists so a
reader of the ADR discovers the measurements rather than only the questions.

WP-36 went after §6's four open items, and WP-37 fixed the defect it found on the way. The
results are in
[**B13 — how often the derived error catalogue can actually be read**](../benchmarks/B13-error-catalogue-resolution.md),
which also states, at length, why its headline number is weaker than it looks.

| §6 open item | What was found |
|---|---|
| The real withheld rate | **42 % withheld** over a corpus of 38 capabilities — 39 % as WP-36 first measured it, plus the one capability that moved from a wrong catalogue to an honest refusal when WP-37 closed the defect below. The corpus was written by the same hand that reports the rate, so it is a property of that file list and not of any codebase. B13 §2 argues that no admissible rate can be taken until FlowX has users, and §9 names a cheaper substitute that does not require them. |
| Whether an error-factory edit invalidates a catalogue elsewhere | **It invalidates correctly**, covered by four tests. The mechanism is that `ForAttributeWithMetadataName` combines with the `CompilationProvider`, so the transform re-runs for every capability on every edit anywhere — which answers the incremental-cost item below as a by-product. |
| Incremental and IDE builds | **The inner loop pays the full derivation cost per edit, by construction**, not a fraction of it. The revisit trigger phrased against this can be considered answered without a timing run. |
| Whether real projects pay more | Both effects measured. One two-term model fits nine points within 1.8 %: `217 kB/flow + types × (92.7 kB + 7.7 kB per extra statement)`. They are the same order of magnitude and cancel; B13 §6.2 gives the break-even table. |

**Two findings that bear on the reasoning above rather than on §6, and that the owner
should weigh before re-affirming §4:**

1. **The derived catalogue *could* be wrong — and WP-37 fixed it.** As WP-36 first
   measured, four of the 38 capabilities were published with a catalogue that disagrees
   with what they return: three claiming `errors: []` for a capability that returns a code,
   one claiming a code it cannot return. That contradicted §8's first positive consequence
   (*"a field that cannot be wrong"*) and the premise §3 C uses to reject a declared list
   (*"a derived one cannot"*).

   **WP-37 made the reader incapable of both.** It no longer identifies a failure by
   searching the class for an `Error`-typed expression; it starts at the capability's
   `ExecuteAsync` and follows the value out, so a failure carried inside a `Result<T>` is
   traced or refused, and a member the entry point never reaches is never read. `errors:
   []` is now published only where every path was traced to a success. The corpus reports
   **zero** wrong catalogues and asserts it as a property of every specimen.

   What it cost, and what it did to the arguments above:

   * **Coverage: less than B13 predicted.** Withheld 39 % → 42 %, not the 47 % B13 §5.3
     estimated, because two of the three under-reporting cases became *correct* catalogues
     rather than withholds. Resolved went 18 → 21.
   * **`samples/ecommerce`'s manifest baseline is byte-identical**, and the generator got
     cheaper: −5.7 % allocated on a 50-flow subject, with byte-identical generated output.
   * **§8's first positive consequence stands as written.** So does §3 B: the argument that
     the field's existing wrongness reduced the marginal harm of option B's fourth state is
     withdrawn.
   * **§3 C's absolute form is restored, with a sharper reason than it gives.** A derived
     catalogue is not inherently incapable of being wrong — it was wrong, and a reader had
     to be changed. What a hand-maintained list cannot match is that the defect was fixable
     in one place and a test now holds it fixed.
   * **None of this bears on the cost question §4 is deciding.** It was a correctness issue
     and it has been settled separately, which is what B13 §8 item 1 asked for.
2. **[07-Capability-Model §4](../07-Capability-Model.md)'s prescribed layout guarantees an
   empty result.** The block that mandates the static error class also says contracts live
   in a dedicated assembly, and the reader cannot follow a symbol into a referenced
   assembly. `samples/ecommerce` does not show this because it is one project. This is
   independent of which option is chosen and should be reconciled either way — it joins §7's
   list.

B13 §8 states what this evidence would change if the owner agrees with it, as a
recommendation.

---

**Back to:** [ADR index](README.md) · [B12 at scale](../benchmarks/B12-scale.md) ·
[B13 catalogue resolution](../benchmarks/B13-error-catalogue-resolution.md) ·
[Roadmap](../20-Roadmap.md)
