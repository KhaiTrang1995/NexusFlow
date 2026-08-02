# ADR-0062: Transport portability is a property of the capability chain, not of a flow class

**Status:** Accepted
**Date:** 2026-08-02 (at the event-driven sample)
**Deciders:** Platform architecture, Runtime team

## Context

Quality goal **Q4** and vision criterion **V2** are stated as *"moving a flow from HTTP to Kafka
to cron changes zero lines of business logic"*, and every page that illustrates the claim
illustrates it the same way — one flow class, three trigger attributes stacked above it, the
`Define` body untouched. `samples/event-driven/README.md` printed exactly that as "commit 3 — all
three at once".

That illustration does not compile, and it has not compiled since the four trigger kinds became
registrations. A trigger that carries a body fixes what the body **is**: `[CronTrigger]` requires
`Flow<ScheduledFire, …>` (FLOWX1038), `[BusTrigger]`, `[KafkaTrigger]` and `[ChangeTrigger]`
require `Flow<BusMessage, …>` (FLOWX1039, FLOWX1041), and `[StreamTrigger]` requires
`Flow<StreamWindowBatch, …>` (FLOWX1042). A class has one base type, so two of those on one class
is unsatisfiable — and the four rules, each seeing only its own transport, alternate between two
messages that each tell the author to declare the contract the other refuses.

None of that is a defect in the trigger model. The input contract is fixed because a delivery, a
change and an occurrence each have exactly one thing to give, and because a flow may not go and
ask (FLOWX1007). It is the *statement of Q4* that is imprecise, and an imprecise quality goal is
the kind that is quietly abandoned rather than deliberately revised.

Three ways to make the claim true as written were considered:

- **A. Let the host default the input.** Start a scheduled flow declared as
  `Flow<IssueInvoice, …>` with `default(IssueInvoice)`. *Rejected:* it journals an instance whose
  recorded request is a value nobody sent, so a replay reconstructs a fiction. This is the
  argument `ScheduledFire`'s own remarks already make.
- **B. Give the flow an ambient accessor** — `ctx.Trigger.As<ScheduledFire>()` — so `TIn` can be
  anything. *Rejected:* a value taken ambiently is in none of the fields a replay reconstructs,
  which is the determinism boundary FLOWX1007 and FLOWX1011 exist to hold; and it puts the
  transport inside the flow body, which is what P3 forbids and what `FlowsAreTransportFree`
  checks from IL.
- **C. State the claim over the capability chain instead.** Chosen.

## Decision

**Transport portability is a property of a flow's capability chain, one adapter step in. It is
not a property of a flow class, and Q4 is to be read that way.**

Concretely:

1. **A transport costs exactly one step.** A flow declares the input contract its trigger fixes
   and spends its first step turning that into the chain's own input — `BusMessage` into a
   request, `ScheduledFire` into what is due. Everything after that step is identical across
   transports, capability for capability, compensation for compensation, event for event. HTTP
   costs zero steps, because its input contract *is* the request.

2. **The adapter is a capability, not flow-body code.** It is where a serialiser context is in
   scope and where a malformed body is a `Result` rather than an exception, and it keeps the
   builder free of the `When`/`Switch` on trigger kind that option B would have invited.

3. **The shared steps declare a stance no transport can fail.** A delivery and an occurrence
   carry no principal, so a capability composed into flows reached by both declares
   `Authorization.Internal` — the honest statement that at this step there is no external caller
   to refuse. `Authenticated` or `Permission` on a shared step is a chain that works over HTTP and
   refuses every message, which is portability lost at run time rather than at build time.

4. **`FLOWX1048` reports the class that tries to be two.** Raised *instead of* FLOWX1038,
   FLOWX1039, FLOWX1041 and FLOWX1042, because those four read as actionable and following any of
   them re-raises another. Kinds that agree on a contract are not in conflict: `Bus` and `Change`
   both take `BusMessage`, and a flow declaring both is one flow with two subscriptions.

5. **The claim is a test, not a sentence.** `tests/EventDriven.Tests` holds both halves and
   neither is sufficient alone: `TransportPortabilityTests` compares the four compiled
   `ExecutionPlan`s against the chain written out, and `TransportEquivalenceTests` runs one
   billing reference through a real HTTP server, a real Redis broker, a real outbox change feed
   and a real cron sweep, and requires one invoice computed by one sequence of journalled steps.

## Consequences

**Positive:** the claim becomes falsifiable, and it fails for the reasons that matter — a step
dropped from one transport's chain, a stance a delivery cannot satisfy, an adapter reading the
wrong field, a contract the journal cannot write. It is also the honest shape: a reader who copies
the sample gets four flows that compile, rather than an illustration that does not. And FLOWX1048
turns the platform's sharpest edge — two rules whose advice alternates — into one message naming
the real constraint.

**Negative / accepted trade-offs:** four files where the documents promised one, and the chain
below the adapter is therefore duplicated four times as source text. That duplication is real and
is not hidden: it is what `TransportPortabilityTests.EveryTransportCompilesToTheDeclaredChain`
exists to catch when one copy drifts. Extracting the chain into a sub-flow would remove the
duplication and add a hop, a second instance row per invocation and a second set of journal
writes — a runtime cost paid on every message to avoid a source-text repetition a test already
guards, which is the wrong trade at this size. The prose in `docs/01-Vision.md` and
`docs/09-Trigger-Model.md` that illustrates Q4 with stacked attributes is now imprecise and is
left as written; superseding is the only amendment this repository makes to an accepted record,
and those are not records.

**Revisit when:** a trigger kind ships whose input contract is the flow's own — an HTTP-shaped
transport, a second agent surface — and the adapter step becomes optional for more than one kind;
or `SubFlowMode.AwaitCompletion` and a cheap inline sub-flow make extracting the shared chain cost
one journal row rather than an instance, at which point the duplication argument above should be
re-run with the real number; or the number of transports over one chain passes five, where four
copies of a chain stop being a repetition a reviewer can hold in their head.
