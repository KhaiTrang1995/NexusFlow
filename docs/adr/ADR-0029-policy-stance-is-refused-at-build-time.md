# ADR-0029: `Authorization.Policy` is refused at build time rather than skipped at run time

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Repository owner · Platform architecture
**Amends:** [15-Security §4](../15-Security.md#4-authorisation-model)

> **Claimed ahead of its content**, for [ADR-0026](ADR-0026-authorisation-runs-in-the-step-loop.md)'s
> reason.

## Context

To be recorded with the implementing commit.

## Decision

We will raise `FLOWX1037` — an error — on a capability declaring
`Authorization = Authorization.Policy`, because the stance names an ASP.NET Core
authorisation policy and `FlowX.Runtime` may not reference ASP.NET Core. A stance the engine
cannot enforce is reported to its author at build time, not skipped in silence at run time.

## Consequences

**Positive:** to be recorded with the implementing commit.

**Negative / accepted trade-offs:** to be recorded with the implementing commit.

**Revisit when:** to be recorded with the implementing commit.
