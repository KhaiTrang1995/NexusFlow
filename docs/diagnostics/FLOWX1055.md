# FLOWX1055 — Event schema version is not a semantic version

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0
> **Applies to:** an `[EventSchema("…")]` whose value is not SemVer 2.0. A contract that
> declares no `[EventSchema]` at all publishes `1.0.0` and is never reported.

## What it means

`[EventSchema]` is the only thing in FlowX that makes an event's published version vary.
Before it existed, `ManifestWriter` stamped the literal `1.0.0` on every event in every
manifest the compiler produced — the constant standing in for a fact that
[ADR-0017 F2](../adr/ADR-0017-manifest-v1-freeze-criteria.md#f2--no-field-is-emitted-as-a-constant-standing-in-for-a-fact)
refuses.

The compiler reads the attribute **once**, and the value it reads reaches two places:

- `event.schemaVersion` in `flowx.manifest.json`, which is what a consumer team reads and
  what `flowx diff` keys an event on when it decides whether a major was bumped
  (`FLOWX-DIFF-020`);
- the `schema_version` column of every outbox row the contract's `.Emit<T>()` stages, which
  is what arrives beside the body.

A value the compiler cannot read is **dropped**, not published — so the contract goes on
emitting `1.0.0` in both places while the source says something else. Nothing downstream can
see the disagreement: the manifest is well-formed, the rows are well-formed, and a subscriber
pinned to the wrong major is told nothing. That is why this is reported rather than tolerated.

Publishing the unreadable value instead would be worse in the other direction: `flowx diff`
takes the major out of the string, so a version it cannot parse is an event nothing can be
compared against.

## Example that triggers it

```csharp
[EventSchema("v2")]
public sealed record OrderPlaced(string OrderId, string Sku, int Quantity);
```

`v2` is not a semantic version. Neither is `2`, `2.0`, `2.0.0.0` or `01.0.0`.

## How to fix it

```csharp
[EventSchema("2.0.0")]
public sealed record OrderPlaced(string OrderId, string Sku, int Quantity);
```

Three numeric identifiers, no leading zeros, with optional pre-release and build metadata:
`2.0.0`, `2.1.0-rc.1` and `1.0.0+build.7` are all accepted. Or delete the attribute — a
contract that declares nothing publishes `1.0.0`, which is what every event in this
repository published before the attribute existed and is not reported.

## What this rule does not judge

**Whether the version is the *right* one for the change the record just took.** Adding a
required member to a contract and leaving the major alone is a compatibility question about
two builds, and `flowx diff` is where it is asked — an analyzer sees one compilation and has
no previous version of the contract to compare against. That is `FLOWX1022`'s reservation,
and this rule presupposes it: it asks only whether the declared value is a version at all.

## Why an error rather than a warning

The declaration ships. Unlike [FLOWX1045](FLOWX1045.md) — the same shape one artifact over, a
compile-time constant a host refuses — nothing fails at run time here, which makes it the
quieter failure and the one no gate downstream can find. The fix is to write three numbers.

## When to suppress

There is nothing a suppression buys. The value still does not reach the manifest or the
outbox row; suppressing this only removes the sentence that says so.

## Related

- [ADR-0017 F2](../adr/ADR-0017-manifest-v1-freeze-criteria.md#f2--no-field-is-emitted-as-a-constant-standing-in-for-a-fact)
  — the freeze criterion this attribute closes, and why a constant standing in for a fact is
  worse than an absent field
- [ADR-0018](../adr/ADR-0018-outbox-publication-and-ordering.md) — what the outbox row carries
  beside the body
- [22-CLI §3.1](../22-CLI.md#31-breaking) — `FLOWX-DIFF-020`, the rule that reads the value
- [FLOWX1045](FLOWX1045.md) — the other rule about an attribute value the compiler must read,
  and where the two severities part company
