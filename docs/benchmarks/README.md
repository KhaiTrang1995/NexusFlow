# Benchmark results

> **Status:** WP-3 complete · **Recorded:** 2026-07-30 · **Runtime:** .NET 10.0.10, X64 RyuJIT
> **Budgets:** [14-Performance](../14-Performance.md) · **Gate policy:** [21-Quality-Gates §7](../21-Quality-Gates.md#7-performance-gates)

Reproduce with `./scripts/run-benchmarks.sh`. The committed baseline is
[`baseline.json`](baseline.json).

---

## 1. What was measured, and what was not

The flow engine (WP-4) and the source generator (WP-5) do not exist yet. These
numbers are the **floor**: what dispatch and traversal cost before any platform
machinery is layered on. That is the entire reason WP-3 is sequenced before WP-4 —
a budget that only becomes measurable after the thing it constrains is built is a
budget that gets renegotiated rather than met.

Read the figures as *"the engine has this much headroom to spend"*, not as
*"FlowX is this fast"*. The latter claim is not available until WP-11.

## 2. Capability dispatch — budget B3 (p99 ≤ 150 ns)

| Shape | Mean | p95 | Allocated | vs fastest |
|---|---:|---:|---:|---:|
| Direct call (sealed type) | 9.52 ns | 9.70 ns | **0 B** | 1.12× |
| Interface dispatch | **8.52 ns** | 8.59 ns | **0 B** | 1.00× |
| Cached delegate | 8.82 ns | 8.86 ns | **0 B** | 1.04× |
| Reflection (cached `MethodInfo`) | 74.96 ns | 75.13 ns | **48 B** | 8.79× |

**All four are inside the 150 ns budget.** The interesting result is not that
FlowX's target shapes are fast — it is the two findings below.

### 2.1 The counter-intuitive result: interface dispatch was not slower

Interface dispatch measured *marginally faster* than the direct call. That is not
a real difference; it is the JIT devirtualising both. `EchoCapability` is sealed
with a single implementation loaded, so guarded devirtualisation collapses the
interface call to the same machine code, and the ~1 ns spread is run-to-run noise.

This is worth stating plainly because it **weakens one argument for ADR-0002** that
would have been convenient to make. Compile-time dispatch cannot be justified by
"it avoids a virtual call" — at this scale, with a monomorphic call site, the JIT
already does. The honest justification is §2.2.

### 2.2 The finding that does matter: reflection allocates

Reflection costs 8.8× more time, which is real but survivable — 75 ns against a
150 ns budget. The disqualifying number is the other column: **48 bytes per
dispatch**.

A four-step flow dispatched reflectively allocates ~192 B per execution. That does
not miss budget B2 by a margin to be optimised; it misses a **hard zero**. At
10 000 flows/second that is 1.9 MB/s of Gen0 pressure produced by the platform
itself, before any business object exists. This is the argument for compile-time
orchestration, and it is an allocation argument rather than a latency one.

## 3. Step loop — budget B1 precursor (p99 ≤ 5 µs)

| Operation | Mean | p95 | Allocated |
|---|---:|---:|---:|
| Walk a 4-step plan | 4.87 ns | 4.99 ns | **0 B** |
| Walk + dispatch each step | 19.99 ns | 20.41 ns | **0 B** |
| Failure path + compensation unwind | 96.58 ns | 98.45 ns | 328 B |
| Build a 4-step plan *(build-time)* | 133.96 ns | 140.66 ns | 456 B |

The skeleton of a four-step flow costs **20 ns against a 5 000 ns budget — 0.4 %**.
The engine, its policy chain, its telemetry and its context pooling have the
remaining 99.6 % to spend. That is a good position to start WP-4 from, and it is
the first evidence that ADR-0002's latency claim is achievable rather than
aspirational.

### 3.1 A known cost, recorded rather than hidden

`CompensateAll` allocates **328 B**: `Stack<T>` grows its backing array and
`Unwind()` is an iterator. Acceptable today — compensation runs once per *failed*
instance, not per step.

It stops being acceptable at WP-4, where the compensation stack becomes part of the
pooled per-instance state. The tripwire is
`AllocationBudgetTests.CompensationStackAllocatesTodayAndWP4MustPoolIt`, which
asserts the allocation is **greater than zero**. When pooling lands, that test
fails, and the fix is to flip the assertion to zero. A test that fails when the
code improves is the cheapest possible reminder that the improvement is owed.

`BuildPlan` allocates 456 B, which is fine: it runs once per flow at startup, not
per execution. It is measured anyway so a validation rule added later cannot
quietly turn a fast build into a slow one.

## 4. How the gate works

Three axes, deliberately weighted differently:

| Axis | Tolerance | Why |
|---|---|---|
| **Allocations** | exact, 0 % | Machine-independent, and B2/B6 are hard zeros. This is the sensitive check. |
| **Ratio to fastest** | ±15 % | Machine-independent. A real regression changes the ratio; runner noise does not. |
| **Absolute ns** | ±40 % | Machine-**dependent**. A catastrophe detector only. |
| **Absolute budget** | hard ceiling | The documented figure from 14-Performance. Non-negotiable. |

Gating absolute nanoseconds tightly would produce a job that fails on a busy CI
runner for reasons nobody can act on — and a job people learn to ignore is worse
than no job. Ratios and allocation counts reproduce exactly across machines, so
they carry the contract.

The gate was verified by injecting a fake 64 B allocation into a passing run: it
failed with exit code 1 and named the benchmark. A gate that has never been seen to
fail is an assumption, not a gate.

## 5. Caveats

These numbers were recorded in a container on shared hardware with
`IterationCount=10`, which is fast enough for a baseline and too few for a
publishable claim. Before WP-11 publishes the kill-criterion report, the run must be
repeated on dedicated hardware with BenchmarkDotNet's default iteration count.

The absolute figures will move. The ratios and the zeros should not.

---

**Back to:** [Performance budgets](../14-Performance.md) · [Quality gates](../21-Quality-Gates.md) · [Plan](../../PLAN.md) · [Checklist](../../CHECKLIST.md)
