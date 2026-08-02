# FLOWX1047 — Data subject is declared where the runtime cannot record it

> **Severity:** Error · **Category:** FlowX · **Since:** P6
> **Applies to:** a `[Subject]` marker on a flow's input or output contract.

> [!IMPORTANT]
> **The failure this prevents is silence.** Every other declaration mistake in this catalogue
> produces something an operator eventually notices — a flow that will not start, a trigger
> that fires nothing, a policy that refuses. This one produces a system that runs perfectly
> and cannot answer an erasure request, and the moment it is discovered is the moment somebody
> exercises the right, months of rows later, when the repair is a migration and a backfill
> **that cannot be done**: the identifiers the handles would have been computed from were
> redacted on the way in and are gone.

## What it means

`[Subject]` marks the member of a flow's input contract that names the person the record is
about. The runtime digests that member — inside `JournalPayload`, before the redaction pass and
without an accessor for the value — and writes the digest onto `flow_instance.subject_digest`.
That is what makes "everything this system holds about this person" an indexed lookup rather
than a scan of every document in the journal, and it is what lets a member be both `[Subject]`
and `[Sensitive]`: stored as `[redacted]`, and still erasable.

The mechanism needs four things at once. The marker must name **exactly one** member, of type
**`string`**, on the **input** contract, of a flow that keeps a **journal**. This rule reports
the four ways that fails.

| What was written | Why the handle would never be recorded |
|---|---|
| Two members marked on one contract | There is no defensible tie-break. Whichever the serialiser wrote first would become the handle, so the same flow rebuilt with a reordered contract would erase by a different member — and every row written before the reorder would be unreachable. |
| The marker on the flow's **output** | The handle is written onto the instance row **before the first step runs**. An output does not exist at that moment, and by the time it does the row it would have identified has already been written. |
| The marked member is not a `string` | A digest is computed over text. A numeric or structured identifier has more than one faithful rendering, and two renderings are two subjects — so which one is canonical belongs to the application that knows, not to the platform. |
| The flow is not `Durable` | It opens no instance row. There is nowhere for the handle to go and nothing for an erasure to find, while the contract reads to a reviewer as a flow whose records can be erased on request. |

## Example that triggers it

```csharp
public sealed record PatientIntake(
    [property: Subject] string NationalId,
    [property: Subject] string PatientNumber,   // ← FLOWX1047: two answers to "whose record is this"
    DateOnly DateOfBirth);
```

Each of the other three shapes triggers it the same way and with its own clause in the message:

```csharp
// ← FLOWX1047: an output does not exist when the handle is written
public sealed record IntakeResult([property: Subject] string PatientId);

// ← FLOWX1047: a digest is computed over text, and a number has more than one rendering
public sealed record PatientIntake([property: Subject] long NationalId);

// ← FLOWX1047: Ephemeral opens no instance row, so the handle has nowhere to go
[Flow("patient.intake", Profile = ExecutionProfile.Ephemeral)]
public sealed partial class PatientIntakeFlow : Flow<PatientIntake, IntakeResult> { … }
```

## How to fix it

Mark one `string` member of the input contract of a `Durable` flow.

```csharp
public sealed record PatientIntake(
    [property: Sensitive][property: Subject] string NationalId,   // ← one, string, on the input
    [property: Sensitive] string FullName,
    DateOnly DateOfBirth,
    CarePurpose Purpose,
    string ConsentReference);
```

A name is not an identifier — two patients share one, one patient changes theirs — so marking
`FullName` as a second subject would give an erasure a handle that matches the wrong people and
misses the right ones. That is the first row of the table.

## What it does *not* say

**Nothing about a flow that marks no subject.** Most flows have none: a funds transfer's
journal is about an account, not about a person the platform can identify. A rule that asked
every `Durable` flow to name a subject would be inventing a requirement no regulation makes.
Declaring the subject is the application's call; declaring one the runtime will ignore is what
this refuses.

## When to suppress

Never. A suppression buys a marked contract and a null column — the exact state the rule
exists to prevent, arriving through the mechanism meant to prevent it. Each of the four shapes
has a one-line fix that produces a flow this runtime serves today, and none of them is a
correct program written for a platform that lacks the feature: the platform has it.

## See also

* [ADR-0061](../adr/ADR-0061-a-subject-is-erased-by-digest-and-a-residency-is-a-refusal.md) — why
  the handle is a digest, what that is and is not worth, and what erasure does with it.
* [`samples/healthcare`](../../samples/healthcare/) — the flow this rule was written against,
  with a national identifier that is both the subject and a sensitive member.
* [FLOWX1040](FLOWX1040.md) — the other rule about `[Sensitive]` reaching a stored document,
  from the opposite direction.
