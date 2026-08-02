# ADR-0044: A cache is a plugin store keyed by the payload the journal would have written, and it declines anything that came out redacted

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Repository owner · Platform architecture

> Recorded because [ADR-0025](ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md) §1.2
> said stage 5 needed "a store, and a store is a plugin contract —
> [ADR-0009](ADR-0009-plugin-contracts.md) territory, decided per contract rather than in
> passing".

---

## 1. Context

The engine knows no contract types, so a cache needs two things it cannot do alone: derive a key
from the step's input, and put a hit back into the bag as a typed value. Both need a
`JsonTypeInfo<T>`, and only generated code can name one. So stage 5 is a store contract, two
dispatcher members, and a decision about what travels between them.

What travels is the thing WP-59 was careful about. `JournalPayload` has no accessor for its
value and its only exit is `ToJson`, which redacts. A cache is a new sink for contract values,
and a worse one than a journal in the way that matters operationally: a Redis a team runs for
caching is very often not the Redis they protect like a database.

But if the payload goes out through the one exit, a `[Sensitive]` member arrives as
`[redacted]`, and that breaks a cache twice. **On the key**, two callers whose inputs differ only
in a marked member key identically and the second is served the first one's result — which is
[10 §2](../10-Policy-Framework.md#2-fixed-stage-order--the-core-decision)'s "tenant A served
tenant B's cached data" arriving through the key rather than through the ordering, and the
ordering guarantee does not prevent it because the ordering is correct. **On the entry**, a hit
restores `[redacted]` where a capability's answer should be.

**Rejected:** serialising the cached value without redaction — the second exit, and a marked
member in a store chosen for throughput; a build-time error refusing `Cache` over a contract
with a `[Sensitive]` member — insufficient rather than wrong, because the redaction that applies
is name-based against the *flow's* `SensitiveMembers`, so a step contract can acquire a redacted
member without carrying a marker and the rule would be silently wrong in the interesting case; a
`HasCachedSteps` flag of its own — ADR-0023 predicted "adding fields to `StepPolicy`, widening
`IsActive`, and nothing else", and that holds because stage 5 runs inside stage 4's nesting; an
in-process cache — a per-process hit rate, and not a seam anything outside FlowX could supply.

---

## 2. Decision

**`IResultCache` is declared in `FlowX.Abstractions`, implemented by `FlowX.Redis` and
`FlowX.Postgres`, and held to both by `ResultCacheConformance`. What travels is what
`JournalPayload.ToJson` produced, and the engine declines any key or entry whose document came
out carrying the redaction placeholder.**

**2.1 Where it runs.** Outside the dispatch and inside stage 4, which is the insertion point
ADR-0025 §2.5 named. The nesting is
`Retry { CircuitBreaker { Bulkhead { Timeout { Cache { capability } } } } }` — so a store round
trip is inside the budget the author wrote, a hit counts as a call the breaker saw succeed, and
a caller the bulkhead refused never reaches the store.

**2.2 The key.** SHA-256 over *capability id · capability version · tenant · principal
permission set · input document*, U+001F-separated. The version, because a capability that
changed its answer for one input is a different capability to a cache. The tenant unless the
scope is `Global`. The permission set only under `CacheScope.Principal`, read through exactly
`StepAuthorization.PermissionClaimTypes` and sorted, so what a cache is scoped by and what a
stance is decided from are the same claims read the same way. Hashed because an input can be
arbitrarily large; SHA-256 rather than a fast hash because a colliding key serves one caller
another's data. U+001F so no component can forge a boundary.

**2.3 A redacted document is used for nothing.** The engine refuses a key carrying
`JournalPayload.Redacted` and refuses to store an entry carrying it. Between them, a
`[Sensitive]` member cannot reach a cache and cannot come back out of one — structurally,
because the payload's only exit is what puts the placeholder there. A step so refused is
dispatched, and that is not counted as a miss: a miss is a cache that was asked.

**2.4 A hit is restored through `RestoreState`.** An entry is composed as a one-member state-bag
document keyed by the contract's simple name — exactly what a resumed instance reads — so there
is one deserialiser for a stored contract value in this runtime.

**2.5 Every failure degrades to a dispatch.** A store that is down, an unbuildable key, an
unreadable entry: the capability is called, which is what the step did before anything cached
it. This is the opposite of what [ADR-0043](ADR-0043-an-audit-record-is-the-journals-payload-redacted-twice.md)
decides for the audit sink, and the asymmetry is ADR-0025 §2.3 and §2.4's.

**2.6 `FLOWX1018` is relied on rather than re-checked.** Declaring `Cache` on a capability with
side effects is already an error at build time and again in `PolicyChain.ForStep`, so the engine
does not ask whether the step it is serving is a read.

---

## 3. Consequences

**Positive:** `.Cache(ttl)` does what it says, proved in the only shape that can tell a
consulted cache from an unconsulted one —
`CachePolicyTests.TheSecondExecutionIsServedFromTheCache` runs one plan twice and asserts the
second execution never dispatches. The seam has two implementations and a suite that does not
know which it is running; neither needed an edit to it. A marked member cannot reach a cache
without any rule, attribute or review step. `flowx_cache_hits_total` and `_misses_total` are
created as a pair, so a hit *rate* has a denominator.

**Negative / accepted trade-offs:**

* **A step whose contract carries a `[Sensitive]` member silently never caches.** Correct and
  invisible: the author declared a cache, the build says nothing, the dependency is called every
  time. §1 says why the obvious build-time rule is unsound as stated; a sound warning is
  outstanding work.
* **Stampede protection is not built.** [10 §8](../10-Policy-Framework.md#8-cache-safety--four-of-five-defaults-are-behaviour)
  lists single-flight among the defaults and there is none: *n* concurrent misses are *n*
  dispatches.
* **Under the default `Tenant` scope two principals in one tenant share an entry.** That is what
  the scope means, and the mitigation is only that a step's stance is decided before the cache
  is consulted, so a caller who could not run the step is never served from it.
* **A cache entry outlives a plan change.** Nothing in the key describes the flow, so a TTL is
  the only bound on serving an answer produced by a build that has been rolled back.
* **PostgreSQL grows a table nothing sweeps.** A read refuses a lapsed row, so correctness does
  not depend on a sweep; space does.

**Revisit when:** single-flight is needed, which widens `IResultCache` rather than the engine; or
a sound build-time rule for the redacted-contract case is designed, which ends the silent
non-caching; or a cached step must be bounded separately from the capability it stands in for,
since the store round trip is inside the timeout today.

---

**See also:** [ADR-0009](ADR-0009-plugin-contracts.md) ·
[ADR-0025](ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md) ·
[ADR-0043](ADR-0043-an-audit-record-is-the-journals-payload-redacted-twice.md)
