# ADR-0027: Identity reaches the engine on the invocation, as the `ClaimsPrincipal` the trigger already resolved

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Repository owner · Platform architecture
**Amends:** [ADR-0004](ADR-0004-universal-trigger-model.md) ·
[15-Security §2](../15-Security.md#2-trust-boundaries)

> **Claimed ahead of its content**, for [ADR-0026](ADR-0026-authorisation-runs-in-the-step-loop.md)'s
> reason.

## Context

To be recorded with the implementing commit.

## Decision

We will carry the caller's identity on `FlowInvocation` as a `ClaimsPrincipal`, resolved once
by the transport plugin from validated claims, and surface it on `FlowContext.Principal` —
which the runtime has answered `null` from since it was declared.

## Consequences

**Positive:** to be recorded with the implementing commit.

**Negative / accepted trade-offs:** to be recorded with the implementing commit.

**Revisit when:** to be recorded with the implementing commit.
