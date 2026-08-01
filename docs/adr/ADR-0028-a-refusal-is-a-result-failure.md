# ADR-0028: A refusal is a `Result` failure carrying `ErrorCategory.Forbidden`, and the model's 401 collapses into it

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Repository owner · Platform architecture
**Amends:** [ADR-0007](ADR-0007-result-over-exceptions.md) ·
[15-Security §4](../15-Security.md#4-authorisation-model)

> **The third of P4's four authorisation decisions.** The first half of it is
> [ADR-0007](ADR-0007-result-over-exceptions.md) applied without argument. The second half is
> a published model losing a distinction it drew, and this record exists mostly for that.

---

## 1. Context

[ADR-0026](ADR-0026-authorisation-runs-in-the-step-loop.md) decides where the check runs.
Something has to come back from it.

### 1.1 The easy half

[ADR-0007](ADR-0007-result-over-exceptions.md) settles this: expected failures are values.
"You are not allowed to do that" is the answer to the question the request asked, not a fault
in the platform. Thrown, it would have to be caught by every trigger's consumer loop, and a bus
consumer would dead-letter a message that was merely not permitted. The engine's one general
`catch` exists to convert a *capability* throwing into `capability.unhandled`
(`ErrorCategory.Internal`) — routing a refusal through it would report a denied caller as a
platform defect and a `500`.

### 1.2 The hard half

[15 §4](../15-Security.md#4-authorisation-model)'s flowchart draws two different answers:

```
D -- no --> X["401"]           ← Authenticated, no valid principal
E -- no --> Y["403 + audit event"]   ← Permission / Policy / Internal
```

`ErrorCategory` has no member for the first. It carries `Validation`, `NotFound`, `Conflict`,
`Forbidden`, `Unavailable` and `Internal`, and its own remarks close the set:

> The set is closed on purpose (ADR-0009 §"not extensible"). Add error **codes**, never
> categories — a new category would silently change the transport mapping table for every
> existing consumer.

### 1.3 Rejected options

* **Add `ErrorCategory.Unauthenticated`.** Rejected on the type's own stated rule. Every
  consumer's `switch` over the category — `ToHttpStatusCode`, `IsTerminal`, a gRPC mapping, a
  dead-letter rule — would silently acquire an unhandled member, and `IsTerminal`'s default arm
  returns `false`, so the new member would be **retryable** by omission. A closed set that is
  opened once is not closed.
* **Report it as `Validation` (400).** Rejected: it is not a malformed request, and a `400`
  tells a caller to fix the payload when the remedy is to obtain a credential.
* **Throw for the unauthenticated case only.** Rejected: two mechanisms for one control, and
  the more surprising one for the more common case.
* **Carry the status on the `Error`'s metadata and have the HTTP mapper prefer it.** Rejected:
  it puts a transport concept on an error the engine produces, which is what
  [ADR-0004](ADR-0004-universal-trigger-model.md) exists to prevent, and it would be read by
  one transport and ignored by the rest.

---

## 2. Decision

**A refused step is a `Result` failure carrying `ErrorCategory.Forbidden`, never an exception —
and an anonymous caller refused by `Authenticated` carries the same category, so
[15 §4](../15-Security.md#4-authorisation-model)'s `401` becomes a `403` at the HTTP boundary.**

### 2.1 The codes stay distinct even though the category does not

| Code | Raised when |
|---|---|
| `authorization.not_authenticated` | the stance needs a principal and the invocation carried none, or one no scheme authenticated |
| `authorization.permission_denied` | the caller is authenticated and does not hold the named grant |
| `authorization.stance_not_enforceable` | a stance reached the engine that it cannot decide |

The category drives the transport mapping; the code is what an operator reads. "Sign in" and
"ask for a grant" lead to different repairs, and collapsing *those* would be the real loss.
`ErrorCategory` was always the coarse axis — that is what makes it mappable — and `Error.Code`
was always the fine one.

### 2.2 What the refusal says, and what it does not

The refusal names the **grant** and never the **caller**. The grant is already public: it is in
`flowx.manifest.json`, which is the whole point of attaching the stance to the capability, so
naming it tells the caller what to ask for. The caller's claims in an RFC 7807 body would be
the information disclosure [15 §3](../15-Security.md#3-stride-per-boundary)'s Boundary 1 row
refuses. `ARefusalNamesTheGrantAndNotTheCaller` asserts both halves against the real endpoint.

### 2.3 `Forbidden` is terminal, and that is load-bearing

`ErrorCategoryExtensions.IsTerminal` already returns `true` for `Forbidden`, so no `Retry`
policy acts on a refusal and no bus consumer treats it as transient. Asking the same question
of the same principal three times gets the same answer three times.
[ADR-0026 §2.3](ADR-0026-authorisation-runs-in-the-step-loop.md) places the check outside the
retry loop anyway, so the flow does not depend on this remaining true — but it is true, and
the two agree.

---

## 3. Consequences

**Positive:**

* **A refusal composes with everything the engine already does with a failure.** The saga
  unwinds behind it: `AnAuthenticatedCallerWithoutThePermissionIsRefusedAndTheHoldIsReleased`
  observes a caller refused at step 2 and the step-1 inventory hold released, over the real
  endpoint. An exception would have skipped the unwind, and a refusal that leaked inventory
  would be the one failure mode that does.
* **`ErrorCategory` stays closed**, so no consumer's mapping table silently acquires a member,
  and nothing becomes retryable by omission.
* **The mapping was already written.** `Forbidden` → `403`, terminal, never retried, and
  `ProblemDetailsMapper` needed no change: the body is built from `Error.Code`, `Category` and
  redacted detail, which is why `ErrorsDoNotLeakInternals` still holds by construction.
* **A caller can still tell the two apart**, by code, which is the axis intended for it.

**Negative / accepted trade-offs:**

* **A published model lost a distinction, and this is the cost.**
  [15 §4](../15-Security.md#4-authorisation-model)'s flowchart says `401` and the platform
  answers `403`. A caller cannot distinguish "you did not authenticate" from "you did and it
  was not enough" **by status code** — which is what an HTTP client library's retry-with-token
  logic keys on. They have to read `code` in the body. That is a real ergonomic loss for a
  documented behaviour, and §15's diagram is now amended rather than implemented.
* **No `WWW-Authenticate` header, and that is why a `401` would have been wrong anyway.**
  RFC 9110 obliges a `401` to carry a challenge naming a scheme. The engine is
  transport-agnostic by construction, so it has no scheme to name and no header to put it in —
  a `401` with no challenge is a malformed response, and emitting one would trade a coarse
  answer for an invalid one. This makes the collapse defensible rather than merely forced, but
  it does not make it free.
* **`stance_not_enforceable` is unreachable and still costs a code.** `FLOWX1037` makes it so,
  and it exists for the plan assembled by hand and the enum member a later release forgets.
  A code nothing raises is a code in a catalogue that has to be explained — accepted, because
  a `switch` whose default arm permits is the defect this whole phase existed to end.
* **Nothing is audited.** [15 §4](../15-Security.md#4-authorisation-model) says "403 + audit
  event" and there is no audit event: `Audit` is a stage-7 policy that
  [ADR-0025](ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md) leaves unexecuted,
  and no store persists one. The refusal is a returned `Error` and appears in whatever the host
  logs. Half of that row of the model is unmet, and this record does not close it.

**Revisit when:** the closed `ErrorCategory` set gains an authentication member for an
unrelated reason, at which point §1.3's argument has already been overruled and this should
follow rather than persist; or a caller demonstrates that it must distinguish the two by status
code to act correctly — a token-refreshing client is the obvious candidate, and a real one
would settle it; or stage 7's `Audit` executes, at which point the second half of
[15 §4](../15-Security.md#4-authorisation-model)'s `403 + audit event` becomes implementable
and this record's last negative can be closed.

---

**See also:** [ADR-0007](ADR-0007-result-over-exceptions.md) ·
[ADR-0026](ADR-0026-authorisation-runs-in-the-step-loop.md) ·
[ADR-0029](ADR-0029-policy-stance-is-refused-at-build-time.md) ·
[15 — Security](../15-Security.md)
