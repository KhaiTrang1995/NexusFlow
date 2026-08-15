# FLOWX1055 — Validate is declared over a contract with no validation rules

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0

## What it means

`PolicySet.Validate()` takes no parameters, because its rules are not in the declaration: they
are the annotations on the step's **input contract**.
[10 §3](../10-Policy-Framework.md#3-the-policy-catalogue) catalogues the row as "generated from
contract annotations", and that is literal — the compiler reads `[Required]`, `[Range]`,
`[StringLength]`, `[MinLength]` and `[MaxLength]` in the same pass that builds the manifest, and
emits the checks into the generated dispatcher.

So a contract that declares none leaves nothing to emit. The policy reaches the plan, the
manifest publishes it at stage 3, the engine calls into the dispatcher on every execution — and
every input is admitted. That is a declaration that reads as satisfied and is not, which is the
shape [ADR-0025](../adr/ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md) refuses
and the shape `FLOWX1032` existed to report before every catalogued kind became declarable.

## Why an error rather than a warning

[FLOWX1035](FLOWX1035.md) reports the comparable "this policy does nothing" for a compensation
retry of a single attempt, and it is a warning. The difference is what the author is relying on.
A one-attempt retry still dispatches the undo, so the flow behaves as written; an unenforced
validation is the reason a step is allowed to trust its input, and
[10 §2](../10-Policy-Framework.md#why-rigidity-is-the-feature) puts Integrity before Execution
precisely so that corrupt data is refused *before* the side effect rather than after it.

There is also no reading under which the declaration is deliberate. An author who wants no checks
writes no `.Validate()`.

## What counts as a rule

Only an annotation the compiler can turn into a comparison. These are read:

| Annotation | On | Emitted check |
|---|---|---|
| `[Required]` | any nullable member | `member is null`; on a `string`, `string.IsNullOrWhiteSpace` unless `AllowEmptyStrings` is set |
| `[Range(min, max)]` | a numeric member, nullable or not | `member < min \|\| member > max`, inclusive at both bounds |
| `[StringLength(max)]`, with `MinimumLength` | a `string` | a length comparison in one direction or both |
| `[MinLength]`, `[MaxLength]` | a `string` | the same |

These are **not**, and a contract carrying only them reports:

* `[Range(typeof(DateTime), "…", "…")]` — its bounds are parsed by a `TypeConverter` against the
  culture at run time, and a generated comparison would have to pick a parse the compiler cannot
  see the result of.
* `[Range]` over a non-numeric member, and any length attribute over an array or a collection —
  each is a different expression, and one of them walks the caller's data before the step runs.
* `[Required]` on a non-nullable value type — an `int` is never null, so the check folds to
  `false`: a rule that cannot fire.

The vocabulary is `System.ComponentModel.DataAnnotations`', which is in the shared framework, so
declaring one costs no package reference. What FlowX does not reuse is `Validator`: it reflects,
which constraint **C2** refuses.

## Example that triggers it

```csharp
public sealed record PlaceOrder(string Sku, int Quantity);   // nothing annotated

public static readonly PolicySet Checked = PolicySet.Named("checked").Validate();

flow.Step<ReserveInventory>().WithPolicy(Policies.Checked)   // FLOWX1055
```

## How to fix it

Annotate the members that have a rule:

```csharp
public sealed record PlaceOrder(
    [property: Required] string Sku,
    [property: Range(1, 100)] int Quantity);
```

Or remove the `.Validate()` from the set. A step with no validation is the ordinary case, and it
is not reported.

## When to suppress

None. A suppressed build keeps a stage-3 policy that examines every input and refuses none, and
the run-time guard behind it is a *refusal* rather than an admission: a dispatcher with no
generated checks answers `ValidationOutcome.Unavailable` and the engine fails the step with
`policy.validation_unavailable`. So suppressing this converts a build error into every call to
that step failing.

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A
suppression without one fails the build.

---

**Back to:** [diagnostics index](README.md) · [FLOWX1035](FLOWX1035.md) · [Policy framework §3](../10-Policy-Framework.md#3-the-policy-catalogue)
