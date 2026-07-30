# FlowX Samples

Nine reference applications. Each one exists to prove a specific claim from the
specification — not to demonstrate syntax. Every sample ships with tests, a
benchmark where a budget applies, and a generated architecture diagram.

| Sample | Proves | Key documents |
|---|---|---|
| [ecommerce](ecommerce/) | The baseline: a saga with compensation, in 6 files | [01 §7 V1](../docs/01-Vision.md#7-measurable-success-criteria), [08](../docs/08-Flow-Definition.md) |
| [banking](banking/) | Deny-by-default security, audit, idempotent money movement | [15](../docs/15-Security.md), [10](../docs/10-Policy-Framework.md) |
| [healthcare](healthcare/) | Multi-tenancy, data residency, PII redaction, consent | [16](../docs/16-Multi-Tenant.md), [15 §9](../docs/15-Security.md#9-compliance-support) |
| [realtime-stream](realtime-stream/) | Windowing, watermarks, checkpoints, backpressure at 250k rec/s | [09 §9](../docs/09-Trigger-Model.md#9-stream-trigger), [14 B13](../docs/14-Performance.md) |
| [ai-agent](ai-agent/) | Capabilities as agent tools with real authorisation | [13 §6](../docs/13-AI-Native.md#6-capabilities-as-agent-tools) |
| [event-driven](event-driven/) | Transport portability: HTTP → Kafka → cron, zero logic changes | [09](../docs/09-Trigger-Model.md), quality goal Q4 |
| [scheduler](scheduler/) | Cron with leader election, overlap and missed-fire policies | [09 §8](../docs/09-Trigger-Model.md#8-schedule-trigger) |
| [polling](polling/) | Long-running external polling without holding resources | [06 §6](../docs/06-Execution-Engine.md#6-suspension-waiting-without-holding-resources) |
| [workflow](workflow/) | Multi-day human-in-the-loop process with signals and timers | [06](../docs/06-Execution-Engine.md), [11](../docs/11-Distributed-Runtime.md) |

## Running any sample

```bash
cd samples/<name>
flowx dev up          # Postgres + Redpanda + OTel collector + Studio
dotnet run
flowx graph --live    # watch it work
dotnet test
```

## What every sample must contain

A sample that does not meet this bar is not merged — samples are how the
specification is proven, so they are held to the same standard as the runtime:

- [ ] A `README.md` stating the claim it proves and the documents it maps to
- [ ] Flows and capabilities in the standard `<Feature>/` folder layout
- [ ] Unit tests for every capability; flow tests for every failure path
- [ ] At least one **failure-path** test (compensation, timeout, or breaker)
- [ ] Generated architecture diagram committed as `docs/graph.md`
- [ ] A benchmark, where the sample maps to a budget in [14-Performance](../docs/14-Performance.md)
- [ ] No infrastructure required beyond `flowx dev up`
