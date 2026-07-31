# FLOWX1009 — Capability or flow holds mutable state

> **Severity:** Warning · **Error** where the compilation shows the type on a durable flow's replay path · **Category:** FlowX · **Since:** 0.1.0

## What it means

[07 §3](../07-Capability-Model.md#3-rules) rule 6: *"Stateless: no mutable instance or static
fields."* Its **Enforced by** cell read "— · **not enforced.** `FLOWX1009` does not exist"
until this rule did, with the note that `RuntimeHasNoMutableStatics` covers `FlowX.Runtime`
and not application capabilities.

**This one is not only a replay concern, and that matters for how seriously to take the
warning.** A capability is resolved once and invoked concurrently by every flow that names
it — the reference sample registers all four as singletons — so a field something can assign
later is shared across every in-flight invocation. The value one order reads is whatever
another wrote. It is right in a unit test, which runs one invocation, and wrong under load,
which is the most expensive shape a defect can have.

On a durable flow it is worse than a race. The journal records a step's inputs, its result
and the three ambient values the context supplied; it knows nothing about a field. A replay
therefore runs against whatever the process happens to hold rather than against what was
recorded, and the divergence leaves no trace in the one place anybody would look.

> [06 §5](../06-Execution-Engine.md#5-the-determinism-boundary) words this row differently:
> *"no mutable static state **reachable from** a flow."* That is the wider claim, and this
> rule proves the narrower one — see [what it cannot prove](#what-it-cannot-prove).

## Example that triggers it

```csharp
[Capability("inventory.reserve", Version = "1.0.0",
    Authorization = Authorization.Internal, Idempotent = true)]
public sealed class ReserveInventory : ICapability<ValidatedOrder, Reservation>
{
    private static int _totalReserved;          // shared by the whole process
    private int _lastQuantity;                  // shared by every concurrent order
    public string Region { get; set; } = "eu";  // and anyone holding the instance can change it

    private readonly IInventoryStore _store;    // fine: readonly, and it is a port

    public ReserveInventory(IInventoryStore store) => _store = store;

    public async ValueTask<Result<Reservation>> ExecuteAsync(
        ValidatedOrder input, CapabilityContext ctx, CancellationToken ct)
    {
        _lastQuantity = input.Quantity;
        _totalReserved += input.Quantity;

        await _store.ReserveAsync(input.Sku, _lastQuantity, ctx.IdempotencyKey, ct).ConfigureAwait(false);

        return new Reservation(input.Sku, _lastQuantity, ctx.IdempotencyKey);
    }
}
```

```
warning FLOWX1009: 'ReserveInventory' holds mutable state: the static field '_totalReserved'
                   can be assigned after construction
warning FLOWX1009: 'ReserveInventory' holds mutable state: the instance field '_lastQuantity'
                   can be assigned after construction
warning FLOWX1009: 'ReserveInventory' holds mutable state: the instance property 'Region'
                   can be assigned after construction
```

The same declarations on a capability a `Durable` flow steps through, or on a `Durable` flow
itself, are reported as **errors**.

## How to fix it

```csharp
// Per-invocation values are locals. They were never state; the field was just a habit.
var quantity = input.Quantity;
await _store.ReserveAsync(input.Sku, quantity, ctx.IdempotencyKey, ct).ConfigureAwait(false);
```

```csharp
// Configuration is readonly and arrives through the constructor, where a reader can see
// where it came from and a test can supply a different one.
private readonly string _region;
public ReserveInventory(IInventoryStore store, InventoryOptions options)
    => (_store, _region) = (store, options.Region);
```

```csharp
// A counter that must outlive one invocation is somebody's dependency, not a field on a
// capability. Behind a port, its lifetime is a decision someone made.
private readonly IReservationMetrics _metrics;
_metrics.Reserved(input.Quantity);
```

```csharp
// Anything genuinely fixed is readonly or const, which this rule accepts.
private const decimal UnitPrice = 19.99m;
private static readonly string[] SupportedRegions = ["eu", "us"];
```

The general shape: **per-invocation state belongs in the input contract, the result, or the
flow's context** — the three places the engine already carries values between steps, and the
only ones the journal records.

## What it detects

A declaration-site rule, and deliberately nothing more. For each capability and each
`[Flow]` type, reading its own declaration:

| Declaration | Verdict |
|---|---|
| A field that is neither `readonly` nor `const`, instance or static | **reported** |
| A property with a `set` accessor, instance or static | **reported** |
| A `readonly` or `const` field | allowed |
| A property with only `get`, or with `init` | allowed — `init` can only run while the object is being constructed |
| A record's positional parameters | allowed — they compile to `init`-only properties |

Each declared name is reported on its own, so `private int _a, _b;` is two findings: a reader
who fixes one should still see the other.

### What it cannot prove

- **It is a declaration-site rule, not a reachability one.** It proves a type declares state
  something can assign. It says nothing about a mutable static declared on *another* type and
  read from a capability body, which is what
  [06 §5](../06-Execution-Engine.md#5-the-determinism-boundary)'s "reachable from a flow"
  wording would cover. That is deliberate, not an oversight: a capability *is* the impure
  zone and legitimately reads the outside world, so a read-site rule there would fire on
  ordinary configuration. Inside a flow's builder delegates,
  [FLOWX1011](FLOWX1011.md) does report the read.
- **`readonly` is taken at face value and is only shallowly true.** A
  `readonly List<T>`, a `readonly Dictionary<,>` or a `readonly` array is mutable state this
  rule permits — exactly as FLOWX1011 permits a `static readonly` one. This is the largest
  hole in the rule and there is no cheap way to close it.
- **A get-only property with a body is permitted**, and its body may return something
  different every time.
- **Only what the type declares is examined.** A mutable field inherited from a base class
  in a referenced assembly is not reported, because the fix is not in this compilation.
- **A field the capability never writes is still reported.** The rule is about what *can* be
  assigned, not about what is: a field left mutable is one the next edit can write, and the
  next edit will not re-derive this analysis.

## When to suppress

The honest case is a `readonly`-adjacent field the language cannot express as `readonly` —
one assigned in an initialisation method rather than a constructor. The fix is almost always
to move the assignment into the constructor rather than to suppress.

A memoised lookup on a capability looks like the other case, and usually is not: a cache
shared by concurrent invocations is a dependency with a lifetime, and putting it behind a
port is both the fix for this rule and the thing that makes the cache configurable.

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A suppression
without one fails the build.

---

**Back to:** [diagnostics index](README.md) · [Capability model §3](../07-Capability-Model.md#3-rules) · [Execution engine §5](../06-Execution-Engine.md#5-the-determinism-boundary)
