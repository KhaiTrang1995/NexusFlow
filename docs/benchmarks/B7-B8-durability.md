# B7 and B8 — durable step commit and journal rehydration

What `docs/14-Performance.md` rows B7 and B8 cost against a real PostgreSQL.

Rig: `tests/FlowX.Durability.Bench`. Run it with `scripts/run-durability-latency.sh`; the
verdict comes from `scripts/check-durability-latency.py`. The recorded run is
[`B7-B8-durability.json`](B7-B8-durability.json).

## What was measured

| Budget | Operation | Ceiling |
| --- | --- | --- |
| B7 | `IFlowJournal.CommitAsync` — one step boundary, durably | p99 15 ms @ 5 000 commits/s/node |
| B8 | `DurableExecution.ResumeAsync` — raise the fence, then read the frontier | p99 8 ms |

Both are the paths the product uses. B8 in particular is the verb a recovering node calls,
not a frontier read on its own: timing only the read would report about half of what a
takeover pays.

## The recorded run

PostgreSQL 16.13, `synchronous_commit = on`, 4 cores, 85 s wall clock.
20 000 measured commits across 32 writers; 2 000 instances of 20 committed steps rehydrated.

| | n | p50 | p99 | max |
| --- | ---: | ---: | ---: | ---: |
| B7 commit, issue → complete | 20 000 | 7.599 ms | **14.146 ms** | 23.065 ms |
| B7 commit, due → complete | 20 000 | 956.442 ms | 1 271.736 ms | 1 283.100 ms |
| B8 rehydration | 2 000 | 1.880 ms | **3.634 ms** | 30.089 ms |

Offered 5 000 commits/s; achieved **4 103.5/s**. No refusals, no failed resumes,
40 000 step rows read back.

**Verdict: INCONCLUSIVE.**

## What that means

**B8 is met.** 3.634 ms against a budget of 8 ms, over 2 000 rehydrations that each read
20 committed steps. This is the first time the row has been measured at all.

**B7 is measurable but not yet measured at its own load.** This four-core container
saturates at roughly 4 100 durable commits per second — a writer sweep at 16, 32 and 64
writers puts the ceiling between 2 700 and 3 100/s when the schedule is removed entirely —
so it cannot offer the 5 000/s the budget names. Past saturation the response latency is
the queue in front of the store, which is why it reads 1.27 s while the store's own cost is
14.1 ms.

The store-side p99 of 14.146 ms sits just inside the 15 ms ceiling at 4 100/s. That is
suggestive and it is not a pass: B7 asks for that latency *at* 5 000/s, and nothing here
has shown the number holds when the last 900 commits a second are added.

Judging B7 needs a machine that can offer the rate. Until one runs it, the row's gate says
so.

## Why the response latency is the gated one

The rig records two clocks per commit. *Service* is issue-to-complete — what the store took.
*Response* is due-to-complete against the offered schedule — what a caller waiting on that
schedule saw, including time spent queued.

Gating on service would be the coordinated-omission mistake: when the store slows down fewer
commits are issued, and each is timed from the moment a writer was free for it, so the p99
*improves* under overload. Response latency cannot do that.

The one place the checker steps back from it is a run that missed the offered rate, where
the response p99 restates the shortfall rather than adding a finding. That downgrade is
confined to B7, and `scripts/selftest-durability-verdict.py` holds the boundary — nine
fabricated verdicts, two of them green.

## Reproducing

```bash
FLOWX_POSTGRES_CONNECTION="Host=localhost;Port=5432;Username=postgres;Database=postgres" \
  ./scripts/run-durability-latency.sh
```

The rig takes its own schema and drops it. Without the variable it skips with a reason;
with the variable set and no server it fails rather than skipping, because a skip there
would report an unmeasured budget as a green run.
