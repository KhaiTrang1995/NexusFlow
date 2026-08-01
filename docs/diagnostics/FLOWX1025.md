# FLOWX1025 — Trigger attribute declares no `[TriggerKind]`

> **Severity:** Warning, raised as an **Error** when the trigger attribute is declared in
> the compilation being built · **Category:** FlowX · **Since:** 0.1.0

## What it means

The flow carries an attribute deriving from `TriggerAttribute`, and that attribute does
not declare which transport family it belongs to in a form the compiler can read. The
trigger will not appear in `flowx.manifest.json`.

A trigger's family is exposed as an abstract property each attribute overrides:

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

`Kind => TriggerKind.Http` is **executable code**, not attribute data. A source generator
reads metadata; it does not run the assembly it is compiling. So the kind is declared a
second time, *as* data, on the attribute class itself:

```csharp
[TriggerKind(TriggerKind.Http)]
public sealed class HttpTriggerAttribute(string method, string route) : TriggerAttribute
```

An enum passed to a constructor **is** attribute data. It survives compilation, is
readable out of a referenced assembly, and needs nothing to run. That is what lets a
trigger attribute FlowX does not ship reach the manifest at all — which
[ADR-0004](../adr/ADR-0004-universal-trigger-model.md))'s "one trigger abstraction for
every transport" requires, if it is to be true for transports FlowX does not ship.

**This diagnostic is what is left over**: an attribute that declares no marker declares no
family, and the compiler will not invent one.
[ADR-0005](../adr/ADR-0005-manifest-as-build-artifact.md)) makes the manifest a build
artifact that downstream tools trust; a guessed `kind` would be a fact nobody declared,
published in the document whose whole value is that it only contains declared facts.

**Why the skip is worth a diagnostic.** It produces an *absence*, and `flowx diff`
classifies a removed trigger as a **breaking** change. An absent `triggers` array reads as
"this flow has no trigger", which is exactly what a flow whose trigger was deleted looks
like. The gate stays green either way. A contract check that silently loses its input is
worse than no check, because a green result is read as a checked result.

## Example that triggers it

A transport plugin ships its own trigger attribute and does not mark it:

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

The build succeeds, the flow runs, and the manifest's entry for `order.place` has no
`triggers` array at all.

## How to fix it

**Add `[TriggerKind]` to the trigger attribute**, matching the value its `Kind` property
returns:

```csharp
[TriggerKind(TriggerKind.Bus)]                      // ← the fix
public sealed class MqttTriggerAttribute(string topic) : TriggerAttribute
{
    public override TriggerKind Kind => TriggerKind.Bus;

    public string Topic { get; } = topic;
}
```

The flow needs no change. `order.place` now publishes:

```json
"triggers": [ { "kind": "Bus" } ]
```

A plugin that factors shared members into its own intermediate base declares the marker
once, on the base — the compiler walks the attribute's base chain, matching how the
attribute behaves under reflection:

```csharp
[TriggerKind(TriggerKind.Bus)]
public abstract class BrokerTriggerAttribute : TriggerAttribute;

public sealed class MqttTriggerAttribute(string topic) : BrokerTriggerAttribute;   // covered
```

### The kind publishes; the address does not

Knowing an attribute is `Bus` says nothing about what its arguments *mean*. Nothing in
metadata says `MqttTriggerAttribute`'s first positional argument is a topic rather than a
broker address or a subscription name, so the manifest records the kind and stops there.
A plugin trigger's entry is `{"kind": "Bus"}` — an honest partial record — while the five
attributes `FlowX.Abstractions` ships publish their full address, because their shape is
part of the platform contract.

That is a real remaining gap, [recorded as one](#what-this-does-not-fix). It is a much
smaller one than an absent trigger: `flowx diff` can now see the trigger, and therefore
see it removed.

### If the attribute belongs to a package you do not own

You cannot add an attribute to a class in someone else's assembly, which is why the rule
stays a warning in that case rather than becoming an error. Three options, in order of
preference:

- **Ask the plugin's author to add the marker.** It is one line, purely additive, and
  breaks no consumer.

- **Declare a built-in trigger attribute as well.** The five kinds are transport families,
  not products: MQTT, RabbitMQ, Azure Service Bus and SQS are all `TriggerKind.Bus`, and
  `KafkaTrigger`'s shape — topic plus consumer group — usually carries the declaration you
  need. The plugin still does the transport work; the attribute is what the manifest
  publishes.

  ```csharp
  [Flow("order.place")]
  [MqttTrigger("orders/requested")]
  [KafkaTrigger("orders/requested", Group = "order-placement")]
  ```

  If the built-in attribute genuinely misdescribes the transport, prefer the next
  option — a wrong declaration in the manifest is worse than a missing one.

- **Accept the gap deliberately**, and record that decision where a reviewer can see it
  (below). Do this only having understood that `flowx diff` will not report this trigger's
  removal as breaking, because it never knew it was there.

## When to suppress

When the flow is reached through a plugin transport whose attribute carries no marker, no
built-in attribute describes it honestly, and the team accepts that the manifest does not
record how this flow is reached.

Because the attribute belongs to a package the consumer does not own, the right place is
`.editorconfig` — a single reviewable decision rather than a `#pragma` copied into every
flow file:

```ini
# FLOWX-DEBT(platform, 2026-12-31): flows reached via the MQTT plugin declare a trigger
#   attribute that carries no [TriggerKind]; their triggers are absent from the manifest
#   and `flowx diff` cannot see them. Revisit when the plugin ships the marker.
[src/Flows/**.cs]
dotnet_diagnostic.FLOWX1025.severity = suggestion
```

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry — see
[21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A suppression
without one fails the build.

Scope it to the flows that need it. Setting it project-wide also silences the next flow
that picks up an unmarked trigger by accident.

**Suppressing the in-source case is almost always wrong.** If the attribute is declared in
your own compilation the rule is an error, and the fix is the one line above.

## Why the severity depends on who can fix it

Every other structural rule in this catalogue is an error, on the stated grounds that a
rule nobody has to obey is not a rule. This one splits, the way
[FLOWX1011](FLOWX1011.md) splits on the execution profile.

**An error when the trigger attribute is declared in the compilation being built.** The
person reading the diagnostic owns the file that fixes it, and the fix is one attribute.
There is no trade-off to weigh, so there is nothing for a warning to buy.

**A warning when it arrives from a referenced assembly.** The developer cannot fix it at
all — the marker belongs on a class in a package they consume — and
[17-Plugin-System §1](../17-Plugin-System.md) commits to the opposite of a platform where
using a third-party transport fails your build: *"if a reasonable person must fork FlowX
to do something reasonable, that is a bug in FlowX."* The source is not wrong there
either; the *artifact* is incomplete, which is the same category as
[FLOWX1024](FLOWX1024.md) and is reported for the same reason: it is otherwise discovered
by whoever trusted the manifest.

The descriptor's default severity stays `Warning` — that is what `.editorconfig`
configures against and what the analyzer release table records; the in-source case is
raised as an error at the report site.

FlowX's own build sets `TreatWarningsAsErrors`, so the warning stops the build *here* too.
A consumer who has weighed the trade-off can downgrade it, which is the correct
distribution of the decision: the platform's default is loud, and the team that accepts
the gap is the team that records accepting it.

## What this does not fix

Two limits, stated because a reader would otherwise assume they were handled.

**The kind is declared twice, and the two can disagree.** `Kind =>` is what the runtime
reads; `[TriggerKind]` is what the manifest publishes. An attribute that returns
`TriggerKind.Stream` while marked `TriggerKind.Bus` runs as one family and is published as
the other, with nothing failing. For the five attributes `FlowX.Abstractions` ships, the
fitness function `EveryShippedTriggerAttributeDeclaresTheKindItsPropertyReturns`
constructs each one and compares the two, so they cannot drift. For a plugin's attribute
arriving as a compiled reference, **nothing can check this**: reading `Kind` means running
a property getter, and a source generator does not run the code it compiles. That is the
cost of the design, and it is the smaller cost — the alternative was that the trigger did
not reach the manifest at all.

**A plugin's constructor arguments are not projected.** `{"kind": "Bus"}`, and no topic.
`AttributeData.AttributeConstructor.Parameters` does carry parameter *names* through
metadata, so a generic projection of `MqttTriggerAttribute(string topic)` into a `topic`
field is technically within reach. It is blocked on the manifest schema rather than on the
compiler: `$defs/trigger` is `additionalProperties: false` over a closed property list, and
widening it is an [ADR-0005](../adr/ADR-0005-manifest-as-build-artifact.md)) decision about
what a manifest promises — not something a trigger reader should settle by emitting a
field.

---

**Back to:** [diagnostics index](README.md) · [Trigger model](../09-Trigger-Model.md) ·
[Plugin system](../17-Plugin-System.md)
