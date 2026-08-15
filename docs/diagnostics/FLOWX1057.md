# FLOWX1057 — `Consent` is declared with no purpose

**Severity:** Error · **Category:** FlowX · **Since:** WP-83

---

## What it means

`PolicySet.Consent(purpose)` names the processing purpose a step may be invoked for. The
engine compares it, ordinally and in whole, with the purpose the invocation carried on a
validated claim (`FlowInvocation.Purpose`), and refuses the step when the two differ or when
the invocation asserted none. That is GDPR Article 5(1)(b) purpose limitation, decided at
stage 2 beside the capability's authorisation stance.

A blank purpose has no comparison to make, and **the direction it fails in is the surprising
one**. It looks like the fail-closed mistake — a gate nobody can pass — and it is the
opposite. `StepPolicy.HasConsent` reads a blank purpose as *no consent declared*, so:

* the step is dispatched to every caller, whatever purpose they asserted;
* `flowx.manifest.json` still publishes an `Identity`-stage policy on that step, because the
  manifest is written from the kinds the set declares and not from their arguments;
* a reviewer reading the manifest, and `flowx diff` comparing two of them, both see a control
  that is not there.

Reading a blank purpose as *refusing* instead was rejected: a purpose nobody wrote is not a
purpose nobody may satisfy, and a step taken permanently out of service by a typo in a
`static readonly PolicySet` is a worse failure than a build error over it. So the run-time
floor is permissive and this rule is what makes the mistake unshippable.

It is an error rather than a warning for the reason every rule in this family is one —
`FLOWX1010`, `FLOWX1030` and `FLOWX1037` are all errors, and
`SafetyDiagnosticsAreErrorsRatherThanWarnings` holds that line. A control that silently does
not run is the failure mode
[ADR-0030](../adr/ADR-0030-policy-stance-is-refused-at-build-time.md) exists over; a warning
here would make the newest member of the family the only one a team may leave switched on.

---

## Example that triggers it

```csharp
public static class Policies
{
    // The purpose was going to be filled in "later".
    public static readonly PolicySet Clinical = PolicySet.Named("clinical")
        .Consent("")
        .Audit(category: "clinical");
}

public sealed partial class IntakeFlow : Flow<PatientIntake, IntakeAccepted>
{
    protected override void Define(IFlowBuilder<PatientIntake, IntakeAccepted> flow) =>
        flow.Step<StoreRecord>()
            .WithPolicy(Policies.Clinical);   // FLOWX1057
}
```

`Consent(null!)` is the same defect spelled differently and is reported identically.

---

## How to fix it

Name the purpose the step serves:

```csharp
public static readonly PolicySet Clinical = PolicySet.Named("clinical")
    .Consent("treatment")
    .Audit(category: "clinical");
```

Or, if the step is not purpose-limited, remove the declaration rather than leaving an empty
one:

```csharp
public static readonly PolicySet Clinical = PolicySet.Named("clinical")
    .Audit(category: "clinical");
```

Two things about the value are worth knowing before choosing it:

* **It is an identifier, not a sentence.** It is compared ordinally, published in the manifest
  and matched against a `purpose` claim an identity provider issues. `treatment` and
  `Treatment` are two purposes.
* **A step that serves two purposes is two steps**, or one purpose named for both. The
  comparison is deliberately an equality and not a hierarchy: whether a consent to be treated
  also covers being studied is a legal and clinical judgement, and a platform that quietly
  widened one would be wrong in the place it matters most.

Where the purpose belongs to the *business record* rather than to the caller's credential —
"this intake form says it is for research" — it goes on the contract and is checked by a
capability against a consent register, which is what `samples/healthcare` does with
`CarePurpose` and `IConsentRegister`. The two are different things and both can be true of one
flow: the policy limits what the credential may be used for, the capability establishes that a
person agreed.

---

## When to suppress

**Do not.** There is no reading under which an empty purpose is what the author meant: an
author who wants no purpose limitation writes no `.Consent(...)`, and one who wants a step
nobody may run removes the step. A suppression buys nothing in any case — the run-time floor
reads the blank purpose as undeclared, so the suppressed build ships a step with no gate on
it and a manifest that says otherwise.

The rule reads a string literal and is silent on anything else, so a purpose composed at run
time is a false negative rather than a suppression: `RS1030` forbids an analyzer from asking
the compilation for another tree's semantic model, which is the same restriction `FLOWX1035`
works under when it reads an attempt count.

---

**See also:** [FLOWX1056](FLOWX1056.md) — the same objection at stage 3, where a `Validate`
over a contract with no rules examines every input and refuses none ·
[FLOWX1037](FLOWX1037.md) — a stance the runtime cannot decide, refused at build time rather
than skipped ·
[ADR-0029](../adr/ADR-0029-a-refusal-is-a-result-failure.md) — why a refusal is a `Result`
failure carrying `Forbidden` ·
[10 §3](../10-Policy-Framework.md#3-the-policy-catalogue) — the catalogue row
