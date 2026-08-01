# FLOWX1016 — Expected failures are values, not exceptions

> **Severity:** Warning · **Category:** FlowX · **Since:** 0.1.0

## What it means

[07 §3](../07-Capability-Model.md#3-rules) rule 2 says a capability
"returns `Result<TOut>`; expected failures are values". The first half needs no analyzer —
`ICapability<TIn, TOut>.ExecuteAsync` returns `ValueTask<Result<TOut>>`, so a capability
that does not return a `Result` does not compile. This rule is the second half.

[ADR-0007](../adr/ADR-0007-result-over-exceptions.md)) sets out what a thrown business
outcome costs, and none of it is style:

| | Returned as `Result.Fail(Error)` | Thrown |
|---|---|---|
| Visible in the signature | yes | no |
| In `flowx.manifest.json`'s error catalogue | yes | no |
| Retry decision | by `ErrorCategory` — `Validation` is terminal, `Unavailable` retries | none; the policy chain sees a defect |
| In telemetry | `flowx_capability_failed_total`, by code | `flowx_capability_unhandled_total`, i.e. a bug alert |
| Cost on the failure path | no allocation, no throw | 5–20 µs, against a 5 µs platform budget (Q1) |

The runtime does catch it — `ICapability`'s own documentation says throwing "signals a
defect and is reported as one". That is a safety net, not a supported way to fail: an
outcome that arrives as a defect is one your dashboards page someone about and your retry
policy cannot classify.

## What it proves, and what it does not

**Containment is a proof.** A `throw` written in the syntax of `ExecuteAsync` — including
inside a lambda or a local function declared in it — is a `throw` the engine will catch.
Nothing has to be inferred for that.

**Whether it is *expected* is not, and cannot be.** "Expected" is a claim about a domain,
not a property of a type. This rule decides it from the exception type against a list of
defect signals, in exactly the sense [FLOWX1003](FLOWX1003.md) matches a list of transport
namespaces. The list is drawn so that every doubtful case is silent:

| Not reported | Why |
|---|---|
| `ArgumentException`, `ArgumentNullException`, `ArgumentOutOfRangeException` | The CA1062 guard clause the engine's own contract requires, and the standard `_ => throw` on an unreachable switch arm. Also the idiomatic .NET spelling of a validation failure — which is a real false negative, accepted so the rule never fires on a guard |
| `NotImplementedException`, `NotSupportedException` | Scaffolding, and "this operation does not exist", neither of which is an outcome a caller can be handed |
| `ObjectDisposedException`, `NullReferenceException`, `InvalidCastException`, `IndexOutOfRangeException`, `OutOfMemoryException`, `StackOverflowException`, `PlatformNotSupportedException`, `UnreachableException` | Defects by definition |
| `OperationCanceledException`, `TaskCanceledException` | The platform's own deadline and cancellation, not the capability's outcome |
| `throw;` | A rethrow of something the capability did not create — ADR-0007's infrastructure-fault signal |
| A `throw new …` inside a `try` that has a `catch` | It may never leave the method; the catch may already be turning it into a `Result`, and the analyzer cannot tell |

Matching is by exact type name, not by base type: a domain exception deriving from
`ArgumentException` is a domain exception, and inheriting from a guard type does not buy
silence.

**What it cannot see at all:**

- **Nothing is interprocedural.** A private helper on the same class, an extension method,
  or an adapter the capability calls may throw anything and this says nothing about it.
- **Only `throw new …` is recognised.** `throw _cached;` and `throw Errors.Declined();`
  both pass, because the type at the `throw` is not written there.
- Silence is therefore not a statement that a capability cannot throw.

## Why a warning and not an error

The other capability rules in this catalogue — [FLOWX1010](FLOWX1010.md),
[FLOWX1015](FLOWX1015.md) — are errors because what they check is structural and provable.
This one rests on a judgement it cannot make, and the [index](README.md#why-these-are-errors-not-warnings)'s
bar for an error is a mistake that is structurally impossible to recover from at run time.
A leaked exception is recovered from: the engine catches it at the capability boundary.

The practical argument matters more. A build-breaking rule that misjudges one `throw` gets
a file-level suppression, and a suppressed rule protects nothing — the failure mode this
package exists to remove. This repository builds with `TreatWarningsAsErrors`, so it is a
break *here*; a consumer who disagrees downgrades it once in `.editorconfig`, which is one
recorded decision rather than a pragma per capability. This is the same reasoning
[FLOWX1025](FLOWX1025.md) is shipped under.

## Example that triggers it

```csharp
[Capability("payment.capture", Version = "1.0.0",
    Authorization = Authorization.Authenticated,
    SideEffects = new[] { "payment-gateway" })]
public sealed class CapturePayment : ICapability<CaptureRequest, Capture>
{
    public async ValueTask<Result<Capture>> ExecuteAsync(
        CaptureRequest input, CapabilityContext ctx, CancellationToken ct)
    {
        var response = await _gateway.ChargeAsync(input.Amount, ct);

        if (response.Declined)
        {
            // FLOWX1016: 'declined' is an outcome the caller handles. Thrown, it is
            // absent from the signature and from the manifest, and the flow's retry
            // policy sees a defect rather than a Conflict.
            throw new PaymentDeclinedException(response.Reason);
        }

        return Result.Ok(new Capture(response.Reference));
    }
}
```

## How to fix it

Give the outcome a code and a category, and return it:

```csharp
public static class PaymentErrors
{
    public static Error Declined(string reason) => new(
        "payment.declined", $"Payment declined: {reason}", ErrorCategory.Conflict);
}

if (response.Declined)
{
    return Result.Fail<Capture>(PaymentErrors.Declined(response.Reason));
}
```

The code reaches `flowx.manifest.json`'s per-capability `errors` array, so `flowx diff` and
blast-radius review can see it; the category decides the HTTP status, the gRPC status and
whether a retry policy will even try again.

Where the exception comes from an adapter you do not own, translate it at the boundary —
which is the discipline ADR-0007 accepts as the cost of the decision:

```csharp
try
{
    var response = await _gateway.ChargeAsync(input.Amount, ct);
    return Result.Ok(new Capture(response.Reference));
}
catch (HttpRequestException e)
{
    return Result.Fail<Capture>(
        new Error("payment.gateway_unavailable", e.Message, ErrorCategory.Unavailable));
}
```

## When to suppress

When the throw really is a defect signal whose type is not on the list above — a
domain-specific invariant-violation exception, say, that no caller is expected to handle.
Say so at the site:

```csharp
#pragma warning disable FLOWX1016 // FLOWX-DEBT: id=… owner=… expires=…
// An unbalanced ledger is a corrupted database, not an outcome a caller can act on.
throw new LedgerCorruptedException(accountId);
#pragma warning restore FLOWX1016
```

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A
suppression without one fails the build.

A team that has decided this rule does not suit them should downgrade it in
`.editorconfig` rather than scatter pragmas:

```ini
dotnet_diagnostic.FLOWX1016.severity = suggestion
```

---

**Back to:** [diagnostics index](README.md) · [Capability model](../07-Capability-Model.md) · [ADR-0007](../adr/ADR-0007-result-over-exceptions.md))
