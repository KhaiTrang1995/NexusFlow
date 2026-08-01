# Benchmark results

> **Status:** WP-4 complete · **Recorded:** 2026-07-30 · **Runtime:** .NET 10.0.10, X64 RyuJIT
> **Budgets:** [14-Performance](../14-Performance.md) · **Gate policy:** [21-Quality-Gates §7](../21-Quality-Gates.md#7-performance-gates)

Reproduce with `./scripts/run-benchmarks.sh`. The committed baseline is
[`baseline.json`](baseline.json).

The P0 kill-criterion verdict this harness exists to produce is in
[**P0.md**](P0.md). Build overhead (**B12**) is in [**B12.md**](B12.md) — **+0.4 %** against a
+8 % budget, measured with `scripts/measure-build-overhead.sh`.

That figure is for the one-flow reference sample and does not survive scale.
[**B12-scale.md**](B12-scale.md) measures P1's exit criterion — a 200-flow synthetic
solution — and records **+18.4 %** (95 % CI +16.3 to +19.9), a **FAIL**, with
`scripts/measure-scale-overhead.sh`. Quote the two together or neither: the same generator
produces both numbers, and which one applies depends entirely on how many flows the project
has.

The number that matters more than either is the **shape**: cost is **linear** in flow
count — `flows^0.91`, CI [0.82, 1.08], or about **9.5 ms of build time per flow with no
fixed term**. A superlinear generator would have put
[ADR-0002](../adr/ADR-0002-compile-time-orchestration.md)) itself in question; a linear one
is a constant factor with an owner. Roughly 62 % of it is `FlowPlanGenerator` and 37 % is
`StepBindingAnalyzer`.

That report **supersedes a provisional +23 %** measured on a loaded machine, and keeps it
visible in its §9 rather than deleting it. The harness now has a third outcome besides pass
and fail — `INCONCLUSIVE`, exit code 2 — returned when the within-arm spread, an A/A
control run in the same rounds, or the confidence interval says this machine cannot resolve
the question. It fired on the first attempt at the verdict above, and that is the feature.

Most of that generator cost buys one manifest field, and
[**B13-error-catalogue-resolution.md**](B13-error-catalogue-resolution.md) asks how often
that field can be produced at all. Against a corpus of 38 capabilities the reader publishes
a catalogue for 58 % and withholds for **42 %** — a number that document spends a section
explaining is a property of the corpus rather than of any real codebase. Of the 38, **55 %
get a catalogue that is both published and correct** and 3 % a correct empty one.

*This paragraph read "61 % / 39 %" and said that **four of the published catalogues are
wrong**, in a design whose stated property is that they cannot be. That was true when B13
was first recorded and is not true now: **WP-37 fixed both directions of the reader, and the
corpus reports zero wrong catalogues** — 0 %, against 17 % of published catalogues before.
Correctness cost less coverage than B13 predicted (it forecast 47 % withheld and got 42 %),
because two of the three under-reporting cases turned into correct catalogues rather than
withholds. The figures above are B13 §3 as restated.*

The finding that does not depend on the sample and did survive WP-37: the project layout
[07-Capability-Model §4](../07-Capability-Model.md) prescribes — contracts and their static
error class in a separate assembly — produces **no catalogue at all**, because the reader
needs a syntax body it cannot get from a referenced assembly. It is the evidence half of
[ADR-0014](../adr/ADR-0014-derived-error-catalogue-vs-build-budget.md)) §6, and it decides
nothing.

**None of that caught a 4.9× generator regression, and §5.1 is why.** A gate against a
budget you are already failing reads the same before a regression as after it.
[**generator-cost-gate.md**](generator-cost-gate.md) records the *relative* gate that
replaces it — blocking, on every pull request, against a committed baseline — together with
the measurement showing that it could not have been built on wall clock.

**Everything above is compile time. The one runtime measurement here that is not a benchmark
is [QR2-chaos.md](QR2-chaos.md)**, which is P2's correctness criterion rather than a budget:
10 000 durable flows per arm, **97 worker processes killed with `SIGKILL`** at a step boundary
against a shared PostgreSQL, **zero duplicate effects against the guarantee** and **zero lost
instances**. Its scope is deliberately narrower than the working package it belongs to —
WP-50 is *"B7, B8 and the QR2 chaos rig"* and **only the rig is built**.

Two of its numbers should always be quoted together. The exposure the rig *does* find is
[ADR-0006](../adr/ADR-0006-journal-and-leases.md))'s documented one — an effect that happened
with no commit to record it — and at concurrency 1 it is exactly one duplicate per kill, and
exactly zero when the kill moves to the other side of the commit. And the resume p99 is
**measured and not gated**: **32.9 s against QR2's 45 s** on the recorded run, **48.1 s** and
**69.9 s** on two others with every correctness row still zero. It is the 30 s lease TTL plus
however long a backlog takes to drain through `MaxConcurrentRecoveries`, so it is not a figure
to quote on its own.

---

## 1. Budget B1 — the engine, measured

Budget: **p99 ≤ 5 000 ns** for a four-step ephemeral flow.

| Flow shape | Mean | p95 | Allocated | Budget used |
|---|---:|---:|---:|---:|
| 4 steps, no compensation | 169 ns | 174 ns | **0 B** | 3.5 % |
| 4 steps, two compensable, success | 216 ns | 218 ns | **0 B** | 4.4 % |
| 4 steps, failure + full unwind | 267 ns | 270 ns | 56 B | 5.4 % |

**Zero allocations on the success path**, which is WP-4's exit criterion and budget
B2. The engine spends about 3.5 % of its latency budget; policies, telemetry,
durability and the transport layer have the remaining 96 % to spend.

The 56 B on the failure path is one iterator object from `CompensationStack.Unwind`,
allocated once per *failed* flow on a path that is about to make a network call
anyway. Deliberately not optimised — see §4.

**It was 40 B when this was first recorded, and the 16 B is not a defect.** An
iterator's state machine carries the value it yields, so its size is a fact about
that value. `Unwind` used to yield a bare `StepNode`; it now yields
`CompensationEntry`, which gained a reference field twice — `FlowContext? Scope` in
WP-29 so a compensation inside a `ForEach` undoes the element its own step processed,
and `StepScope JournalScope` in WP-57 so the compensation row for the third element is
keyed as the third element. Both are correctness. Getting back to 40 B means giving
one of them up, so the budget moved instead. §4 records what the number is made of.

## 2. Budget B3 — capability dispatch

Budget: **p99 ≤ 150 ns**.

| Shape | Mean | p95 | Allocated |
|---|---:|---:|---:|
| Direct call (sealed type) | 19.96 ns | 20.62 ns | **0 B** |
| Interface dispatch | 22.03 ns | 22.65 ns | **0 B** |
| Cached delegate | 20.62 ns | 21.34 ns | **0 B** |
| Reflection (cached `MethodInfo`) | 97.88 ns | 99.33 ns | **48 B** |

Interface dispatch is not meaningfully slower than a direct call — the JIT
devirtualises a sealed, monomorphic call site. So "compile-time dispatch avoids a
virtual call" is **not** an argument for
[ADR-0002](../adr/ADR-0002-compile-time-orchestration.md)), and it is not made.

The argument that holds is the last column. Reflection allocates 48 B per dispatch;
a four-step flow allocates ~192 B before any business object exists. That does not
miss budget B2 by a margin to be optimised — it misses a **hard zero**.

## 3. What WP-4 changed, and how it was found

The engine did not hit zero on the first attempt. It allocated **592 B** per
four-step flow, and review had already passed on the code that did it. Measurement
found three separate causes:

| Cause | Cost | Fix |
|---|---:|---|
| `ConcurrentBag` in the context pool | ~150 B | `ConcurrentBag.Add` allocates a node per item, so the pool built to avoid allocating allocated on every return. Replaced with a fast-slot + fixed array claimed by `Interlocked.CompareExchange`. |
| `new Random(0)` in the context reset | ~150 B | Made lazy. Most flows never ask for randomness, and the durability contract journals the seed on first use anyway — so lazy is both cheaper and more correct. |
| `new CompensationStack()` per execution | 288 B | Gave the type a `Reset` and moved ownership into the pooled context. `Clear` keeps the backing arrays. |

None of these were visible by reading the code. All three were found by a test that
asserts a number.

The async machinery, which was the first suspect, turned out to cost **0 B**: an
`async ValueTask<T>` whose awaits all complete synchronously never boxes its state
machine. `EngineAllocationTests.RunSync` now asserts the engine completes
synchronously when its steps do, so a future change that quietly introduces a
suspension fails loudly.

## 4. Costs recorded rather than removed

| Cost | Size | Why it stays |
|---|---:|---|
| `CompensationStack.Unwind` iterator | 56 B | Once per *failed* flow, immediately before a compensation makes a network call. Hand-rolling a struct enumerator would trade real readability for an allocation nobody will profile. 16 B of object header and method table, 4 B of iterator state, 4 B of thread id, 8 B for the stack it drains, and 24 B for the `CompensationEntry` it yields — `StepNode`, `FlowContext? Scope`, `StepScope JournalScope`, one reference each. |
| `ExecutionPlan` construction | 520 B | Once per flow at **startup**, not per execution. Measured so a validation rule added later cannot quietly turn a fast build into a slow one. |

Both are asserted by tests that require them to be *greater than zero*. If either
becomes free, the test fails — which is the cheapest way to notice that a comment
about a trade-off has stopped being true.

**"Greater than zero" was not enough, and the unwind iterator is how we found out.**
It walked 40 B → 48 B → 56 B across two working packages while that assertion and its
`< 256` ceiling stayed green, because a band cannot see a number move inside it. The
only thing that objected was the benchmark gate, on a job that was already failing for
other reasons and that nobody was reading. `UnwindingAllocatesOneIteratorPerFailedFlow`
now pins **56 B exactly**, in the same commit as the figure above and the one in
`baseline.json`, so the three cannot drift and the next byte fails a unit test on the
pull request that adds it.

## 5. Gate design — and a claim WP-3 got wrong

WP-3 asserted that ratios between benchmarks in the same run are machine-independent
and could therefore be gated tightly at ±15 %, while absolute times could not.

**That was wrong, and the evidence is two runs of the identical commit on the
identical container:**

| Benchmark | Run 1 | Run 2 | Absolute Δ | Ratio Δ |
|---|---:|---:|---:|---:|
| Dispatch, direct | 9.52 ns | 19.96 ns | +110 % | −10 % |
| Dispatch, interface | 8.52 ns | 22.03 ns | +159 % | +10 % |
| Dispatch, reflection | 74.96 ns | 97.88 ns | +31 % | **−44 %** |
| Walk + dispatch | 19.99 ns | 29.70 ns | +49 % | **+63 %** |
| Build plan | 133.96 ns | 193.54 ns | +44 % | **+58 %** |

The reason is straightforward in hindsight: the ratio's denominator is the fastest
benchmark in the run, which sits at 10–20 ns — on the measurement noise floor. A
denominator that moves ±100 % moves every ratio with it. Ratios are only
machine-independent when the baseline is comfortably above the noise.

So the gate now splits by what is actually reproducible:

| Check | Class | Tolerance | Why |
|---|---|---|---|
| **Allocations** | **blocking** | exact, 0 % | Deterministic and machine-independent. This is the real gate. |
| **Budget ceiling** | **blocking** | hard | The documented figure from 14-Performance. A four-step flow at 170 ns against 5 000 ns means an order-of-magnitude regression fails loudly. |
| Absolute drift | advisory | ±40 % | Reported for a human. Not reliable here. |
| Ratio drift | advisory | ±15 % | Same. |

The honest limitation: a **2× regression would not be caught** by these gates on this
hardware. Only a 30× one would. That is why WP-11 re-records the baseline on
dedicated hardware and runs the checker with `--strict`, which promotes drift to
blocking.

The gate was verified by injecting a 72 B allocation into a passing run: blocked,
exit 1, benchmark named. A gate nobody has seen fail is an assumption.

### 5.1 The same split, applied to compile time

[**generator-cost-gate.md**](generator-cost-gate.md) is this section's argument carried
over to the generator, because the same thing happened again for the same reason. A commit
made `FlowPlanGenerator` **4.9× more expensive** and merged unnoticed, because the only
gate on compile-time cost was *absolute*, against a +8 % budget the project was already
failing — so it read the same before the regression as after it. See
[B12-scale.md](B12-scale.md) §5.2 and §5.3.

The replacement gates **bytes allocated by one run of the generator** against a committed
baseline. Measured over twelve identical runs on a container at load 5.8 to 21.1:

| Metric, the same twelve runs | Worst disagreement between two identical runs | What the real 4.9× regression produces |
|---|---:|---:|
| Elapsed wall clock | **+139 %** | +77 % |
| **Bytes allocated** | **0.071 %** | **+103.6 %** |

The timing row is why the gate is not a timing gate: **a threshold wide enough not to fire
on nothing is too wide to fire on the incident.** That holds in-process, with MSBuild and
the compiler server already removed, so it holds a fortiori on a hosted runner. The
allocation row is why the gate can be blocking, at a **+2 %** threshold — 29× the worst
deviation of the statistic it gates, and 1/50 of the regression it exists to stop.

| Check | Class | Tolerance |
|---|---|---|
| **Generator allocations vs the committed baseline** | **blocking** | +2 % |
| **Subject or Roslyn version moved** | **blocking** | exact — re-record, never compare across it |
| Generator elapsed time | advisory | reported, never gated |
| Baseline gone pessimistic | advisory | −2 %, a notice asking for a re-record |

Verified the way this section demands, and one further way: a fabricated 2.5 % regression
is rejected in CI (`generator-cost-self-test`), and the real commit `c7ae70a` fails the
gate at **+102 %**. **P1's +8 % criterion is untouched by all of this and is still
failing** — `scripts/check-generator-cost.py` reprints it on every run, including passing
ones, so that a green relative gate cannot be read as a budget that is met.

### 5.2 The gate above has been red on `dev`, and that is why 16 bytes got in

§5.1 says a gate against a budget you are already failing *"reads the same before the
regression as after it"*. The **Benchmark budgets** job is the same failure in a second
form: not a gate too loose to fire, but a gate already firing for reasons nobody was
acting on, so one more error line changed nothing anybody could see.

It triggers on every push and pull request to `master` and `dev`, it is blocking, and it
has been failing on `dev` continuously since at least run **#41** (2026-07-31 01:18,
commit `1c654eb`) — sixty-odd consecutive pushes. That run named **three** blocking
failures, and the failure path was not among them:

```
::error::StepLoopBenchmarks.BuildPlan: allocated 528 B, baseline 520 B (allocation counts are exact)
::error::CompilerBenchmarks.GeneratorOnly: allocated 770994 B, more than 15% above the baseline 588937 B
::error::CompilerBenchmarks.WithGenerator: allocated 1800422 B, more than 15% above the baseline 1508524 B
```

`EngineBenchmarks.SagaFailure` then went 40 B → 48 B → 56 B across WP-29 and WP-57, and
`StepLoopBenchmarks.CompensateAll` went 328 B → 440 B behind it. By run **#104**
(2026-08-01, commit `386a1a3`) the same step reports **five** blocking failures instead
of three — two more error lines on a job that had been red for two days, which is no more
visible than three. **Nothing else objected**: the two unit tests over this path assert
`> 0` with a ceiling of 256 B and 2 048 B, and a band cannot see a number move inside it.

What this commit closes, and what it deliberately does not:

| Gate failure | Status |
|---|---|
| `EngineBenchmarks.SagaFailure` 40 → 56 B | **Resolved.** Cause bisected to `744b005` and `16b6988`, baseline restated above with the reason, and `UnwindingAllocatesOneIteratorPerFailedFlow` now pins the exact figure so the *Allocation budget (B2)* job — which is green and read — catches the next byte. |
| `StepLoopBenchmarks.CompensateAll` 328 → 440 B | **Resolved.** Same root cause, same commit; 328 B reproduces exactly at `e6fcd37` and at `1c654eb`, and the new 440 B is reported by the container and by the hosted runner alike (run #104). |
| `StepLoopBenchmarks.BuildPlan` 520 B committed | **Open, and not re-recorded.** It does not reproduce at its own commit, and it does not reproduce across machines: **456 B** at `e6fcd37` here on the recorded runtime with the recorded 10 warmups and 30 iterations, **464 B** here at `dev`, **528 B** on the hosted runner at `dev` — against **520 B** in the file. In the very same runs `CompensateAll` agreed to the byte on both machines, so this is one entry rather than a broken harness. An exact gate on a figure that is not reproducible is gating something other than the code, and the fix is to establish which of the four numbers is the subject — not to overwrite the baseline with whichever machine ran last. |
| `CompilerBenchmarks.GeneratorOnly` / `.WithGenerator` over their 15 % band | **Open, and not re-recorded.** These measure Roslyn plus the generator: **827 906 B** against a committed 588 937 B, and **2 257 176 B** against 1 508 524 B, with `WithGenerator` at **51.2 ms against a committed 11.2 ms**. That is compile-time cost, which is [B12-scale.md](B12-scale.md) §5.2's subject and `generator-cost`'s; re-recording it here would erase the evidence of a regression the project is tracking. |
| `CompilerBenchmarks.WithGenerator` p95 over budget **B12** | **Open, and it is the budget ceiling rather than an allocation.** p95 **61.97 ms** against 60 ms — 3 % over, and it does not fire every run: the previous full run on the same commit and the same container measured 58.03 ms. It is the only blocking check here that is a timing check, and it sits close enough to its ceiling that this container decides it. Same subject as the row above. |

So the job still exits 1, on failures that predate the failure-path allocation and have
nothing to do with it — three allocation entries, plus a B12 p95 ceiling that this
container crosses on some runs and not others. **Recorded rather than papered over** — the
alternative on offer was to move four baselines in one commit and call the gate green,
which is the behaviour that produced this section.

Measured on the container this baseline was recorded on, Release, .NET 10.0.10, x64,
after the two entries above were restated:

```
EngineBenchmarks.Query            235.24 ns     0 B
EngineBenchmarks.SagaSuccess      309.07 ns     0 B
EngineBenchmarks.SagaFailure      418.86 ns    56 B    <- gate accepts
StepLoopBenchmarks.CompensateAll  224.52 ns   440 B    <- gate accepts
```

## 6. Caveats

Recorded in a container on shared hardware with `IterationCount=10` — enough for a
baseline, not enough for a published claim. Before WP-11 publishes the
kill-criterion report, the run must be repeated on dedicated hardware with
BenchmarkDotNet's default iteration count.

The absolute figures will move. The zeros should not.

---

**Back to:** [Performance budgets](../14-Performance.md) · [Quality gates](../21-Quality-Gates.md) · [Plan](../../PLAN.md) · [Checklist](../../CHECKLIST.md)
