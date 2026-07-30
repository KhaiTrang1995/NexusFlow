# Documentation assets

Binary assets referenced by the documentation set. Three files are **required** —
the README and the architecture document reference them by path, and the CI
`docs` job fails while any is missing.

## Required files

| File | Source image | Referenced from | Content |
|---|---|---|---|
| `flowx-overview.png` | dark poster, "The Universal Application Platform" | [`README.md`](../../README.md) hero | Universal triggers → runtime platform → core abstractions → infrastructure connectors → deploy & operate → what you get |
| `flowx-platform-map.png` | dark, numbered sections 1–10 | [`docs/05-Architecture.md`](../05-Architecture.md) §2 | Core philosophy, unified trigger layer, runtime platform, core abstractions, connectors, platform capabilities, deployment, AI layer, end-to-end flow, quality attributes |
| `flowx-runtime-architecture.png` | light, "FlowX Runtime Architecture" | [`README.md`](../../README.md) architecture section, [`docs/05-Architecture.md`](../05-Architecture.md) §5 | Six numbered layers: front door → runtime core → core abstractions → infrastructure adapters → data & state → deployment, plus design principles, cross-cutting concerns and Studio tooling |

## Conventions

- **PNG**, at most 2 MB each. Anything larger is downscaled — a README that
  takes four seconds to paint is a README nobody reads.
- Width ≥ 1400 px so the dense infographics stay legible when GitHub scales them.
- Every reference in Markdown carries descriptive alt text. These diagrams carry
  real architectural content; a screen reader user must not lose it.
- Both a dark-background and a light-background variant exist in the set on
  purpose. GitHub renders README images against the reader's theme, so the light
  `flowx-runtime-architecture.png` is the one used inline in body sections, and
  the dark `flowx-overview.png` is used as a hero where full-bleed is intended.

## Why these are not Mermaid

The rest of this documentation set uses Mermaid, and [CONTRIBUTING](../../CONTRIBUTING.md)
requires it for anything a reader must be able to diff. These three are
deliberate exceptions: they are *communication* artifacts — a single dense
overview of the whole platform, designed for a landing page — not
*specification* artifacts. The specification lives in
[`docs/05-Architecture.md`](../05-Architecture.md) as diffable Mermaid, and it is
the source of truth. If a poster and the specification ever disagree, the
specification wins and the poster is regenerated.
