# ADR-0058: A poll is one wait re-entered, not a race between two

**Status:** Accepted
**Date:** 2026-08-02
**Deciders:** Runtime team, Platform architecture

## Context

`samples/polling/README.md` specified a construct the platform did not have — `PollUntil`,
`RaceUntil` and `Backoff.Exponential(from:, to:)` appeared nowhere else in the repository — and
the specification is right about the problem and wrong about one of the two shapes it proposes.

The problem is real and the page states it exactly: a third-party OCR service returns a job id
and takes between thirty seconds and four hours, and every ordinary answer costs more than the
waiting is worth. `while (!done) await Task.Delay(5s)` is a thread and a container per document,
and loses all of them on the next deployment. A recurring job scanning a table polls everything
on one schedule. A webhook alone has no fallback for the day the provider's webhook fails.

Durable suspension already answers the first two.
[ADR-0015](ADR-0015-journal-schema-and-durable-execution.md) and WP-63's timer half made a
parked instance one `Suspended` row carrying `wake_at`, `wake_step_id` and `wake_scope`, holding
no thread, no pooled context and no lease. What was missing was a construct that parks
*repeatedly*, and a decision about whether the webhook fast path belongs beside it.

Two questions had to be answered, and the second is the contested one.

### 1. What shape is the loop?

Every target in a compiled plan points strictly forward. That is not tidiness — it is the whole
of the termination argument for `FlowEngine`'s step loop, which has no iteration cap by design,
and `StepNode.RequireForwardTarget` makes a backward target unrepresentable rather than merely
unlikely.

- **A. A backward `Jump`.** *Rejected:* it deletes the termination proof for every flow in
  order to give one construct a loop, and the engine's loop would then need a cap that no other
  kind needs.
- **B. Unroll the attempts at compile time.** *Rejected:* the attempt count is a function of a
  duration and a jittered schedule, so there is no number to unroll to — and a plan whose size
  depended on a timeout would make the manifest a function of a tuning value.
- **C. Re-enter a contiguous span, as `ForEach` already does.** Chosen. Nothing in the array is
  duplicated, no target points backwards, and the termination argument moves from "a step runs
  at most once" to "the number of passes is bounded by a value fixed before the first one" —
  which is the argument `StepKind.ForEach` already makes, with a duration in place of an element
  count.

### 2. Does the webhook fast path belong in the same construct?

The README proposes it as a fork:

```csharp
.RaceUntil(
    signal: r => r.AwaitSignal<OcrCompleted>(),
    poll:   p => p.PollUntil<CheckOcrStatus>(interval: Backoff.Exponential("PT30S","PT5M")),
    timeout: TimeSpan.FromHours(4))
```

*"Whichever arrives first wins; the loser is cancelled."*

- **A. Build it as written — a `Parallel` over two suspending branches.** *Rejected*, and the
  reasons are all pre-existing commitments rather than implementation difficulty.

  A fork's branches share one pooled context, so
  [ADR-0015](ADR-0015-journal-schema-and-durable-execution.md)'s limit §6.1 — an overlapping fork
  does not replay, because a `NondeterminismCapture` taken at a branch's commit takes everything
  minted since the last commit *including a sibling's* — would apply to every instance of every
  flow that used it. A construct whose documented purpose is reliability cannot be the one that
  makes an instance unreplayable.

  A parked branch has nothing to cancel. "The loser is cancelled" describes threads; here both
  branches are the same one row, and the row carries one `wake_at`. Two branches with two
  wake instants cannot both be recorded, and the engine already resolves that by taking the
  earlier — so the poll's schedule would silently become the instance's schedule and the signal
  branch would be re-entered on it.

  And it is expressible without a fork, which makes the fork's costs unnecessary rather than
  merely large.

- **B. One suspension point with two ways to end it.** *Deferred, not refused.* A poll's park
  and a signal's park are the same row; ending the wait early on a delivered signal is
  `DurableExecution.TakeSignal` called from the poll's arrival path, and the honest spelling is
  a modifier on the poll rather than a second construct — `.PollUntil<T>(…).OrSignal<TSignal>()`.
  It needs a signal contract in the state bag, a route from `EndpointEmitter`, a `signal` field
  on a `Poll` step in the manifest, and a `flowx diff` rule for it. None of that is contested;
  all of it is a work package.

- **C. Ship the poll alone and say so.** Chosen for this package.

## Decision

**`PollUntil` is one suspension point whose body is re-entered once per attempt, bounded by a
duration read back from the journal. `RaceUntil` is not built, and the fork it describes is
refused rather than deferred.**

Concretely:

1. **`StepKind.Poll` carries no body target.** The attempt is the single capability step at
   `Index + 1`, implied by the layout exactly as a `ForEach`'s body start is. `StepNode.Target`
   is the *satisfied* path, one past the `.OnTimeout` block — which is
   `StepNode.ForAwaitSignal`'s convention read from the other side, and for its reason: the
   escalation is the block that can be contiguous, because the other path out of a wait is the
   rest of the flow.

2. **The body is one capability, not a block.** A multi-step body would need each attempt
   journaled as a range, and would let an author put a compensable step inside a loop that runs
   an unbounded number of times — a compensation stack of unbounded depth for one declaration.
   The DSL enforces it by returning `IAwaitBuilder`, which has no `CompensateWith`.

3. **The attempt number is the journal scope.** `flow_step` rows commit under `0`, `1`, `2`… —
   `StepScope`, unchanged, doing the job it was added for. So a resumed poll knows which attempt
   it is on by scanning committed rows, and an operator can read how many times a provider was
   asked without joining anything.

4. **The budget is measured from the instant the first attempt read, not from the row's
   timestamp.** `NondeterminismCapture.UtcNow` on attempt zero's row is the engine's own
   `IClock`; `JournalStep.CommittedAt` is the store's. Bounding a loop by the difference between
   two clocks is `FLOWX1007`'s ambient read performed by the engine instead of by a capability,
   and under a test clock the difference is four hours rather than four milliseconds. The
   store's stamp is a fallback and nothing more.

5. **The predicate is asked before an attempt as well as after one.** A poll node commits no row
   of its own, so nothing in the journal says "this loop is finished"; an instance resumed past a
   completed poll would otherwise make one more call every time anything touched it. What stops
   it is the same question that ended the poll — the last attempt's answer is in the restored
   state bag and `until` still holds.

6. **`Backoff` is the one this platform already had.** A `Retry`'s and a poll's schedules are the
   same three numbers and the same formula; `CompensationPolicy.DelayBefore` now delegates to
   `Backoff.After`, so there is one implementation rather than two to keep in agreement. Two ISO
   8601 overloads were added because a poll's schedule is part of a flow's own declaration,
   beside `[FlowDeadline("PT6H")]`, rather than configuration beside an attempt count.

7. **The manifest publishes `"kind": "Poll"`, the two blocks, and the folded `timeout`. It does
   not publish the interval.** How long a flow keeps trying before it escalates is structure a
   consumer compares between versions; how often it asks in the meantime is a tuning number in
   exactly the sense `ForEachOptions.MaxDegreeOfParallelism` is one, and the schema has a field
   for neither. This is [ADR-0021](ADR-0021-manifest-publishes-the-wait.md)'s split applied to a
   second construct, and the `timeout` field is that record's, widened rather than duplicated.

## Consequences

### Positive

- **A hundred thousand documents in flight cost a hundred thousand rows and no compute**, which
  is the claim `samples/polling` exists to make and `PollingTests.WaitingCostsOneRowAndHoldsNoLease`
  is the measurement of.
- **The termination argument is unchanged.** No plan gained a backward target, and the engine's
  step loop still has no iteration cap.
- **Nothing in a polled capability knows it is being polled.** `ocr.status` is a function of a
  job id — no loop, no delay, no attempt counter — so it is testable by construction and
  replaceable by a webhook receiver without the flow changing shape.
- **Two build-time rules that were latent became statable.** `FLOWX1044` refuses polling a
  capability that has not declared repetition safe, which is `FLOWX1014`'s argument reached by a
  different door and the stronger case of the two: a retry repeats after a failure, a poll
  repeats after every success. `FLOWX1043` reports a first gap longer than the budget, which
  compiles to one attempt and an escalation and reads in a journal exactly like a dependency
  that never answered.

### Negative

- **The webhook fast path does not exist**, so a provider that publishes completions is polled
  anyway. A deployment that wants both today has to run two flows and correlate them, which is
  the shape this construct exists to replace. Decision 2B is the intended repair and is a work
  package rather than a design question.
- **The gap is a lower bound and never an upper one**, because `FlowTimerScan` is a sweep on an
  interval. A five-second interval under a ten-second sweep is between five and fifteen seconds.
  That is the promise a scheduled trigger already makes and the only one a sweep can keep.
- **A poll body of one capability is a real restriction.** "Check the status, and if it changed,
  record it" is two steps and cannot be written inside the loop; it goes after it.
- **A poll inside a `Parallel` inherits §6.1.** Nothing new — but the construct makes it easier
  to reach, because polling is a thing an author wants to do concurrently.

## Revisit when

- A deployment measures the poll traffic a webhook would have removed, at which point decision 2B
  is worth its work package — or when a provider this repository's samples name publishes
  completions and the fast path stops being hypothetical.
- A poll body genuinely needs two steps, and the compensation-depth argument in decision 2 can be
  answered by something other than "one step" — a body whose steps are refused a
  `CompensateWith` at build time would be the shape to argue for.
- `FlowTimerScan` gains a per-instance timer rather than a sweep, at which point the lower-bound
  caveat above stops being the only promise available and the interval becomes worth publishing.
