# V5 — cold start

What [01-Vision](../01-Vision.md) §7's fifth success criterion costs: **≤ 200 ms,
NativeAOT-compatible**. Stated since the vision was written, verified by "startup benchmark",
and until this rig there was no startup benchmark — so the criterion had never produced a
number, in either direction.

Rig: `tests/FlowX.ColdStart.Bench`. The recorded run is
[`V5-cold-start.json`](V5-cold-start.json).

## What was measured

The rig publishes `samples/ecommerce` with the AOT job's own command, then starts the
resulting native binary thirty times and times two things per start:

| Phase | From | To |
| --- | --- | --- |
| `process.listening` | immediately before `Process.Start` | the port accepts a TCP connection |
| `flow.firstResponse` | immediately before `Process.Start` | `POST /api/v1/orders` answers 2xx carrying `receiptId` |

Both clocks start before the process exists, so `fork`/`exec` and the loader are inside the
number — a replica scaling out charges its caller for those too.

The endpoint is a **flow** route rather than `/health`, and it is authorised: the request
carries the cashier token, because `order.place` refuses at `payment.capture` without
`payment.write` and a refusal is not a served flow. A 200 with the wrong body is not accepted
either. That makes the measurement "the engine, the generated dispatcher, the capability
catalogue and the authorisation decision are all ready", not "the host is up".

## The recorded run

NativeAOT, `linux-x64`, self-contained. .NET 10.0.10 on Ubuntu 24.04, 4 cores, shared
container, load average 0.9–3.5. 30 measured starts after 3 discarded.

| | n | min | p50 | p95 | p99 = max |
| --- | ---: | ---: | ---: | ---: | ---: |
| Process start → listening | 30 | 47.5 ms | 52.6 ms | 75.7 ms | 91.4 ms |
| Process start → first flow response | 30 | 57.7 ms | **63.0 ms** | 86.7 ms | **102.2 ms** |

Native binary **18 910 528 bytes (18.0 MiB)**; the whole publish directory is 57.1 MiB
because it keeps the separate `.dbg` symbols and the XML documentation beside it. Resident
set at the first response: **35.4 MiB** at p50, 35.8 MiB at the worst of the thirty.

The first start after a publish cost **117.7 ms** — reading 18 MB off disk once — and is one
of the three discarded starts rather than a sample, because it happens once per deployment
and not once per start.

With n = 30 the p99 and the max are the same observation, and the column says so. Nothing
here interpolates.

**Verdict: MET on this hardware — 102.2 ms at p99 against 200 ms, and 63.0 ms at p50 —
recorded on a shared container, which §"Is shared hardware good enough" below argues is
admissible for this particular question and would not be for a tighter one.**

## Is shared hardware good enough to decide on?

The same arithmetic [P0.md](P0.md) §5 makes for B1, with a much smaller margin, so it is
worth doing rather than asserting.

The measured value is **102 ms at p99, 63 ms at p50**. The ceiling is **200 ms**. For the
verdict to flip, the true value on a comparable machine would have to be **2.0× worse at
p99**, or 3.2× worse at the median.

The observed spread is knowable here, because the rig was run twice: an earlier run of the
same commit on the same container reported **p50 64.2 ms, p95 91.1 ms, p99 128.5 ms**. The
medians agree to 2 %; the p99 — which is the max of thirty samples and therefore whatever the
scheduler did once — moved by 26 %. A tail that moves 26 % between runs does not close a
factor of 2.0.

**Where this hardware would not be good enough.** Two places, named so the figure is not
carried further than it goes. A verdict resting on a 10–30 % difference — "did this commit
make start-up worse" — is not decidable here at the tail, and a regression gate on p99 would
fire on the container rather than on the code; the p50 is the stable statistic and the gate
in `ci.yml` is on it. And this is one container's *kind* of hardware: a cold
container on a cloud host, where the image is pulled and the page cache is empty, pays for
things this machine had already paid for. That is a different measurement, not a worse
version of this one.

**What would change the verdict.** Something structural rather than slower: a reflection path
that AOT has to resolve at start-up, a manifest read from disk, a plan catalogue built per
request instead of once. The 11 ms between the port opening and the flow answering is what
that would show up in, which is why the two phases are recorded apart.

## Reproducing

```bash
dotnet run --project tests/FlowX.ColdStart.Bench -c Release
```

It publishes into `.artifacts/coldstart-aot/`, writes `.artifacts/cold-start.json`, and
takes about a minute including the publish. `--runs`, `--warmup`, `--timeout`, `--json` and
`--no-publish` change what it does; `--readytorun` publishes ReadyToRun instead of NativeAOT
and labels its own verdict `NOT V5`, because the criterion names NativeAOT and a number
measured any other way is a different fact.

It is opt-in by construction: an `Exe` with no test framework in it, like
`tests/FlowX.Chaos` and `tests/FlowX.Durability.Bench`, so `dotnet test` cannot run it and
only an explicit `dotnet run` does.

**A run that cannot measure records nothing.** Verified by pointing the rig at a route the
sample does not serve: it retried until the timeout, exited 3, printed the 404 and the
process's own log, and wrote no results file. A rig that answered "200 ms" there would have
been worse than the unmeasured criterion it replaced.

## What this does not answer

The criterion has two halves and this document measures one of them. *NativeAOT-compatible*
is the AOT job in `ci.yml`, which links the sample and serves a request from it on every
push; *≤ 200 ms* is the row above, and that same job now measures it — it hands this rig the
binary it just published and `scripts/check-cold-start.py` fails the run when the median
exceeds 200 ms. The tail is printed beside it and gates nothing, for the resolution reason
above.

The measurement is also of one sample. `samples/ecommerce` is the repository's only
AOT-published assembly, so it is the only start-up there is to time, and a deployment with a
journal, a broker connection and a real OIDC handler starts differently.

---

**Back to:** [Vision §7](../01-Vision.md) · [Benchmark results](README.md) · [Performance budgets](../14-Performance.md)
