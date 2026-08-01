# ADR-0022: A flow that suspends answers 202, and its signal endpoint is generated

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Repository owner · Platform architecture · Plugin team
**Amends:** [ADR-0004](ADR-0004-universal-trigger-model.md)) ·
[09 §6](../09-Trigger-Model.md#6-http-trigger) ·
[08 §3.5](../08-Flow-Definition.md#35-waiting)

> **[ADR-0004](ADR-0004-universal-trigger-model.md)) says a flow's transport is an attribute
> and nothing else. Since WP-63 that has been false for exactly one kind of flow: the one
> durable execution exists for.**
>
> `samples/workflow`'s `offer.accept` suspends, and it carries **no** `[HttpTrigger]`. Not
> because a route would be wrong, but because the generated endpoint answers `200` with
> `result.Value`, and `FlowExecutionResult<TOut>.Value` **throws** on a suspended flow —
> deliberately, since the flow's `.Return(...)` reads values the steps after the wait have not
> produced. So the sample maps two routes by hand with `RequestDelegate`, and its README
> records the gap. The one flow in this repository that demonstrates the platform's headline
> capability is also the one flow that cannot use its trigger model.
>
> That is the *"per-transport base classes"* failure mode ADR-0004 rejected, arriving from the
> other direction: not a flow shaped by its transport, but a flow **excluded** from the
> transport because its shape has no answer there.

---

## 1. Context

Three facts, each checked against a file rather than remembered.

**`FlowEndpointExtensions` has two outcomes and needs three.** `HandleAsync` runs the flow,
writes RFC 7807 when `result.IsFailure`, and otherwise writes `200` with `result.Value`. A
suspended result is neither: `IsFailure` is false — nothing failed — and `IsSuccess` is false
too, so the `200` branch is taken and `Value` throws `InvalidOperationException` with a message
explaining precisely why it must not be read. A suspending flow behind an `[HttpTrigger]`
today produces a `500` whose body is a stack trace's worth of correct reasoning.

**Delivering a signal has no shape.** `FlowHost.SignalAsync(instanceId, registration, signal)`
is the whole of the runtime contract and it is complete. What is missing is everything between
an HTTP request and that call: parsing the instance id, matching the identity, deserialising
the payload into the contract the flow declared, and turning the outcome into a status code.
`samples/workflow` writes about fifty lines of it. Every application that waits would write
the same fifty lines, differing only in three type names — which is
[ADR-0004](ADR-0004-universal-trigger-model.md))'s rejected option **B**, *"adapters written by
users … leaves the boilerplate we set out to delete"*.

**Constraint C2 is why the sample used `RequestDelegate`, and it still applies.**
`plugins/FlowX.Http` and everything it emits are `IsAotCompatible` with
`EnableAotAnalyzer`, guarded by `EveryShippedRuntimeProjectIsAotAnalyzed`. Minimal-API
delegate binding reflects over handler parameters; `FlowEndpointExtensions` says so in its own
remarks and was rewritten once because the trim analyzer rejected the obvious version. Nothing
here relaxes that: every endpoint below is a `RequestDelegate` plus a caller-supplied
`JsonTypeInfo<T>`, and the payload's type is a **generic parameter instantiated in the user's
assembly by generated code**, which is how the plugin can deserialise a contract it has never
heard of without a single reflective call.

---

## 2. Decision

**A flow that suspends is a first-class HTTP citizen.** `[HttpTrigger]` on a suspending flow
generates two endpoints instead of one, and the plugin gains the `202` path and the delivery
handler that make them work.

### 2.1 The run endpoint gains a third outcome

```
POST /api/v1/offers
  200  the flow completed          → its projected output, unchanged
  202  the flow suspended          → where to continue it
  4xx  RFC 7807                    → unchanged
```

The `202` body is the smallest thing a caller needs to act:

```jsonc
{
  "instanceId": "019fbd44-6b4c-7c1a-9a3e-2f0f8a0f0b21",
  "status": "suspended",
  "awaiting": [
    { "signal": "offer.countersigned",
      "deliverTo": "/api/v1/offers/019fbd44-6b4c-7c1a-9a3e-2f0f8a0f0b21/signals/offer.countersigned" }
  ]
}
```

* **`instanceId`** is what makes the instance reachable at all. It is minted inside `FlowHost`
  when the lease is taken, and `FlowExecutionResult.InstanceId` exists precisely because
  nothing gave it back before.
* **`awaiting`** is read off the **compiled plan**, not guessed: every `StepKind.AwaitSignal`
  node in `plan.Graph.Steps` carries a `SignalType`. It is therefore the same set of identities
  the manifest publishes ([ADR-0021](ADR-0021-manifest-publishes-the-wait.md))) and the same set
  the generator emitted routes for — one reading, three consumers.
* **`deliverTo`** is composed at run time from the request's own path and the instance id, so
  it is correct under a path base, a reverse proxy prefix and a parameterised route without the
  generator having to be told about any of them.
* **`Location`** is set to the single `deliverTo` when the flow declares exactly **one**
  suspension point, and omitted when it declares several. A header that can name only one of
  three addresses is a header that misleads two callers out of three;
  [RFC 9110 §10.2.2](https://www.rfc-editor.org/rfc/rfc9110#field.location) has no plural form,
  and the body does.

**The body is written with `Utf8JsonWriter`, by hand**, for the reason `ProblemDetailsJson` is:
it is a small closed shape, and hand-writing it means the plugin ships no serialiser context of
its own and adds nothing to a consumer's trim graph.

### 2.2 The signal endpoint is generated, one per (flow, signal)

```
POST /api/v1/offers/{instanceId:guid}/signals/offer.countersigned
```

The identity is **in the route as a literal**, not as a `{signalType}` parameter the handler
compares. Three things follow, and all three are why:

1. **An unknown identity is a routing miss, so it is a `404` before any code runs** — which is
   what [09 §6](../09-Trigger-Model.md#6-http-trigger)'s table always specified, produced by
   the router rather than by a hand-written string comparison in every application.
2. **Each endpoint has its own `TSignal`.** A flow that waits twice for two different contracts
   gets two endpoints, each closed over the right `JsonTypeInfo<T>`. One endpoint with a
   `{signalType}` parameter would need a dictionary of type infos keyed by string, which is a
   dispatch table that has to be kept in step with the plan.
3. **The route is derived from the run route** — `{route}/{instanceId:guid}/signals/{identity}`
   — so a caller who knows where to start a flow can construct where to continue it, and the
   `deliverTo` the `202` hands back matches the route the generator registered by
   construction rather than by agreement.

**The handler is `FlowHost.SignalAsync` and nothing else.** There is no second way to run a
flow: the lease is acquired, the fence is raised, the frontier is read, and the same
`FlowEngine.ExecuteAsync` a recovery scan enters is entered. The plugin's whole contribution
is translation, which is what `FlowEndpointExtensions`'s own remarks say an endpoint is for.

### 2.3 Signal delivery answers 202, never the flow's output

Delivering a signal succeeds with `202` and a body naming the instance and what it did:

```jsonc
{ "instanceId": "019fbd44-…", "status": "completed" }
{ "instanceId": "019fbd44-…", "status": "suspended",
  "awaiting": [ { "signal": "…", "deliverTo": "…" } ] }
```

**It does not return the flow's projected output, and that is a decision rather than a
limitation.** It would be buildable — the generator knows `TOut` and the flow's `Projection` —
and it is refused on two independent grounds:

* **The signaller is not the caller.** The person who countersigns an offer is not the person
  who requested it. Projecting `.Return(...)` onto the delivery response would hand the second
  party a document assembled from the first party's request, under whatever authorisation the
  signal endpoint carries rather than the one the run endpoint carried. That is a disclosure
  decision being made by a status-code convenience.
* **A delivery is an acknowledgement, not an invocation.** The instance may complete, suspend
  again at a second wait, fail and compensate, or step over a wait that was already satisfied —
  `FlowHost` documents the last as *inert, not an error*. `202 Accepted` is the honest
  description of all four; `200 OK` with a body would be honest for one of them.

`status` is therefore the outcome as the runtime reports it, and a caller who needs the flow's
result reads it from wherever the flow's own effects put it. Failures map through
`ProblemDetailsMapper` exactly as on the run path, so `lease.held` is a `503`, a journal
refusal keeps its category's status, and a malformed body is a `400` — no new mapping and no
new error catalogue.

### 2.4 What is not decided here

* **`GET /api/v1/flows/{instanceId}`** — the instance-status resource
  [09 §6](../09-Trigger-Model.md#6-http-trigger) lists — is not built and is not decided by
  this record. `Location` therefore points at the signal endpoint, which exists, rather than at
  an instance resource, which does not.
* **Authorisation on the signal endpoint.** It inherits whatever the application configures for
  the route, exactly as the run endpoint does. FlowX has no authorisation execution at all yet
  (a capability's stance reaches the manifest and nothing enforces it), so a decision here
  would be a decision about a subsystem that does not exist.
* **Idempotency.** The run endpoint's `Idempotency-Key` rule is not extended to the signal
  endpoint, because redelivery is already inert by construction: the second delivery re-enters
  an instance whose wait now has a committed row, steps over it and changes nothing. That is
  the frontier's idempotence, not a check written for signals, and adding a key requirement on
  top would be a second mechanism for a property that already holds.

---

## 3. Options rejected

- **A. Leave it. Applications that suspend map their own routes.** *Rejected:* it is the status
  quo, it is ADR-0004's rejected option B, and it costs every waiting application the same fifty
  lines. It also leaves the platform's flagship sample unable to use the platform's trigger
  model, which is the clearest possible statement that the model does not cover the case.
- **B. Answer `200` with a "pending" envelope instead of `202`.** *Rejected:* a caller
  distinguishing completion from suspension would have to parse the body, and every generic
  HTTP client, proxy and retry policy in front of it would treat an unfinished flow as a
  finished one. The status code is the one part of the answer every intermediary reads.
- **C. Hold the connection open until the signal arrives.** *Rejected:* it is the exact
  resource-holding this feature exists to eliminate — 06 §6's *"zero threads, zero memory held
  anywhere"* — and it converts a wait measured in days into a timeout measured in seconds.
- **D. One signal endpoint per flow, with `{signalType}` as a route parameter.** *Rejected:*
  §2.2. It needs a runtime map from identity to `JsonTypeInfo`, which is a second copy of what
  the plan already says, and it turns an unknown identity from a routing miss into an
  application-level comparison that every application writes slightly differently — the sample
  writes it as a `string.Equals` followed by a `404`.
- **E. Return the resumed flow's projected output from the signal endpoint.** *Rejected:*
  §2.3, on disclosure grounds and on modelling grounds.
- **F. Generate the signal endpoint from a new attribute (`[SignalTrigger]`).** *Rejected:* the
  flow already declares what it waits for, in the `Define` body, in the one place that cannot
  disagree with the plan. An attribute would be a second declaration of the same fact and could
  name an identity no `AwaitSignal` step carries — a route serving a signal nothing waits for.

---

## 4. Consequences

**Positive**
- **ADR-0004's claim becomes true of durable flows.** A suspending flow declares
  `[HttpTrigger]` like any other, and the two endpoints it needs are generated from the same
  reading of the same attribute that produced the manifest's `triggers` block.
- **`samples/workflow` loses its hand-mapped routes.** The two `app.Map(...)` blocks and the
  `RequestDelegate` reasoning in `Program.cs` are deleted, and `offer.accept` carries a real
  `[HttpTrigger]`. That deletion is the acceptance test for this record.
- **The 202 tells the caller what to do next, from the plan.** No application writes down the
  identity of the signal its own flow waits for; `samples/workflow` currently declares
  `Signals.OfferCountersigned` as a `const` beside a test whose whole job is to check that the
  constant still agrees with the plan.
- **Nothing about the runtime moved.** No new host method, no new engine path, no journal
  change. Every endpoint here calls a `FlowHost` method that shipped at WP-63.

**Negative / accepted trade-offs**
- **A flow's HTTP surface now depends on its body, not only on its attributes.** Adding an
  `.AwaitSignal<T>` adds routes. That is a real coupling between the `Define` method and the
  application's URL space, and it is accepted because the alternative (option F) is a second
  declaration that can disagree. It is also *visible*: `FLOWX-DIFF-022` reports the flow
  gaining a wait, and `FLOWX-DIFF-103` reports nothing, because the run route did not move —
  so a reviewer sees the cause and has to know that the routes follow it.
- **The signal endpoint's response is deliberately thin.** A caller who wants to know what the
  flow produced cannot learn it here (§2.3). For `offer.accept` that is right; for a flow whose
  signaller *is* its caller — a two-step wizard, say — it is an extra round trip that this
  design does not offer, and the escape hatch is the one 09 §12 already names: map a raw
  endpoint beside the flow.
- **`deliverTo` is a path, not an absolute URL.** It is built from `PathBase + Path`, so it is
  correct under a prefix and silent about scheme and host — which a service behind a load
  balancer cannot know reliably anyway. A caller resolves it against the URL it just posted to.
- **Two endpoints per waiting flow multiplies the route table.** A flow with three waits
  publishes four routes. That is proportionate to what the flow declares, and every one of them
  is derivable from the manifest, but it is more surface than the one-route-per-trigger model
  the endpoint generator was written for.

**Revisit when:** any one of —
- **an instance-status resource is built** (`GET /flows/{instanceId}`), at which point
  `Location` should point at it rather than at the signal endpoint, and §2.4's first bullet is
  the thing to reopen;
- **a second transport gains a signal path** — a Kafka topic that delivers signals, say. The
  `202` and the `Location` are HTTP's shape, but `awaiting` and the identity are not, and a
  second implementation is what would show whether the shape belongs in the plugin or above it;
- **authorisation execution lands.** The signal endpoint's stance is currently the
  application's to configure, and once a capability's declared stance is actually enforced, a
  delivery that resumes a flow into a step the *signaller* may not invoke becomes a question
  this record deferred;
- **a timer arms the declared wait**, which gives an expired instance a fourth outcome the
  `202` body would have to describe.

---

**Back to:** [ADR index](README.md) · [ADR-0004](ADR-0004-universal-trigger-model.md)) ·
[ADR-0021](ADR-0021-manifest-publishes-the-wait.md)) ·
[09 — Trigger Model](../09-Trigger-Model.md) · [08 §3.5](../08-Flow-Definition.md#35-waiting) ·
[06 §6](../06-Execution-Engine.md#6-suspension-waiting-without-holding-resources)
