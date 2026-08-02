# 25 — What is left to build

Forward-looking only. When something ships, its section is **deleted**, not
annotated — the code and its tests are the record. Built subsystems are listed
in [CHECKLIST §5e2](../CHECKLIST.md#5e2-platform-subsystems--what-runs-what-is-declared-what-is-absent).

| # | Feature | Why it is here |
|---|---|---|
| 1 | **Tenant fairness** | [16 §4](16-Multi-Tenant.md)'s six mechanisms are unbuilt. One tenant at 10× quota can starve another; `Row` isolation stops them reading each other's rows and does nothing about this |
| 2 | **`Schema` / `Database` isolation** | Refused at startup rather than implemented. A deployment that needs either gets a clear error and no feature |
| 3 | **`Stream` trigger** | Last unbound kind of eight. Blocked on the engine below |
| 4 | **Stream engine** | **Not next.** Nothing defines the checkpoint format, watermark generation or how window state is journaled. Implementing it means inventing it, and P7 is the least specified phase in the roadmap |
| 5 | **Studio** | **Not next.** Sixteen one-line mentions across the docs and no design at all |

## Known defects, unfixed

- **`AllocationBudgetTests.UnwindingAllocatesOneIteratorPerFailedFlow`** — two
  agents have reported it failing at 80 B against a committed 56 B; it passes
  here in Release, both with and without a fresh build. Unreproduced, so
  unfixed. Whoever reproduces it should restate `docs/benchmarks/baseline.json`
  in the same commit.
- **`EveryShippedRuntimeProjectIsAotAnalyzed` walks only `src/`** — no plugin is
  covered, including the five that ship.
- **`published_at` no longer means "nobody needs this row."** A host with a
  change subscription and no broker never sets it, so retention holds those
  instances for ever.
- **An indirect emit/observe cycle across two flows is not refused** and nothing
  bounds it. The direct case is refused at startup.
