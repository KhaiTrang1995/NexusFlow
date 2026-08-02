# ADR-0045: Logs emit through `DiagnosticSource`, and the `ILogger` bridge lives above `FlowX.Abstractions`

**Status:** Accepted
**Date:** 2026-08-02
**Deciders:** Platform architecture, Runtime team
**Amends:** [ADR-0009](ADR-0009-plugin-contracts.md)

> This is the decision [12 §4](../12-Observability.md#4-logs) said the third pillar was blocked
> on, written down because it is a **project-shape** decision an architecture test forced rather
> than a preference. `AbstractionsHasNoDependencies` decided it, and a record is what stops the
> next person reopening it by adding one package reference that looks harmless.

## Context

1. **Two pillars were buildable and one was not, for a reason that has nothing to do with
   logging.** `FlowX.Abstractions` carries an `ActivitySource` and a `Meter` because
   `System.Diagnostics.DiagnosticSource` is in the `net10.0` shared framework.
   `Microsoft.Extensions.Logging.Abstractions` is a **package**, and
   [ADR-0009](ADR-0009-plugin-contracts.md) gives that project zero package references —
   enforced by `AbstractionsHasNoDependencies`, whose message is *"it is referenced by every
   plugin and by all user code; a dependency here is inherited by everyone"*. So the pillar that
   looks cheapest was the one that costs a dependency for every consumer of the platform.

2. **The three emitters are in three assemblies and must agree.** The flow boundary is in
   `FlowX.Hosting`, the step boundary in `FlowX.Runtime`, the stores behind `IFlowJournal`. They
   already share one `ActivitySource` and one `Meter` from `FlowX.Abstractions` for exactly this
   reason, so whatever logs are emitted through has to be reachable from the bottom of the graph.

3. **`[Sensitive]` redaction in this repository is structural, not procedural.** A journal write
   travels as a `JournalPayload`, which exposes no accessor for the object graph and whose only
   exit — `ToJson()` — redacts. A logging design that let a record carry a value would be a
   second exit with no redaction on it, in a sink that fans out further than a journal row does.

4. **Budget B6 is a hard zero** — *telemetry with no listener costs 0 ns and 0 B per step* — and
   `TelemetryCostTests` has already caught one violation of it, where an interpolated span name
   was built and discarded because C# evaluates an argument before the call.

Options rejected:

- **Accept `Microsoft.Extensions.Logging.Abstractions` in `FlowX.Abstractions`.** The honest
  version of "just add the package". It weakens the one rule that makes the contract surface
  safe to reference from anywhere, for one type, and every plugin and all user code inherits it
  whether or not they log. It also requires editing an architecture gate, which is the signal
  that the design is wrong rather than the gate.
- **Emit no logs from the platform and document that a capability should log for itself.** Loses
  the correlation that is the entire value: the flow, step, tenant and trace ids are facts only
  the runtime holds.
- **Define FlowX's own `IFlowXLogger` interface in `FlowX.Abstractions`.** A third logging
  abstraction in an ecosystem that has settled on one, which every host would then have to adapt
  to `ILogger` anyway — a bridge with an extra interface in front of it.
- **`EventSource` instead of `DiagnosticSource`.** In the shared framework and would satisfy the
  dependency rule, but its payloads are primitives shaped for ETW, so a `JournalPayload` could
  not travel as itself and force 3 would be lost.

## Decision

### 1. `FlowXLog` publishes through a `DiagnosticListener` named `FlowX`

In `FlowX.Abstractions`, alongside `FlowXTelemetry`, and named the same as the `ActivitySource`
and the `Meter` so an operator wires `"FlowX"` once. Zero package references, unchanged.

### 2. The `ILogger` bridge is `src/FlowX.Logging`, a separate project

It references `FlowX.Abstractions` and `Microsoft.Extensions.Logging.Abstractions` and nothing
else, and **nothing in the repository references it** — not `FlowX.Hosting`, not a plugin, not a
sample. A host that wants FlowX's logs on its `ILogger` adds one package and calls
`FlowXLogBridge.Attach(loggerFactory)`; a host that wants neither references neither. This is the
second of the two options [12 §4](../12-Observability.md#4-logs) named, and it is strictly better
than the first because it is not a compromise: nobody pays who has not asked.

### 3. A log event carries a `JournalPayload`, never a value

`FlowLogRecord` has no property a contract instance could be assigned to. The step boundary takes
its payload from `IStepDispatcher.DescribeStep` — the same object the journal is handed, already
carrying the flow's `SensitiveMembers` — so redaction is inherited rather than re-implemented, and
a subscriber has nothing to reach. `ALogRecordHasNoPropertyAValueCouldBeAssignedTo` gates the
shape; `ASensitiveMemberIsRedactedOnTheWayOutOfALogRecord` gates the behaviour at both ends.

### 4. The message is a constant, and that is the cardinality rule for logs

[12 §4](../12-Observability.md#4-logs) requires "data as fields, never interpolated into the
message". `NoMessageCarriesDataAndEveryMessageIsAConstant` asserts it by **reference equality**
against the frozen set, because an interpolated string that happens to equal a constant is still
an interpolated string and the next record it produces will not equal anything.

### 5. `DiagnosticSource.Write`'s `RequiresUnreferencedCode` is answered, not registered as debt

This is the contested part of the record. `Write` is annotated because the usual subscriber
reflects over an `object` payload the trimmer cannot see, and constraint C2 turns that annotation
into a **build error** here rather than a warning. It is answered at a single write site with an
`UnconditionalSuppressMessage` carrying the proof, plus a `DynamicDependency` that roots
`FlowLogRecord`'s public properties so a reflecting subscriber finds them after trimming.

**It is deliberately not entered in `docs/DEBT.md`.** That register is for a rule switched off
with an owner and an expiry, and its gate — `SuppressionsAreAccountable` — exists so a suppression
comes back up for review. This is not deferred work: it is a trim annotation asserting a fact
about code that is true permanently, and giving it an expiry would schedule the removal of a
correct annotation. `UnconditionalSuppressMessage` is the BCL's own mechanism for exactly this
distinction, and it is a different attribute from `SuppressMessage` rather than a spelling of it.

## Consequences

**Positive**

- **`AbstractionsHasNoDependencies` never moved**, and neither did any of the other 70 gates. The
  rule that was blocking the feature turned out to be the rule that designed it.
- **The cost is opt-in and visible.** The one package reference this feature adds is in one
  `.csproj` that nothing else references, so the dependency graph shows who pays.
- **`[Sensitive]` cannot leak into a log by construction**, which is a stronger claim than the
  journal's own and was free — the payload type already existed and already had one exit.
- **B6 survived a third pillar.** With no subscriber a record is never built, measured rather
  than asserted, and the measurement was verified to fail when the guard is removed.

**Negative / accepted trade-offs**

- **A host must wire the bridge explicitly.** There is no `AddFlowXLogging(this IServiceCollection)`
  — that would cost `Microsoft.Extensions.DependencyInjection.Abstractions` in this project, and
  the surface a host actually needs is one call. It is one line of composition and it is not
  discoverable from `AddFlowX`.
- **A capability's own `ILogger` is still not correlated.** Opening a logging scope around a
  capability means calling `BeginScope` from `FlowX.Runtime`, which is precisely the assembly this
  record keeps ignorant of `ILogger`. [12 §4](../12-Observability.md#4-logs) marks that clause as
  still specification, and this shape is the reason it is hard rather than an oversight.
- **`DiagnosticSource` is a less familiar seam than `ILogger`.** A subscriber that is not the
  bridge has to know the payload type, which is why `FlowXLog.Subscribe` is typed — but it is one
  more concept than "inject a logger".
- **A suppression exists in `src/` that the debt gate does not see.** Justified in §5 and here,
  and it is the first one; a second would be the point at which the gate should learn about
  `UnconditionalSuppressMessage` rather than each author arguing it again.

## Revisit when

- **`Microsoft.Extensions.Logging.Abstractions` moves into the shared framework**, at which point
  force 1 evaporates, the bridge could fold into `FlowX.Abstractions`, and this record is the
  argument for whether it should — the answer is probably still no, because a host that exports
  no logs should not link a logging abstraction either.
- **A second `UnconditionalSuppressMessage` is needed anywhere under `src/`**, at which point
  `SuppressionsAreAccountable` should be taught the distinction in §5 explicitly rather than
  leaving it to a regex that does not match the attribute by accident.
- **Correlating a capability's own logger becomes a requirement**, which needs either a scope
  opened above `FlowX.Runtime` — a decorator in `FlowX.Logging` around the dispatcher — or an
  ambient mechanism the runtime can set without naming `ILogger`. The first is a real design and
  is where this should go; it was not built because nothing has asked for it yet.
- **A subscriber other than the bridge appears** and wants a payload shape `FlowLogRecord` does
  not have, at which point the record's field list stops being a projection of §2 and becomes a
  contract with its own evolution rules.
