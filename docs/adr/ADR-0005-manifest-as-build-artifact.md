# ADR-0005: Emit the application graph as a first-class build artifact

**Status:** Accepted
**Date:** 2026-07-30
**Deciders:** Platform architecture

## Context

Architecture documentation drifts from implementation immediately and
permanently, because the two are maintained separately. Diagrams, OpenAPI
annotations, dashboards, alert rules, dependency inventories and onboarding docs
are all restatements of facts that already exist in the code — restated by hand,
and therefore wrong within weeks.

Quality goal Q3 requires 100 % of flows, capabilities, policies and events to be
statically knowable. Principle P6 requires machine-readable structure.

Options considered:

- **A. Generate documentation from XML doc comments.** *Rejected:* captures
  prose, not structure; cannot express policy composition, event topology or
  authorisation.
- **B. Reflect over the running application and expose an endpoint.** *Rejected:*
  requires the app to be running (useless in CI), and contradicts ADR-0002.
- **C. A separate model file maintained alongside the code** (Structurizr-style).
  *Rejected:* it is a second place to state the truth, so it drifts — the exact
  problem being solved.
- **D. The compiler emits a complete, versioned JSON manifest as a build
  output.** Chosen.

## Decision

We will emit `flowx.manifest.json` — a complete, versioned, machine-readable
description of every flow, capability, trigger, policy, event, error, side effect
and owner — as a **first-class build artifact**, on par with the assembly,
because a fact stated in exactly one place is the only kind of fact that stays
true.

The `ManifestIsComplete` fitness function fails the build when a declared flow or
capability is absent from the manifest, or when a step names one the manifest
never describes. *This sentence used to name `flowx verify --complete`, which is
not a CLI verb — the CLI has `graph`, `manifest` and `diff`
([22-CLI](../22-CLI.md)) — and the check that does exist is narrower than the
claim: it does not yet cover policies or events, for the reasons in
[05 §12](../05-Architecture.md#12-architecture-fitness-functions).*

## Consequences

**Positive**
- Documentation, diagrams, OpenAPI, AsyncAPI, alerts, dashboards, C4 models, test
  scaffolds, MCP tool descriptors and impact analysis are all *derived* — never
  maintained. *One derivation ships: `flowx graph`. The rest are **P8**; this
  decision is what makes them possible, not what delivers them.*
- Breaking-change detection (`flowx diff`) becomes a CI gate rather than a code
  review hope.
- AI tooling receives architecture as data, not as a repository to
  reverse-engineer ([13-AI-Native](../13-AI-Native.md)).
- Cross-service topology becomes possible: publish each service's manifest to a
  registry and the estate-wide graph assembles itself.

**Negative / accepted trade-offs**
- **The manifest schema is a public contract we must version and support
  forever.** Consumers pin a major version; an `extensions` field absorbs custom
  metadata so we are not pressured into breaking changes for one-off needs.
- **Build output grows** and must be published with the artifact; the manifest is
  meaningless if it is not shipped alongside what it describes.
- **Disclosure surface.** A manifest describes the application's operations. It
  is structure-only by construction (never data, never secrets), scanned in CI by
  `ManifestContainsNoSecrets`, and treated as internal-confidential — but it is a
  real artifact that must not be published carelessly.
- Completeness is an obligation: every new language feature in the DSL must also
  be representable in the manifest, or Q3 silently degrades.

**Revisit when:** never expected — this decision is foundational. If it is
reopened, the platform's AI-native and knowability claims must be withdrawn with it.
