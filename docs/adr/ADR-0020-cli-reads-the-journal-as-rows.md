# ADR-0020: The CLI reads the journal as rows, and no journal document is published

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Platform architecture, Tooling
**Relates to:** [ADR-0005](ADR-0005-manifest-as-build-artifact.md) ·
[ADR-0015](ADR-0015-journal-schema-and-durable-execution.md) ·
[ADR-0016](ADR-0016-postgres-journal-adapter.md) ·
[ADR-0017](ADR-0017-manifest-v1-freeze-criteria.md)

> **This record answers one question and declines the next one.** It makes
> `flowx replay --mode inspect` legal. It does **not** make `simulate`, `resume` or `fork`
> legal, and [§5](#5-what-this-record-does-not-decide) says why that is a property of the
> decision rather than an omission from it.

> **The rule this record argues about has since been renamed, exactly as
> [§Owed work](#owed-work-named-so-it-is-not-lost) item 1 asked.** The fitness test in
> `tests/FlowX.Architecture.Tests/DependencyRuleTests.cs` is now
> `CliLinksNoFlowXAssembly`. Where the old name `CliDependsOnNothingButTheManifest` still
> appears below it is being *quoted* rather than used — §1 and §2 are an argument about that
> name, and rewriting them would delete the argument.

## Context

[PLAN WP-64](../../PLAN.md) states the conflict rather than leaving it to be discovered:
`CliLinksNoFlowXAssembly` — then still named `CliDependsOnNothingButTheManifest` — is a
green fitness function, `flowx replay` reads a journal, and one of the two has to give. [22-CLI §8](../22-CLI.md#8-flowx-replay-and-the-fitness-function--decided)
narrows it to two resolutions — publish a `flowx.journal.schema.json` beside the manifest's
and make `replay` a second *document* consumer, or amend the fitness function by ADR — and
says explicitly that choosing between them is not that page's call.

### 1. What the rule protects, separated from what its name says

The rule asserts one thing: `FlowX.Cli.csproj` has no `ProjectReference`. It counts links,
not inputs. Three properties have been resting on it, and they are not equally well
supported. (It was called `CliDependsOnNothingButTheManifest` when this was written, which
is the gap §2 is about; it is now named for what it asserts.)

| Property | Held by the assertion? |
|---|---|
| **(a)** The CLI links no FlowX assembly, so the manifest is demonstrably consumable from outside this repository — ADR-0005's evidence | **Yes.** This is exactly what it asserts |
| **(b)** The CLI cannot drift into a second runtime. A tool that could reference `FlowX.Runtime` could execute a step, and then `replay --mode simulate` becomes "call the engine" and there are two engines wearing one name | **Yes**, as a consequence of (a) |
| **(c)** The CLI is runnable against an artifact with no database | **No.** Nothing asserts this. It has been true by accident — no verb needed a store — and the rule's *name* is what made it look asserted |

(c) is the one this package changes, and it is the one that was never checked.

### 2. "Depends on nothing but the manifest" was never the invariant

Read literally, the rule's name is already false and has been since the CLI shipped.
`flowx manifest --assembly <path>` reads a **built assembly**, which is not a manifest;
`flowx diff` reads two files, neither of which is *the* manifest. What the tool has always
depended on is **published contracts rather than FlowX internals**, and "no
`ProjectReference`" is a faithful encoding of *that* sentence — not of the one in the name.

So the question is not whether to weaken a true rule. It is whether a journal read is a
published contract or an internal. That is a question about the journal, and it is the
only question here worth arguing.

### 3. What a journal schema commits you to that a manifest schema does not

The manifest can carry [ADR-0017](ADR-0017-manifest-v1-freeze-criteria.md)'s eight freeze
criteria because of what kind of object it is:

- **The manifest is a build artifact.** It is regenerated from source on every build. Every
  manifest that exists was produced by some compiler, and "freeze the schema at v1.0" means
  "stop changing the emitter" — a promise about *code*, enforceable by a gate on one repository.
- **A journal is a live store.** It holds rows written by every version that ever ran against
  it. A promise about a journal is a promise about **data at rest**, which no gate in this
  repository can enforce and no migration can retract.

And the shape is not settled. `plugins/FlowX.Postgres/Migrations` is on its **fourth**
migration; [ADR-0016](ADR-0016-postgres-journal-adapter.md) has amended the row shape twice
already — `json` rather than `jsonb`, and a lease with no foreign key to the instance it
precedes — and WP-63's timers are still to come. Publishing a versioned document over that
now would be freezing the half of the system that is still moving in order to unblock a
read-only verb.

**There is also a category error in the proposal as sketched.** A JSON Schema describes a
*document*. Nothing writes a journal document: there is no `flowx.journal.json` that any
process emits, and there is no plan to make one. `flowx.journal.schema.json` would be a
schema for a file that does not exist — shaped like the manifest's because the manifest's is
the artifact we happen to have, not because it fits what a journal is.

### 4. The contract the journal already publishes, in the only form it can take

A store's contract is its DDL. This one is already published, deliberately and for exactly
this kind of reader: `plugins/FlowX.Postgres/Migrations/*.sql` ships as SQL rather than as C#
string constants *"so that a DBA can read, review and apply them without a .NET toolchain"*.
That file is versioned by the migrator, reviewed as SQL, and readable by anything that speaks
PostgreSQL. It is a published contract in every sense the manifest is one, minus the
freeze plan it is too early to have.

And the join `inspect` needs has both halves already. A `flow_step` row carries
`(scope, step_id, attempt, capability_id, outcome, duration_ms, nondeterministic)`;
`step_id` means nothing without the plan; **the manifest publishes the plan** — steps, ids,
kinds and branches. So rendering an instance's history is a join between two published
contracts and needs no FlowX type on either side of it.

## Decision

**`flowx replay --mode inspect` reads the journal as rows over the published migration
contract, through a third-party ADO.NET driver, and joins them against the manifest.** It
links no FlowX assembly.

Three consequences of that, stated as decisions because each was a live option:

### 1. No `flowx.journal.schema.json` is published

Rejected for §3: it would freeze a live store shape that is still on migration 4, and it
would publish a schema for a document nothing writes. The journal's contract is its DDL,
and that is already published (§4).

### 2. `CliLinksNoFlowXAssembly` is **not** amended

It stays exactly as written and stays green. Nothing forces the amendment: the rule counts
`ProjectReference` items, a data read adds none, and `Npgsql` is a `PackageReference` — the
same kind of dependency `System.Reflection.MetadataLoadContext` already is.

Amending a green fitness function that nothing is actually blocking would trade real
evidence for no gain. What is true is that its **name** now overclaims further than it did,
because a second input has been added and the name mentions one. The name is left wrong for
one round rather than fixed here: it lives in `tests/FlowX.Architecture.Tests`, this package
does not own that project, and a rename is a mechanical change that should not ride in on a
feature branch. `CliLinksNoFlowXAssembly` is the name it should have, and
[the owed-work list](#owed-work-named-so-it-is-not-lost) records it as owed. *That rename
has since been made; the assertion is unchanged.*

### 3. The invariant the name was hiding gets its own assertion

Property (c) — *the CLI is runnable against an artifact with no database* — stops being
accidental the moment one verb needs a store, so it becomes an assertion instead:
`EveryVerbButReplayRunsWithNoStore`, in `tests/FlowX.Cli.Tests`. It runs `graph`, `manifest`,
`diff` and `verify` with no connection string in the environment and requires each to behave
exactly as it does today.

This is the compensating control for widening the CLI's inputs, and it is stronger than the
property it replaces, because it was previously enforced by nobody having tried.

## 5. What this record does *not* decide

[12-Observability §5](../12-Observability.md#5-flow-replay--the-differentiator) specifies four
replay modes. `inspect` is *"render history, no execution"* and is the only one that needs no
engine. **That is the whole reason this decision is available**, and it does not generalise:

| Mode | Needs | Reachable under this decision |
|---|---|---|
| `inspect` | rows + the plan | **Yes** — this record |
| `simulate` | re-execute with capabilities stubbed from the journal | **No** — needs the engine |
| `resume --from` | continue the real instance | **No** — needs the engine, a lease and a fence |
| `fork` | a new instance seeded from history | **No** — needs the engine and a journal *write* |

The other three put the CLI in the position property (b) exists to forbid, and they will
force this question again on much worse terms — an execution path, not a read. This record
takes no position on them beyond noting that "reading rows is fine" is not an argument that
reaches them, and that the honest options there are an out-of-process engine the CLI shells
to, or accepting that three of the four modes are not CLI verbs at all.

## Consequences

**Positive**

- ADR-0005's evidence is untouched. No fitness function was weakened to ship a verb, which is
  the outcome 22-CLI §8 argued for and the one this repository has paid for elsewhere by not
  getting it.
- Property (c) is now checked rather than assumed, so the CLI is *more* constrained after this
  change than before it, in the one dimension that actually moved.
- Reading rows rather than a curated document forces the verb to be honest about what is
  genuinely in the store. `flow_instance.input` is NULL on every row written so far, and a
  document consumer would have been free to render a tidy `{}`; a row reader has to say
  `unknown` because that is what it read. See [22-CLI §9](../22-CLI.md).
- A third party could write this verb. Nothing in it is privileged.

**Negative / accepted trade-offs**

- **The CLI now knows PostgreSQL column names.** `Replay/JournalReader.cs` names
  `flow_instance` and `flow_step` columns that `0001_initial_schema.sql` defines, and no
  compiler check connects the two. A column rename breaks `replay` at run time. The only
  thing that catches it is a test that runs the verb against a migrated schema — which is why
  `ReplayInspectTests` connects to a real database and why it must never be allowed to
  degrade into a skip. This is the cost of the decision, stated plainly rather than argued
  away.
- **`replay` is store-specific.** It works against PostgreSQL and nothing else, while every
  other journal consumer in this repository goes through `IFlowJournal` and works against any
  store. A second journal adapter makes this a real gap.
- The tool grows a database driver it loads for one verb out of five. `Npgsql`'s static
  ADO.NET surface is the trim- and AOT-safe half of the library — the same subset
  `FlowX.Postgres` restricts itself to — so constraint C2 is unaffected.
- `CliDependsOnNothingButTheManifest` is left carrying a name that is further from its
  assertion than it was. Recorded as owed work below rather than fixed quietly. *Since
  closed — see item 1.*

**Owed work, named so it is not lost**

1. ~~Rename `CliDependsOnNothingButTheManifest` to `CliLinksNoFlowXAssembly` in
   `tests/FlowX.Architecture.Tests/DependencyRuleTests.cs`, and update the six documents that
   cite it by name. Mechanical; owned by whoever next touches that project.~~ **Done.** The
   test is `CliLinksNoFlowXAssembly`; its assertion, its subject and its failure message's
   substance are unchanged, and the message now states that the rule counts project links
   rather than package inputs, so the gap that produced the collision warning in the plan
   cannot be re-read out of a failing gate. Struck rather than deleted, because the reason
   the rename was owed is the argument in §1 and §2 and a reader should be able to reach it.
2. `docs/22-CLI.md` §1's *"reads `flowx.manifest.json` and nothing else"* is widened by this
   record, and §8 is superseded by it. Both are edited in the commit that carries this ADR.

**Revisit when:** a second journal store adapter ships — at which point `replay` needs either
a reader per store or the published document this record declined to write, and the argument
in §3 has to be re-run against a shape that has stopped moving; **or** a `replay` mode has to
execute rather than render (§5), which reopens the dependency question on terms this record
does not address; **or** the manifest freeze ([ADR-0017](ADR-0017-manifest-v1-freeze-criteria.md))
closes with `replay` still reading columns that nothing versions, because a frozen contract
next to an unversioned one is the asymmetry that makes the second one look like a contract
when it is not.
