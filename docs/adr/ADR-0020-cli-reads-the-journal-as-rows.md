# ADR-0020: `flowx replay --mode inspect` reads the journal as rows, not as a published document

**Status:** Proposed — number claimed, record being written (WP-64)
**Date:** 2026-08-01
**Deciders:** architecture

This record is a claim on the number. It is committed before the decision is written so
that a second author reading "the next free number" out of the index below finds 0020
taken rather than free — the rule `PLAN.md` §2 states after four collisions, three of
which happened to authors who had followed it.

The decision it will carry: `flowx replay --mode inspect` needs the journal, and
`CliDependsOnNothingButTheManifest` is green today. [22-CLI §8](../22-CLI.md) sketches two
resolutions. This record picks one and argues it.
