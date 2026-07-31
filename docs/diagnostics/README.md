# Diagnostics

Every `FLOWX####` the compiler can report, with what it means, an example that
triggers it, and the fix. The `helpLinkUri` on each descriptor points here, and
`EveryDiagnosticIsHelpful` fails the build if a descriptor lacks one.

## Why these are errors, not warnings

Each rule below encodes something that is either structurally impossible to recover
from at run time, or expensive enough that discovering it in production is the wrong
place. A warning is a rule nobody has to obey; if a rule is worth having, it stops
the build.

Eleven entries below are not errors, and each says why on its own page. Three of the eleven —
the determinism set [FLOWX1007](FLOWX1007.md), [FLOWX1008](FLOWX1008.md) and
[FLOWX1009](FLOWX1009.md) — say why *together*, in
[the section below](#the-severity-of-the-determinism-set), because
[06 §5](../06-Execution-Engine.md#5-the-determinism-boundary) asked for that stance to be
taken as a set rather than one row at a time.
[FLOWX1024](FLOWX1024.md) and [FLOWX1025](FLOWX1025.md) report gaps between the
manifest and what the build can actually deliver, rather than mistakes in the source
— and `FLOWX1025` is additionally about an attribute the developer sometimes does not
own, which an error would make unusable. It follows `FLOWX1011`'s asymmetry rather
than being lenient everywhere: an **error** when the trigger attribute is declared in
the compilation being built, where the fix is one line the reader owns, and a
**warning** when it arrives from a referenced package, where it is not their omission
to fix. [FLOWX1028](FLOWX1028.md) is the same shape
taken to its limit — the declared execution profile is one the runtime does not
implement at all — and an error there would be actively harmful, because its only
repair is to delete the declaration P2 will need to find. [FLOWX1027](FLOWX1027.md) reports code that
has no effect rather than code that is wrong, which is exactly what C#'s own
`CS0162` is and exactly the severity C# gives it. [FLOWX1011](FLOWX1011.md) is an error in `Durable`
flows and a warning in `Ephemeral` ones, which is the asymmetry
[ADR-0003](../adr/ADR-0003-execution-profiles.md) ratified for the determinism rules:
a durable flow is replayed and must take the branch it took the first time, an
ephemeral one is not replayed at all.
[FLOWX1012](FLOWX1012.md) is the one rule here whose remedy has a prerequisite outside the
source file, which is most of why it is not an error;
[the section below](#the-severity-of-flowx1012-which-is-not-the-determinism-sets-argument)
is its argument, kept apart from the determinism set's on purpose.

## The severity of the determinism set

[ADR-0003](../adr/ADR-0003-execution-profiles.md) says the determinism rules are **errors
under `Durable` and informational under `Ephemeral`**.
[06 §5](../06-Execution-Engine.md#5-the-determinism-boundary) repeats it, and then asks for
the stance to be revisited **as a set** once the journal exists — "`FLOWX1011`'s deviation
included, rather than one row at a time". WP-52 gave the runtime a journal and WP-58 raised
`FLOWX1007`–`FLOWX1009`. This is that revisit, and this section is its record.

**The set:** `FLOWX1007`, `FLOWX1008`, `FLOWX1009`, `FLOWX1011`. `FLOWX1006` joins it when
WP-59 lands and should take the same shape.

### The decision

> **Warning by default. Error where the compilation can prove the code is on a durable
> flow's replay path. Never informational.**

For a flow, "can prove" is its own `Profile`. For a capability — which has no profile, and
is reached from flows that may not be in this compilation at all — it means a `Durable` flow
in this compilation names it as a step, directly or through a sub-flow it composes.

### Why not `Info`, which is what the ADR says

Because an `Info` diagnostic never appears in a build log. `dotnet build` does not print it,
MSBuild does not fail on it, and no gate in [21-Quality-Gates](../21-Quality-Gates.md)
notices it. `Ephemeral` is the default profile and the one nearly every flow uses, so a set
of rules that is informational there is a set of rules that does nothing in nearly every
build — the precise "documented but unenforced" state this catalogue was written to end, and
the state all four of these ids were already in.

This is not a new argument. `FLOWX1011` deviated from the ADR for exactly this reason and
recorded the deviation as provisional; `FLOWX1028` rejected `Info` on the same grounds and
said so on its page. Taking the set together, the honest conclusion is that the deviation was
right and the ADR's `Info` was wrong: **`Warning` is now the rule and `FLOWX1011` is no
longer an exception to anything.** Nothing about `FLOWX1011`'s behaviour changes; what
changes is that it stops being a deviation. ADR-0003's bullet and 06 §5's paragraph are
superseded on this point and should be amended to match.

### Why `Warning` is not the lenient option

This repository sets `TreatWarningsAsErrors`, so every rule in the set stops **this** build.
What the warning buys is not leniency, it is the right unit of decision for a consumer: one
line in `.editorconfig`, written down in the repository that took the decision, instead of a
`#pragma` per capability or a rule that cannot be adopted incrementally at all. That is the
trade [FLOWX1016](FLOWX1016.md) and [FLOWX1028](FLOWX1028.md) already make.

### Why the escalation is a proof and not a guess

[FLOWX1011](FLOWX1011.md) escalates on the flow's declared profile;
[FLOWX1025](FLOWX1025.md) escalates when the offending attribute is declared in the
compilation being built. Both choose severity by **who can act on the finding**, and both
escalate only on something the compiler can see. The determinism set follows them.

The asymmetry is deliberate. Failing to escalate — a capability whose durable caller lives in
a referenced assembly — costs a warning instead of an error, and under
`TreatWarningsAsErrors` that is still a stopped build. Escalating wrongly would stop someone
else's build on a claim the compiler cannot support, and a rule that does that is a rule
suppressed everywhere, which protects nothing. So the reach analysis is transitive through
sub-flows, because a sub-flow runs inside its parent's instance and the escalation would
otherwise be one composition away from being avoidable — and it stops at the edge of the
compilation, because that is where the evidence stops.

### What the escalation is *not* claiming

That an ephemeral violation is acceptable. `FLOWX1009` is the clearest case: a mutable field
on a capability is a **race between concurrent invocations** under either profile, because
capabilities are resolved once and invoked concurrently. Its ephemeral warning is a statement
about who can act on it and how urgently, not about whether the code is correct.

### Why this is now worth doing at all

Because a durable flow genuinely journals. Until WP-52 the runtime read `ExecutionProfile`
nowhere, so `Durable` ran the ephemeral engine and a determinism violation in a "durable"
flow was a defect in a replay that could not happen. It now commits one journal row per step
boundary, captures `ctx.UtcNow`, `ctx.NewId()` and `ctx.Random`'s seed per step, and resumes
by replaying committed rows into the same step loop. A violation in such a flow is a real
replay defect. What is still missing — stated so that this section does not overclaim — is
that **nothing replays the capture back into execution yet**: `ReplayDeterminismTest` is
WP-61, and until it exists a determinism leak still leaves no trace at run time. Which is the
argument for a build-time rule, not against one.

## The severity of `FLOWX1012`, which is not the determinism set's argument

`FLOWX1012` is *about* a profile, arrived through the same ADR bullet, and was unraised for
the same two phases — so the tempting move is to fold it into the set above and inherit its
answer. That would be wrong in the one place it matters, and the difference is worth stating
here rather than only on the page.

**The set's rule is "Warning by default, Error where the compilation can prove the code is on
a durable flow's replay path". `FLOWX1012` fires *because* the flow is not durable.** Its
trigger condition and the set's escalation condition are mutually exclusive: there is no
compilation in which this rule reports and that proof is available. The escalation does not
transfer, and inventing a different one would be inventing a rule, not applying a stance.

There is exactly one escalation a reader will propose — a `Durable` parent composing this
flow as a sub-flow, by the same transitive reasoning the determinism set uses — and it is
the one case the runtime does **not** currently honour. A resumed parent skips a completed
sub-flow's row and deliberately does not rebuild that child's compensation stack: the entry
is bound to a context that died with the node, and rebuilding it from the child's own rows
is WP-57. Escalating there would promise a guarantee the engine does not deliver, on the
strength of a profile that does not reach the thing being escalated about.

**Why not an error, then.** Three reasons, and only the third is about adoption:

1. **The source is not wrong.** A compensable `Ephemeral` flow compensates correctly on
   every ordinary failure — the capture declines, the unwind runs, the reservation comes
   back. What it loses is the crash window. [ADR-0003](../adr/ADR-0003-execution-profiles.md)
   records that trade deliberately, and `docs/DEBT.md` names it as the example of a
   *decision* rather than debt. An error would make a decision the ADR ratified
   inexpressible.
2. **The remedy has a prerequisite the compiler cannot see.** `Profile = Durable` needs a
   host with a journal and a lease store registered; without one, `FlowInvocation` refuses
   the flow with `flow.durability_not_configured` before its first step. An error would stop
   a build until the author made an edit whose correctness depends on a deployment fact no
   analyzer can check. That is also why this rule ships with **no code fix** — see the page.
3. **`Ephemeral` is the default profile**, so an error here breaks every compensable flow in
   every codebase that has not already opted into durability, on the day it is switched on.

**Why not `Info`, for the same reason as the set, only more so.** Info never reaches a build
log; and this rule reports *only* on flows that did not opt into durability, which is the
overwhelming majority. An Info `FLOWX1012` would be invisible in essentially every build
that could ever contain it — which is precisely the state it was already in for two phases,
and the state raising it was supposed to end.

**`Warning` is not the lenient option here either.** This repository sets
`TreatWarningsAsErrors`, and the rule's first finding was `samples/ecommerce` — the
reference application, a compensable saga on `Ephemeral`. It stopped that build, which is
the evidence that the rule reports on real code rather than on a fixture. What the sample
did about it is on [the page](FLOWX1012.md#the-reference-sample-fires-this-rule).

## Catalogue

| Id | Rule | Prevents |
|---|---|---|
| [FLOWX1001](FLOWX1001.md) | Flow must be partial | The generated plan has nowhere to live |
| [FLOWX1002](FLOWX1002.md) | Step type is not a capability | A step the engine cannot invoke |
| [FLOWX1003](FLOWX1003.md) | Capability references a transport | Losing quality goal Q4 — the same flow behind any transport |
| [FLOWX1004](FLOWX1004.md) | Capability invokes another capability | Turning the capability set back into a call graph |
| [FLOWX1005](FLOWX1005.md) | Flow inherits from another flow | Control flow invisible to the graph and the manifest |
| [FLOWX1007](FLOWX1007.md) | Time is read from the ambient clock rather than the context | A replay reproducing a different instant from the one the journal captured |
| [FLOWX1008](FLOWX1008.md) | Identity or randomness is taken outside the context | **A duplicate charge on a retried step, and a replay minting an id the journal never saw** |
| [FLOWX1009](FLOWX1009.md) | Capability or flow holds mutable state | **Two concurrent invocations of one singleton capability racing on a field** |
| [FLOWX1010](FLOWX1010.md) | Capability declares no authorisation stance | A permissive default nobody chose |
| [FLOWX1011](FLOWX1011.md) | Condition, selector or projection reads something outside the flow's state | A branch that takes a different path on replay, or a step input that is not the journaled one |
| [FLOWX1012](FLOWX1012.md) | Compensation is declared on a flow that is not durable | **A reservation, a hold or an authorisation left standing because the node that would have released it died first** |
| [FLOWX1013](FLOWX1013.md) | Parallel branches must write disjoint context slots | **Two concurrent branches racing on one context slot** |
| [FLOWX1014](FLOWX1014.md) | Retry requires an idempotent capability | **A duplicate charge** |
| [FLOWX1015](FLOWX1015.md) | Capability implements more than one contract | Ambiguous dispatch, meaningless manifest entry |
| [FLOWX1016](FLOWX1016.md) | Expected failures are values, not exceptions | A business outcome arriving as a defect alert, missing from the error catalogue and unclassifiable by retry |
| [FLOWX1017](FLOWX1017.md) | AwaitSignal requires the Durable profile | A waiting flow vanishing with its node |
| [FLOWX1018](FLOWX1018.md) | Cache requires no side effects | Reporting a write that never happened |
| [FLOWX1019](FLOWX1019.md) | Flow deadline is shorter than the step timeouts it must contain | A flow that runs out of budget mid-way, reported as a timeout several steps from its cause |
| [FLOWX1020](FLOWX1020.md) | Step consumes a contract no earlier step produces | A flow that throws on its first request |
| [FLOWX1021](FLOWX1021.md) | Sub-flow composition forms a cycle | **A stack overflow, or a deadline breach several flows from its cause** |
| [FLOWX1026](FLOWX1026.md) | Sub-flow cannot be composed | A composition silently missing from the plan, the manifest and the diagram |
| [FLOWX1023](FLOWX1023.md) | Flow declares no steps | A flow that silently does nothing |
| [FLOWX1024](FLOWX1024.md) | Emit step is recorded but not published | A consumer waiting for an event the manifest promised. *Status revisited at WP-56: the outbox and its publisher now exist, the engine still stages nothing for an `Emit` step, so the warning stands for a narrower reason* |
| [FLOWX1025](FLOWX1025.md) | Trigger attribute declares no `[TriggerKind]` | A trigger missing from the manifest, and `flowx diff` unable to tell |
| [FLOWX1027](FLOWX1027.md) | Step is unreachable after `Fail` | A plan, a manifest and a diagram listing work the flow can never do |
| [FLOWX1029](FLOWX1029.md) | Step input mapping produces the wrong contract | A `CS1503` inside generated source, about a call the developer cannot see |
| [FLOWX1028](FLOWX1028.md) | Execution profile is declared but not honoured by the runtime | **A payment saga declaring `Durable` and losing its instance on the next deploy** |

> **Every id above is raised and covered by a test.** Four of them were not, until
> WP-13: `FLOWX1014` and `FLOWX1018` ask what is in a policy set, and nothing resolved
> one; `FLOWX1003` and `FLOWX1004` read a capability's dependencies, which the flow
> generator never looks at and which needed a separate `DiagnosticAnalyzer`. All four
> were documented as compile errors the whole time.
>
> `FLOWX1003` has a stated limit worth reading before relying on it: it matches a
> **list** of transport namespaces, not a proof. `FLOWX1020` has stated limits for the
> opposite reason: it is silent wherever it cannot resolve the chain, because a rule
> about step order that fires on a valid flow would be suppressed and then protect
> nothing. `FLOWX1011` has both kinds at once: its scope rules are a proof, its
> catalogue of impure statics is a list, and it is not interprocedural — a helper
> method called from a condition can read a clock and it will not notice. It covers
> every `IFlowBuilder` delegate that takes the flow context, not only `When`; the
> constructs are a table on its page.
>
> The two added in WP-39 state theirs the same way. `FLOWX1016` proves containment — a
> `throw` inside `ExecuteAsync` is a `throw` the engine catches — and *lists* the
> exception types that mean a defect rather than an outcome; it is not interprocedural.
> `FLOWX1019` reports a **floor**: everything it cannot read counts as zero, so its
> silence is never a statement that a flow's deadline is adequate.

## Ids reserved but not yet raised

The catalogue is deliberately smaller than the numbering suggests. Codes appear here
only once the compiler actually reports them — a documented diagnostic that nothing
raises is a promise the compiler is not keeping. Reserved for later phases:
`FLOWX1006` (state must be serialisable) and
`FLOWX1022` (contract compatibility **across versions** — the analyzer counterpart
of `flowx diff`, distinct from `FLOWX1020`, which checks one flow's steps against
each other). `FLOWX1021` left this list when sub-flows landed; `FLOWX1016` and
`FLOWX1019` left it in WP-39.

`FLOWX1007`–`FLOWX1009` left this list in WP-58, with the meanings every other document
already gave them: ambient clock, ambient identity and randomness, and mutable state on a
capability or a flow. **`FLOWX1012` left it in WP-60**, with the meaning every other
document already gave it too: `.CompensateWith` on a flow whose profile is not `Durable`.
It was reserved for longer than any of them, and the row that used to sit below said why
— its remedy. That remedy is now real in both halves: WP-52 made the runtime read
`ExecutionProfile`, and WP-53 and WP-55 gave a host a journal and a lease store to
register, so `Profile = Durable` no longer means either "changes nothing" or "refuses to
run". The rule ships as a **Warning**; the argument, which is *not* the determinism set's
argument, is [below](#the-severity-of-flowx1012-which-is-not-the-determinism-sets-argument).

**What each remaining reservation is blocked on**, so that "reserved" does not
quietly become "forgotten":

| Id | Blocked on |
|---|---|
| `FLOWX1006` | The generated `System.Text.Json` context [ADR-0008](../adr/ADR-0008-serialization-and-schema.md) chose. Nothing generates one and `IPayloadSerializer` does not exist, so there is no membership the rule could check a contract against. *This cell also said `ctx.State` is serialised nowhere. Since WP-52 there is a path — the journal's state-bag snapshot — but it runs through `JournalPayload.Of<T>`, which requires a `JsonTypeInfo<T>` the caller must already have, and no generator emits one: the shipped dispatchers describe no payloads. The generated payload writer is WP-59, and this rule lands with it.* Checking "is this type serialisable in principle" instead would be a different, weaker rule under a number already spoken for |
| `FLOWX1022` | `flowx diff`'s question, asked of two manifests. An analyzer sees one compilation and cannot see the previous version's contracts at all |

**A new rule takes the next id above the catalogue, never a reserved one.** Each
reservation above already has a meaning written down in at least one other document,
and reusing one would leave two rules describing themselves with the same number —
a mistake this project has already made once, when a check was built as `FLOWX1022`
while three documents described it as `FLOWX1020`. `FLOWX1026` took the next free id
for exactly that reason: `FLOWX1022` is spoken for, and "sub-flow cannot be
composed" is not contract compatibility. `FLOWX1027` took the one after it, for the
same reason. `FLOWX1028` is the clearest case yet for the rule: "the runtime does not
honour this profile" is not any of `FLOWX1006`–`1009` or `FLOWX1012`, all of which are
*about* profiles and all of which are spoken for. `FLOWX1029` followed it.

**Two rules were authored against `FLOWX1028` at the same time**, in separate branches,
and the collision was caught at merge rather than by either author — which is the failure
mode this paragraph exists to prevent, arriving from the one direction it did not cover.
Reading "the next free id" is not enough when someone else is reading it too. Claim the id
in this file *first*, in its own commit, before writing the rule.

The next is `FLOWX1030`. The range is `FLOWX1001`–`FLOWX1099`.

## Adding a diagnostic

1. Add the descriptor to `FlowXDiagnostics`, with a message naming the offending
   symbol, a description saying what to do instead, and a help URI.
2. Add the id to `AnalyzerReleases.Unshipped.md`. The build fails without it —
   RS2008 — which is intentional: a diagnostic id is public surface, because teams
   write suppressions against it.
3. Write the page in this directory.
4. Add the test that proves it fires, and the test that proves it does not fire on
   valid code. The second one matters more; a rule with false positives gets
   suppressed everywhere and then protects nothing.

---

**Back to:** [Quality gates](../21-Quality-Gates.md) · [Architecture](../05-Architecture.md)
