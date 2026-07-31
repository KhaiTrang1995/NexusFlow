# ADR-0019: A lease's expiry is a value, not a Redis key TTL

**Status:** Accepted
**Date:** 2026-07-31
**Deciders:** Runtime team, Platform architecture
**Amends:** [ADR-0006](ADR-0006-journal-and-leases.md) ·
[ADR-0015](ADR-0015-journal-schema-and-durable-execution.md), commitment 1's portability note

> [ADR-0016](ADR-0016-postgres-journal-adapter.md) is this record's sibling: it is what the
> first store found out about a design, and this is what the **second** store found out
> about a *suite*. The two questions are different, and only the second one can answer
> whether "pluggable" was ever more than a claim.

## Context

[ADR-0006](ADR-0006-journal-and-leases.md)'s central assertion is that the journal and lease
primitives are **store-independent**. Until WP-54 every abstraction in this repository had
exactly one real implementation, so the assertion rested on nobody having tried — and
`docs/17`'s own warning box said so: *"one implementation per abstraction, so 'no abstraction
ships with fewer than two real implementations' is as untested as it was."*

WP-54 built `plugins/FlowX.Redis` and ran `LeaseStoreConformance` against Redis 7.0.15.

**The suite was inherited across an assembly boundary with zero edits.** All thirteen
assertions pass at the suite's own default 200 ms `LeaseTtl` — the override hook PostgreSQL
needed was not used at all. WP-51 wrote a suite, not a description of PostgreSQL, and that
is the finding this package existed to produce. The plan said so in advance: *"if it needs a
change to accept Redis, the suite was written against Postgres and WP-51 failed."*

**And the suite is not vacuous against this store.** Mutating the implementation back to the
idiomatic Redis lease — `SET key owner NX PX ttl`, `DEL` on release — turns **three**
assertions red, and only one of them is the obvious one:

| Assertion | Why it fails against the idiomatic lease |
|---|---|
| `EveryAcquisitionIssuesAStrictlyGreaterToken` | expiry deleted the key, so the counter restarted at 1 |
| `ASupersededTokenCannotRenew` | the zombie's token now *equals* its successor's, so the fence check admits it |
| `ASupersededTokenCannotRelease` | same cause: it can release a lease another node holds |

That is [ADR-0016](ADR-0016-postgres-journal-adapter.md)'s split brain reproduced in a
different technology, from a different cause, caught by an unmodified suite. It is the
strongest evidence available that the fencing-token argument is store-independent rather
than an artefact of one schema.

## Decision

### 1. Expiry is a field in a hash; the key carries no TTL

`{prefix}:{instance}:lease` is a hash of `token` / `owner` / `expires`. **No code path issues
`EXPIRE`, `PEXPIRE` or `DEL`.** Both endings — release and expiry — move `expires` into the
past and leave `token` exactly where it is.

The obvious implementation is the wrong one, and it is wrong for a reason this project has
already paid for once. ADR-0016 records that deleting the Postgres lease row on release
loses the per-instance counter, so the next acquisition hands a returning zombie a token
equal to its successor's, *"after which every fence check downstream passes"*. **A Redis key
TTL is that same deletion, one layer down and automatic.** The store that expires its own
lease key destroys the monotonicity the whole fencing argument rests on, and does it on a
timer rather than on a code path anybody reviews.

All four operations are Lua evaluated against `redis.call('TIME')`, so exclusivity does not
depend on the caller's clock — the same choice `PostgresLeaseStore` makes with `now()`,
reached independently by a different author against a different store. Lua rather than
`WATCH`/`MULTI` because each operation is a read plus a conditional write that must not be
separable.

Two tests pin the *cause* rather than the consequence, because the suite can only observe
the consequence and only after waiting out a TTL: `TheLeaseKeyNeverCarriesARedisExpiry`
checks all four paths, and `TheCounterLivesInRedisRatherThanInTheProcess` reads the hash
field directly and continues the sequence through a second store object — refusing an
in-process counter that would pass the suite on one node and fail on two.

### 2. `StepScope.Root` renders as `-`, and the fold is at the client, not the server

[ADR-0015](ADR-0015-journal-schema-and-durable-execution.md) and ADR-0016 both record that
commitment 1 works in PostgreSQL *partly by luck of dialect*: `Root` renders as the empty
string, PostgreSQL treats `''` as distinct from `NULL`, and a store that folds the two
rejects every root-scope row. WP-54 is the first adapter after the first, so this is where
the note had to become a test.

`ILeaseStore` carries no scope — none of the thirteen assertions mentions one — so the
obligation is discharged where a lease store's identifiers are actually formed: the key
space. `RedisKeys.RootScope` is `-`, `Root` never renders as the empty string, and
`ParseScope("")` throws rather than silently reading as `Root`.

**The hazard is one layer above where ADR-0016 expected to find it, and that is worth
recording.** Redis itself keeps the distinction: `HEXISTS` is true for a stored `""` and
false for a missing field. The fold is at the **client boundary** — a missing hash field and
a stored empty string arrive as two values whose `IsNullOrEmpty` is identical, so the
ordinary way to read an optional value collapses them by construction. A mapping justified
by a hazard nobody demonstrated would be superstition, so it was demonstrated against a live
server: mutating the sentinel to `""` turns four tests red.

## Consequences

**Positive**

- ADR-0006's store-independence claim has evidence rather than only advocates, and
  `docs/17`'s "one implementation per abstraction" caveat is retired for `ILeaseStore`.
- The conformance suite is proved to travel: unmodified, across an assembly boundary, to a
  store built on a different primitive. That is the precondition **WP-70** needs before it
  can publish the suite as a package, and the mitigation `05 §11` names for risks R3 and R8.
- The split-store arrangement `ILeaseStore`'s remarks describe — a Redis lease store and a
  Postgres journal sharing no transaction — is now buildable rather than hypothetical.

**Negative / accepted trade-offs**

- **The key space must not live under an `allkeys-*` eviction policy.** Eviction is deletion
  by another name and would silently restore the reset counter that decision 1 exists to
  prevent — with no error, no log line and no failing test, on a schedule nobody chose.
  `maxmemory` sizing must account for lease hashes that outlive the leases themselves.
- **Nothing reclaims a dead lease's key.** Decision 1's cost is that the hash is immortal by
  construction. Reclaiming it is a retention decision — which instances are finished, and
  how long their history is kept — and that is the journal's question, not a lease store's.
  A lease store that answered it would be a lease store that had opinions about instance
  lifecycle.
- **The split store is not atomic, in both directions.** Between a successful `AcquireAsync`
  and the `IFlowJournal.FenceAsync` that raises the journal's fence, the new owner holds a
  lease the journal has never heard of, **and its predecessor's token is still the highest
  one the journal will accept** — a window in which the old owner's writes are still
  admitted. It is bounded by one round trip; it is not zero. In the other direction, a crash
  between the two leaves a lease held with an unraised fence, which the next acquisition
  repairs precisely because the counter never restarts. This is why the ordering in
  `ILeaseStore`'s remarks is an obligation and not a suggestion.
- **Two stores now have to be operated**, and their failure modes are not the same one twice.
  A deployment choosing Redis for leases and PostgreSQL for the journal has taken on two
  availability stories to keep one guarantee.

**Revisit when:** the key space must live under an `allkeys-*` eviction policy — at which
point decision 1 cannot hold and the counter needs a home that is not the lease — or when a
lease store is asked to carry state a journal should own, which is the shape of pressure that
turns an `ILeaseStore` into a second journal.

---

**Back to:** [ADR index](README.md) ·
[ADR-0006 — journal and leases](ADR-0006-journal-and-leases.md) ·
[ADR-0016 — what a real database said](ADR-0016-postgres-journal-adapter.md) ·
[11 — Distributed Runtime](../11-Distributed-Runtime.md)
