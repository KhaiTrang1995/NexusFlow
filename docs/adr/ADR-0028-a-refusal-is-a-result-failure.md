# ADR-0028: A refusal is a `Result` failure carrying `ErrorCategory.Forbidden`, and the model's 401 collapses into it

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Repository owner · Platform architecture
**Amends:** [ADR-0007](ADR-0007-result-over-exceptions.md) ·
[15-Security §4](../15-Security.md#4-authorisation-model)

> **Claimed ahead of its content**, for [ADR-0026](ADR-0026-authorisation-runs-in-the-step-loop.md)'s
> reason.

## Context

To be recorded with the implementing commit.

## Decision

We will report a refused step as a `Result` failure with `ErrorCategory.Forbidden`, never as
an exception — and we will report an anonymous caller refused by `Authenticated` with the same
category, accepting that [15 §4](../15-Security.md#4-authorisation-model)'s `401` becomes a
`403` at the HTTP boundary, because `ErrorCategory` is a closed set that no new member may
join.

## Consequences

**Positive:** to be recorded with the implementing commit.

**Negative / accepted trade-offs:** to be recorded with the implementing commit.

**Revisit when:** to be recorded with the implementing commit.
