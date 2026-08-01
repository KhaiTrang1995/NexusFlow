# ADR-0021: The manifest publishes what a flow waits for, and how long it declared to wait

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Repository owner · Platform architecture
**Amends:** [ADR-0017](ADR-0017-manifest-v1-freeze-criteria.md) ·
[22-CLI §2.2](../22-CLI.md#22-what-is-never-reported)

> **This record exists because [ADR-0017](ADR-0017-manifest-v1-freeze-criteria.md)'s third
> Revisit-when has fired.** That clause reads: *"the schema gains a field before the freeze —
> every addition re-opens F1 and F5 for that field, and adding one is cheap only while
> `schemaVersion` is `0.x`"*. WP-63 gave a `Durable` flow a suspension point; the manifest
> writes `"kind": "AwaitSignal"` and **nothing else**, so this is a request to add two fields
> to a schema whose freeze is being counted down to. The clause says such a request is a
> decision, not a patch. This is that decision.
>
> **The whole of the argument is that the two fields arrive with their producers and their
> diff rules already attached.** ADR-0017's F1 counts *schema-declared fields that nothing
> writes*; twelve of those are outstanding. A thirteenth and fourteenth added the ordinary way
> — declared now, produced later — would push that count to fourteen and lengthen the road to
> the freeze. Declared, produced and classified in one change, they leave it at twelve.
> **That is the price of admission, and this record is what fixes it as the price.**

---

## 1. Context

A `Durable` flow can suspend. `.AwaitSignal<TSignal>(timeout)` compiles to a
`StepKind.AwaitSignal` node, `FlowEngine` stops there, the instance is sealed `Suspended` at
its resume frontier, and `FlowHost.SignalAsync` delivers a signal that resumes it
([06 §6](../06-Execution-Engine.md#6-suspension-waiting-without-holding-resources),
[ADR-0015](ADR-0015-journal-schema-and-durable-execution.md)).

`ManifestWriter.WriteStep` publishes that step as:

```json
{ "id": 1, "kind": "AwaitSignal" }
```

and the committed schema's `step` object is `additionalProperties: false` with no property
that could carry more. So:

* **Two waiting flows are indistinguishable in the manifest.** `offer.accept` waiting for a
  countersignature and an onboarding flow waiting for an email verification produce byte-identical
  step entries. A reader — human, `flowx graph`, or an agent — can see *that* the flow stops
  and cannot see *what would restart it*.
* **`flowx diff` cannot report that a flow changed which signal it waits for.** Not because
  the rule is missing, but because there is no field for a rule to read. That is the same
  shape as the defect ADR-0017's F5 was written from — `FLOWX-DIFF-015` comparing an
  `authorization.value` nothing wrote — with the halves reversed: there, a rule existed and
  the field did not; here, neither does.

### 1.1 Why this is not "just another unpublished field"

The twelve fields in [ADR-0017 §1](ADR-0017-manifest-v1-freeze-criteria.md)#what-is-wrong-with-the-schema-right-now)
are all facts *about* something the manifest already describes — an owner, a source pointer,
a deprecation notice. A suspension point is different in kind: **it is an address the outside
world has to use.**

The manifest already publishes one class of inbound address — `triggers`, *"how the flow can
be started from outside the process"*. A wait is the other half of the same fact: how the flow
is **continued** from outside the process. An application whose manifest lists its routes and
its topics but not its signals has published half of its inbound surface, and the missing half
is the half that only exists on flows that take days.

`FlowSignal`'s own documentation already assumed this field existed. Its remarks read *"the
identity is what the plan publishes — `StepNode.SignalType`, and the `signals[]` entry of
`flowx.manifest.json`"*. There is no `signals[]` entry and never was; the sentence describes
the artifact this record produces, written a day early.

### 1.2 What makes the omission dangerous rather than merely incomplete

`FlowHost.SignalAsync` documents that **a signal for an instance that is not waiting for it is
inert, not an error**: *"The instance runs forward to wherever it actually is and stops there,
leaving `Suspended` untouched."* That is the right runtime behaviour — refusing would mean the
host deciding what a flow is waiting for — and it is exactly what makes a silent change to a
signal's identity so expensive. Rename the contract a flow awaits, and:

* every existing sender keeps posting the old identity,
* every delivery is accepted and does nothing,
* no exception is raised, no status code changes, no metric moves,
* and every instance waits until its `[FlowDeadline]` runs out.

**Nothing fails.** That is the profile of a change `flowx diff` exists for, and it is
currently invisible to it.

---

## 2. Decision

**The `step` object gains two properties, `signal` and `timeout`, written by the compiler in
the same change that declares them, and classified by `flowx diff` in the same change again.**

### 2.1 `signal` — the identity, always written

```json
{ "id": 1, "kind": "AwaitSignal", "signal": "offer.countersigned", "timeout": "P7D" }
```

`signal` is `#/$defs/identity` — the same `<domain>.<verb>` shape a capability id, a flow id
and an event type use, and the same string `StepNode.SignalType` carries and a transport reads
out of `/signals/{signalType}`. It is derived by the compiler from the signal contract's name,
so **every** `AwaitSignal` step that reaches the manifest carries it. There is no unresolvable
case: a step whose type argument could not be resolved produces no step at all.

It is structure and never a value, by the test the rest of this document uses: it is the same
string the *sender* uses to address a delivery, exactly as `trigger.route` is the string a
caller uses to address a request. It says nothing about what any instance carried.

### 2.2 `timeout` — the declared wait, folded to a duration, or omitted

`timeout` is `#/$defs/duration` — ISO-8601, the same `$ref` the flow's `deadline` uses, so a
consumer that already parses one budget parses both with one code path.

**It is the folded duration, not the author's expression, and that is the decision inside the
decision.** `StepModel.SignalTimeout` carries the expression verbatim —
`Waits.Countersignature` in `samples/workflow` — for a reason its own remarks give: *"the
generator does not constant-fold, so a duration written as `Policies.OfferWindow` reaches the
plan as that and the plan means what the source means."* That reasoning is correct **for the
plan and wrong for the manifest**, and the difference is the consumer:

* The plan is C#. `Waits.Countersignature` in generated C# *is* the duration, evaluated by the
  same compilation that declared it.
* The manifest is JSON, read by tools that have never seen the assembly.
  `"timeout": "Waits.Countersignature"` publishes a **symbol name** — and a `flowx diff` rule
  over it would fire when somebody renames the constant and stay silent when somebody changes
  its value, which is the exact inversion of what the rule is for.

So the compiler evaluates the declared expression where it can, and **omits the field where it
cannot**. That is `merge`'s precedent, taken verbatim: *"Omitted entirely when the strategy
could not be read statically. An absent field is a consumer asking; a guessed one is a
consumer misled."*

**What folds.** `TimeSpan.Zero`; `TimeSpan.FromDays / FromHours / FromMinutes / FromSeconds /
FromMilliseconds` with a compile-time-constant argument; and **one level of indirection**
through a field or property whose declaration initialises it with one of those. The one level
is not generosity — it is what makes the field have a producer in the repository's only
waiting flow. `samples/workflow` deliberately declares `Waits.Countersignature` as a named
property rather than a literal at the call site, and a `timeout` field that the one real
manifest could not populate would be a field with a producer on paper and none in practice.
Nothing further is folded: an expression that reaches a method call, a conditional or a
configuration lookup is a duration this compiler cannot know, and it is omitted.

**What this field does not claim.** Nothing arms it. There is no scheduler and no timer table
([06 §6](../06-Execution-Engine.md#6-suspension-waiting-without-holding-resources)), so the
declared wait bounds nothing at run time; the only enforced budget on a waiting instance is
its own `[FlowDeadline]`. Publishing it anyway is the same stance the manifest already takes
on `policies`, which [05 §12](../05-Architecture.md#12-architecture-fitness-functions) records
as *"a manifest entry and nothing more"*: the manifest publishes what the author **declared**,
and a reader who wants to know what executes reads the documents that say so. This is
deliberately **not** ADR-0017's F2 — F2 forbids *"a constant standing in for a fact"*, a value
the compiler invented. `P7D` is a value the author wrote.

### 2.3 Three `flowx diff` rules, and why they are not one

| Code | Severity | Change |
|---|---|---|
| `FLOWX-DIFF-021` | **Breaking** | a flow no longer waits for a signal it waited for |
| `FLOWX-DIFF-022` | **Breaking** | a flow waits for a signal it did not wait for |
| `FLOWX-DIFF-206` | Neutral | the declared wait for a signal changed |

**021 and 022 are separate because they break different people in different ways.**

*Removed* (021) breaks the **sender**. Every party addressing that identity keeps posting it,
`FlowHost` accepts each delivery and does nothing with it (§1.2), and the instances wait until
their deadline. It is the most silent failure this diff reports.

*Added* (022) breaks the **caller**. A flow that ran to completion on the request that started
it now stops in the middle: over HTTP the answer changes from `200` with the flow's output to
`202` with an instance id ([ADR-0022](ADR-0022-http-shape-of-a-suspending-flow.md)), and the
work does not finish until somebody delivers a signal the baseline never told them about. A
flow that already waited and now waits twice breaks the same caller the same way.

A single "signals changed" code would have made a rename report one finding whose message the
reader still had to decompose. Two codes make a rename read as what it is — one address gone,
one appeared — and each half carries the consequence that belongs to it.

**206 is Neutral, and it is the same argument `FLOWX-DIFF-203` makes about a deadline.** A wait
window is an operational budget, tuned against how long real people take, not a promise in the
contract. Nobody's code stops compiling and no request stops binding because an offer is open
for fourteen days instead of seven. It is reported because shortening one changes when
instances give up, and a reviewer should see it; it is not gated, because a gate that fails the
build on a tuning change is a gate people route around.

### 2.4 22-CLI §2.2 is amended, narrowly

*"A flow's `steps` — the implementation of a flow, not its contract"* is currently absolute.
It stops being absolute for two properties of one kind of step, and the carve-out is stated
rather than assumed:

> a flow's `steps`, **except** an `AwaitSignal` step's `signal` and `timeout` — every other
> step describes what the flow does, and refactoring that is what FlowX exists to make safe. A
> wait describes what the flow **requires from outside**, which is the same kind of fact as a
> `trigger` and is compared for the same reason.

Adding, removing, reordering or replacing any other step remains unreported, including the
steps around a wait. Moving a wait behind a `When` produces no finding, because the set of
identities the flow can be continued by has not changed.

**Waits are keyed by identity, and the first wait on an identity is the one compared.** A flow
that declares the same identity twice is ambiguous to a deliverer already, and the journal
resolves that ambiguity in one direction: `FlowHost.SignalAsync`'s remarks state that *"the
open wait is the first `AwaitSignal` with no committed row"*, so the first is the one a
delivery satisfies. This is deliberately the opposite choice from `ManifestDiff.Addressed`,
where the **last** trigger on an address wins — two triggers on one address are the same
endpoint declared twice and neither is privileged, where two waits on one identity are
strictly ordered by the frontier.

---

## 3. Options rejected

- **A. Publish nothing; leave `"kind": "AwaitSignal"` as the whole of it.** *Rejected:* it is
  the status quo, and §1.2 is what it costs. It also leaves the manifest claiming completeness
  — ADR-0005's *"a complete, versioned, machine-readable description"* — while omitting the
  only inbound address a long-running flow has.
- **B. Publish the signal and not the timeout.** *Rejected*, though it was close. It halves
  the schema cost and every argument in §2.1 survives intact. What decided against it is that
  the timeout is the *only* number a reader can use to tell a wait that is meant to be minutes
  from one that is meant to be quarters, and a flow's operational shape is exactly what a
  manifest consumer is entitled to read off it — `deadline` is published for the same reason
  and is no more enforced by the transport than this is.
- **C. Publish the timeout as the author's source expression.** *Rejected:* §2.2. It publishes
  a symbol name into a document whose readers have no symbols, and it makes
  `FLOWX-DIFF-206` fire on renames and stay silent on changes.
- **D. A top-level `signals` array, beside `events`.** *Rejected:* it is the wrong shape twice
  over. `events` is a top-level list because an event has an identity and a schema version
  independent of any one flow that emits it, and a subscriber pins the event, not the producer.
  A signal has neither — it is meaningful only as the thing one flow stops at, and lifting it
  out would separate the identity from the step whose position gives it meaning, then require
  a back-reference to restore it. It would also be a second unproduced field the day it landed:
  nothing would write a signal's own version, because nothing declares one.
- **E. Put the fields on the step but leave `flowx diff` to a later work package.** *Rejected,*
  and this is the option ADR-0017 exists to refuse. It is precisely how `authorization.value`
  came to be a Breaking rule that could not fire, and it would take F1's count from twelve to
  fourteen while the freeze approaches.

---

## 4. How ADR-0017's criteria move

Stated explicitly, because ADR-0017's Revisit-when requires this record to answer for the
addition against each of them.

| Criterion | Effect of this change |
|---|---|
| **F1** — every declared field has a producer | **Unchanged at twelve.** Both new fields are written by `ManifestWriter` in the commit that declares them. `timeout` is *conditionally* written, which is the same standing `merge`, `deadline`, `triggers`, `sensitive`, `errors` and `authorization.value` already have — a field omitted for a stated reason is produced, a field omitted because nobody wrote the code is not. F1's eventual corpus test must therefore assert that a manifest **containing a foldable wait** carries `timeout`, not that every manifest does |
| **F2** — no field is a constant standing in for a fact | **Unchanged.** `signal` is derived from the declared contract; `timeout` is folded from the declared expression and omitted when it cannot be. Neither is a literal the writer chose, which is what F2 forbids and what `event.schemaVersion` still is |
| **F3** — `ManifestIsComplete` covers all four of Q3's nouns | **Untouched.** A wait is not one of the four nouns |
| **F4** — the two absent producers have landed | **Untouched.** The outbox and the policy engine are unaffected |
| **F5** — `flowx diff` can see every field the freeze makes permanent | **Unchanged, and satisfied for these two fields on the day they land.** `signal` is classified by `FLOWX-DIFF-021` and `022`; `timeout` by `FLOWX-DIFF-206`. F5 itself stays open, because the *instrument* that would find the next unclassified field is still unwritten — this record adds a third field↔rule pair verified by hand, which is F5's own argument for needing a test |
| **F6** — the escape hatch is exercised | **Untouched.** Nothing here writes `extensions`, and the fact that a real need was met by a schema change rather than by `extensions` is worth noting *against* F6: the hatch was not reached for, because two typed fields with diff rules are the right answer and an untyped bag would have been the wrong one |
| **F7** — the bump is one atomic change | **Untouched**, and slightly cheaper: these fields are added while `schemaVersion` is `0.1.0`, which is when ADR-0017 says a schema change is free |
| **F8** — no field whose record is still Proposed is frozen | **Unchanged, and this record is Accepted.** `signal` and `timeout` are governed by this ADR, so neither adds to F8's exposure. ADR-0014 remains the only Proposed record governing a field |

**The count of fields the schema declares that nothing writes stays at twelve.** That is the
sentence this record was written to be able to say.

---

## 5. Consequences

**Positive**
- **A waiting flow is legible.** Two suspended flows in one manifest are told apart by the
  identity that restarts them, which is the fact a reader, `flowx graph` and an agent all need
  first.
- **The silent break in §1.2 becomes a Breaking finding.** Renaming a signal contract now
  reports `FLOWX-DIFF-021` plus `FLOWX-DIFF-022` and exits 1, where before it reported nothing
  and every sender kept posting into a wait that had moved.
- **A flow acquiring its first wait is reported.** `FLOWX-DIFF-022` catches the change that
  turns a synchronous endpoint into an asynchronous one — a wire-visible change with no
  signature movement, which is the class of finding this diff exists for.
- **The two fields are the input the generated signal endpoint needs.** The same reading of
  the same steps produces the manifest entry and the route
  ([ADR-0022](ADR-0022-http-shape-of-a-suspending-flow.md)), so there is no second copy of the
  identity to drift — the arrangement `EndpointEmitter` already has with `triggers`.

**Negative / accepted trade-offs**
- **The schema grew by two fields before a freeze.** That cost is real and is why this is a
  record rather than a commit. It is discharged, not waived: §4 accounts for the addition
  against all eight criteria and F1's count does not move.
- **`timeout` is sometimes absent, and absence has two meanings a consumer cannot tell
  apart** — "this step is not a wait" and "this wait's duration could not be folded". The
  first is disambiguated by `kind`; the second is not disambiguated at all, and a consumer
  reading a manifest with a wait and no `timeout` learns only that the compiler could not
  evaluate it. Emitting a sentinel would be worse (`merge` makes the same choice for the same
  reason), and emitting `null` would need a schema type that admits it. **Accepted, and named
  here so a later reader knows it was chosen.**
- **The folding rules are a small language the compiler now understands.** Five factory
  methods, `TimeSpan.Zero`, and one level of member indirection. Every rule outside that set
  produces an omission rather than an error, so the failure mode is silence — and silence is
  what the previous bullet says a consumer cannot interpret. The set is deliberately small so
  that it can be stated in one sentence in [08 §3.5](../08-Flow-Definition.md#35-waiting).
- **22-CLI §2.2's "steps are never compared" is no longer a rule with no exceptions**, and a
  rule with one exception is a rule people have to read carefully. The exception is stated in
  the table itself rather than in prose below it, so it cannot be missed by a reader scanning
  the row.

**Revisit when:** any one of —
- **a timer arms the declared wait.** The moment `.OnTimeout(...)` compiles to a branch and a
  scheduler fires it, `timeout` stops being a declaration and becomes a contract term — at
  which point `FLOWX-DIFF-206` has to be re-argued, because shortening an *enforced* window
  cancels work that a lengthened one would have completed. That is a Breaking-shaped change
  and this record classifies it as Neutral today on the explicit ground that nothing arms it;
- **a second construct suspends.** `.Delay(duration)` produces no step today
  ([`FLOWX1031`](../diagnostics/FLOWX1031.md)); when it produces one, that step needs the same
  question asked of it and the answer may not be the same, because a delay has no identity for
  anyone outside to address;
- **the folding set proves too small in practice** — a real application declares waits the
  compiler cannot evaluate often enough that `timeout` is usually absent, at which point either
  the set grows or the field is not worth its place in a frozen schema;
- **the manifest freeze (ADR-0017) closes** with either field still lacking a rule, which §4
  says cannot happen and which F5's unwritten instrument is the reason nobody can yet prove.

---

**Back to:** [ADR index](README.md) · [ADR-0005](ADR-0005-manifest-as-build-artifact.md) ·
[ADR-0017](ADR-0017-manifest-v1-freeze-criteria.md) ·
[ADR-0022](ADR-0022-http-shape-of-a-suspending-flow.md) · [22-CLI](../22-CLI.md) ·
[08 §3.5](../08-Flow-Definition.md#35-waiting)
