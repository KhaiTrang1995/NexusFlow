# ADR-0066: A poll's second ending is a row

**Status:** Accepted
**Date:** 2026-08-02
**Deciders:** Runtime team, Platform architecture

## Context

[ADR-0058](ADR-0058-a-poll-is-one-wait-not-a-race-between-two.md) refused `RaceUntil` as a fork
over two suspending branches and named the honest shape in its place:

> `.PollUntil<T>(…).OrSignal<TSignal>()` — one wait with two ways to end it, one `TakeSignal`
> call.

It called that a work package rather than a design question, and most of it is. A signal
identity on the `Poll` node, a `TakeSignal` call on the arrival path, the signal's contract in
the state bag, a route from `EndpointEmitter`, a `signal` field on a `Poll` step in the manifest
— none of those is contested, and each falls out of a construct that already exists beside it.

One thing does not, and it contradicts ADR-0058's own decision 5.

That decision says a poll node commits no row, and that what stops a resumed instance polling
one more time is the predicate: *"the last attempt's answer is in the restored state bag and
`until` still holds"*. That argument is exactly right for a poll with one ending, and it fails
for a poll with two. When a delivery is what ended the wait, `until` says **not yet** — that is
why the delivery mattered — and the signal has been consumed by the invocation that took it,
because `DurableExecution.PendingSignal` is one slot per invocation. So an instance resumed past
a signal-ended poll finds a poll that is not finished by any test it can perform, and goes back
to calling the provider. For ever, once per touch.

## Decision

**A poll ended by a delivered signal commits one row, on the poll's own node, under the poll's
own scope. That row is the whole of the difference between a poll with one ending and a poll
with two; every other part of `.OrSignal<TSignal>()` is the existing wait machinery reused.**

Concretely:

1. **The row is the witness the predicate cannot be.** It is committed by
   `FlowEngine.CommitStepAsync` — the same call every other step boundary uses — under
   `(instance, poll scope, poll index, attempt 1)`, carrying the state-bag snapshot that holds
   the delivered payload. On a later arrival the step loop's ordinary frontier scan finds it and
   skips the node, exactly as it skips a satisfied `AwaitSignal`. Nothing new reads it and
   nothing new writes it. ADR-0058's decision 5 is unchanged for a poll that declares no signal:
   that poll still commits nothing, and the predicate is still what says the loop is over.

2. **`StepNode.Identity` names the signal, so the row can be read.** A poll with one ending has
   an empty identity and never commits, so the two facts arrive together: the only
   `capability_id` a poll node can ever put in an instance's history is the identity of the
   delivery that ended it. An operator reading `flow_step` sees `ocr.completed` at the poll's
   index and knows which of the two endings happened, without joining anything.

3. **The three questions are asked in a fixed order: predicate, signal, schedule.** Each
   position is load-bearing.

   *The predicate first*, because a finished poll is re-walked on the way to whatever the
   instance is actually parked at. A poll that took a delivery there would consume the signal a
   later wait in the same flow is open for — one slot, one invocation — and leave that wait
   parked until its timeout on a delivery that was made.

   *The signal before the schedule*, because that is the sentence the construct exists to make
   true. **A signal arriving between two attempts ends the wait**: no attempt is made, the gap
   the instance was parked on is not waited out, the escalation is not entered, and control
   continues where a satisfied predicate would have taken it — one past the escalation block, or
   past the one-step attempt when there is none.

   *Both after the flow's deadline check*, which is where it already was: an instance whose
   whole budget has gone fails rather than committing another row.

4. **One row, one `wake_at`, one wake path — unchanged.** The instance parks on the same
   `flow_instance` row carrying the same `wake_at`/`wake_step_id`/`wake_scope` triple it always
   did. A delivery arrives at that row; the commit that records the ending clears the wake with
   the state, because the wait ended once. Nothing here needs a second instant, and if it had,
   this would have been the fork ADR-0058 refused wearing a different name.

5. **`FLOWX1050` reports the one thing two endings make possible.** Both endings continue at the
   same index, so every step after the poll runs on either — and only the delivery ending leaves
   `TSignal` in the bag. A step binding it works when the webhook fires and throws when the
   polling does its job, which is the ordinary path. It is `FLOWX1020`'s argument narrowed to the
   one construct that produces conditionally, and is reported *instead of* `FLOWX1020` on that
   line: the type genuinely is in the bag on one of the two paths, so the older rule's advice
   would be wrong.

6. **The manifest publishes the signal in the field a wait already uses, and `flowx diff` gains
   no rule.** A poll's second ending is an inbound address in exactly the sense a suspension
   point's is — the identity a transport addresses a delivery to — so it is the same `signal`
   field, indexed by the same `Awaited` walk, classified by the same `FLOWX-DIFF-021`, `022` and
   `206`. A poll that starts accepting a webhook has a new inbound address; a poll that stops
   breaks every sender in silence. Both are what those codes already say.

## Consequences

### Positive

- **The webhook fast path exists**, which was ADR-0058's one named negative consequence. A
  provider that publishes completions is no longer polled anyway, and a deployment that wants
  both no longer runs two flows and correlates them.
- **A signal between two attempts costs one row and no extra call.**
  `PollSignalHostTests.ASignalBetweenTwoAttemptsEndsTheSameWaitOnTheSameRow` measures it against
  PostgreSQL: one attempt row before, one attempt row after, one poll-node row naming the signal,
  and the wake gone.
- **Nothing about the layout moved.** `.OrSignal<T>()` spends no index: the attempt is still
  `Index + 1`, the escalation still follows it, and the satisfied path is still where it was. A
  poll that declares no signal emits the identical node it emitted before.
- **`FLOWX1017` and the `Durable`-only requirement are untouched**, and could not be otherwise:
  the second ending is a journal row and a `TakeSignal` call, both of which need the journal the
  first ending already needed.

### Negative

- **ADR-0058 decision 5's argument no longer covers every poll**, which is why this record
  exists rather than a comment. The rule is now: a poll that ends on its predicate commits
  nothing and is stopped by the predicate; a poll that ends on a delivery commits a row and is
  stopped by the row. Two rules where there was one, and a reader of decision 5 alone would draw
  the wrong conclusion about a poll with a signal.
- **A poll node can now appear in `flow_step`**, so a consumer that assumed the poll's index
  never held a row is wrong. Nothing in this repository assumed it; the assumption was available
  to be made.
- **The stale-attempt reading is not checked and cannot be.** On the delivery ending the last
  attempt's output is by definition the one the predicate said was *not* terminal, and a step
  after the poll binding it will read that. `FLOWX1050` reports the mirror image — a step binding
  the signal — because that one throws; this one merely means something the compiler cannot
  weigh. It is stated on the diagnostic's page rather than left for a reader to discover.
- **Two waits on one identity in one flow are still ordered only by the journal.** A poll's
  second ending and a later `AwaitSignal` on the same contract are resolved by "the first with no
  committed row", which is the rule `AwaitSignal` already documented. Decision 3 is what keeps it
  true; nothing refuses the declaration.

## Revisit when

- A delivery has to reach a poll that has **not yet parked** — today a signal arriving at an
  invocation that walks into a fresh poll ends the wait before its first attempt, which is
  defensible and untested against a real deployment. A deployment that observes it and wants the
  first attempt made anyway would be arguing for the signal to be asked after the attempt.
- A poll needs **more than one** second ending. `IPollBuilder.OrSignal` returns
  `IAwaitBuilder`, so a second call does not compile — deliberately, because one row has one
  delivery slot. Two would need the invocation to carry a set rather than a signal, which is
  `DurableExecution.PendingSignal`'s own revisit condition and not this one's.
- `flow_step` gains a column that says *why* a step committed, at which point decision 2's use of
  `capability_id` to distinguish a poll's two endings becomes a workaround rather than the only
  place to put it.
