# ADR-0057: A journal write budget is drawn in blocks and paces rather than refuses

**Status:** Accepted
**Date:** 2026-08-02
**Deciders:** Repository owner · Platform architecture
**Relates to:** [ADR-0040](ADR-0040-a-rate-limit-is-shared-or-it-is-not-a-rate-limit.md) ·
[ADR-0046](ADR-0046-a-tenant-is-resolved-at-admission.md)

> [16 §4](../16-Multi-Tenant.md#4-fairness--the-noisy-neighbour-problem) names six fairness
> mechanisms. Five shipped on 2026-08-02 and the sixth was left out because **both obvious
> shapes are wrong**, which is a decision and had no record. This is that record, and the third
> shape.

---

## 1. Context

### 1.1 The two shapes that were rejected before this

| Shape | Cost | Why it was refused |
|---|---|---|
| Spend a shared budget per commit | one `IRateLimiterStore` round trip in front of every journal write | The journal write is already the dominant term of a durable step ([14 §5](../14-Performance.md)). Putting a second network call in front of it roughly doubles the latency of the operation the budget exists to protect, on every commit, for every tenant that has a budget at all |
| Keep the budget per process | free | [ADR-0040](ADR-0040-a-rate-limit-is-shared-or-it-is-not-a-rate-limit.md)'s anti-conservative limiter: n nodes admit n × the declared rate, the factor is the replica count and nothing declares it. For a **write** budget the consequence is sharper than for a rate limit — the thing it names in its own summary, the shared durable store, is the thing that is not protected |

### 1.2 The direction that does not work, and why it is worth writing down

Charging the budget at admission is the obvious escape, because
[16 §4](../16-Multi-Tenant.md#4-fairness--the-noisy-neighbour-problem) already says *"all limits
are enforced at stage 1 (Admission) — before authentication, before any allocation, before any
journal write"*, and `TenantAdmissionControl` already spends two shared buckets there. It needs a
figure: how many rows this invocation will write.

**There is no such figure.** A flow's row count is not a property of its plan:

* a `ForEach` commits once per iteration, over a collection the plan never sees;
* a retry writes a new row per attempt — `StepKey.Attempt` is in the primary key precisely
  because it does;
* `StepKind.Jump` can target a lower index, so a plan may loop.

`ExecutionPlan.Graph.Count` is therefore a lower bound and not a bound. Charging it would
under-count exactly the flows that write the most, which is the population a write budget exists
for. Charging some multiple of it would be a number nobody can derive and nobody can audit.

### 1.3 What the lease could have carried, and why it does not

A node holds a fenced lease per instance and could draw write credit alongside it. It was
rejected on granularity: a lease is per **instance**, and instances of one tenant are many and
short. Credit drawn per lease is credit drawn per instance, which is a round trip per instance
rather than per row — better than per commit and still proportional to the wrong thing, since an
instance may write one row or ten thousand. Credit belongs to the tenant, which is what the
budget is declared against, and it outlives any one instance.

---

## 2. Decision

**A tenant's journal write budget is held as a bucket of *blocks* in `IRateLimiterStore`. A node
draws one block — `JournalWriteBlock` rows of credit — in one call, and then spends it locally,
one row at a time. A tenant with no credit and an empty bucket **waits**; it is not refused.**

### 2.1 A block draw is not a local budget, and it is not a cache

ADR-0040 §3 forbids "a local cache in front of [the limiter], because a cache is the
process-local limiter this record refuses, arriving as an optimisation". A block of credit is
neither of the two things that argument is about:

| | Process-local budget | Cached verdict | **Block draw** |
|---|---|---|---|
| Where the total is decided | each node, independently | the first node to ask | the one shared bucket |
| Fleet total against a declared *W* per window | n × *W* | unbounded within the TTL | **≤ *W*, always** |
| Direction of the error | admits more than declared | admits more than declared | admits **less** — credit a node holds when the window turns over is lost |

The credits a node spends were removed from the shared bucket before any of them could be spent,
and no other node can spend them. So the property ADR-0040 exists to protect — *"`RateLimit(20,
PT1S)` means twenty per second across the deployment"* — holds here without qualification, and
what varies with the replica count is only how much of a declared budget goes unused. That is the
conservative direction, and it is the direction a per-process budget does not have at any block
size.

The bucket is denominated in blocks rather than in rows so that the existing one-permit
`TryAcquireAsync` grants a block, which is why no plugin contract changed and both existing
stores back this unmodified. The arithmetic is exact because `FlowXOptionsValidator` refuses a
budget the block size does not divide: rounding it down would enforce 96 while the configuration
said 100, and a limit that does not mean what it says is the one failure this whole mechanism has
to be free of.

### 2.2 Exhaustion paces, and this is the part that is genuinely contested

Every other refusal in this repository is a `Result` failure
([ADR-0029](ADR-0029-a-refusal-is-a-result-failure.md)), and the other five fairness mechanisms
refuse at admission. This one does not refuse at all, and the reason is where the budget is
spent rather than what it is:

* An admission refusal costs the caller nothing that had started. `TenantErrors.RateLimited`
  says so — *"the call was refused at admission, before a flow instance existed"*.
* A journal write happens inside a durable flow that already holds a lease, an instance row and a
  partially executed saga. Refusing one there is not backpressure. It strands the instance with
  its compensations unrun, and the recovery sweep then picks it up and is refused again for the
  same reason, for as long as the tenant is over budget.

`FlowHost.AdmitAsync` already draws this exact distinction for continuations, in as many words:
*"refusing it now is not backpressure, it is abandoning a saga halfway with its compensations
unrun"*. A write budget that refused would reintroduce that defect at the row instead of at the
call.

So a tenant over its budget waits for credit, and the wait is bounded by the caller's
cancellation token — the flow's deadline, the host's drain — and by nothing else. The tenant's
flows complete late; the shared store is protected because late is what the pacing achieves; and
the noisy tenant's waiting holds nothing the quiet tenant needs, because the buckets and the
credit are both per tenant.

**A store that cannot answer is still a refusal**, unchanged from ADR-0040 §2.2. A limiter that
cannot be reached does not know whether the tenant is inside its budget, and a durable flow whose
commit fails is left for the recovery sweep, which is a state the platform is built to recover
from. Refusing on doubt and pacing on a "no" are the same stance: never write on an answer the
budget did not give.

### 2.3 Bound to the journal, not to the engine

`FlowHost.JournalFor` wraps the tenant-scoped journal in `BudgetedJournal` where a budget is
declared, and returns the store's own journal where one is not. There is no branch on the commit
path to be predicted and no field to read, which is the bargain `ExecutionPlan`'s flags struck one
layer down. A single-tenant deployment does not reach the wrapping code at all — `FlowHost`
branches on `TenantIsolation.None` first — so budget **B2** is untouched structurally rather than
carefully, and an `Ephemeral` flow has no journal to be charged for in the first place.

Three of the six journal members are charged: `StartAsync`, `CommitAsync` and `CompleteAsync`,
which are the three that write rows. Reads are free. `FenceAsync` is free because it is what a
node issues to **take over** an instance, and charging it would make an exhausted tenant's
recovery wait on the budget its own stuck instances filled.

---

## 3. Consequences

**Positive:**

* **`JournalWritesPerWindow` means that many rows across the deployment**, per tenant, per
  window. `TenantWriteBudgetTests.ANoisyTenantsSpentWriteBudgetIsSpentOnEveryNodeAndOnNoOtherTenant`
  is the proof, against real PostgreSQL with two independently constructed limiter clients — the
  same arrangement `RateLimiterConformance` uses, applied to the thing that consumes the limiter.
* **The cost is one round trip per block, not one per row.** A flow's rows are bought with one
  draw; the rest of a block costs an interlocked decrement.
* **No plugin contract changed.** `IRateLimiterStore` is untouched, so `RedisRateLimiterStore`
  and `PostgresRateLimiterStore` back this without a line of new arithmetic and the conformance
  suite still pins exactly what it pinned before.
* **[16 §4](../16-Multi-Tenant.md#4-fairness--the-noisy-neighbour-problem)'s table has no absent
  row.**

**Negative / accepted trade-offs:**

* **Declared budget goes unspent.** A node holding credit when the window turns over loses it, so
  a fleet of n nodes with a block of k can leave up to n × k rows a window unused. Larger blocks
  are cheaper and waste more; the setting is the dial and both ends of it are conservative.
* **A node can burst up to a block ahead of the declared pace**, because credit is granted whole
  and spent at whatever rate the node writes. It cannot bank across windows — credit expires with
  the window it was drawn in — so the burst is bounded by the block and does not accumulate.
* **Not enforced at stage 1, unlike the other five.** §1.2 is why, and the cost is real: a tenant
  over its write budget has already been admitted, has taken a lease and has opened an instance
  row before it is paced. The mitigation is that a deployment bounding writes will normally also
  bound admissions, and the two compose.
* **A paced flow holds a lease and a bulkhead slot while it waits.** That is what makes it a
  completion rather than an abandonment, and it means a badly sized budget shows up as a tenant's
  flows taking longer and its concurrency filling, rather than as errors. The deadline is what
  bounds it.
* **The window is a token bucket's, so it refills continuously.** A tenant that spends a daily
  budget in an hour is paced to a trickle rather than stopped until tomorrow — the same limitation
  `TenantFairness.QuotaPerWindow` already records, for the same reason.

**Revisit when:** a deployment needs a write budget that is not per tenant — per flow, per
partition — at which point the key becomes a declaration rather than a constant; or the unspent
remainder is measured to matter, which would make the case for returning credit to the bucket at
the end of a window and is a second round trip to weigh against it; or `IRateLimiterStore` gains a
cost parameter for another reason, at which point the bucket can be denominated in rows and the
block becomes an implementation detail rather than a declared number.

---

**See also:** [ADR-0009](ADR-0009-plugin-contracts.md) ·
[ADR-0029](ADR-0029-a-refusal-is-a-result-failure.md) ·
[ADR-0040](ADR-0040-a-rate-limit-is-shared-or-it-is-not-a-rate-limit.md) ·
[ADR-0046](ADR-0046-a-tenant-is-resolved-at-admission.md) ·
[16 — Multi-Tenancy](../16-Multi-Tenant.md)
