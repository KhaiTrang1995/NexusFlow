# Benchmark results

> **Status:** WP-4 complete · **Recorded:** 2026-07-30 · **Runtime:** .NET 10.0.10, X64 RyuJIT
> **Budgets:** [14-Performance](../14-Performance.md) · **Gate policy:** [21-Quality-Gates §7](../21-Quality-Gates.md#7-performance-gates)

Reproduce with `./scripts/run-benchmarks.sh`. The committed baseline is
[`baseline.json`](baseline.json).

The P0 kill-criterion verdict this harness exists to produce is in
[**P0.md**](P0.md).

---

## 1. Budget B1 — the engine, measured

Budget: **p99 ≤ 5 000 ns** for a four-step ephemeral flow.

| Flow shape | Mean | p95 | Allocated | Budget used |
|---|---:|---:|---:|---:|
| 4 steps, no compensation | 169 ns | 174 ns | **0 B** | 3.5 % |
| 4 steps, two compensable, success | 216 ns | 218 ns | **0 B** | 4.4 % |
| 4 steps, failure + full unwind | 267 ns | 270 ns | 40 B | 5.4 % |

**Zero allocations on the success path**, which is WP-4's exit criterion and budget
B2. The engine spends about 3.5 % of its latency budget; policies, telemetry,
durability and the transport layer have the remaining 96 % to spend.

The 40 B on the failure path is one iterator object from `CompensationStack.Unwind`,
allocated once per *failed* flow on a path that is about to make a network call
anyway. Deliberately not optimised — see §4.

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
[ADR-0002](../adr/ADR-0002-compile-time-orchestration.md), and it is not made.

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
| `CompensationStack.Unwind` iterator | 40 B | Once per *failed* flow, immediately before a compensation makes a network call. Hand-rolling a struct enumerator would trade real readability for an allocation nobody will profile. |
| `ExecutionPlan` construction | 520 B | Once per flow at **startup**, not per execution. Measured so a validation rule added later cannot quietly turn a fast build into a slow one. |

Both are asserted by tests that require them to be *greater than zero*. If either
becomes free, the test fails — which is the cheapest way to notice that a comment
about a trade-off has stopped being true.

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

## 6. Caveats

Recorded in a container on shared hardware with `IterationCount=10` — enough for a
baseline, not enough for a published claim. Before WP-11 publishes the
kill-criterion report, the run must be repeated on dedicated hardware with
BenchmarkDotNet's default iteration count.

The absolute figures will move. The zeros should not.

---

**Back to:** [Performance budgets](../14-Performance.md) · [Quality gates](../21-Quality-Gates.md) · [Plan](../../PLAN.md) · [Checklist](../../CHECKLIST.md)
