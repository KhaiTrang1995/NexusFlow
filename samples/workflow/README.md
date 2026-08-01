# Sample — Multi-step orchestration

**Claim proved:** a saga with a value branch, a conditional, three concurrent tracks, a
bounded loop, a composed child flow and a business rejection is **one readable file**, and
every effect it has is reversed in strict reverse order when any part of it fails —
including the child's, and including one undo per element of the loop.

**Second claim proved, since WP-63:** a **multi-day process with a human wait** is one
readable file too. `offer.accept` sends an offer out for countersignature and stops. The
request that started it returns `202` with an instance id, the instance is one `Suspended`
row in PostgreSQL holding no thread, no pooled context and no lease, and a countersignature
arriving days later on a different request resumes it through the same step loop a recovery
scan uses — including the compensation the first node registered and did not live to run.
[§2](#2-the-wait-what-it-used-to-do-instead-and-what-is-still-missing) is the account, and it is the section that used
to say the opposite.

**Claim still not proved:** the *escalations and timers* half. `.Delay(...)` and
`.OnTimeout(...)` compile to nothing and say so as
[`FLOWX1031`](../../docs/diagnostics/FLOWX1031.md), so the duration an `AwaitSignal` declares
reaches the plan and nothing arms it. What bounds a waiting instance is its own
`[FlowDeadline]`. That is the other half of **WP-63**.

Run it:

```bash
FLOWX_POSTGRES_CONNECTION="Host=localhost;Port=5432;Database=postgres;Username=postgres" \
  dotnet run --project samples/workflow
```

It needs a database. That is not incidental — see [§4](#4-why-this-application-needs-a-database).

---

## 1. The flow

```csharp
[Flow("employee.onboard", Version = "2.0.0", Profile = ExecutionProfile.Durable, Owner = "people-ops")]
[FlowDeadline("PT60S")]
[HttpTrigger("POST", "/api/v1/onboarding", Idempotent = true)]
public sealed partial class OnboardEmployeeFlow : Flow<OnboardEmployee, OnboardingResult>
{
    protected override void Define(IFlowBuilder<OnboardEmployee, OnboardingResult> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<ValidateOffer>()

            .Switch(ctx => ctx.Input.Employment)
                .Case(EmploymentType.Permanent,  b => b.Step<OpenPayrollRecord>().CompensateWith<ClosePayrollRecord>())
                .Case(EmploymentType.Contractor, b => b.Step<SignSupplierAgreement>().CompensateWith<VoidSupplierAgreement>())
                .Default(b => b.Fail(OnboardingErrors.UnsupportedEmployment))

            .Step<CreateIdentity>().CompensateWith<DisableIdentity>()
                .WithPolicy(Policies.DirectoryService)

            .Parallel(p => p
                    .Branch(hardware => hardware.Step<OrderLaptop>().CompensateWith<CancelLaptopOrder>())
                    .Branch(access   => access.Step<GrantSystemAccess>().CompensateWith<RevokeSystemAccess>())
                    .Branch<ScheduleInduction>(),
                merge: MergeStrategy.AllMustSucceed)

            .ForEach(
                ctx => ctx.Input.Equipment,
                item => item
                    .When(ctx => ctx.Get<EquipmentRequest>().NeedsApproval, b => b.Step<RecordEquipmentApproval>())
                    .Otherwise(b => b.Step<AutoClearEquipment>())
                    .Step<AssignEquipment>().CompensateWith<ReturnEquipment>(),
                options: new ForEachOptions { MaxDegreeOfParallelism = 1 })

            .SubFlow<ProvisionWorkspaceFlow, ProvisionWorkspace>(ctx => new ProvisionWorkspace(
                ctx.Get<Identity>().EmployeeId,
                ctx.Input.Site))

            .When(ctx => ctx.Input.RequiresBackgroundCheck, b => b.Step<StartBackgroundCheck>())
            .Otherwise(b => b.Step<WaiveBackgroundCheck>())

            .Step<SendWelcomePack>()
            .Emit<EmployeeOnboarded>(ctx => new EmployeeOnboarded(
                ctx.Get<Identity>().EmployeeId, ctx.Input.Site, ctx.Input.Equipment.Count))
            .Return(ctx => new OnboardingResult(
                ctx.Get<Identity>().EmployeeId, ctx.Input.Equipment.Count));
    }
}
```

The child, composed above and reached no other way:

```csharp
[Flow("workspace.provision", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "facilities")]
[FlowDeadline("PT20S")]
public sealed partial class ProvisionWorkspaceFlow : Flow<ProvisionWorkspace, WorkspaceReady>
{
    protected override void Define(IFlowBuilder<ProvisionWorkspace, WorkspaceReady> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<AllocateDesk>().CompensateWith<ReleaseDesk>().WithPolicy(Policies.FacilitiesUndo)
            .Step<IssueBuildingPass>().CompensateWith<CancelBuildingPass>()
            .Return(ctx => new WorkspaceReady(ctx.Get<DeskAllocation>().DeskId, ctx.Get<BuildingPass>().PassId));
    }
}
```

### Where each construct is, and what covers it

| Construct | Where | Covered by |
|---|---|---|
| `Step` | throughout | `ControlFlowTests.TheHappyPathWalksEveryShapeInOrder` |
| `CompensateWith` | six steps, in a case block, on the main line, inside a fork branch, inside a loop body, and in the child | `CompensationTests` (whole class) |
| `Switch` / `Case` / `Default` | engagement type | `TheSwitchTakesThePermanentArm`, `TheSwitchTakesTheContractorArm`, `AValueNoCaseMatchesReachesTheDefaultArmAndFails` |
| `Fail` | the `Default` arm | `AValueNoCaseMatchesReachesTheDefaultArmAndFails` |
| `Parallel` | laptop ‖ access ‖ induction | `TheForkRunsEveryBranchAndJoins`, `AFailedForkBranchUnwindsTheStepsBeforeTheFork` |
| `ForEach` | one item of kit at a time | `AnEmptyCollectionRunsTheBodyNotAtAll`, `EachElementTakesItsOwnArmOfTheConditionalInsideTheLoop` |
| `When` / `Otherwise` | **inside the loop**, and again at the top level | the two loop tests above, `ScreeningRunsWhenTheOfferAsksForIt`, `ScreeningIsWaivedWhenTheOfferDoesNotAskForIt` |
| `SubFlow` | `workspace.provision` | `SubFlowTests` (whole class) |
| `WithPolicy` | `identity.create`, `workspace.allocate_desk` | `WithPolicyTests` — **and see [§3](#3-withpolicy-reaches-the-plan-one-of-its-policies-runs)** |
| `Emit` | `employee.onboarded` | `ManifestTests.TheEventIsPublishedWithoutASuppression`, and the outbox row in [§5](#5-what-it-actually-does-when-you-run-it) |
| `Durable` + `[FlowDeadline]` | all three flows | `TimeoutTests`, `ResumeTests` |
| `AwaitSignal` | not here — `offer.accept`, the third flow, is where the wait is | `SuspensionTests`, and [§2](#2-the-wait-what-it-used-to-do-instead-and-what-is-still-missing) |
| `OnTimeout` | **absent** | [§2](#2-the-wait-what-it-used-to-do-instead-and-what-is-still-missing) |

The nesting is the part a table cannot show: the loop contains a conditional whose two arms
meet, the fork's branches carry their own compensations, and the child is composed from the
parent and unwound by it.

### The unwind

When `welcome.send` fails, with everything before it complete:

```
workspace.cancel_pass      ← the child, innermost and last to complete, in its own reverse order
workspace.release_desk
equipment.return  (phone)      ← the loop, one per element, in reverse element order
equipment.return  (laptop-bag)
access.revoke   ⎫           ← the fork's two compensable branches, in whichever order they finished
hardware.cancel ⎭
identity.disable           ← the main line, in reverse
payroll.close              ← the arm the switch took
```

`induction.book` declares no inverse and contributes nothing. That is
`CompensationTests.FailingLateUnwindsEveryCompletedStepInStrictReverse`, which also asserts
the effects on the adapters: no desk held, no kit outstanding, no grant standing, no account
open, no payroll record.

---

## 2. The wait, what it used to do instead, and what is still missing

**This section is now half history.** The claim this file used to make was: *"a multi-day
process with human approvals, escalations, timers and reversible steps is one readable file —
and it survives every deployment that happens during those days."* When it was written, only
the last four words of the first clause were true. **The human wait is true now**; the
escalations and the timers are not.

### 2.0 What waits, and what it costs

`offer.accept` is the second flow in this application and it exists for one construct:

```csharp
[Flow("offer.accept", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "people-ops")]
[FlowDeadline("P30D")]
public sealed partial class AcceptOfferFlow : Flow<OfferToAccept, AcceptedOffer>
{
    protected override void Define(IFlowBuilder<OfferToAccept, AcceptedOffer> flow) => flow
        .Step<SendOfferForSignature>().CompensateWith<WithdrawOffer>()
        .AwaitSignal<OfferCountersigned>(Waits.Countersignature)   // seven days
        .Step<StartOnboarding>()
        .Return(ctx => new AcceptedOffer(
            ctx.Input.CandidateId, ctx.Get<OnboardingStarted>().OnboardingId));
}
```

Run it against a real PostgreSQL and the two requests look like this:

```
$ curl -i -X POST :5199/api/v1/offers -d '{"candidateId":"c-42","role":"staff-engineer","site":"london"}'
HTTP/1.1 202 Accepted
{"instanceId":"019fbd44-20de-77fc-9c6a-524782b5ad60","awaitingSignal":"offer.countersigned"}
```

```sql
select state from flowx.flow_instance where instance_id = '019fbd44-…';  -- Suspended
select step_id, capability_id from flowx.flow_step where instance_id = '019fbd44-…';
--  0 | offer.send
select count(*) from flowx.flow_lease
 where instance_id = '019fbd44-…' and expires_at > now();                --  0
```

One row, no lease. Then, on a different request:

```
$ curl -i -X POST :5199/api/v1/offers/019fbd44-…/signals/offer.countersigned \
       -d '{"envelopeId":"env-c-42","signedBy":"ada","signedAt":"2026-08-01T12:00:00Z"}'
HTTP/1.1 200 OK
```

```sql
select step_id, capability_id, outcome from flowx.flow_step where instance_id = '019fbd44-…';
--  0 | offer.send          | Success
--  1 | offer.countersigned | Success   ← the signal, journaled as the step's own row
--  2 | onboarding.start    | Success
select state from flowx.flow_instance where instance_id = '019fbd44-…';  -- Completed
```

**Four things in that are worth reading off rather than being told.**

1. **There is no signal table.** The delivered signal is the suspension point's own
   `flow_step` row, with the payload as its `result` and the state-bag snapshot beside it. So
   the frontier that skips committed steps is also what makes a redelivered signal inert —
   `SuspensionTests.ACountersignatureDeliveredTwiceStartsOneOnboarding` — and nothing about
   the schema changed to support suspension.
2. **`onboarding.start` binds `OfferCountersigned`**, a contract no step produced. The engine
   seeds a delivered signal into the state bag under the type the flow declared, before it
   dispatches the suspension point, so a step after the wait reads it exactly as it reads an
   earlier step's output — and nothing in the capability knows there was a wait.
3. **The unwind spans the wait.** `offer.send`'s compensation was registered on the
   invocation that suspended, and that invocation is long gone. Make `onboarding.start` fail
   and the offer is still withdrawn: the resumed flow rebuilds the unwind stack from the
   journal's committed rows, and the undo binds an `OfferToAccept` restored from the
   journaled state bag —
   `SuspensionTests.AFailureAfterTheWaitWithdrawsTheOfferThatWentOutBeforeIt`.
4. **A waiting instance is not swept up.** Both shipped recovery indexes list `Pending`,
   `Running` and `Compensating` only, so a scan does not take a lease on every waiting offer
   every TTL, resume it, find the same wait open and put it back —
   `SuspensionTests.AWaitingOfferIsNotSweptUpAsAbandonedWork`. Left running for three minutes,
   the second offer above is still `Suspended` with one row.

**`offer.accept` has no `[HttpTrigger]`, and that is a gap in the transport rather than a
choice.** `MapFlowX` generates an endpoint that answers `200` with the flow's projected
output, and a suspended flow has none — its `.Return(...)` reads values the steps after the
wait were going to produce. `202 Accepted` with the instance id is the answer that shape
needs, `plugins/FlowX.Http` has no path for it, and `Program.cs` therefore maps the two routes
above by hand rather than publishing a route that fails on the request that suspends.

**And the timeout is carried, not armed.** `Waits.Countersignature` reaches
`StepNode.SignalTimeout` — that is the fabricated one hour below gone — and nothing fires when
it expires. What bounds this flow is `[FlowDeadline("P30D")]`, checked at every step boundary
*including* the one that decides whether to suspend, so an instance whose budget has gone
times out rather than waiting for a signal it can no longer act on.

### 2.1 What `AwaitSignal` used to do — kept, because it is why the fix has the shape it has

`AwaitSignal`, `Delay` and `OnTimeout` are declared on `IFlowBuilder`, documented in
`docs/08-Flow-Definition.md §3.5`, and shown in the flow this README used to print. Here is
what each one does, measured rather than described. The evidence below comes from compiling
a throwaway project that declares them, with `FlowX.Compiler` referenced as an analyzer
exactly as this sample references it:

```csharp
flow.Step<ValidateOffer>()
    .AwaitSignal<Signed>(TimeSpan.FromDays(7))
        .OnTimeout(f => f.Step<WithdrawOffer>())
    .Delay(TimeSpan.FromDays(1))
    .Step<Finish>()
    .Return(ctx => ctx.Get<Done>());
```

That **used to compile** with **0 errors and 0 warnings** under `Profile = Durable`, and
produce:

```csharp
// obj/generated/FlowX.Compiler/FlowX.Compiler.FlowPlanGenerator/…Flow.g.cs
StepGraph.Create(new StepNode[]
{
    StepNode.ForCapability(0, Descriptors.Step0),                          // ValidateOffer
    StepNode.ForAwaitSignal(1, "event.signed", TimeSpan.FromHours(1)),     // ← see below
    StepNode.ForCapability(2, Descriptors.Step2),                          // Finish
});
```

Three separate failures, none of them reported:

1. **`OnTimeout`'s body is discarded.** `WithdrawOffer` is not in the plan, not in the
   dispatcher, not in `flowx.manifest.json` and not in the generated `Descriptors`.
   `FlowAnalyzer` has no `case "OnTimeout"` in the switch that turns builder calls into
   steps, so the call falls into the `default:` arm and is skipped. There is no diagnostic:
   the arm exists precisely so that a generator does not error on methods it has not learned
   yet, which is the right default and is why this is silent.
2. **`Delay` produces no step at all.** Same `default:` arm, same silence. A flow that says
   "wait a day before sending the welcome pack" sends it immediately.
3. **`AwaitSignal` compiled to a step that completes.** `FLOWX1017` fires only outside the
   `Durable` profile, so a durable flow was accepted. The generated dispatcher answered
   `StepOutcome.Success` for that index — *"Emit and AwaitSignal have no capability to call"*
   — and `FlowEngine` had no case for `StepKind.AwaitSignal` at all, so it fell through to
   the ordinary capability path and returned. `TheAbsentHalfTests.AnAwaitSignalStepDoesNotWaitForAnything`
   ran that shape on the real engine against a real journal — three step boundaries, three
   committed rows, the instance `Completed`, and the clock never moving — and was written to
   go red the day WP-63 landed. It did, and it is gone;
   [`SuspensionTests`](../../tests/Workflow.Tests/SuspensionTests.cs) is what replaced it.

   Note also `TimeSpan.FromHours(1)` in the generated plan. The author wrote seven days.
   `FlowEmitter` emitted the constant for every `AwaitSignal`, so the declared timeout was
   discarded too. *This README said the manifest published that hour as well. It did not:
   `ManifestWriter` publishes `"kind": "AwaitSignal"` and no duration at all, so the
   fabrication reached the plan and the generated source and stopped there. The manifest
   still publishes no signal identity and no timeout — that is owed, and it is a schema change
   under [ADR-0017](../../docs/adr/ADR-0017-manifest-v1-freeze-criteria.md) rather than a line
   of emitter code.*

**So a flow that used these compiled, ran green, and quietly did not wait.** That was worse
than a build error, and it was exactly the shape the old README documented as a feature.

### 2.2 What the compiler says now

[`FLOWX1031`](../../docs/diagnostics/FLOWX1031.md) closed the silence, as an error on
`AwaitSignal` and a warning on the other two. **WP-63 narrowed it to the other two.** The same
throwaway project, same profile, same three lines:

```
Flows.cs(30,14): warning FLOWX1031: Flow 'AcceptOfferFlow' declares 'OnTimeout', which this
  release cannot honour: the block is discarded, so its steps reach no plan, no dispatcher
  and no manifest
Flows.cs(31,10): warning FLOWX1031: Flow 'AcceptOfferFlow' declares 'Delay', which this
  release cannot honour: the call produces no step at all, so the flow continues without
  waiting
```

The `AwaitSignal` line is gone, and the plan it produces carries the author's own expression:

```csharp
StepNode.ForAwaitSignal(1, "offer.countersigned", Waits.Countersignature),
```

**The refusal in `FlowEmitter` was made unnecessary, not deleted.** The error existed because
`StepNode.ForAwaitSignal` demands a duration and the compiler's step model had no field for
one, which left two endings — publish a value nobody wrote, or publish no plan. WP-63 added
the field, so the emitter writes what the author declared; the `InvalidOperationException` is
still in that arm, still saying this generator does not invent a duration, and nothing reaches
it because `AwaitSignal<TSignal>(TimeSpan timeout)` has no overload without a timeout.
`SuspensionConstructTests.TheEmitterStillRefusesAnAwaitSignalItHasNoDurationFor` pins it.

`Delay` and `OnTimeout` stay **warnings**, because dropping a call leaves a plan that says
less than the source and nothing untrue — the category `FLOWX1027` occupies at the severity
C# gives `CS0162` — and because an error would erase the `.Delay(...)` grep the timer package
needs to find the flows that asked for one. Narrowing rather than deleting is what
`FLOWX1028` did when `Durable` started running, and for the same reason: deleting the rule
outright would hand a discarded `OnTimeout` block the silence `AwaitSignal` used to have.

One consequence is worth stating plainly, because this README used to state its opposite:
`FLOWX1017` is an error below `Durable` and `FLOWX1031` no longer reports at it, so
**`AwaitSignal` has exactly one profile it can declare and that profile honours it** — and
`AwaitSignalRequiresDurableCodeFixProvider` is a quick action whose result now compiles and
waits. For two phases it was a quick action whose result was a different diagnostic.

**The one timeout that does work is the flow's own deadline.** `[FlowDeadline("PT60S")]` is
an absolute budget set when the flow starts, checked at every step boundary, and enforced by
failing the flow and unwinding everything compensable — `TimeoutTests` moves the clock past
it from inside a step and asserts that the overrunning step finishes, the step after it never
starts, and the saga unwinds. It is also the only thing bounding a **wait**: the boundary check
runs before the suspension point is evaluated, so an instance whose budget has gone times out
rather than waiting for a signal it can no longer act on
(`SuspensionTests.AWaitPastTheFlowsDeadlineTimesOutRatherThanWaiting`, in
`FlowX.Runtime.Tests`).

---

## 3. `WithPolicy` reaches the plan; one of its policies runs

`docs/10-Policy-Framework.md` says exactly one policy is applied at run time —
`CompensationRetry`, at stage 7 — and that on the forward path there is no policy execution
at all. The first half is true of the **engine**: `FlowEngine` reads
`StepNode.CompensationRetry` and honours attempts, backoff and retryable categories, which
`CompensationPolicyTests` proves against a hand-built plan.

**It used not to be true of a flow written in the DSL.** `FlowX.Compiler`'s `FlowEmitter`
emitted no `PolicyChain` anywhere, so every generated `StepNode.ForCapability(...)` carried an
id, a descriptor and at most a compensation — and `StepNode.Policies` and
`StepNode.CompensationPolicies` were `PolicyChain.Empty` on every step of every compiled flow.
`ManifestWriter` is a different class and published the set regardless, so a reader of
`flowx.manifest.json` saw this on `identity.create`:

```json
"policies": [ { "kind": "CircuitBreaker", "stage": "Resilience" },
              { "kind": "Retry",          "stage": "Resilience" },
              { "kind": "Timeout",        "stage": "Resilience" } ]
```

…and nothing armed any of them, nor could. The emitter now splits a declared set by **what
each policy wraps** — `CompensationRetry` onto the compensation's chain, checked against the
*compensating* capability's idempotency, everything else onto the step's — and passes both
halves to `StepNode.ForCapability`:

```csharp
StepNode.ForCapability(7, Descriptors.Step7, Descriptors.Step7Compensation,
    policies: PolicyChain.ForStep(Policies.DirectoryService, Descriptors.Step7)),
```

The three above are still armed by nothing: the Policy Engine is P4 and the forward path
executes zero policies. What changed is that the plan now *states* what was declared, so a
reader of the plan and a reader of the manifest stop disagreeing about the same source line.

**The one that does run** is `Policies.FacilitiesUndo` on `workspace.allocate_desk`.
`CompensationRetry` is the one policy kind the engine knows how to execute, attached to the
one kind of step it applies to, and it now arrives: `ProvisionWorkspaceFlow.Plan
.HasCompensationPolicies` is `true`, and a `workspace.release_desk` that fails with a
retryable category is dispatched up to three times before the unwind gives up on it.
`WithPolicyTests` asserts the publication, the plan and the retried dispatch together — and
also that `employee.onboard`, which declares no compensation policy on any of its six
compensable steps, keeps `HasCompensationPolicies == false` and pays nothing.

What *is* real is the build-time half. `Policies.DirectoryService` declares a retry, and
`FLOWX1014` refuses a retry on a capability that does not declare itself idempotent. That
check runs whether or not anything arms the retry — a safety diagnostic and a runtime feature
are different things, and only one of them ships.

---

## 4. Why this application needs a database

`samples/ecommerce` is `Ephemeral` and runs with nothing behind it. This one declares
`Durable`, and a durable flow on a host that registered no journal and no lease store is
**refused** with `flow.durability_not_configured` before its first step — not run
ephemerally. So `Program.cs` wires `AddFlowXPostgres(...)` and the application will not serve
a request without PostgreSQL.

That has one consequence for the tests, and it is a real gap in the platform rather than a
quirk of this sample. **`FlowTestHost` cannot run a `Durable` flow.** Every `RunAsync` on it
reaches the `FlowEngine.ExecuteAsync` overload that passes `durable: null`, and there is no
seam — no `WithJournal`, no durable overload — for supplying one. The refusal is correct;
the consequence is that the whole `Durable` half of the DSL is untestable through the surface
the platform ships for testing flows. `WhyTheseTestsDoNotUseFlowTestHostTests` is that
attempt, asserted:

```
run.Error.Code   →  "flow.durability_not_configured"
run.Trace        →  (nothing ran)
```

`OnboardingHarness` is this project's stand-in: `FlowHost` with the conformance suite's
reference journal and lease store behind it, and a recording, substituting dispatcher whose
rules are copied from `FlowX.Testing`'s `internal SubstitutingDispatcher`. It should be
deleted the day that seam lands, and the test above goes red to say so.

---

## 5. What it actually does when you run it

```
$ curl -s -X POST localhost:5199/api/v1/onboarding -H 'Idempotency-Key: run-001' \
    -d '{"candidateId":"c-1001","employment":0,"site":"london",
         "equipment":[{"item":"laptop-bag","needsApproval":false},
                      {"item":"phone","needsApproval":true}],
         "requiresBackgroundCheck":false,"nationalId":"NI-123-456"}'

{"employeeId":"c-1001","equipmentIssued":2}
```

The rejection arm, on the wire, as RFC 7807:

```
$ curl -s -i -X POST … -d '{"candidateId":"c-1002","employment":2, …}'

HTTP/1.1 400 Bad Request
Content-Type: application/problem+json

{"type":"https://flowx.dev/errors/onboarding.unsupported_employment",
 "title":"The request is not valid","status":400,
 "detail":"This flow onboards permanent employees and contractors only.",
 "code":"onboarding.unsupported_employment","correlationId":"0HNNFEHCVV8PE:00000001"}
```

And the journal it left in PostgreSQL — this is the run above, verbatim:

```sql
select i.flow_id, s.scope, s.step_id, s.capability_id, s.outcome
  from flowx.flow_step s join flowx.flow_instance i using (instance_id)
 order by s.committed_at;
```

```
 employee.onboard    |      |  0 | offer.validate        | Success
 employee.onboard    |      |  2 | payroll.open          | Success
 employee.onboard    |      |  7 | identity.create       | Success
 employee.onboard    |      |  9 | hardware.order        | Success   ⎫ the fork, in the order
 employee.onboard    |      | 10 | access.grant          | Success   ⎬ the scheduler ran it
 employee.onboard    |      | 11 | induction.book        | Success   ⎭
 employee.onboard    |  0   | 16 | equipment.auto_clear  | Success   ⎫ element 0, Otherwise arm
 employee.onboard    |  0   | 17 | equipment.assign      | Success   ⎭
 employee.onboard    |  1   | 14 | equipment.approve     | Success   ⎫ element 1, Then arm
 employee.onboard    |  1   | 17 | equipment.assign      | Success   ⎭
 employee.onboard    |      | 18 | (subflow)             | Success
 workspace.provision |      |  0 | workspace.allocate_desk | Success ⎫ the child's own instance
 workspace.provision |      |  1 | workspace.issue_pass    | Success ⎭
 employee.onboard    |      | 22 | screening.waive       | Success
 employee.onboard    |      | 23 | welcome.send          | Success
 employee.onboard    |      | 24 | (emit)                | Success
```

Two things in that table are the whole design. **Step 17 appears twice with different
`scope` values** — the loop body is laid out once in the plan and re-entered per element, so
the graph is independent of the size of the data and the two iterations are still distinct
rows. **`workspace.provision` is a second `flow_instance`** — a sub-flow is one node in the
parent and a flow of its own underneath.

The rejected request left `step 0 Success, step 6 Failure` and an instance in state `Failed`:
`.Fail(...)` reaches the engine through the same call a declined dependency comes back on, so
it is journaled like any other failed step.

The event is staged in the same transaction as the step that emitted it:

```
 employee.onboarded | 019fbb25-55c5-… | {"employeeId":"c-1001","site":"london","equipmentIssued":2}
```

`partition_key` is the instance. **"Published" means "written to the outbox and handed to a
publisher"** — `IEventPublisher` is declared and no plugin implements it, so nothing carries
it to a broker (ADR-0018).

---

## 6. The two limits this shape walks into

Both are stated in `ADR-0015` and pinned upstream against hand-built plans. Both apply to
*this* flow, because it forks and it composes a child, and `ShippedLimitTests` measures them
on `employee.onboard` itself.

### 6.1 An overlapping `Parallel` fork does not replay

One pooled context is shared by every branch of a fork, so the `NondeterminismCapture` taken
at a branch's commit takes everything minted since the last commit — **including a sibling's**.
`ShippedLimitTests.AForkWhoseBranchesOverlapAttributesOneBranchsIdToItsSiblingsRow` pins the
interleaving with a rendezvous and reads the rows back out of the journal: the branch that
committed first carries two ids, and the branch that actually minted the first one carries
none. Replaying that instance hands the empty row back faithfully, so the branch mints a
fresh id and the replay diverges at the first thing it did.

**What it costs this sample specifically.** The three real capabilities behind this fork
complete synchronously against in-memory adapters, so today they do not overlap and the
instance replays. Point any one of them at a network — which is the entire reason to fork —
and they do. Nothing warns about the change: the flow is identical, the manifest is
identical, and the instance silently stops being replayable. **If you fork in a `Durable`
flow, the fork is the part of your history you cannot trust.**

### 6.2 A resumed parent does not rebuild a skipped `SubFlow`'s compensation stack

The parent records the composition as one entry in its own unwind stack, bound to the
*child's* context — and that context dies with the node. On resume the composition's journal
row is committed, so the loop steps over it (composing the child again would re-run its
effects), and nothing puts the child's completed steps back on the stack.

So `A · child(X, Y) · B` should unwind `B, Y, X, A` and, after a resume, unwinds `B, A`.
`ShippedLimitTests.AResumedParentDoesNotUndoTheChildItSteppedOver` runs it: the node dies on
the commit after `workspace.provision`, a second node takes the instance over, a later step
fails, and

```
second.Compensated        →  ["identity.disable", "payroll.close"]
Facilities.HeldDesks      →  1
finished.Compensation     →  CompensationOutcome.Succeeded
```

**The desk is still held and the saga reports a clean unwind.** That is the consequence in
the only terms that matter, and it is why the gap is dangerous rather than merely present.

It is not simply fixed: rebuilding the child's stack needs the child's instance id, and the
only way to get it from the parent is to ask "which instances exist under this parent".
`IFlowJournal` deliberately does not answer that — it is a recovery scan's query, which is
why `IRecoveryIndex` was split out — and widening the journal contract would oblige every
store to serve a query some deployments never run.

**If you compose a sub-flow inside a saga, a crash costs you the child's undo.**

---

## 7. Three more things this sample found

None of these is in a document yet. Each has a test that goes red when it is fixed.

### 7.1 A resumed flow could not bind anything a skipped step produced — fixed at WP-59

**This finding is closed, and the paragraph it replaces is worth keeping because the fix is
not where a reader would look for it.** No state bag was journaled: the generated dispatcher
emitted `DescribeStep` only for `Emit` steps, so `flow_instance.state_bag` stayed null and
`IStepDispatcher.RestoreState` was never called. A resumed instance re-entered with an empty
context holding only the input the trigger re-seeded, and the first step past the frontier
that bound a value an earlier step produced failed with `capability.unhandled` — in this
sample, `screening.waive` looking for the `Identity` that `identity.create` made.

The generated payload writer now describes the state bag at every step boundary and restores
it before the first resumed step, so `ResumeTests.AResumedStepBindsTheValueAnEarlierStepProduced`
asserts what it was named against: the flow finishes on the second node. **The cost is one
attribute per contract.** Every type the bag holds needs `[JsonSerializable]` on
`WorkflowJsonContext` — `JournalPayload.Of` requires generated metadata and has no overload
that reflects over a type — and `FLOWX1006` fails the build naming any that is missing. The
fifteen declarations in `Infrastructure.cs` are that list.

What a resume still does **not** get back is a `[Sensitive]` member's value: the snapshot
stored `[redacted]`, because the journal never held anything else.

This was never a property of this sample — any flow whose steps pass values to each other
had it, which is every flow the DSL is for. **"Resumes on another node" was true of the loop
and not of a flow; it is now true of both.**

One piece of advice from the finding survives its fix, and this sample still does it: **in a
`Durable` flow, prefer control-flow delegates that read `ctx.Input`.** A `Branch` and a `Switch` are
never journaled — the engine resolves them before it asks whether a step has committed — so
the arm is recomputed on every pass. A selector over the flow input can be re-derived after a
resume from something the trigger re-seeds, without depending on the snapshot having been
written — which is one fewer thing that has to have gone right. This flow's first draft switched on
`ctx.Get<ValidatedOffer>().Employment` and a resume died at `flow.selector_failed` on step 1.

### 7.2 A compensable step inside a `ForEach` must bind the element type

Only the *element* is scoped to its iteration. What a body step **returns** goes into the
flow's shared, type-keyed bag, so after the loop that bag holds one value: the last element's.
A compensation is invoked with the input of the step it undoes, and it runs after the loop has
finished — so a compensable body step bound to the conditional's output makes every undo
reverse the same element.

This sample was written that way first. `AssignEquipment` took the `ApprovedEquipment` its
two arms produced, both undos read `"phone"`, `"laptop-bag"` stayed outstanding, and the trace
was green: two `equipment.return` entries, both reporting success.
`CompensationTests.AnUndoSeesItsOwnElementAndTheLoopsLastSharedValue` now measures both
columns side by side.

### 7.3 `[Sensitive]` currently protects the wire, not the store

`NationalId` is marked, and the marking is real: it reaches `flowx.manifest.json`, it is
emitted onto the flow as `SensitiveMembers`, the HTTP endpoint strips it from an error
response, and `JournalPayload` redacts every marked member on its only exit. But the
generated dispatcher writes no payload for a step's result and none for the trigger input, so
after the three runs in §5 every row has `flow_instance.input IS NULL` and
`flow_step.result IS NULL`. There is nothing in the store to redact yet. The redaction that
*is* exercised is the event body, and that one works.

---

## 8. Things to try

1. Make `workspace.issue_pass` fail. The child releases its own desk, then the parent unwinds
   the loop, the fork and the main line — `SubFlowTests.TheChildsOwnFailureUnwindsTheChildAndThenTheParent`.
2. Post an `employment` of `2`. The `Default` arm rejects it as a 400, and because nothing
   compensable had run, the unwind is empty — which is a property of *where the arm sits*, not
   of `Fail`.
3. Post an empty `equipment` array. The loop runs zero times and the flow carries on;
   `foreach: 0` appears in the trace.
4. Raise `MaxDegreeOfParallelism` above 1 and watch nothing break — then read §7.2 and notice
   that both elements now race for one `ApprovedEquipment` slot, and that the runtime
   guarantees only that the failure mode is a wrong value.

---

## 9. What would make the original claim true

| Missing | Work package |
|---|---|
| ~~`AwaitSignal` — a durable suspension point, a signal table, and a compiler that keeps the declared timeout~~ **done at WP-63**, and by a narrower construction than this row named: there is **no signal table** and no manifest column. A delivered signal is the suspension point's own `flow_step` row, so no schema changed; the step model got the timeout field, and [`FLOWX1031`](../../docs/diagnostics/FLOWX1031.md) was narrowed rather than deleted | WP-63 |
| `Delay` — a durable timer, and a `case` in `FlowAnalyzer` that lays out a step rather than reporting one | WP-63, second half |
| `OnTimeout` — a layout for the branch, and something to time out of | WP-63, second half |
| A signal identity and a duration in `flowx.manifest.json` — the step object is `additionalProperties: false`, so this is a schema decision under [ADR-0017](../../docs/adr/ADR-0017-manifest-v1-freeze-criteria.md) | unassigned |
| A `202 Accepted` shape in `plugins/FlowX.Http`, so an `[HttpTrigger]`ed flow may suspend. Until then `Program.cs` maps `offer.accept`'s two routes by hand | unassigned |
| An inline composed child that may wait — refused today as `flow.suspension_inside_composition`, because a parent's composition is one row written when the child finishes | unassigned; the same schema question `SubFlowMode.AwaitCompletion` needs |
| A resumed flow that can bind step outputs (§7.1) | WP-59 |
| A forward policy that *executes* — the chain is in the compiled plan (§3), and nothing arms it | P4 |
| A per-branch context, so a fork replays (§6.1) | unassigned |
| A resumed parent that rebuilds a child's unwind stack (§6.2) | unassigned; needs a journal or index contract change |

*This paragraph read: "Until WP-63, a process with human waits is not something this platform
can express, and a sample claiming otherwise is the documented-but-not-produced failure the
manifest exists to eliminate."* **The wait is expressible and this sample expresses it.** What
a process with human waits still cannot express here is the *escalation* — the branch taken
when nobody signs — because there is no clock to take it from, and a sample claiming that one
would be the same failure with a different word in it.
