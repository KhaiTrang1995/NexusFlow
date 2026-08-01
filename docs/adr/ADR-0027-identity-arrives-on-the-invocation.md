# ADR-0027: Identity reaches the engine on the invocation, as the `ClaimsPrincipal` the trigger already resolved

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Repository owner · Platform architecture
**Amends:** [ADR-0004](ADR-0004-universal-trigger-model.md) ·
[15-Security §2](../15-Security.md#2-trust-boundaries)

> **The second of P4's four authorisation decisions**, and the one that turned out to be
> mostly already made. What it needed was connecting.

---

## 1. Context

[ADR-0026](ADR-0026-authorisation-runs-in-the-step-loop.md) puts the check in the step loop.
The step loop needs a caller. There was none.

### 1.1 What already existed, which is nearly all of it

* `TriggerHeaders` — in `FlowX.Abstractions` — has carried
  `ClaimsPrincipal? Principal` since the trigger model was written.
* `FlowContext.Principal` has been declared as an abstract property since the first commit.
* `FlowX.Testing`'s `TestFlowContext` implements it.
* `HttpTriggerReader` reads `HttpContext.User` to resolve the tenant.

And `FlowExecutionContext.Principal` — the implementation the engine actually runs against —
was:

```csharp
public override ClaimsPrincipal? Principal => null;
```

Unconditionally. A capability asking who the caller was got "nobody", every time, with nothing
saying so. `FlowInvocation` carried `TenantId` and not the principal it was derived from, so
the reader resolved a principal, took one string off it, and dropped it.

### 1.2 A premise worth correcting

It is natural to assume `ClaimsPrincipal` is unavailable in `FlowX.Abstractions`, because
`AbstractionsHasNoDependencies` forbids that project any dependency. It does not follow, and
the gate itself says why:

```csharp
RepositoryLayout.PackageReferences(abstractions!).ShouldBeEmpty(...);
RepositoryLayout.ProjectReferences(abstractions!).ShouldBeEmpty(...);
```

It forbids **package** and **project** references. `System.Security.Claims` is in the shared
framework, so it costs neither — which is why `TriggerEnvelope.cs` has opened with
`using System.Security.Claims;` all along. There was never an obstacle here; there was an
unconnected wire.

### 1.3 Rejected options

* **An ambient principal — `AsyncLocal<ClaimsPrincipal>` or `IHttpContextAccessor`.**
  Rejected. An authorisation decision that depended on where a continuation happened to be
  running would be a different decision after a thread-pool hop, and `FLOWX1008` already
  refuses ambient identity in capabilities on the same grounds. It would also make the engine
  untestable without a synchronisation context and would tie authorisation to ASP.NET Core,
  which [ADR-0004](ADR-0004-universal-trigger-model.md) forbids.
* **A FlowX-owned `Principal` type.** Rejected: a second identity model to keep in step with
  the one every host already has, and every plugin would need a mapping. `docs/15 §7`'s
  load-bearing sentence is that no parallel permission system exists to get out of sync; a
  parallel *identity* system is the same mistake one level down.
* **Passing the principal to `IStepDispatcher.ExecuteAsync`.** Rejected: it widens a generated
  interface for a value the context already carries, and a capability reading identity should
  read it where it reads the tenant.
* **Persisting the principal on the journal instance row**, so a resumed flow has one.
  Rejected, and the argument is §2.2's.

---

## 2. Decision

**Identity reaches the engine on `FlowInvocation` as a `ClaimsPrincipal`, resolved once by the
transport plugin from validated claims and from nothing else, and surfaced on
`FlowContext.Principal`.**

It travels the path `TenantId` already travelled: `TriggerHeaders` → `FlowInvocation` →
`FlowExecutionContext` → `FlowContext`. `HttpTriggerReader` now passes `context.User`
alongside the tenant it was already deriving from it.

### 2.1 Validated claims, and nothing else

`docs/15 §3`'s Boundary 1 elevation-of-privilege row: headers are never trusted for identity.
`HttpTriggerReader` passes `HttpContext.User`, which is what an authentication scheme
populated; `StepAuthorization` reads claims. Neither reads a header, and there is deliberately
no configuration hook to add one — the same stance `TenantClaimTypes` takes.

`HttpContext.User` is never `null` in ASP.NET Core: an unauthenticated request carries a
`ClaimsPrincipal` whose identity is not authenticated. So the question asked is
`principal?.Identity?.IsAuthenticated == true` and not `principal is not null`. A null check
alone would admit every anonymous caller, and it is the single most likely way for this control
to be written and still fail open —
`AnAuthenticatedStanceRefusesANonNullButUnauthenticatedPrincipal` is the test that holds it.

### 2.2 A resumed instance is authorised by whoever resumes it, and the row keeps no claims

A resumed instance's invocation is rebuilt from its journal row, which carries a correlation
id, a tenant and a deadline. **No claims, deliberately.** Persisting them would put credentials
at rest for the life of the instance — a `Durable` flow may wait a week — and would authorise a
payment on Friday with a grant proved on Monday, which the issuer has had four days to revoke.
It would also make the journal a target worth attacking for something other than business data,
and `docs/15 §3`'s Boundary 3 already lists the journal's contents as a known limitation.

So `FlowHost.SignalAsync` takes the deliverer's principal, and the steps after the wait are
decided against whoever is delivering *now*. The generated signal endpoint passes
`context.User`, which is the only principal that request has validated.

### 2.3 `IsContinuation`, for the resumes nobody is making

A timer sweep and a recovery scan have no caller and never will. Deciding a stance against that
absence would refuse every step after a wait — making `.Delay(TimeSpan.FromHours(1))` a
construct no author could place before an authenticated step, and turning a node restart into a
refusal.

`FlowInvocation.IsContinuation` says so explicitly. It is set in exactly one place,
`FlowHost.ResumeAsync`, and only where there is neither a signal nor a principal.

**It is not a bypass, and the distinction is that nothing reachable by asking can set it.** A
signal is somebody delivering something now and leaves it false. Three tests hold the line: the
same invocation without the flag is refused, and a principal lacking the grant is refused
whether or not the flag is set.

---

## 3. Consequences

**Positive:**

* **No new concept and no new dependency.** The type was already in `FlowX.Abstractions`, the
  property was already on `FlowContext`, and the path was already carrying the tenant. What
  landed is a field and four assignments.
* **A stance means the same thing whatever transport invoked the flow**, which is
  [ADR-0004](ADR-0004-universal-trigger-model.md)'s requirement: the engine sees a
  `ClaimsPrincipal` and cannot tell whether a request, a broker record or a cron tick produced
  it.
* **`FlowContext.Principal` stops lying.** It was declared, documented and always `null`, which
  is worse than absent — a reader saw identity available and concluded it was plumbed.
* **The principal is reset with the rest of the pooled context**, so it cannot outlive the
  invocation that supplied it and be read by the next flow to rent it — a cross-request
  identity leak of exactly the shape `docs/15 §3`'s Boundary 2 spoofing row describes.
* **Credentials are never at rest.** §2.2 is a security property, not only a limitation.

**Negative / accepted trade-offs:**

* **A durable flow's authorisation is discontinuous across a wait.** The steps before are
  decided against the starter, the steps after against the deliverer, and these may be
  different people with different grants. That is defensible — each is authorised for what they
  actually caused — but it means a flow's authorisation cannot be read off its start, and an
  auditor reconstructing "who authorised this transfer" must read two events. Nothing yet
  writes those events; `docs/15 §10`'s `CrossTenantAccessIsDenied` remains blocked.
* **A signal delivered with no principal resumes anonymously**, so a stanced step after the
  wait is refused. Correct, and a trap for a host that forgets to pass one: the failure is a
  `403` on a flow that worked before it suspended. The parameter is optional for source
  compatibility, which is exactly what makes it forgettable.
* **`IsContinuation` is a second thing to keep honest.** It is one property, set in one place,
  under a condition spelled out in the call — the smallest surface this could have — but a
  future caller that sets it for a third reason would widen a permit without widening a test.
* **`ClaimsPrincipal` is a large type to put on a struct field.** It is a reference, so the
  copy is a word; but `FlowInvocation` is now a struct whose equality walks a
  `ClaimsPrincipal` reference, and two invocations naming the same caller by different
  instances compare unequal. Nothing compares invocations today.
* **The tenant is now resolved in `samples/ecommerce` where it was not before**, because the
  sample now authenticates. `TheSampleResolvesNoTenantSoItsMetricsAreBucketed` asserted the
  absence and was inverted. A gain, but it is a behaviour change in a reference application.

**Revisit when:** an audit event exists to record the two authorisation decisions §3's first
negative describes, at which point "who authorised this instance" becomes answerable and the
discontinuity stops being invisible; or a delegation or impersonation model is needed — an
agent acting for a user is [15 §7](../15-Security.md#7-ai-and-agent-security)'s subject and
needs two principals where this carries one; or a transport arrives whose identity is not
expressible as claims, which would reopen §1.3's rejection of a FlowX-owned type; or
`IsContinuation` acquires a third setter.

---

**See also:** [ADR-0004](ADR-0004-universal-trigger-model.md) ·
[ADR-0026](ADR-0026-authorisation-runs-in-the-step-loop.md) ·
[ADR-0028](ADR-0028-a-refusal-is-a-result-failure.md) ·
[15 — Security](../15-Security.md)
