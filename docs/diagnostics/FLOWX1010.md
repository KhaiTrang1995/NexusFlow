# FLOWX1010 — Capability does not declare an authorisation stance

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0

## What it means

There is no permissive default anywhere in FlowX. Authorisation attaches to the business operation rather than to a route, so it holds identically over HTTP, over a bus and from an AI agent — but only if it is declared.

## Example that triggers it

```csharp
[Capability("payment.capture", Version = "2.1.0")]   // no Authorization
```

## How to fix it

```csharp
[Capability("payment.capture", Version = "2.1.0",
    Authorization = Authorization.Permission, Permission = "payment.write")]
```

## When to suppress

`Authorization.Public` is allowed and requires an `[ApprovedBy]` naming the reviewer. That is friction on purpose: an explicit, greppable, reviewable statement is not the same thing as an omission.

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A
suppression without one fails the build.

---

**Back to:** [diagnostics index](README.md) · [Quality gates](../21-Quality-Gates.md)
