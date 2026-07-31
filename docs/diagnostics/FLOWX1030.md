# FLOWX1030 — Authorisation stance names no permission or policy

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0

## What it means

`Authorization.Permission` and `Authorization.Policy` are each a claim that some *named*
grant is required. Neither is complete without the name.

[FLOWX1010](FLOWX1010.md) refuses a capability that declares no stance, because an
omission is not a decision. This rule refuses a stance that decides nothing checkable. A
capability declaring `Authorization.Permission` and no `Permission = "…"` compiles, reads
as enforced at the declaration site, and reaches `flowx.manifest.json` as:

```json
"authorization": { "mode": "Permission" }
```

which is a published claim that a grant is required, naming none. A reviewer sees the
capability protected. An agent's tool descriptor says the same. Nothing can act on
either, because there is nothing to check a principal against.

It is also what left half of a security gate dead. `flowx diff`'s **FLOWX-DIFF-015** is
*"authorisation tightened, **or the named permission changed**"* — a Breaking finding that
blocks a merge. Its second half compares `authorization.value` on both sides. A stance
with no name has no value to move, so a permission that quietly changed from
`payment.write` to `payment.admin` would be reported by nothing.

`Public`, `Authenticated` and `Internal` are complete in themselves and are never
reported here.

## Example that triggers it

```csharp
[Capability("payment.capture", Version = "2.1.0",
    Authorization = Authorization.Permission)]      // required — which permission?
public sealed class CapturePayment : ICapability<Reservation, Payment>
```

An empty or whitespace-only name is treated the same way. `Permission = ""` publishes
`"value": ""` — a permission whose name is blank — which is the same unenforceable stance
wearing a value.

## How to fix it

Name the grant:

```csharp
[Capability("payment.capture", Version = "2.1.0",
    Authorization = Authorization.Permission, Permission = "payment.write")]
```

`Authorization.Policy` takes `Policy = "…"` instead. The diagnostic message names
whichever property the declared mode reads, so it always points at the one to add.

If no named grant is actually required, the honest stance is one that says so:

```csharp
Authorization = Authorization.Authenticated   // any authenticated principal
Authorization = Authorization.Internal        // not reachable from outside
```

Changing the mode to silence this rule is only wrong when the capability really does
need a named grant — in which case the fix is the name, not a weaker stance.

## Why an error rather than a warning

On [FLOWX1010](FLOWX1010.md)'s argument, not a new one. The remedy is one string the
author owns and no tool can supply on their behalf — which is precisely why FLOWX1010's
quick action offers `Authenticated` and `Internal` and withholds `Permission` and
`Policy`. A warning here would be a rule nobody has to obey, guarding the thing this
catalogue treats as least negotiable, and `SafetyDiagnosticsAreErrorsRatherThanWarnings`
holds that line for the rest of the security set.

## What it does not check

Reported where FLOWX1010 is reported: at the `.Step<TCapability>()` that resolves the
capability. A capability no flow composes is not checked by either rule, and neither is
one attached with `.CompensateWith<TCapability>()` — the compensation reaches the manifest
as a capability entry in its own right, but its stance is read without either rule seeing
it. Both are FLOWX1010's existing limits, and this rule inherits them rather than
diverging from its sibling.

The rule says nothing about whether the named permission exists in any identity provider,
or whether anything enforces it at run time. It is a statement about the published
contract: the manifest names a grant, so a diff can see the grant move and a reviewer can
see which one it is.

## When to suppress

There is no case where a `Permission` or `Policy` stance is genuinely better off unnamed.
If a capability is in that position, the stance is wrong, not the rule.

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A
suppression without one fails the build.

---

**Back to:** [diagnostics index](README.md) · [FLOWX1010](FLOWX1010.md) · [Quality gates](../21-Quality-Gates.md)
