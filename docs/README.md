# FlowX Documentation

This directory is the **specification** for FlowX. It is normative: code that
contradicts it is either a bug in the code, or an ADR that has not been written
yet (constraint C8).

## Reading paths

**"I want to understand the idea"** (30 min)
→ [01 Vision](01-Vision.md) → [02 Manifesto](02-Manifesto.md) → [04 Core Concepts](04-Core-Concepts.md)

**"I want to build on it"** (2 h)
→ [04 Core Concepts](04-Core-Concepts.md) → [07 Capability Model](07-Capability-Model.md) → [08 Flow Definition](08-Flow-Definition.md) → [09 Trigger Model](09-Trigger-Model.md) → [10 Policy Framework](10-Policy-Framework.md) → [19 SDK](19-SDK.md) → [23 Testing Strategy](23-Testing-Strategy.md) → [samples](../samples/README.md)

**"I want to review the architecture"** (3 h)
→ [05 Architecture](05-Architecture.md) → [ADR index](adr/README.md) → [06 Execution Engine](06-Execution-Engine.md) → [11 Distributed Runtime](11-Distributed-Runtime.md) → [14 Performance](14-Performance.md) → [15 Security](15-Security.md)

**"I have to operate it"** (1.5 h)
→ [12 Observability](12-Observability.md) → [18 Cloud-Native](18-Cloud-Native.md) → [11 Distributed Runtime](11-Distributed-Runtime.md) → [16 Multi-Tenancy](16-Multi-Tenant.md)

**"I want to extend it"** (1 h)
→ [17 Plugin System](17-Plugin-System.md) → [09 Trigger Model §11](09-Trigger-Model.md#11-writing-a-trigger-plugin) → [ADR-0009](adr/ADR-0009-plugin-contracts.md)

## Index

| # | Document | Layer |
|---|---|---|
| 01 | [Vision](01-Vision.md) | Why |
| 02 | [Manifesto](02-Manifesto.md) | Why |
| 03 | [Design Principles](03-Design-Principles.md) | Why |
| 04 | [Core Concepts](04-Core-Concepts.md) | What |
| 05 | [Architecture](05-Architecture.md) | **How — arc42 + C4, the primary design document** |
| 06 | [Execution Engine](06-Execution-Engine.md) | How — runtime |
| 07 | [Capability Model](07-Capability-Model.md) | How — programming model |
| 08 | [Flow Definition](08-Flow-Definition.md) | How — programming model |
| 09 | [Trigger Model](09-Trigger-Model.md) | How — ingress |
| 10 | [Policy Framework](10-Policy-Framework.md) | How — cross-cutting |
| 11 | [Distributed Runtime](11-Distributed-Runtime.md) | How — distribution |
| 12 | [Observability](12-Observability.md) | How — operations |
| 13 | [AI-Native](13-AI-Native.md) | How — the manifest and agents |
| 14 | [Performance](14-Performance.md) | Quality |
| 15 | [Security](15-Security.md) | Quality |
| 16 | [Multi-Tenancy](16-Multi-Tenant.md) | Quality |
| 17 | [Plugin System](17-Plugin-System.md) | Extension |
| 18 | [Cloud-Native](18-Cloud-Native.md) | Operations |
| 19 | [SDK](19-SDK.md) | Developer experience |
| 20 | [Roadmap](20-Roadmap.md) | Delivery |
| 21 | [Quality Gates](21-Quality-Gates.md) | How a change is proven |
| 22 | [CLI](22-CLI.md) | `flowx manifest`, `graph`, `diff` |
| 23 | [Testing Strategy](23-Testing-Strategy.md) | How a FlowX application is tested, at each level |
| — | [ADRs](adr/README.md) | Decisions |
| — | [Diagnostics](diagnostics/README.md) | Every `FLOWX####` the compiler raises |
| — | [Benchmarks](benchmarks/README.md) | Budgets, baselines and their reports |

## Conventions

- **Diagrams** are standard Mermaid only — no ASCII art, no experimental C4
  syntax. They render on GitHub, GitLab, Azure DevOps and in IDE previews.
- **Quality goals** are measurable. "Fast" is not a goal; "p99 ≤ 5 µs" is.
- **Every sequence diagram involving an external call has its failure twin.**
- **Every architectural rule has a fitness function** in
  `tests/FlowX.Architecture.Tests`, **or is marked at the point it is stated as
  not yet enforced, with the phase that makes it enforceable** — see
  [05 §12](05-Architecture.md#12-architecture-fitness-functions) for the "Lives
  in" column and [21 §2.4](21-Quality-Gates.md#24-gates-named-here-but-not-yet-enforced)
  for the gates that are blocked rather than overlooked.
- **Every decision has an ADR** with an explicit "Revisit when".
- Diagnostic ids are `FLOWX1001`–`FLOWX1099`; each **that exists** has a title, a
  fix and a help URI, asserted by `EveryDiagnosticIsHelpful`. Ids are allocated
  when something raises them, so the range is not contiguous — see
  [diagnostics](diagnostics/README.md).

## How to read a claim in these documents

This set is normative and it is ahead of the code, which is deliberate: P0 is
complete and P1 is in progress out of ten phases in
[20-Roadmap](20-Roadmap.md). Where the two disagree the document says so at the
point of the claim, in one of three forms:

| Marker | Means |
|---|---|
| **Status** line naming a phase in a document header | the whole document is a specification for work not yet done |
| *"not enforced" / "does not exist"* next to a rule, test or diagnostic id | the rule is real; nothing checks it yet |
| A struck-through or corrected name | the document named something that never existed under that name |

**A claim with none of these markers is a claim about code that exists.** That is
the whole contract, and it is worth more than a document that reads as if
everything were finished.
