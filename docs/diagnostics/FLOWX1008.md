# FLOWX1008 — Identity or randomness is taken outside the context

> **Severity:** Warning · **Error** where the compilation shows the code on a durable flow's replay path · **Category:** FlowX · **Since:** 0.1.0

## What it means

`Guid.NewGuid()` and `Random.Shared` answer differently on every call. That is what they are
for, and it is exactly why a flow may not call them: the same three values the journal
reproduces — `ctx.UtcNow`, the ids `ctx.NewId()` produced, and the seed `ctx.Random` was
built from — are the only non-deterministic values a replay can reconstruct
(`NondeterminismCapture`, one row per step boundary). An identifier minted outside that seam
is in none of those fields.

**And this one bites before any replay does.** A `Retry` policy re-executes a capability;
the engine ships that today. `ctx.IdempotencyKey` is deliberately stable across the retry —
"pass it to downstream systems so they can deduplicate; this is what makes at-least-once
delivery produce effectively-once effects" — and a key the capability minted itself is not.
A payment gateway deduplicating on that key sees two captures where the flow performed one.

That is why this is a separate id from [FLOWX1007](FLOWX1007.md) rather than half of one
rule: a clock that moves changes a **decision**, an identifier that moves duplicates an
**effect**, and the two have different fixes.

> **The documents do not agree on the granularity, and this page follows the more specific
> one.** [06 §5](../06-Execution-Engine.md#5-the-determinism-boundary) has a row per id —
> `FLOWX1007` for `DateTime.Now/UtcNow` and `DateTimeOffset.Now/UtcNow`, `FLOWX1008` for
> `Guid.NewGuid()` and `Random.Shared` — which is the split implemented here.
> [07 §3](../07-Capability-Model.md#3-rules) rule 7 and `ICapability`'s remarks both state
> them as one rule carrying two ids ("time, identifiers and randomness come from
> `CapabilityContext` only — would be FLOWX1007/1008"). The substance is identical; only the
> count differs, and one id per remedy is the more useful of the two.

### Where it applies

The same subjects as [FLOWX1007](FLOWX1007.md#where-it-applies): a capability's members, and
a flow's members other than the builder delegates, which belong to
[FLOWX1011](FLOWX1011.md). Everything that page says about the split, about why a capability
is in scope at all, and about severity applies here unchanged.

## Example that triggers it

```csharp
[Capability("payment.capture", Version = "2.1.0",
    Authorization = Authorization.Permission, Permission = "payment.write",
    Idempotent = false, SideEffects = ["payment-gateway"])]
public sealed class CapturePayment : ICapability<Reservation, Payment>
{
    private readonly IPaymentGateway _gateway;

    public CapturePayment(IPaymentGateway gateway) => _gateway = gateway;

    public async ValueTask<Result<Payment>> ExecuteAsync(
        Reservation input, CapabilityContext ctx, CancellationToken ct)
    {
        var key = Guid.NewGuid().ToString();               // a new key on every attempt
        var jitter = Random.Shared.Next(50);

        await Task.Delay(jitter, ct).ConfigureAwait(false);

        var receipt = await _gateway.CaptureAsync(input.ReservationId, key, ct).ConfigureAwait(false);

        return receipt is null
            ? OrderErrors.PaymentDeclined("insufficient funds")
            : new Payment(input.ReservationId, 0m, receipt);
    }
}
```

```
warning FLOWX1008: 'CapturePayment' reads 'Guid.NewGuid', which mints an identifier or a
                   random value outside the context; the journal captures ctx.NewId() and
                   ctx.Random's seed, and reproduces those
warning FLOWX1008: 'CapturePayment' reads 'Random.Shared', which mints an identifier or a
                   random value outside the context; …
```

The same reads inside a capability a `Durable` flow steps through are reported as **errors**.

## How to fix it

```csharp
// A key a remote system deduplicates on: use the one that is stable across retries AND
// replays. This is the fix in the reference sample, and the comment there says why.
var receipt = await _gateway
    .CaptureAsync(input.ReservationId, ctx.IdempotencyKey, ct)
    .ConfigureAwait(false);
```

```csharp
// An identifier the flow will carry: ctx.NewId() is captured into the step's journal row.
var reservationId = ctx.NewId();
```

```csharp
// Randomness that has to be reproducible: ctx.Random is seeded from a value the journal
// records, so the same instance draws the same numbers on the way through again.
var jitter = ctx.Random.Next(50);
```

```csharp
// Randomness that must NOT be reproducible — a nonce, a salt, a token — does not belong in
// a value the flow keeps at all. Generate it inside the adapter behind the port, where it
// never reaches the flow's state and nothing has to replay it.
```

That last case is the one worth pausing on. If a secret must be unpredictable, making it
replayable is a security defect, not a fix; the answer is to move it out of the flow's state
rather than to draw it from `ctx.Random`.

## What it detects

The catalogue is shared with [FLOWX1011](FLOWX1011.md), in `AmbientReads`:

`Guid.NewGuid()`, `Guid.CreateVersion7()`, and every member of `Random` and
`RandomNumberGenerator` — reached statically, or constructed with `new Random(...)`.

### What it cannot prove

- **Nothing is interprocedural.** `_ids.Next()` is accepted, and its body may call
  `Guid.NewGuid()`.
- **The catalogue is a list, not a proof.** A `Ulid` or `NanoId` package, a `KSUID` helper,
  a `Guid` produced by a database — none is detected. **Silence here is never a statement
  that a capability is deterministic.**
- **A `Guid` parsed or copied from input is not reported**, and should not be: it came from
  the caller, so a replay is handed the same one.
- **Only this compilation is visible.** A capability whose durable caller lives in another
  assembly is a warning, not an error.
- Reflection, `dynamic`, and anything that does not bind are skipped.

## When to suppress

A nonce, a salt or a cryptographic token that must be unpredictable is the real case — and
the right fix is usually to move the generation behind the port rather than to suppress,
because a value that must not be predictable must also not be journaled.

The other honest case is a capability that is provably never retried and never on a durable
path, generating an id for a log line. That is cheap to fix with `ctx.NewId()` instead, and
the suppression is rarely worth writing.

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A suppression
without one fails the build.

---

**Back to:** [diagnostics index](README.md) · [Execution engine §5](../06-Execution-Engine.md#5-the-determinism-boundary) · [Capability model §7](../07-Capability-Model.md#7-idempotency)
