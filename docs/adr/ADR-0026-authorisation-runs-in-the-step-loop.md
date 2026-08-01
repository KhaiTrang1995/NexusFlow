# ADR-0026: The authorisation check runs in the step loop, reached through a plan flag and a resolved node field

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Repository owner · Platform architecture
**Amends:** [ADR-0023](ADR-0023-policy-stages-hook-through-the-plan.md) ·
[15-Security §4](../15-Security.md#4-authorisation-model)

> **Claimed ahead of its content**, per the rule
> [`docs/diagnostics/README.md`](../diagnostics/README.md#adding-a-diagnostic) states for
> diagnostic ids and [the index](README.md) states for numbers: take the number in its own
> commit, because two authors branching from one base cannot see each other's claim.

## Context

To be recorded with the implementing commit.

## Decision

We will run the authorisation check inside `FlowEngine`'s step loop, immediately before the
step's retry loop, reached through `ExecutionPlan.HasAuthorizedSteps` and a resolved
`StepNode.StepAuthorization` — never by reading the capability attribute at run time.

## Consequences

**Positive:** to be recorded with the implementing commit.

**Negative / accepted trade-offs:** to be recorded with the implementing commit.

**Revisit when:** to be recorded with the implementing commit.
