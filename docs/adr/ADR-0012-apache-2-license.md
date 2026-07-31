# ADR-0012: License FlowX under Apache-2.0

**Status:** Accepted
**Date:** 2026-07-30
**Deciders:** Platform architecture, Legal

## Context

FlowX is intended for adoption inside enterprises, including regulated ones, and
depends on an ecosystem of third-party plugins and capability packages. The
licence choice determines whether enterprise legal review is a formality or a
blocker, and whether contributors' patents are handled explicitly.

Options considered:

- **A. MIT.** Simple and permissive, but grants no explicit patent licence.
  Enterprise legal teams increasingly flag this for platform-level dependencies
  where contributors may hold relevant patents.
- **B. Apache-2.0.** Permissive, with an explicit patent grant and a patent
  retaliation clause; the de facto standard for infrastructure platforms
  (Kubernetes, Kafka, Terraform pre-BSL, OpenTelemetry). Chosen.
- **C. GPL/AGPL.** *Rejected:* copyleft obligations make embedding in commercial
  applications impractical, which contradicts the goal of being an application
  platform.
- **D. BSL / source-available with a delayed open licence.** *Rejected:* protects
  a commercial model but suppresses exactly the third-party plugin ecosystem the
  platform depends on (risk R8), and is a documented cause of community forks.
- **E. Dual licence (open core + commercial).** *Deferred:* a future FlowX Cloud
  or enterprise Studio may be separately licensed, but the runtime, compiler, SDK
  and first-party plugins stay Apache-2.0.

## Decision

We will license the FlowX runtime, compiler, SDK, CLI and all first-party plugins
under **Apache-2.0**, because the explicit patent grant is what makes enterprise
adoption and third-party plugin contribution frictionless — and ecosystem breadth
is the platform's principal long-term risk.

Constraint C6 follows: no dependency may carry a licence incompatible with
Apache-2.0 redistribution, verified by an automated licence scan in CI.

## Consequences

**Positive**
- Enterprise legal review is routine; Apache-2.0 is on virtually every
  pre-approved list.
- Contributors grant patent rights explicitly, protecting all users.
- Third parties can build and sell commercial plugins and capability packages
  without licence conflict — directly serving ecosystem growth.
- Consistent with the surrounding ecosystem (.NET is MIT, OpenTelemetry is
  Apache-2.0, Kubernetes is Apache-2.0).

**Negative / accepted trade-offs**
- **No protection against a cloud provider offering FlowX as a managed service**
  without contributing back. Accepted: adoption is worth more than that
  protection at this stage, and BSL-style protection would cost the plugin
  ecosystem.
- **Dependency choice is constrained** — no GPL/AGPL dependencies, ever, even
  transitively. *Enforced* since the licence gate landed:
  `DependencyLicencesAreCompatible` reads the resolved transitive graph out of
  `obj/project.assets.json` and checks every package against the register in
  [docs/DEPENDENCIES.md](../DEPENDENCIES.md). Until then the constraint held only
  because `FlowX.Abstractions` has zero dependencies by gate
  (`AbstractionsHasNoDependencies`) and the rest of the solution took only
  Microsoft and test-framework packages — an observation about that day's
  dependency set, not a control. Two things the observation had wrong, and the
  gate found: `SonarAnalyzer.CSharp` is under the SONAR Source-Available
  Licence rather than MIT, and `Microsoft.NETCore.Platforms` 1.1.0 under a
  proprietary Microsoft EULA. Neither is copyleft and neither ships — the gate
  admits them only because the resolved graph proves they contribute no
  assembly — but "only Microsoft and test-framework packages" was not the same
  statement as "only permissive licences", which is the distinction this
  consequence exists to draw.
- Contributors must sign a DCO (`Signed-off-by`), a small submission friction.
- Attribution and NOTICE-file obligations must be maintained in distributions.

**Revisit when:** a governance model requires a different licence (e.g. donation
to a foundation), or a managed-service free-rider materially harms the project's
sustainability — noting that a licence change after adoption is expensive and
has forked communities before.
