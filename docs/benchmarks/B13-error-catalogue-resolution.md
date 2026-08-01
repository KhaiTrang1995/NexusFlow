# B13 — how often the derived error catalogue can actually be read

> **This is evidence for a decision, not a decision.**
> [ADR-0014](../adr/ADR-0014-derived-error-catalogue-vs-build-budget.md)) §6 lists four
> things nobody knows and says the decision turns on them. Three are measured here. The
> ADR's recommendation is deliberately left as it stands; §8 below states what this
> evidence would change if the owner agrees with it, as a recommendation and not as an
> edit.
>
> **Headline, with the caveat attached to it because it cannot be separated from it:**
> against a corpus of **38 capabilities that I wrote**, the reader publishes a catalogue
> for **22** and withholds for **16** — a **42 % withheld rate**. ADR-0014's revisit
> trigger is *"the withheld rate on real code exceeds 20 % of capabilities"*. This is not
> real code, and [§2](#2-why-this-number-is-mine-and-not-the-worlds) is a long argument
> that no honest rate can be taken today.
>
> **The finding that did not depend on my sampling at all, and what happened to it:** as
> first recorded, 4 of the 23 published catalogues were **wrong** — three claiming a
> capability returns no error when it returns one, one claiming an error the capability
> cannot return. That contradicted the premise ADR-0014 §8 lists first among the decision's
> positive consequences — *"a field that cannot be wrong: correct, or explicitly absent"* —
> and the premise §3 C uses to reject a declared list. **WP-37 fixed both directions.** The
> corpus now reports **zero** wrong catalogues. See
> [§5](#5-two-defects-in-the-reader-found-here-fixed-in-wp-37).
>
> **What correctness cost, which is less than this document predicted.** The first
> recording estimated the fix would move the corpus from 39 % to 47 % withheld. It moved it
> to **42 %**: of the three under-reporting cases, **two became correct catalogues rather
> than withholds**, because the code was in the source all along and only the reader's
> question was wrong. Resolved went **18 → 21**.
>
> **Recorded:** 2026-07-31 · **P1** · `tests/FlowX.Compiler.Tests/Corpus/`,
> `scripts/measure-catalogue-shape.py` ·
> **§3, §4, §5 and §8 restated after WP-37**; §6 and §7 are as first measured, with one
> instrument caveat added at [§6.5](#65-what-wp-37-did-to-this-instrument).

---

## 1. What was measured, and what was not

| ADR-0014 §6 open question | Status here | Where |
|---|---|---|
| The real withheld rate | Measured against a hand-written corpus; **not** against real code, because none exists | [§3](#3-the-corpus-result) |
| Whether an error-factory edit invalidates a catalogue in another file | **Answered: it does.** Four tests | [§7](#7-incremental-invalidation-it-is-correct-and-that-is-what-it-costs) |
| Whether real projects pay more or less than the synthetic one | Both effects measured on their own axes and together | [§6](#6-project-shape-bodies-against-reuse) |
| Incremental and IDE build cost | Bounded, not timed: the derivation re-runs in full on every edit | [§7](#7-incremental-invalidation-it-is-correct-and-that-is-what-it-costs) |

Two things this document found that ADR-0014 did not ask about, both defects in the reader
and both **since fixed in WP-37**: a catalogue that could be positively wrong, and an
instrument that measures a subject which does not bind. [§5](#5-two-defects-in-the-reader-found-here-fixed-in-wp-37)
and [§6.5](#65-what-wp-37-did-to-this-instrument).

Not measured: end-to-end wall-clock overhead at 200 flows since the duplicated-bind fix
(that is `scripts/measure-scale-overhead.sh` and half an hour); the reader's cost in
isolation from the rest of the generator, which would need a change to `src/`.

---

## 2. Why this number is mine and not the world's

ADR-0014 §6 is right that `samples/ecommerce` proves nothing: four capabilities, four
complete catalogues, written by the reader's own authors against the exact convention it
was built to read. The reason that is a tautology is worth stating precisely, because the
same objection applies to this document and has to be answered rather than dodged.

**There is no FlowX code in the world.** The population this rate is supposed to describe —
capability implementations in projects that adopted FlowX and were not written by the
people who wrote the compiler — is empty. So a corpus cannot be sampled from it. Anything
I write is a set of guesses about what that code will look like, and I chose every one of
them.

What follows from that, and what I have tried to do about it:

**The aggregate is a property of my file list.** Add four more delegating capabilities and
the withheld rate moves several points. It is reported because reporting it is better than
refusing to, and it is labelled everywhere it appears — including in the test that holds
it.

**The per-pattern table is not.** Whether `Result.Fail<T>(code, message, category)`
resolves is a fact about `ErrorCatalogueReader`, true regardless of how often anyone writes
it. [§4](#4-every-specimen-and-what-happened-to-it) is the transferable result; a reader who
disagrees with my weighting can take the table and apply their own.

**The corpus is committed source, one file per theme, with ground truth declared per
specimen.** `tests/FlowX.Compiler.Tests/Corpus/` is C# a reviewer can open and argue with.
Each capability carries a `[Specimen]` attribute stating the pattern it stands for, every
error it can actually return, the outcome claimed, and why. The truth strings are the one
input nothing can check, so they are the place to attack this document.

**How the shapes were chosen.** Three sources, deliberately mixed rather than curated:

1. **The convention as documented.** [07-Capability-Model §4](../07-Capability-Model.md)'s
   static factory class per domain, inline construction, `.With(...)` decoration, the
   conditional and switch forms the reader has explicit cases for. Eight specimens, all
   expected to resolve, present so the corpus is not a list of things that break.
2. **Ordinary C# that a capability grows into.** A base class, an injected collaborator, an
   extension method, a shared builder, a table, a dictionary, a delegate, a `??`, a
   defensive `throw` arm, a one-line wrapper. Nothing here is exotic; every one of them is
   what a second or third developer does to a file that got long.
3. **The first-party API surface.** `Result.Fail<T>(code, message, category)`,
   `Result<T>.Error`, the implicit `Error`-to-`Result<T>` conversion, and delegation to a
   service returning `Result<T>`. These are in `FlowX.Abstractions` and are not guesses
   about anyone's style.

**Two choices that push the number in known directions, stated so they can be discounted:**

* **Toward *more* withholding than a disciplined team would see.** Group 2 is
  over-represented relative to any codebase that has a linter and a review culture aimed at
  the documented convention.
* **Toward *less* withholding than a real codebase would see.** Each specimen is minimal
  and carries **one** obstacle. Real capabilities are hundreds of lines with several. The
  catalogue is **all-or-nothing** — `MixedSources` shows one shared-library call erasing
  three resolvable codes — so obstacles do not average, they absorb. A capability with four
  patterns of which three resolve is withheld, and the withheld rate over long capabilities
  is therefore strictly higher than over the shapes they are made of.

I believe the second effect is the larger, so I believe 42 % understates. That belief is
not a measurement and nothing here should be quoted as though it were.

**What one specimen is not:** a claim about frequency. `InterpolatedCode` and
`CrossFileFactory` count one each, and in a real codebase the second is surely more common.
The corpus is a catalogue of shapes, and it is weighted evenly because I have no defensible
basis for weighting it any other way — which is exactly what
[§9](#9-what-a-real-measurement-would-need) says a real measurement must supply.

---

## 3. The corpus result

38 capabilities, held by `ErrorCatalogueCorpusTests`. Adding a specimen fails that test
until the numbers below are restated in the same commit.

| Outcome | Count | Share | As first recorded |
|---|---:|---:|---:|
| **Resolved** — published and correct | 21 | 55 % | 18 |
| **Resolved-empty** — published, empty, and the capability really cannot fail | 1 | 3 % | 1 |
| **Withheld** — not published, and the capability can fail | 16 | **42 %** | 15 |
| **False-complete** — published and wrong | 0 | **0 %** | 4 |

Read three ways, because they answer different questions:

* **A catalogue is published for 58 % of capabilities.** That is what a consumer sees as
  "this field is here". It was 61 %, and four of those were lies.
* **Of the catalogues that are published, 0 % are wrong.** That is the number that matters
  to a consumer acting on the field, and the design says it must be zero. It was 17 %.
* **58 % of capabilities get a catalogue that is both present and correct**, against 50 %
  before. That is the field's actual delivered value on this corpus, and it went *up* while
  the field became trustworthy — which is not what §5 predicted and is worth saying plainly.

**Coverage did not have to be spent to buy correctness here, and the reason is specific
rather than lucky.** Two of the three under-reporting cases were failures the reader could
read perfectly well and had never been asked to look at:
`Result.Fail<T>(code, message, category)` puts the code and the category at the call site,
and a guard helper returning `Result<T>` is an ordinary static method in the same
compilation. Only `DelegatingCapability` — where the result comes back from an injected
service — became a withhold, and that is the same answer the reader already gave the
spelling of that capability which happens to mention an `Error`.

For comparison, the same reader over `samples/ecommerce` resolves 4 of 4, and its committed
manifest baseline is byte-identical before and after WP-37. Both figures are real and
neither is evidence; they differ because the sample does.

---

## 4. Every specimen, and what happened to it

Grouped by file. `→` is the outcome. The full reasoning for each is in the specimen's own
`[Specimen(Why: …)]`, which is longer than a table cell.

### Conventional — the shapes the reader was built for

| Specimen | Pattern | → |
|---|---|---|
| `InlineError` | `new Error(...)` in the body | Resolved |
| `CrossFileFactory` | static factory class, different file, same assembly | Resolved |
| `ImplicitConversion` | `return someError;` via the implicit `Result<T>` conversion | Resolved |
| `DecoratedError` | factory result decorated with `.With(...)` | Resolved |
| `SeveralFailures` | three independent failure paths | Resolved |
| `NamedArguments` | named, reordered constructor arguments | Resolved |
| `PrivateHelper` | private static helper on the capability | Resolved |
| `Infallible` | cannot fail | Resolved-empty |

Eight for eight. The reader does what it claims on the convention.

### Branching

| Specimen | Pattern | → |
|---|---|---|
| `TernaryArms` | `?:` between two errors | Resolved |
| `SwitchArms` | switch expression over a domain enum | Resolved |
| `LocalFunctionError` | local function inside the body | Resolved |
| `SwitchWithThrowArm` | exhaustive switch, `_ => throw` | **Withheld** |
| `CoalescedFallback` | `Lookup(x) ?? Default()` | **Withheld** |

Both withheld cases lose codes the reader had already resolved. The `throw` arm is a
`ThrowExpressionSyntax` and `??` is a `BinaryExpressionSyntax`; neither has a case, so both
reach the default and refuse. Written as a ternary instead, `CoalescedFallback` resolves.

### Indirection

| Specimen | Pattern | → |
|---|---|---|
| `AbstractHook` | base template method over an abstract hook | Resolved *(see below)* |
| `ExtensionMethodError` | extension method on the input type | Resolved |
| `TwoHopFactory` | domain factory forwarding whole to another | Resolved |
| `InheritedHelper` | inherited factory, plus an override nothing calls | Resolved *(was **false-complete**)* |
| `SharedBuilder` | domain factory over a builder that takes the code | **Withheld** |
| `InjectedTranslator` | error from an injected collaborator | **Withheld** |

`AbstractHook` resolves, and — since WP-37 — for the reason it looks like. As first
recorded the base's `Reject<T>` was never followed at all: its type is `Result<T>`, which
the reader could not see as a failure path, so the abstract hook it calls was never
reached, and the catalogue was right only because the override happens to be declared
inside the capability's own class where a lexical scan found its construction. The reader
now follows the result into `Reject<T>` and dispatches the hook to this capability's
override, which is a compile-time fact for a concrete capability. **The caveat that
survives:** move that one line into a factory in a contracts assembly and the same
capability still withholds — see [the cross-assembly group](#across-assemblies--the-layout-the-documentation-prescribes).

`InheritedHelper` is the over-report, and it is closed. Its `Invalid` override is still
there and still uncalled; the manifest no longer names its code.

`InjectedTranslator` is the reader being right: which implementation is registered is not a
compile-time fact, and refusing is the only correct answer.

### Composition — where the code string comes from

| Specimen | Pattern | → |
|---|---|---|
| `ConstFieldCode` | `const string` | Resolved |
| `ConstConcatenation` | `Codes.Domain + "out_of_range"` | Resolved |
| `NameofCode` | `nameof(...)` | Resolved |
| `InterpolatedCode` | `$"catalogue.{sku}"` | **Withheld** |
| `StaticReadonlyCode` | `static readonly string` | **Withheld** |
| `RuntimeCategory` | category chosen at run time | **Withheld** |

The rule is not "the code must be a literal", it is "the code must be a **constant
expression**", and C# folds more than an author would guess. `Codes.Domain + "out_of_range"`
resolves and `$"catalogue.{sku}"` does not; `const string` resolves and `static readonly
string` does not. The two members of `Codes` differ by one keyword, appear identically at
the call site, and land on opposite sides. Nothing tells the author which they wrote.

`NameofCode` resolves and publishes `Discontinued` — a code that violates the
`<domain>.<snake_case>` form the manifest documents. Correct behaviour by the reader; worth
noting that nothing else catches it either.

### As data — errors as values rather than calls

| Specimen | Pattern | → |
|---|---|---|
| `ErrorField` | `static readonly Error` returned by name | Resolved |
| `ErrorProperty` | static get-only property | Resolved |
| `ErrorFromArray` | `Table[i]` | **Withheld** |
| `ErrorFromDictionary` | `ByReason["no_carrier"]` | **Withheld** |
| `ErrorFromDelegate` | `Func<string, Error>` field | **Withheld** |

The split is on how the value is *reached*, which is not something an author has any reason
to think about. Naming a member works; indexing one does not, because an element access
falls to the default case. The array in `ErrorFromArray` is in the same file and entirely
literal.

### Across assemblies — the layout the documentation prescribes

| Specimen | Pattern | → |
|---|---|---|
| `ReferencedFactory` | factory in a contracts assembly | **Withheld** |
| `ReferencedField` | `static readonly Error` in a contracts assembly | **Withheld** |
| `MixedSources` | one local failure, one from a shared library | **Withheld** |

**This group deserves more attention than its three rows.**
[07-Capability-Model §4](../07-Capability-Model.md) mandates the static error class in the
same block that opens *"Contracts live in a dedicated assembly with no dependencies"*, and
whose rule table repeats *"Contracts live in `<App>.Contracts`, referenced by nothing
else"*. Its worked example, `PaymentErrors`, is declared inside that block — and the stated
reason for the mandate is that *"error codes are enumerable — they appear in the manifest
and in generated OpenAPI"*.

`ErrorCatalogueReader` can only follow a symbol that has `DeclaringSyntaxReferences`, which
a symbol from a referenced assembly does not have. **A team that follows §4 exactly gets no
catalogue at all.** The instruction whose justification is the manifest field is the
instruction that empties it.

`samples/ecommerce` does not hit this because it is a single project: its `Contracts.cs` and
`Capabilities.cs` are two files in one assembly, and its errors are in `Capabilities.cs` —
which is not the layout §4 describes.

`MixedSources` shows the compounding: one shared-library call anywhere in a capability
discards every other code it declares, because the catalogue is all-or-nothing by design.

### Propagation — failures that travel inside `Result<T>`

| Specimen | Pattern | → |
|---|---|---|
| `PropagatedError` | `return Result.Fail<T>(inner.Error);` | **Withheld** |
| `ErrorAsParameter` | conventional error through a one-line private wrapper | **Withheld** |
| `ResultFailFromParts` | `Result.Fail<T>(code, message, category)` | Resolved *(was **false-complete**)* |
| `DelegatingCapability` | delegates wholesale to a service returning `Result<T>` | **Withheld** *(was **false-complete**)* |
| `ResultReturningHelper` | guard helper whose return type is `Result<T>` | Resolved *(was **false-complete**)* |

The first two are the reader working: the failure takes the shape of an `Error` somewhere,
the reader sees it, cannot follow it, and refuses. `ErrorAsParameter` is worth a second
look — its call site is textbook, and what loses the catalogue is a one-line wrapper a
developer added for readability, whose parameter has no declaration to follow. **That one
is still open**, and it is the cheapest coverage left on the table: binding a parameter to
the argument at the call site is real interprocedural work and WP-37 deliberately did not
attempt it, because a fix for a correctness defect is the wrong place to add a first
approximation of dataflow.

The last three were [§5.1](#51-a-failure-that-never-took-the-shape-of-an-error-was-published-as-no-failure-at-all).
`PropagatedError` and `DelegatingCapability` are the same situation written two ways, and
before WP-37 they produced opposite manifests. They now agree, and the answer they agree on
is the true one.

---

## 5. Two defects in the reader, found here, fixed in WP-37

Both were in `src/`. They were reported and not fixed, because which way to fix them was a
design decision; WP-37 took the direction the design already had — *withhold* — and found
that most of the affected cases did not need it. This section states the defects as they
were, because the argument they bear on is still live, and then what closing them cost.

### 5.1 A failure that never took the shape of an `Error` was published as no failure at all

`ErrorCatalogueReader`'s premise, in its own remarks, was that *"every expression of type
`Error` inside the capability is a failure path"* — an expression's type is the only thing
that identifies one. The converse does not hold, and the reader treated it as though it
did.

When a failure is carried inside a `Result<T>` for its entire journey through the
capability's source, there is no `Error`-typed expression anywhere. The scan found nothing.
It also found nothing it *could not follow*, so `Complete` stayed true, and
`ManifestWriter` emitted:

```json
"errors": []
```

which the schema, `CapabilityErrorCatalogue`'s remarks and ADR-0014 §3 B(1) all define as a
positive statement: *"an empty **present** array is a positive statement: analysed, and
returns no declared error."* It was a lie in all three specimens:

* **`ResultFailFromParts`** uses `Result.Fail<T>(string code, string message, ErrorCategory
  category)` — a **first-party overload in `FlowX.Abstractions`** whose own summary says it
  is *"for call sites that do not have a shared error factory"*. The code is a string
  literal, sitting in the capability's own body, and the manifest said the capability
  returns nothing.
* **`ResultReturningHelper`** calls a guard helper whose return type is `Result<T>`. Same
  cause.
* **`DelegatingCapability`** is one line of delegation to an injected service returning
  `Result<T>` — the shape of a very large fraction of thin capabilities.

The severity was not that three specimens were wrong. It was the **direction**. Everywhere
else, an obstacle produces *absence*, which a consumer can see. Here it produced a
confident, complete, positive claim that there are no failures — for a consumer that
ADR-0014 §3 B describes as *"an agent deciding which failures it must handle"*.

The distinction was razor-thin and invisible to an author. `PropagatedError` writes
`Result.Fail<T>(inner.Error)`, which mentions an `Error`, so it was honestly withheld.
`DelegatingCapability` writes `return _service.PlaceAsync(...)`, which does not, so it was
confidently wrong. Same situation, opposite manifests.

**What WP-37 changed.** The reader no longer identifies a failure path by searching for a
type. It starts at the capability's `ExecuteAsync` — the one member the contract says
produces the output — and follows the value: through the `ValueTask` that carries the
result, through `Result.Ok` (a success, and the *only* thing that now entitles a capability
to an empty catalogue), through both `Result.Fail<T>` overloads, and into any method whose
source this compilation has. A `Result<T>` whose provenance it cannot name reaches the same
refusal every other unreadable shape already reached. `errors: []` is now a conclusion the
reader has to earn, and `TheOnlyEmptyCatalogueIsOneTheReaderEstablished` holds the list of
capabilities entitled to it.

### 5.2 An `Error` in unreachable code was published as one the capability can return

`Roots` walked the capability's whole class declaration and asked each node its type. It
never asked whether anything reached that node. So any `Error` constructed anywhere in the
capability's own source was published as a failure the capability returns.

`InheritedHelper` calls exactly one factory and can return exactly one code. It also
overrides an abstract hook that nothing in it calls, and the manifest published that hook's
error too.

Less alarming than 5.1 — a consumer that handles an error which never arrives has wasted
effort, where one that misses an error which does arrive has a bug — but it was the same
premise breaking, from the other side. Dead code, a helper left by a refactor, and a branch
behind a disabled feature flag all reached the manifest.

**What WP-37 changed.** Rooting the scan at `ExecuteAsync` and following values answers the
reachability question by construction rather than by adding an analysis: a member nothing
calls is never visited. This is not full reachability — a branch that is dead for a reason
only the runtime knows is still followed, and should be — it is the much smaller claim that
*a member the entry point does not reach is not part of the contract*.

### 5.3 What closing them cost, and what it did to ADR-0014's argument

**Cost, measured on the same corpus:** withheld went 39 % → **42 %**, not the 47 % this
section originally predicted, and resolved went **18 → 21**. Two of the three
under-reporting cases turned into *correct catalogues* rather than withholds. The one that
became a withhold, `DelegatingCapability`, is a capability whose failures genuinely are not
a compile-time fact.

`samples/ecommerce`'s manifest baseline is **byte-identical** across the change, and the
generator got **cheaper** — see [§6.5](#65-what-wp-37-did-to-this-instrument).

What this does to the ADR:

* **§8's first positive consequence is true again** — *"a field that cannot be wrong:
  correct, or explicitly absent."* It was false when this document was first recorded; the
  corpus now asserts it as a property of every specimen rather than of four named ones, so
  a fifth shape finding the same hole fails the test rather than joining a list.
* **§3 C's rejection of a declared list is restored to its absolute form** — *"a declared
  list can be wrong, and a derived one cannot."* The right reading is narrower than the
  ADR's wording: a derived catalogue is not *inherently* incapable of being wrong, it was
  wrong for two years' worth of shapes and a reader had to be made incapable of it. That is
  still a categorically better position than a hand-maintained list, whose drift no test
  can catch — but it is a property that has to be defended, not one that comes free.
* **The argument that cut against option B is withdrawn.** It read: if the field can
  already be silently wrong, the marginal harm of B's fourth state is smaller than §3 B
  argues. The field can no longer be silently wrong, so §3 B stands as written.
* **Nothing here bears on the cost question**, which is what ADR-0014 is actually deciding.
  This was always a correctness issue and it is now settled independently, which is what
  [§8](#8-what-this-should-change-in-adr-0014--a-recommendation-not-an-edit) item 1 asked
  for.

---

## 6. Project shape: bodies against reuse

ADR-0014 §6: *"Real capability bodies are larger, which costs more per capability. But the
synthetic project has 262 capability types across 50 flows (5.2 per flow) and real projects
reuse capabilities ... The two effects point in opposite directions and neither has been
measured."*

Both are measured now, on the allocation instrument
[the cost gate uses](generator-cost-gate.md), at a **constant 50 flows** so the
flow-analysis half of the generator is held fixed. `--capability-body-lines` pads every
capability body with ordinary statements **without adding failure paths**, so a moved number
is attributable to body size alone. `--capability-pool` shares capability types across
flows. Both are off by default; the gate reports **+0.00 %** drift against its committed
baseline with them present.

| flows | padding | pool | cap types | /flow | allocated | B/flow | B/cap type |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 50 | 0 | — | 262 | 5.24 | 35 121 616 | 702 432 | 134 051 |
| 50 | 10 | — | 262 | 5.24 | 54 693 192 | 1 093 863 | 208 752 |
| 50 | 25 | — | 262 | 5.24 | 85 959 992 | 1 719 199 | 328 091 |
| 50 | 50 | — | 262 | 5.24 | 135 173 872 | 2 703 477 | 515 930 |
| 50 | 0 | 25 | 130 | 2.60 | 23 163 400 | 463 268 | 178 180 |
| 50 | 0 | 10 | 52 | 1.04 | 15 950 736 | 319 014 | 306 744 |
| 50 | 0 | 5 | 25 | 0.50 | 13 162 320 | 263 246 | 526 492 |
| 50 | 10 | 10 | 52 | 1.04 | 19 845 016 | 396 900 | 381 634 |
| 50 | 25 | 10 | 52 | 1.04 | 26 035 064 | 520 701 | 500 674 |

*Within-point spread ≤ 0.47 %; the whole grid reproduced across two independent runs to
within 0.05 %. Elapsed milliseconds are collected and are advisory, for the reason
[generator-cost-gate.md](generator-cost-gate.md) gives.*

### 6.1 One two-term model fits every point

```
allocated  ≈  217 kB × flows  +  capability_types × (92.7 kB + 7.7 kB × extra statements)
```

Largest deviation across the nine points: **1.8 %**. Both terms are real:

* **Capability count is not the whole cost.** Cutting capability types by 10.5× (262 → 25)
  cuts total cost by only 2.7×, because ~217 kB per flow does not depend on capabilities at
  all.
* **Body size is roughly linear and it is the reader's.** Within `FlowPlanGenerator`,
  exactly one thing reads a capability's *body*: `ErrorCatalogueReader`. `FlowAnalyzer`
  walks the flow's `Define` chain, `CapabilityReader` reads the attribute and the interface,
  and `StepBindingAnalyzer` is a separate analyzer the probe does not run. So the
  7.7 kB-per-statement slope is the derivation, and it is measured rather than apportioned.

### 6.2 Which effect wins

The two cancel, and where they cancel is computable. Holding total cost equal to the
synthetic project's:

| Extra statements per capability body | Capability types per flow at which cost is unchanged |
|---:|---:|
| 0 | 5.24 *(the synthetic project)* |
| 5 | 3.66 |
| 10 | 2.87 |
| 20 | 1.98 |
| 40 | 1.22 |

So the answer to *"does a real 200-flow solution pay more or less than this measurement
predicts"* is **neither reliably** — the effects are the same order of magnitude, and which
wins depends on a ratio nobody has measured either. What can be said:

* A project whose capability bodies are **10 statements longer** than the synthetic ones
  pays **more** unless it also gets **below ~2.9 capability types per flow** — that is,
  unless it reuses capabilities roughly twice as heavily as the synthetic project.
* **12 extra statements per capability body doubles the per-capability-type cost** on its
  own. ADR-0014's revisit trigger is *"a real project is measured and the derivation costs
  more than 2× the ~3 ms per capability type recorded here"*. Twelve statements is not a
  large capability.
* Heavy reuse alone is bounded: even 0.5 capability types per flow only reaches −63 %,
  because the per-flow term does not move.

### 6.3 The finding that bears on the ADR's decision, not on its facts

ADR-0014 §4(1) commits to re-expressing the budget as **"milliseconds per flow and
milliseconds per capability type"**. Measured per capability type, this generator reports
**134 kB** on the default project and **526 kB** on a reuse-heavy one — **3.9×, with the
same compiler and the same flow count**, because dividing a per-flow term by a capability
count is not a per-unit figure.

A budget stated as two independent divisions has the defect §4(1) is trying to fix: it can
be met or missed by changing the shape of the subject rather than the cost of the
generator. The fix is small — state the budget as the **two fitted coefficients** of the
model in §6.1, at a stated body size, rather than as two quotients. That is testable at any
size and any shape, which is what §4(1) asks for.

### 6.4 What this measurement is not

The padding is arithmetic over locals. Real bodies call methods, instantiate generics and
use LINQ, all of which bind differently and probably more expensively, and they add failure
paths as they grow — which this deliberately does not. The slope is a **lower bound**. The
reuse mode also makes flows textually similar, which is inherent to what reuse means but is
a confound for the flow half of the generator; the per-flow term is stable across the grid,
which is weak evidence that it does not matter much.

### 6.5 What WP-37 did to this instrument

**The reader got cheaper, and by less than the cost gate says.** Two numbers, and the gap
between them is the point.

| 50 flows, capability body padding | before WP-37 | after | change |
|---|---:|---:|---:|
| 0 extra statements | 37,812,208 B | 35,643,864 B | **−5.7 %** |
| 25 extra statements | 88,663,320 B | 86,557,176 B | **−2.4 %** |

Generated output is **byte-identical** at both points, so this is the same work producing
the same answer for less. The saving is in the **constant** per-capability term — the old
reader asked every node in the whole class declaration for its type, including the
constructor, the fields and any member the entry point does not reach; the new one walks
`ExecuteAsync` and stops descending at the outermost result-carrying expression, resolving
what is under it structurally instead. The **slope is unchanged**: 7,764 B per extra
statement per capability type before, 7,773 B after, which is [§6.1](#61-one-two-term-model-fits-every-point)'s
7.7 kB either way. The padding lives in `ExecuteAsync`, so the reader still walks it, which
is correct — a longer body really is more to read.

**The caveat, which is about the harness and not about the generator.**
`scripts/measure-generator-cost.py` — and therefore this section's grid, and the cost gate —
parses the synthetic project with default parse options, while the project's own csproj
sets `ImplicitUsings=enable`. Under the probe, `ValueTask`, `Task` and `CancellationToken`
never bind, so **the subject the gate measures does not compile.** The old reader did not
notice: it searched for `Error`-typed nodes, which bind fine, and read them. The new reader
resolves the capability's `ExecuteAsync` through `ICapability<,>` before it reads anything,
that lookup fails on an unbindable signature, and every capability is withheld immediately.

So the gate reports **−40 %** where the honest figure is **−5.7 %**, and the difference is
the reader declining to read a compilation that does not bind. That is the right behaviour
and the wrong measurement. The two rows in the table above were taken on the same
50-flow subject with the five missing `using` directives prepended to each file, which is
the only difference.

Consequences, none of which are this document's to act on:

* **The committed baseline was not re-recorded.** Re-recording it against a subject that
  does not bind would freeze a number that now means "the reader refused", and the next
  change to the reader would be measured against it.
* **The body-size grid in [§6](#6-project-shape-bodies-against-reuse) was taken on the same
  unbindable subject and its slope no longer reproduces under it** — with the new reader,
  padding 0 / 10 / 25 / 50 all land within 0.1 % of each other, because nothing reads the
  bodies. The fitted model in §6.1 is unaffected as a statement about the generator: the
  slope reproduces exactly on a subject that binds, as the table above shows.
* **The fix is one line in the probe** (`CSharpParseOptions` with the implicit global
  usings, or a generated `GlobalUsings.cs` in the subject), and it changes the committed
  baseline, so it belongs to whoever owns that baseline.

---

## 7. Incremental invalidation: it is correct, and that is what it costs

ADR-0014 §6: *"the transform reads the whole `Compilation` to follow factories into other
files, and there is no test in the repository covering whether an edit to an error factory
correctly invalidates the catalogue of a capability declared in a different file. That is a
correctness question as much as a performance one."*

**It invalidates correctly.** `ErrorCatalogueIncrementalTests` covers it, four ways, all
through a single driver so the second pass is a real incremental run rather than a second
cold one:

| Test | Result |
|---|---|
| A factory's code literal changes in another file | The manifest follows |
| A factory becomes unreadable in another file | The catalogue is withheld on the next run |
| An unrelated file changes | The catalogue is untouched |
| Nothing about the catalogue is cached across any edit | Holds |

**Why it is correct is the performance answer.** `ForAttributeWithMetadataName` combines its
syntactic node table with the `CompilationProvider` before invoking the user's transform.
The compilation changes on every edit anywhere, so the combined input is always modified and
the transform is re-invoked for **every attributed node in the compilation**. Roslyn's own
step tracking reports those re-runs as `Unchanged` — *it ran, the answer was the same* — and
never as `Cached`, which would mean *it did not run*.

So the inner loop pays the **full** derivation cost on every edit, for every capability, not
a fraction of it. ADR-0014's *"the inner loop may be far cheaper — or may not"* resolves to
*may not*: it is the same cost as a cold build's derivation, on every keystroke that reaches
the compiler.

This is a bound rather than a timing. Turning it into milliseconds needs an IDE-shaped
harness and is not attempted here. What the bound settles is the ADR's revisit trigger *"an
incremental-build measurement shows the inner loop paying full derivation cost per edit"* —
**it does, by construction**, and no measurement is needed to know it.

---

## 8. What this should change in ADR-0014 — a recommendation, not an edit

The ADR's recommendation is untouched, and it should stay the owner's to make. What this
evidence supports, in the order it should be considered:

1. ~~**§5.1 is the item to decide first, and it is not about the budget at all.**~~
   **Done, in WP-37.** The recommendation was that a field which can be silently, positively
   wrong is a different object from the one ADR-0014 argues about, and that it should be
   separated from the cost question rather than decided alongside it. It was: the reader was
   fixed, the corpus asserts the property, and every argument in §3 B, §3 C and §8 that
   turns on *"correct, or absent"* can now be read as written. The ADR's decision is
   untouched and is still about cost.
2. **The withheld rate is above the ADR's own revisit threshold on this corpus, and the
   corpus is not admissible as proof.** 42 % against a 20 % trigger. I do not recommend
   treating that as the trigger firing. I recommend
   [§9](#9-what-a-real-measurement-would-need)'s cheaper substitute, which can be done in a
   day and would be admissible.
3. **§3 C's rejection of a declared list holds, and the reason it holds is worth writing
   down.** The original recommendation here was that the argument survives but its absolute
   form does not. WP-37 restores the absolute form on this corpus — and it did so by
   changing the reader, which is exactly the move a hand-maintained list does not have
   available. That, rather than "a derived one cannot be wrong", is the durable version of
   §3 C's argument: a derived catalogue's defects are *fixable in one place*, and a test can
   assert they stay fixed.
4. **07-Capability-Model §4 should be reconciled with the reader before anything else is
   decided.** Today the documented project layout guarantees an empty result. Whether §4
   changes or the reader learns to read a referenced assembly's metadata is a design
   question, but the two documents cannot both stay as they are. This is cheap and it is
   independent of the ADR's outcome.
5. **§4(1)'s "ms per capability type" should become two fitted coefficients at a stated body
   size.** See [§6.3](#63-the-finding-that-bears-on-the-adrs-decision-not-on-its-facts). The
   intent is right and the arithmetic as stated is 3.9× unstable across project shapes.
6. **§6's "nobody knows whether a real 200-flow solution pays more or less" can now be
   stated as a trade rather than as an unknown**, with the break-even table in §6.2. That
   does not decide anything, but it lets the owner decide with a number instead of with two
   opposing intuitions.
7. **Nothing here argues for deleting the feature.** §2 of the ADR is unaffected: the
   pre-catalogue tree measured +18.4 % and every option still fails +8 %. This evidence
   changes what the field *is worth*, not what removing it would buy.

---

## 9. What a real measurement would need

Everything above is a substitute for the measurement nobody can take yet. What would make
one admissible, roughly in order of cost:

1. **A frequency-weighted corpus rather than an even one.** Take the shapes in
   [§4](#4-every-specimen-and-what-happened-to-it) as a checklist and count how often each
   appears in an existing `Result<T>`-style .NET codebase — `FluentResults`,
   `ErrorOr`, `OneOf` and `CSharpFunctionalExtensions` all have public consumers, and the
   question "how is the error value produced at this return site" is answerable by a Roslyn
   script over them. That gives real weights for a table this document already has, and it
   is the single cheapest thing on this list. **It would not need FlowX to exist.**
2. **A port.** Convert one non-trivial existing service to FlowX capabilities without
   consulting the reader, and measure. One project is not a sample either, but it is a
   sample of something.
3. **Telemetry from the first real adopters**, if the generator ever reports an anonymous
   resolved/withheld count. That is the only thing on this list that measures the actual
   population, and it is available no earlier than P8.
4. **A withheld *reason* in the model.** `CapabilityErrorCatalogue` records `IsComplete` and
   not why. Carrying the reason would make every future measurement — including a user's own
   — a matter of reading the manifest rather than of writing a corpus, and it would let an
   author be told which line cost them the catalogue. This is the change that would make
   this document unnecessary next time.

---

**Reproduce:**

```
dotnet test tests/FlowX.Compiler.Tests --filter FullyQualifiedName~ErrorCatalogue
python3 scripts/measure-catalogue-shape.py --json /tmp/shape.json
```

**Back to:** [Benchmarks](README.md) · [B12 at scale](B12-scale.md) ·
[ADR-0014](../adr/ADR-0014-derived-error-catalogue-vs-build-budget.md)) ·
[Capability model](../07-Capability-Model.md) · [Roadmap](../20-Roadmap.md)
