# FLOWX1054 — Declared wait is not a compile-time constant

> **Severity:** Warning · **Category:** FlowX · **Since:** 0.1.0
> **Applies to:** the `timeout` argument of `.AwaitSignal<TSignal>(timeout)` and the
> `timeout:` argument of `.PollUntil<TCapability>(until:, interval:, timeout:)` — the two
> declarations the manifest publishes as a step's `timeout`.

> [!NOTE]
> **This is the omission [ADR-0021](../adr/ADR-0021-manifest-publishes-the-wait.md) §2.2 chose,
> said out loud.** That record folds the declared wait for the manifest and *omits the field
> where it cannot* — `merge`'s precedent, and still the right stance for the **field**: an
> absent field is a consumer asking, a guessed one is a consumer misled. It was the wrong
> stance for the **author**, who was told nothing at all.

## What it means

A declared wait reaches two artifacts, and they need different things from it.

* **The plan is C#.** `StepNode.SignalTimeout` carries the expression *verbatim*, and generated
  code evaluates it in the compilation that declared it. A wait written as
  `Waits.Countersignature` — or as a configuration read — waits for exactly what the source
  says. **Nothing about the flow's execution is wrong, which is why this is a warning.**
* **The manifest is JSON**, read by tools that have never seen the assembly. `"timeout":
  "Waits.Countersignature"` would publish a *symbol name*, and a `flowx diff` rule over a symbol
  fires when somebody renames a constant and stays silent when somebody changes its value —
  the exact inversion of what the rule is for. So the compiler folds the duration, and where it
  cannot, it writes no `timeout` at all.

Two things are lost when the fold fails, and the message names both:

1. **The published contract loses the only number a reader can use** to tell a wait meant to be
   minutes from one meant to be quarters. `deadline` is published for that reason and this is
   the same kind of fact.
2. **[`FLOWX-DIFF-206`](../22-CLI.md) has no window to compare.** Shortening an offer window
   from fourteen days to seven stops being a reported change and becomes an invisible one.

### What folds

`TimeSpan.Zero`; `TimeSpan.FromDays`, `FromHours`, `FromMinutes`, `FromSeconds` and
`FromMilliseconds` over a numeric literal; and **one level of indirection** through a field or
property whose declaration initialises it with one of those. `FromTicks` is deliberately absent
— a wait expressed in ticks is not a wait anybody declared on purpose — and a negative duration
is refused, because the schema's ISO-8601 grammar has no sign.

That set is small on purpose, and the rule is raised **from the fold's own answer** rather than
from a second reading of the expression: `FlowAnalyzer.FoldDeclaredWait` reports exactly when it
is about to return nothing. So the diagnostic and the field cannot disagree — there is no
version of the foldability rule that could report a wait the manifest carried anyway, or stay
quiet about one it dropped.

## Example that triggers it

```csharp
public static class Waits
{
    // Read from the environment so a demonstration need not wait seven days.
    public static TimeSpan Countersignature { get; } =
        Environment.GetEnvironmentVariable("FLOWX_SAMPLE_OFFER_WINDOW") is { } window
            ? TimeSpan.Parse(window, CultureInfo.InvariantCulture)
            : TimeSpan.FromDays(7);
}

flow
    .Step<SendOfferForSignature>()
    .AwaitSignal<OfferCountersigned>(Waits.Countersignature)   // FLOWX1054
        .OnTimeout(f => f.Fail(OfferErrors.NotCountersigned()));
```

**This is not a fixture.** It is what `samples/workflow` shipped for one merge: the timer half of
WP-63 made the wait an environment read so a demonstration would not sit for a week, the flow
went on working, and the manifest silently stopped publishing `timeout` — losing
[ADR-0021](../adr/ADR-0021-manifest-publishes-the-wait.md)'s new field its only producer in the
repository. The build stayed green. One test noticed, because it is the one test that reads the
field. That is the whole reason this rule exists.

**What stays silent:**

- A literal at the call site: `.AwaitSignal<OfferCountersigned>(TimeSpan.FromDays(7))`.
- A named constant one hop away — `public static TimeSpan Countersignature { get; } =
  TimeSpan.FromDays(7);` — which is the form the samples use, because a duration is a business
  decision and belongs where it can be read without opening a flow.
- A call with no argument at all. The DSL declares one overload and the argument is required, so
  that is a half-typed buffer and C# is already saying something more useful.
- A `.Delay(duration)`. It publishes no `timeout` field for anyone to lose.

## How to fix it

1. **Write the duration as a compile-time constant**, naming it as a `static` field or property
   if it belongs outside the flow. One hop is followed, so the named form costs nothing.
2. **If a shorter wait is wanted for a demonstration or a test, shorten it in the test rather
   than in the declaration.** A test that drives a flow controls its own clock and its own
   signals; the declaration is the thing being published.
3. **If the window genuinely belongs to the deployment**, keep the run-time read and suppress
   the rule with a reason. The flow is correct; what you are accepting is a manifest that does
   not say how long it waits.

## When to suppress

When the window genuinely belongs to the deployment rather than to the contract, and a manifest
that does not say how long the flow waits is accepted.

The unit of suppression is a project one — an `.editorconfig` entry or a `<NoWarn>` — not a
`#pragma` at the call site:

```ini
dotnet_diagnostic.FLOWX1054.severity = none
```

**A `#pragma` does not turn this rule off.** It is reported by the source generator rather than
by a `DiagnosticAnalyzer`, and a generator's diagnostics are filtered against the compilation's
options, which is where an `.editorconfig` entry and `<NoWarn>` both land — the in-source
directive is not consulted. That is also the unit the
[severity section](README.md#why-warning-is-not-the-lenient-option) argues for: one line, in the
repository that took the decision.

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry — see
[21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A suppression without one
fails the build. The reason is worth writing even though nothing about the running flow changes:
the consequence lands on somebody else — a reader of the manifest, and the `flowx diff` a release
gate runs — and neither of them can see the suppression.

Under this repository's `TreatWarningsAsErrors` a warning still stops the build, which is the
point: the finding is a decision somebody takes on purpose rather than a line in a log.

## Related

- [ADR-0021](../adr/ADR-0021-manifest-publishes-the-wait.md) §2.2 — the fold, and why the field
  is omitted rather than guessed at.
- [FLOWX1036](FLOWX1036.md) — the same shape one artifact over: a declaration the compiler
  cannot read, reported rather than left silent.
- [FLOWX1043](FLOWX1043.md) — reads two folded durations and compares them; it is silent when
  either cannot be folded, which is now the case this rule reports.
- [FLOWX1019](FLOWX1019.md) — the flow deadline against the step timeouts it must contain.
