# FLOWX1025 — Trigger attribute cannot be read by the compiler

> **Severity:** Warning · **Category:** FlowX · **Since:** 0.1.0

## What it means

The flow carries an attribute deriving from `TriggerAttribute`, and the compiler
cannot read it. That trigger will not appear in `flowx.manifest.json`.

The reason is structural rather than an oversight. A trigger's family is exposed
as an abstract property each attribute overrides:

```csharp
public abstract class TriggerAttribute : Attribute
{
    public abstract TriggerKind Kind { get; }
}

public sealed class HttpTriggerAttribute(string method, string route) : TriggerAttribute
{
    public override TriggerKind Kind => TriggerKind.Http;   // an expression, not a value
}
```

`Kind => TriggerKind.Http` is **executable code**, not attribute data. A source
generator reads metadata; it does not run the assembly it is compiling. So for a
`TriggerAttribute` subclass the abstractions do not ship, there is no way to ask
what family it belongs to, nor what its constructor arguments mean. The compiler
recognises the five attributes `FlowX.Abstractions` ships — `HttpTrigger`,
`KafkaTrigger`, `CronTrigger`, `StreamTrigger`, `AgentTrigger` — by type, and
refuses to invent a kind for anything else.

Refusing is correct. [ADR-0005](../adr/ADR-0005-manifest-as-build-artifact.md)
makes the manifest a build artifact that downstream tools trust; a guessed
`kind` would be a fact nobody declared, published in the document whose whole
value is that it only contains declared facts.

**Why it is worth a diagnostic anyway.** The skip produces an *absence*, and
`flowx diff` classifies a removed trigger as a **breaking** change. An absent
`triggers` array reads as "this flow has no trigger", which is exactly what a
flow whose trigger was deleted looks like. The gate stays green either way. A
contract check that silently loses its input is worse than no check, because a
green result is read as a checked result.

## Example that triggers it

A transport plugin ships its own trigger attribute:

```csharp
// in the plugin package
public sealed class MqttTriggerAttribute(string topic) : TriggerAttribute
{
    public override TriggerKind Kind => TriggerKind.Bus;

    public string Topic { get; } = topic;
}
```

and a flow declares it:

```csharp
[Flow("order.place")]
[MqttTrigger("orders/requested")]          // FLOWX1025
public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
{
    protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
        .Step<ReserveInventory>()
        .Return(ctx => new OrderResult("id"));
}
```

The build succeeds, the flow runs, and the manifest's entry for `order.place`
has no `triggers` array at all.

## How to fix it

- **Declare a built-in trigger instead.** The five kinds are transport families,
  not products: MQTT, RabbitMQ, Azure Service Bus and SQS are all
  `TriggerKind.Bus`, and `KafkaTrigger`'s shape — topic plus consumer group —
  usually carries the declaration you need. The plugin still does the transport
  work; the attribute is what the manifest publishes.

  ```csharp
  [Flow("order.place")]
  [KafkaTrigger("orders/requested", Group = "order-placement")]
  ```

  If the built-in attribute genuinely misdescribes the transport, prefer the next
  option — a wrong declaration in the manifest is worse than a missing one.

- **Accept the gap deliberately**, and record that decision where a reviewer can
  see it (below). Do this only having understood that `flowx diff` will not
  report this trigger's removal as breaking, because it never knew it was there.

## When to suppress

When the flow is reached through a plugin transport that no built-in attribute
describes honestly, and the team accepts that the manifest does not record how
this flow is reached.

Because the attribute usually belongs to a package the consumer does not own,
the right place is `.editorconfig` — a single reviewable decision rather than a
`#pragma` copied into every flow file:

```ini
# FLOWX-DEBT(platform, 2026-12-31): flows reached via the MQTT plugin declare a
#   trigger the compiler cannot read; their triggers are absent from the manifest
#   and `flowx diff` cannot see them. Revisit when the plugin declares a readable kind.
[src/Flows/**.cs]
dotnet_diagnostic.FLOWX1025.severity = suggestion
```

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A
suppression without one fails the build.

Scope it to the flows that need it. Setting it project-wide also silences the
next flow that picks up an unreadable trigger by accident.

## Why this is a warning and not an error

Every other structural rule in this catalogue is an error, on the stated grounds
that a rule nobody has to obey is not a rule. Two things make this one different.

The source is not wrong. The flow declares a trigger; the *artifact* is
incomplete. That is the same category as [FLOWX1024](FLOWX1024.md) — a gap
between what the manifest promises and what the build can deliver — and it is
reported for the same reason: it is otherwise discovered by whoever trusted the
manifest.

And the developer often cannot fix it. The attribute belongs to a plugin
package, and [17-Plugin-System §1](../17-Plugin-System.md) commits to the
opposite of a platform where using a third-party transport fails your build:
*"if a reasonable person must fork FlowX to do something reasonable, that is a
bug in FlowX."* An error would make an extension point that the plugin system
explicitly promises into something you must suppress before you can use it.

FlowX's own build sets `TreatWarningsAsErrors`, so this stops the build *here*.
A consumer who has weighed the trade-off can downgrade it, which is the correct
distribution of the decision: the platform's default is loud, and the team that
accepts the gap is the team that records accepting it.

---

**Back to:** [diagnostics index](README.md) · [Trigger model](../09-Trigger-Model.md) ·
[Plugin system](../17-Plugin-System.md)
