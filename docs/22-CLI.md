# 22 — The `flowx` Command-Line Tool

> **Status:** Accepted · **Audience:** engineers, release managers, CI authors
> **Answers:** what does the CLI do with the manifest, and what exactly makes a change "breaking"?

---

## 1. What the tool is for

`flowx` is a .NET tool that reads `flowx.manifest.json` and nothing else. It has no
project reference to any FlowX assembly, deliberately: it consumes the manifest exactly
as a third-party tool would, which is the strongest available evidence that the document
is genuinely self-describing rather than only usable from inside this repository
([ADR-0005](adr/ADR-0005-manifest-as-build-artifact.md)). The architecture test
`CliDependsOnNothingButTheManifest` holds that line.

| Verb | Reads | Produces |
|---|---|---|
| `flowx graph` | a manifest | a Mermaid flowchart of every flow |
| `flowx manifest` | a built assembly | the manifest compiled into it |
| `flowx diff` | two manifests | a compatibility verdict, and a non-zero exit on a break |

```
flowx graph    [--manifest <path>] [--flow <id>] [--output <path>]
flowx manifest  --assembly <path>  [--output <path>]
flowx diff      --old <path> --new <path> [--format text|json] [--output <path>]
```

| Exit code | Meaning |
|---|---|
| 0 | success — for `diff`, no breaking change |
| 1 | `diff` found a breaking change |
| 2 | usage error |
| 3 | a file was not found |

`1` is distinct from `2` on purpose. A CI job has to tell "the gate says no" apart from
"the gate could not run": the first blocks a merge, the second is a broken pipeline, and
a tool that returns the same code for both gets the step deleted the first time somebody
mistypes a path.

---

## 2. `flowx diff` — the gate

```bash
flowx diff --old baseline/flowx.manifest.json --new artifacts/flowx.manifest.json
```

The baseline is the last released manifest — published alongside the artifact it
describes, per ADR-0005. The candidate is what this build produced. Every difference is
classified as **breaking**, **additive** or **neutral**, and a single breaking finding
exits 1.

### 2.1 The compatibility unit is `id@major`

Flows pin the major version of every capability they compiled against
([07 §5](07-Capability-Model.md)), and a capability may legitimately exist at two majors
side by side. So the diff groups flows, capabilities and events by `id@major` and
compares the newest entry within each group.

Three consequences fall out of that one decision, and all three are what people expect:

- Publishing `payment.capture@3.0.0` **alongside** `2.1.0` is purely additive.
- Publishing `3.0.0` **instead of** `2.1.0` is a removal of the `@2` contract, and
  breaking — which is exactly what it is for a flow pinned to `@2`.
- A patch or minor bump within a major is not a change at all, because a minor bump is
  compatible by definition; if it was not, the contract change itself is reported.

It also means there is no "was the version bumped?" waiver anywhere in the tool. Bumping
the major *is* publishing a new contract; deleting the old one is what breaks people.

### 2.2 What is never reported

A rule that fires on every build is worse than no rule, because it teaches people to
ignore the output.

| Ignored | Why |
|---|---|
| `source` (file:line) | moves whenever anyone edits above a declaration; it is navigation metadata |
| `application.version`, `commit`, `builtAt` | they change on every release by design |
| a flow's `steps` | the implementation of a flow, not its contract — and refactoring it is what FlowX exists to make safe |
| a flow's `emits` and `errors` | both are aggregated by the compiler from steps and capabilities; the same facts appear once more, with versions, in `events` and each capability's `errors` |
| array order anywhere | every list is compared as a set |

---

## 3. The classification rules

### 3.1 Breaking

| Code | Change | Why it breaks somebody |
|---|---|---|
| `FLOWX-DIFF-001` | flow removed | every trigger bound to it stops resolving |
| `FLOWX-DIFF-002` | flow input contract changed | callers construct that type; the same request no longer binds |
| `FLOWX-DIFF-003` | flow output contract changed | callers deserialise it; the response no longer round-trips |
| `FLOWX-DIFF-004` | `Durable` profile withdrawn | instances stop surviving a process kill and in-flight work is lost — with no signature change to catch it |
| `FLOWX-DIFF-005` | trigger removed | a route stops answering, or a consumer group stops draining a topic producers keep filling |
| `FLOWX-DIFF-006` | member no longer `sensitive` | a value that was redacted now reaches logs, traces and the journal, for the whole retention window |
| `FLOWX-DIFF-010` | capability removed | flows pinned to that major no longer resolve |
| `FLOWX-DIFF-011` | capability input contract changed within a major | within a major the contract is frozen; nobody can detect this from the version |
| `FLOWX-DIFF-012` | capability output contract changed within a major | as above |
| `FLOWX-DIFF-013` | `idempotent: true → false` | retry policies already attached at call sites become a compile error (`FLOWX1014`), in other repositories |
| `FLOWX-DIFF-014` | authorisation relaxed | **security regression** — reachable by principals the baseline refused, and nothing else in the build notices |
| `FLOWX-DIFF-015` | authorisation tightened, or the named permission changed | callers authorised under the baseline are now denied |
| `FLOWX-DIFF-016` | side effect added | blast radius widened; agent confirmation prompts and every impact assessment made against the baseline are now wrong |
| `FLOWX-DIFF-017` | error code removed | consumers branching on it silently stop matching — codes disappear far more often because they were renamed |
| `FLOWX-DIFF-018` | error category changed | the category drives the transport status code, so a client keyed on 409 now sees 403 for the same failure |
| `FLOWX-DIFF-020` | event removed, or its schema major bumped without keeping the old one | subscribers pinned to that major receive nothing, and nothing in their build says so |

### 3.2 Additive

| Code | Change |
|---|---|
| `FLOWX-DIFF-100` | flow added |
| `FLOWX-DIFF-101` | capability added |
| `FLOWX-DIFF-102` | event added |
| `FLOWX-DIFF-103` | trigger added |
| `FLOWX-DIFF-104` | error code added |
| `FLOWX-DIFF-105` | `idempotent: false → true` |
| `FLOWX-DIFF-106` | side effect removed |
| `FLOWX-DIFF-107` | member marked `sensitive` |

### 3.3 Neutral — reported, never gated

| Code | Change | Why it is not a break |
|---|---|---|
| `FLOWX-DIFF-200` | manifest schema major differs | says how to read the document, not what the application promises — but the rest of the report is then advisory |
| `FLOWX-DIFF-201` | application renamed | almost always the wrong pair of files, which is worth saying before a reader trusts a hundred findings |
| `FLOWX-DIFF-202` | execution profile changed other than losing `Durable` | cost and delivery semantics change; the contract does not |
| `FLOWX-DIFF-203` | deadline changed | an operational budget tuned against production latency, not a promise — though shortening one can turn slow-but-successful executions into timeouts |
| `FLOWX-DIFF-204` | deprecation notice added or removed | nothing breaks today; it is the signal to start migrating |

---

## 4. Three rules worth arguing about

### 4.1 Both directions of an authorisation change are breaking

Relaxing is a security regression and is labelled as one, in those words, because
"authorization changed" in a CI log does not make anybody stop reading.

Tightening is breaking too, which is the less comfortable half. It is unambiguously the
right change to make, and it still stops callers that worked yesterday from working
today. The gate is not saying tightening is wrong; it is saying tightening needs the same
coordination as any other break, because shipping it unannounced turns a security
improvement into an outage. It gets its own code so the two never read as the same event.

`Permission` and `Policy` are not ordered against each other — a named policy can be
broader or narrower than a named permission — so a move between them is reported as a
change rather than guessed at in either direction.

### 4.2 `sensitive` is asymmetric, on purpose

**Marking** a member sensitive is additive. It strictly increases protection. It can
break something — a dashboard scraping the value out of a log line loses it — but a
secret leaking into a log is not a dependency this platform undertakes to preserve, and a
gate that failed the build when an engineer marked a password would teach engineers not
to mark passwords. That is the opposite of the intended effect.

**Un-marking** one is breaking, and it is the more serious of the two. The value now
flows into logs, traces and a journal retained for the replay window, so the regression is
durable and applies to every instance recorded after the change. It is the same class of
finding as a relaxed authorisation stance: no signature moved, the security posture got
worse, and that is precisely what code review misses.

### 4.3 Adding a side effect is breaking

Nothing about the call changes, which makes this the least obvious rule. But
`sideEffects` is what blast-radius review reads and what decides whether an agent asks a
human before invoking the tool ([13 §6](13-AI-Native.md)). A capability that consumers
assessed as touching nothing outside the process, and which now writes to a payment
gateway, has invalidated every assessment made against it. Removing one narrows the blast
radius and invalidates no decision anyone made, so it is additive.

---

## 5. Output

Text is the default, because the overwhelmingly common reader is a person scrolling a CI
log after a build went red.

```
flowx diff — Ecommerce: 1.0.0 -> 1.1.0

BREAKING (2)
  FLOWX-DIFF-006  flow order.place@1 input
      member is no longer sensitive: PaymentToken
      Data exposure regression: this value was redacted from logs, traces and the
      durable journal, and now reaches all three for the whole retention window.
  FLOWX-DIFF-014  capability payment.capture@2
      authorization relaxed: Permission -> Public
      Security regression: the capability is now reachable by principals the baseline
      refused. Nothing else in the build will notice this.

NEUTRAL (1)
  FLOWX-DIFF-203  flow order.place@1
      deadline changed: PT30S -> PT10S
      A shorter budget can turn slow-but-successful executions into timeouts.

2 breaking changes, 0 additive, 1 neutral — INCOMPATIBLE.
```

`--format json` produces the same verdict for whatever consumes the build afterwards.
Both renderings come from one report rather than two traversals of the manifests, so the
gate and the log cannot disagree about what was found.

```jsonc
{
  "application": "Ecommerce",
  "baselineVersion": "1.0.0", "candidateVersion": "1.1.0",
  "compatible": false,
  "breaking": 2, "additive": 0, "neutral": 1,
  "findings": [
    { "code": "FLOWX-DIFF-014", "severity": "Breaking",
      "subject": "capability payment.capture@2",
      "summary": "authorization relaxed: Permission -> Public",
      "consequence": "Security regression: …" }
  ]
}
```

The verdict and the counts come before the findings, so a consumer that only needs the
gate's answer reads the first few hundred bytes rather than the whole document.

---

## 6. Using it in CI

```bash
flowx manifest --assembly artifacts/App.dll --output new.manifest.json
flowx diff --old baseline/flowx.manifest.json --new new.manifest.json
```

No `if` and no wrapper: a breaking change exits 1 and fails the step. A pipeline that
wants the machine-readable form as well can write it out and still get the verdict from
`$?`, because `--output` does not change the exit code.

Waiving a finding is deliberately not a flag. A breaking change ships behind a major
version bump or an ADR ([15 §9](15-Security.md), change control), and a `--ignore` option
would turn both of those into an argument in a YAML file.

---

**Back to:** [README](../README.md) · [AI-Native](13-AI-Native.md) · [Capability Model](07-Capability-Model.md) · [ADR-0005](adr/ADR-0005-manifest-as-build-artifact.md)
