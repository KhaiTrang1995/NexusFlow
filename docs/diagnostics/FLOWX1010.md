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

## The quick action, and what it withholds

An IDE fix offers exactly two stances: **`Authenticated`** and **`Internal`**.

`Public` is deliberately absent. Offering it would clear a security error with one
keystroke and make the capability world-readable — the outcome this rule exists to
prevent — and it additionally requires an `[ApprovedBy]` that no tool can author on
your behalf.

`Permission` and `Policy` are absent for a different reason: each needs a name that
nothing in the source implies, and emitting the stance alone would produce a declaration
that reads as enforced while naming nothing to enforce. This page used to add that nothing
rejected such a declaration. [FLOWX1030](FLOWX1030.md) now does — so a quick action
offering these two would be trading one error for another. Write them by hand, with their
names.

There is no **Fix All** for this diagnostic. Answering a security question once and
applying the answer solution-wide is the permissive default wearing a different hat.

## When to suppress

`Authorization.Public` is allowed and requires an `[ApprovedBy]` naming the reviewer. That is friction on purpose: an explicit, greppable, reviewable statement is not the same thing as an omission.

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A
suppression without one fails the build.

---

**Back to:** [diagnostics index](README.md) · [Quality gates](../21-Quality-Gates.md)
