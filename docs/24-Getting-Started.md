# 24 — Getting Started

> **Status:** Verified against the code · **Audience:** an engineer who has never used FlowX
> **Answers:** how do I get one flow running, and what will the compiler stop me doing?

This is a path, not a reference. It starts with a project that runs and adds one thing at a
time: capabilities, the manifest, compensation, durability, an event, a test. Each step ends
where the next begins, and the last two sections are the ones a reference document would
leave out — the diagnostics you will hit in the first hour, and what the platform cannot do
yet.

**Every code block on this page is checked by a test.** `GettingStartedTests` reads this
file, compiles the C# with the FlowX generator and every FlowX analyzer, and asserts that
each block produces exactly the diagnostics the page says it does — including the blocks
that are *supposed* to fail. Blocks quoted from a file are compared against that file
verbatim. A block with no verification comment fails the test, so nothing can slip in
unchecked. [How to read this page's checks](#14-how-this-page-is-verified) explains the
markers if you are editing it.

---

## 1. Before you start

You need the .NET 10 SDK. You do **not** need a database, a broker or a container for
anything up to [§8](#8-going-durable); §8 needs PostgreSQL.

Nothing is published to nuget.org yet, so there is one pre-release step: build the FlowX
packages into a local feed.

<!-- verify: prose a shell transcript -->
```bash
templates/local-feed.sh                       # pre-release only
dotnet new install templates/FlowX.Templates
```

`local-feed.sh` packs seven packages into `.artifacts/local-feed` and registers it as a
NuGet source. The day the packages publish, that line disappears and nothing else changes —
the generated project already references FlowX as ordinary `PackageReference`s.
`templates/local-feed.sh --remove` undoes it. The details, including why the script evicts
those seven ids from the NuGet cache before packing, are in
[templates/README.md](../templates/README.md).

---

## 2. Your first flow

<!-- verify: prose a shell transcript -->
```bash
dotnet new flowx -o Ordering
cd Ordering
dotnet build -c Release
```

<!-- verify: prose real output of the commands above -->
```
The template "FlowX application" was created successfully.

  Determining projects to restore...
  Restored /home/you/Ordering/Ordering.csproj (in 444 ms).
  Ordering -> /home/you/Ordering/bin/Release/net10.0/Ordering.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)
```

Seven files: five of C#, the project file, and a README.

| File | What it holds |
|---|---|
| `Contracts.cs` | The records on the wire and between steps. No behaviour. |
| `Capabilities.cs` | Two capabilities and the one port they depend on. All the business rules. |
| `OpenTicketFlow.cs` | The control flow: order, and where recovery would go. |
| `Program.cs` | Composition. Registrations, and `MapFlowX()` for every declared endpoint. |
| `Infrastructure.cs` | The in-memory adapter and the JSON serialiser context. |

`dotnet run` it and post a ticket:

<!-- verify: prose a shell transcript -->
```bash
curl -X POST http://localhost:5000/api/v1/tickets \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: ticket-1' \
  -d '{"subject":"Printer on fire","reporter":"ops","contactPhone":"+44 7700 900000"}'
```

<!-- verify: prose the response body -->
```json
{"ticketId":"ticket-1","subject":"Printer on fire"}
```

Three things in that exchange are worth naming now, because the rest of the page builds on
them.

**The route is not in `Program.cs`.** It is on the flow, as `[HttpTrigger("POST",
"/api/v1/tickets")]`, and the compiler generates the registration `app.MapFlowX()` calls.
Change the route on the flow and the served address moves with it; there is nothing to keep
in step because nothing restates it.

**The `Idempotency-Key` header is required because the flow said so.** Omit it and you get
`400` with `http.idempotency_key_required` before any capability runs.

**A business failure is not an exception.** Post a blank subject and you get RFC 7807
problem details carrying the code the capability returned:

<!-- verify: prose the response body -->
```json
{"type":"https://flowx.dev/errors/ticket.subject_required","title":"The request is not valid",
 "status":400,"detail":"A ticket needs a subject.","instance":"/api/v1/tickets",
 "code":"ticket.subject_required","correlationId":"0HNNFDPRFSDTF:00000001"}
```

---

## 3. Capabilities and contracts

FlowX splits an application in two, and the split is the whole model.

A **capability** is one business operation: a class with one method, no knowledge of how it
was invoked, and a declaration of what it is. A **flow** is the order those operations run
in, and *only* the order — no business rules, no transport.

### The contracts

Records, and nothing else. They are the vocabulary the steps pass between them.

<!-- verify: preamble -->
```csharp
public sealed record OpenTicket(
    string Subject,
    string Reporter,
    [property: Sensitive] string ContactPhone);

public sealed record ValidatedTicket(string Subject, string Reporter);

public sealed record TicketOpened(string TicketId, string Subject);
```

`[Sensitive]` is not a comment. The member is listed under the contract's `sensitive` array
in the manifest, and it is redacted in every sink the platform owns: the generated HTTP
endpoint strips it out of error responses, and — for a `Durable` flow — it is `[redacted]` in
the stored input, in every step result, in the state-bag snapshot and in an emitted event
body. That last group is one mechanism, not four: a value reaches a store only as a
`JournalPayload`, which has no accessor for what it holds and one exit that redacts.
It is still *narrower than it sounds*, in the direction that matters: nothing stops your own
code writing the value somewhere the platform does not see.
[`SensitiveAttribute`](../src/FlowX.Abstractions/Capabilities/CapabilityAttribute.cs) says so
at the declaration.

### The error catalogue

Every failure the application can return, declared once so two capabilities cannot invent
two spellings of the same condition. Each code reaches the manifest and the `type` URI a
caller sees, which makes it part of the contract.

<!-- verify: preamble -->
```csharp
public static class TicketErrors
{
    public static Error SubjectRequired() =>
        new("ticket.subject_required", "A ticket needs a subject.", ErrorCategory.Validation);
}

public interface ITicketStore
{
    ValueTask SaveAsync(string ticketId, string subject, CancellationToken ct);

    ValueTask DeleteAsync(string ticketId, CancellationToken ct);
}
```

`ITicketStore` is a port. The capability depends on it rather than on a database, which is
what lets the whole flow be tested with no infrastructure in [§10](#10-testing-with-flowtesthost).

### A read

<!-- verify: preamble -->
```csharp
[Capability("ticket.validate", Version = "1.0.0",
    Authorization = Authorization.Authenticated,
    Idempotent = true)]
public sealed class ValidateTicket : ICapability<OpenTicket, ValidatedTicket>
{
    public ValueTask<Result<ValidatedTicket>> ExecuteAsync(
        OpenTicket input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        // An expected failure is a value, not an exception.
        return ValueTask.FromResult(string.IsNullOrWhiteSpace(input.Subject)
            ? Result.Fail<ValidatedTicket>(TicketErrors.SubjectRequired())
            : Result.Ok(new ValidatedTicket(input.Subject.Trim(), input.Reporter)));
    }
}
```

Four declarations on that attribute are contract, not documentation: the **id**, the
**version** of the contract, **who may call it**, and **whether calling it twice is safe**.
All four reach `flowx.manifest.json`. Two of them change what the compiler will let you
write — see [§11](#11-the-five-diagnostics-you-will-meet-first).

### A write

<!-- verify: preamble -->
```csharp
[Capability("ticket.record", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "ticket.write",
    Idempotent = true,
    SideEffects = ["ticket-store"])]
public sealed class RecordTicket : ICapability<ValidatedTicket, TicketOpened>
{
    private readonly ITicketStore _store;

    public RecordTicket(ITicketStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public async ValueTask<Result<TicketOpened>> ExecuteAsync(
        ValidatedTicket input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        // The identity comes from the context. A new Guid would make the same request
        // produce a different ticket on every retry.
        await _store.SaveAsync(ctx.IdempotencyKey, input.Subject, ct).ConfigureAwait(false);

        return new TicketOpened(ctx.IdempotencyKey, input.Subject);
    }
}
```

`Idempotent = true` is a **promise the implementation keeps**, here by keying the write on
`ctx.IdempotencyKey`. Declaring it falsely is how a retry becomes a duplicate; declaring it
honestly is what lets you attach a retry policy at all.

`SideEffects` names the external things this touches. It drives blast-radius analysis and
one build error (`FLOWX1018`: caching a capability with side effects).

### The flow

<!-- verify: compiles -->
```csharp
[Flow("ticket.open", Version = "1.0.0", Profile = ExecutionProfile.Ephemeral, Owner = "support")]
[FlowDeadline("PT10S")]
[HttpTrigger("POST", "/api/v1/tickets", Idempotent = true)]
public sealed partial class OpenTicketFlow : Flow<OpenTicket, TicketOpened>
{
    protected override void Define(IFlowBuilder<OpenTicket, TicketOpened> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<ValidateTicket>()
            .Step<RecordTicket>()
            .Return(ctx => ctx.Get<TicketOpened>());
    }
}
```

`partial`, because the compiled plan, the step dispatcher and the projection are generated
into the other half of the class at build time. They land on disk under `obj/generated` as
ordinary C# with `#line` directives back into your file — set a breakpoint on a step and it
stops in `Capabilities.cs`.

Steps bind **by type**: `RecordTicket` consumes a `ValidatedTicket` because `ValidateTicket`
produced one. Reorder them and the build fails with `FLOWX1020` — a step consuming a
contract no earlier step produces — rather than throwing on the first request.

### Wiring it up

`Program.cs` is the composition root, and it is deliberately the only place that names a
service lifetime. This is the template's, verbatim:

<!-- verify: excerpt templates/FlowX.Templates/content/FlowX.Web/Program.cs -->
```csharp
builder.Services.AddFlowX(options => options.ApplicationName = "Ordering");

// Infrastructure. In memory here; the capabilities do not know or care.
builder.Services.AddSingleton<ITicketStore, InMemoryTicketStore>();
```

<!-- verify: excerpt templates/FlowX.Templates/content/FlowX.Web/Program.cs -->
```csharp
builder.Services.AddSingleton<ValidateTicket>();
builder.Services.AddSingleton<RecordTicket>();
builder.Services.AddSingleton<OpenTicketFlow.Dispatcher>();
```

The generated `Dispatcher` takes each capability as a constructor parameter, so a missing
registration is a start-up failure that names the type — not a null reference on the first
request. There is no assembly scan.

`app.MapFlowX()` registers every endpoint the flows declared. It names no method and no
route, and `templates/verify.sh` asserts that it does not.

---

## 4. The manifest

Every build produces a manifest: the structure of the application as declared, with no
behaviour in it. It comes from the same reading of your attributes that produced the plan,
so it cannot describe a different program from the one that shipped
([ADR-0005](adr/ADR-0005-manifest-as-build-artifact.md)).

**It is not a file on disk.** The compiler emits it as a generated type,
`FlowX.Generated.FlowXManifest`, inside the assembly — so it travels with the binary and
cannot be edited apart from it. The CLI writes it out:

<!-- verify: prose a shell transcript -->
```bash
dotnet run --project src/FlowX.Cli -- manifest \
  --assembly bin/Release/net10.0/Ordering.dll \
  --output flowx.manifest.json
```

This is what the project from [§2](#2-your-first-flow) produces, abridged:

<!-- verify: prose the generated manifest, abridged -->
```json
{
  "schemaVersion": "0.1.0",
  "application": { "name": "Ordering", "version": "1.0.0" },
  "flows": [
    {
      "id": "ticket.open",
      "version": "1.0.0",
      "profile": "Ephemeral",
      "deadline": "PT10S",
      "input": { "type": "Ordering.OpenTicket", "sensitive": [ "ContactPhone" ] },
      "output": { "type": "Ordering.TicketOpened" },
      "triggers": [
        { "kind": "Http", "method": "POST", "route": "/api/v1/tickets", "idempotent": true }
      ],
      "steps": [
        { "id": 0, "kind": "Capability", "capability": "ticket.validate@1.0.0" },
        { "id": 1, "kind": "Capability", "capability": "ticket.record@1.0.0" }
      ],
      "emits": [],
      "errors": [ "ticket.subject_required" ],
      "source": "OpenTicketFlow.cs:29"
    }
  ],
  "capabilities": [
    {
      "id": "ticket.record",
      "version": "1.0.0",
      "input": "Ordering.ValidatedTicket",
      "output": "Ordering.TicketOpened",
      "authorization": { "mode": "Permission", "value": "ticket.write" },
      "idempotent": true,
      "sideEffects": [ "ticket-store" ],
      "errors": []
    }
  ],
  "events": []
}
```

Three things it is for:

- **`flowx diff`** compares two manifests and fails a build on a breaking change — a
  tightened authorisation stance, a renamed permission, a removed error code. See
  [22-CLI](22-CLI.md).
- **`flowx graph`** renders the flow as Mermaid.
- **An agent** reads it as a tool catalogue, which is why `sideEffects` and `authorization`
  are in it: a tool descriptor that cannot say what a call does is not one you let an agent
  invoke.

The manifest publishes **declared** facts. `"profile": "Ephemeral"` is what the attribute
said, not a claim about what the runtime did with it — a distinction that matters, and that
[§12](#12-what-flowx-cannot-do-yet) returns to.

---

## 5. What the compiler does with all this

Worth pausing on, because it explains most of the rest of the page.

There is no reflection at run time and no assembly scan. At build time
`FlowX.Compiler` — an **analyzer package**, not a library you ship — reads the `Define`
chain and emits:

| Emitted | Where | Why you care |
|---|---|---|
| `OpenTicketFlow.Plan` | `obj/generated` | a flat `StepNode[]`; the engine's only input |
| `OpenTicketFlow.Dispatcher` | `obj/generated` | calls your capabilities; takes them as constructor parameters |
| `FlowXEndpoints.g.cs` | `obj/generated` | what `app.MapFlowX()` registers; only exists if you reference `FlowX.Http` |
| `FlowX.Generated.FlowXManifest` | `obj/generated`, then the assembly | the declared contract; `flowx manifest` writes it out |

Because it is all generated, the application publishes with NativeAOT — which the template
turns on from the first build so the trim and AOT analyzers report a reflection call while
it is still one line.

And because the compiler has read your flow, it can refuse things. That is the subject of
[§11](#11-the-five-diagnostics-you-will-meet-first).

---

## 6. Adding a step

Adding a capability is: a class with one `ExecuteAsync`, a `[Capability]` attribute, one
registration in `Program.cs`, one `.Step<T>()` in the flow.

<!-- verify: preamble -->
```csharp
public sealed record NotificationSent(string TicketId);

[Capability("ticket.notify", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true,
    SideEffects = ["email"])]
public sealed class NotifyReporter : ICapability<TicketOpened, NotificationSent>
{
    public ValueTask<Result<NotificationSent>> ExecuteAsync(
        TicketOpened input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        return ValueTask.FromResult(Result.Ok(new NotificationSent(input.TicketId)));
    }
}
```

The moment a third step exists, a question appears that two steps did not raise: the
notification can fail *after* the ticket was written. That is the next section.

---

## 7. Adding compensation

FlowX's answer to a step that fails after an earlier step took an effect is a **saga**: each
compensable step declares its business inverse, and the engine unwinds the completed ones in
reverse order.

<!-- verify: preamble -->
```csharp
[Capability("ticket.delete", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true,
    SideEffects = ["ticket-store"])]
public sealed class DeleteTicket : ICapability<ValidatedTicket, TicketOpened>
{
    private readonly ITicketStore _store;

    public DeleteTicket(ITicketStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public async ValueTask<Result<TicketOpened>> ExecuteAsync(
        ValidatedTicket input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        await _store.DeleteAsync(ctx.IdempotencyKey, ct).ConfigureAwait(false);

        return new TicketOpened(ctx.IdempotencyKey, input.Subject);
    }
}
```

A compensation takes **the input of the step it undoes**, because that is the value it has
to reverse. It is dispatched by the engine, never by you.

Now declare it — and read what the compiler says:

<!-- verify: reports FLOWX1012 -->
```csharp
[Flow("ticket.open", Version = "1.0.0", Owner = "support")]
[FlowDeadline("PT10S")]
public sealed partial class OpenTicketFlow : Flow<OpenTicket, TicketOpened>
{
    protected override void Define(IFlowBuilder<OpenTicket, TicketOpened> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<ValidateTicket>()
            .Step<RecordTicket>().CompensateWith<DeleteTicket>()
            .Step<NotifyReporter>()
            .Return(ctx => ctx.Get<TicketOpened>());
    }
}
```

**`FLOWX1012` — compensation on a flow that is not durable.** The unwind stack is a field of
an in-memory context. If `NotifyReporter` fails, the delete runs. If the *process* dies
between the write and the notification, the instance is gone, the unwind never runs, and
nothing anywhere records that a ticket was supposed to be removed. The first evidence is
data that does not add up.

The fix is [§8](#8-going-durable). Staying ephemeral is a legitimate answer for an effect
that is cheap to leak or that something else reclaims, and
[FLOWX1012](diagnostics/FLOWX1012.md) says how to record that decision — but it is a
decision, not a default.

---

## 8. Going `Durable`

Three parts, and **the attribute on its own makes things worse**, so change all three in the
same commit.

### Part one — the flow

<!-- verify: reports FLOWX1006 -->
```csharp
[Flow("ticket.open", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "support")]
[FlowDeadline("PT10S")]
public sealed partial class OpenTicketFlow : Flow<OpenTicket, TicketOpened>
{
    protected override void Define(IFlowBuilder<OpenTicket, TicketOpened> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<ValidateTicket>()
            .Step<RecordTicket>().CompensateWith<DeleteTicket>()
            .Step<NotifyReporter>()
            .Return(ctx => ctx.Get<TicketOpened>());
    }
}
```

`FLOWX1012` is gone, and something else has appeared in its place.

**`FLOWX1006` — a state-bag contract is outside every generated JSON context.** A `Durable`
flow journals what each step produced and the flow's state bag as it stands after it, and a
value reaches the journal only through `JournalPayload`, whose `Of<T>` requires the
source-generated `JsonTypeInfo<T>` — there is no overload that reflects over a type, which is
what keeps the write path trim- and AOT-safe. So every contract the bag holds needs the same
`[JsonSerializable]` declaration an emitted event needs, and the compiler names the ones it
cannot find. That is part two.

The block above reports it because these snippets are compiled on their own, with no
serialiser context anywhere in the compilation. In a real project the context is the one the
template already generates.

### Part two — the contracts

Everything in the state bag: the flow's input, which the engine puts there before the first
step, and the output of every capability step. `samples/banking` is the shape to copy —
`ExecuteTransfer` and `TransferResult` are on the wire, `TransferCompleted` is the event, and
the six below are the step results the journal has to write:

<!-- verify: excerpt samples/banking/Infrastructure.cs -->
```csharp
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ExecuteTransfer))]
[JsonSerializable(typeof(TransferResult))]
[JsonSerializable(typeof(TransferCompleted))]
[JsonSerializable(typeof(ValidatedTransfer))]
[JsonSerializable(typeof(ScreeningDecision))]
[JsonSerializable(typeof(CorrespondentRoute))]
[JsonSerializable(typeof(DebitPosted))]
[JsonSerializable(typeof(CreditPosted))]
[JsonSerializable(typeof(Settlement))]
internal sealed partial class BankingJsonContext : JsonSerializerContext;
```

For the ticket flow that is `OpenTicket`, `ValidatedTicket`, `TicketOpened` and
`NotificationSent` on `AppJsonContext`. The list is not maintained by reading the flow: add
the attribute the compiler names, rebuild, repeat until it stops naming one.

**This is what makes a resume work rather than merely happen.** Without it the journal records
which steps ran and nothing about what they produced, so a second node re-enters the loop with
an empty state bag and the first step past the frontier that binds an earlier step's output
fails. With it, the snapshot committed alongside each step is restored before the resumed loop
starts.

**A `[Sensitive]` member does not come back.** It is stored as `[redacted]`, because the
journal never held anything else: the writer hands values to `JournalPayload`, whose only
exit replaces every declared member by name at every depth. A flow that needs a secret after
a resume has to fetch it, not remember it.

### Part three — the host

A `Durable` flow on a host that registered no journal is **refused before its first step**,
with the error `flow.durability_not_configured`. Not run ephemerally — refused. Running it
on the ephemeral engine would be the silent gap the profile exists to close, one layer down
and with no diagnostic left to raise it.

So the host has to register a journal and a lease store. Registering them is what opts a
process into durability; there is no flag:

<!-- verify: excerpt tests/FlowX.Postgres.Tests/PostgresRecoveryHostTests.cs -->
```csharp
        services.AddFlowX(options =>
        {
            options.ApplicationName = "Sample.App";
            options.NodeName = LiveNode;
        });

        services.AddFlowXPostgres(
            "Host=localhost;Database=postgres;Username=postgres",
            new PostgresJournalOptions { RegisterRecoveryIndex = registerIndex });
```

`AddFlowXPostgres` registers three service types:

| Service | What it does | Optional? |
|---|---|---|
| `IFlowJournal` | commits one row per `(instance, scope, step, attempt)` | no |
| `ILeaseStore` | exclusive ownership plus a fencing token every commit carries | no |
| `IRecoveryIndex` | finds instances whose lease expired, so another node can finish them | yes — a host without it runs durable flows and never sweeps |

**Both of the first two, or neither.** A journal with no lease store would write under a
token nothing issued; a lease store with no journal would fence nothing. `AddFlowX` resolves
the pair together and treats "one of them" as "none"
([`FlowXServiceCollectionExtensions`](../src/FlowX.Hosting/FlowXServiceCollectionExtensions.cs)).

It does **not** migrate. Applying DDL as a side effect of building a container makes every
replica of a rolling update race to migrate at start-up; call `PostgresMigrator.MigrateAsync`
from wherever your deployment runs schema changes.

### What `Durable` buys, exactly

One journal row per step boundary. When a node dies, the recovery scan finds the instance,
another node takes the lease and re-enters the same step loop; the loop skips the steps the
journal shows completed, and **as it skips a completed compensable step it puts that step
back on the unwind stack**. A failure after the resume unwinds work a previous node did.

### And what it does not

Two limits that are real today:

1. **The unwind is not itself journaled.** A process that dies halfway through a
   compensation still loses the rest of it. `Durable` shrinks the exposure window from "the
   whole flow" to "the unwind"; it does not close it.
2. **A resumed parent does not rebuild a composed sub-flow's compensations.** The engine
   skips the entry rather than approximating the child's stack, because a compensation stack
   that is silently short is the failure a saga exists to prevent.

Neither is a reason to stay ephemeral. Both are reasons not to read `Durable` as "solved".

---

## 9. Emitting an event

`.Emit<TEvent>(…)` publishes a domain event through a transactional outbox: the row is
written in the same transaction as the step, so it is never lost and never published before
the step is durable.

Add one to the durable flow, and the compiler has something to say:

<!-- verify: reports FLOWX1006 FLOWX1024 -->
```csharp
public sealed record TicketRaised(string TicketId, string Reporter);

[Flow("ticket.open", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "support")]
[FlowDeadline("PT10S")]
public sealed partial class OpenTicketFlow : Flow<OpenTicket, TicketOpened>
{
    protected override void Define(IFlowBuilder<OpenTicket, TicketOpened> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<ValidateTicket>()
            .Step<RecordTicket>().CompensateWith<DeleteTicket>()
            .Emit<TicketRaised>(ctx => new TicketRaised(
                ctx.Get<TicketOpened>().TicketId,
                ctx.Input.Reporter))
            .Return(ctx => ctx.Get<TicketOpened>());
    }
}
```

`FLOWX1006` is [§8](#part-two--the-contracts)'s and is here for the same reason it was there:
these blocks compile with no serialiser context in the compilation. `FLOWX1024` is the new
one, and it is the same requirement reaching a different payload.

**`FLOWX1024` — the emit step stages no event.** The event body is written through a
source-generated `JsonSerializerContext`; `JournalPayload.Of` takes a `JsonTypeInfo<T>` and
has no overload that reflects over a type, which is what keeps the write path trim- and
AOT-safe. No context declares `TicketRaised`, so nothing could write the body.

The fix is one attribute on the context the template already generates:

<!-- verify: excerpt templates/FlowX.Templates/content/FlowX.Web/Infrastructure.cs -->
```csharp
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OpenTicket))]
[JsonSerializable(typeof(TicketOpened))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
```

Add `[JsonSerializable(typeof(TicketRaised))]` and the diagnostic goes. **Exactly one
context, not at least one**: two contexts declaring the same contract is the same answer as
none, because picking one would make the event's wire shape depend on file order.

The other trigger for `FLOWX1024` is the profile. An `Ephemeral` flow keeps no journal, so
there is no transaction for the event to be part of, and the diagnostic's message names
`Profile = Durable` as the fix. Both cases are asserted in `EmitStagingTests` —
`AnEmitWhoseContractNoContextDeclaresIsReportedAndSaysSo`,
`AnEphemeralEmitIsReportedAndSaysWhichProfileWouldPublishIt`, and the silent case,
`AStageableEmitRaisesNothing`.

> **One honest residue, now smaller.** *This box said "published" means "handed to an
> `IEventPublisher`" because the repository shipped no broker plugin. It ships one:*
> `AddFlowXRedisStreams` registers `RedisStreamEventPublisher`, which appends each event to
> its `partition_key`'s own Redis stream, and `PublisherConformance` holds it and the
> recording double to one contract. What is left is the list of brokers: there is no Kafka,
> RabbitMQ, Service Bus, Event Hubs or SNS publisher — see
> [§12 below](#12-what-flowx-cannot-do-yet) and
> [ADR-0018](adr/ADR-0018-outbox-publication-and-ordering.md).

---

## 10. Testing with `FlowTestHost`

Three levels, and picking the wrong one is the most common waste of effort
([23-Testing-Strategy](23-Testing-Strategy.md)).

| Question | Level | What you need |
|---|---|---|
| Does this business rule hold? | capability | construct the class, call the method, `TestCapabilityContext` |
| Does the flow run the steps in the right order, and unwind correctly? | flow | `FlowTestHost` |
| Does the endpoint return the right status code and media type? | endpoint | a real server |

`FlowTestHost` runs **the real runtime**: the real engine, the real generated plan, the real
pooled context, the real compensation stack. It contributes exactly two things — a
dispatcher that can substitute a capability by id, and a trace. A test that passes here is a
statement about the runtime, not about a simulation of it.

This is the reference sample's flow test, verbatim — a declined payment must release the
reservation:

<!-- verify: excerpt tests/Ecommerce.Tests/PlaceOrderFlowTests.cs -->
```csharp
        var host = FlowTestHost
            .For(
                PlaceOrderFlow.Plan,
                new PlaceOrderFlow.Dispatcher(
                    capturePayment: new CapturePayment(new AlwaysApprovesGateway()),
                    releaseInventory: new ReleaseInventory(inventory),
                    reserveInventory: new ReserveInventory(inventory),
                    validateOrder: new ValidateOrder()))

            // The gateway above always approves, so a substitution that did not take
            // effect would leave this flow succeeding — which is what makes the
            // assertions below statements about the substitution as well as the saga.
            .Substitute("payment.capture", OrderErrors.PaymentDeclined("insufficient funds"))
            .WithInvocation(new FlowInvocation("corr-5", "key-5"))
            .Build();
```

<!-- verify: excerpt tests/Ecommerce.Tests/PlaceOrderFlowTests.cs -->
```csharp
        run.Error!.Code.ShouldBe("payment.declined");
        run.Compensation.ShouldBe(CompensationOutcome.Succeeded);
```

Four things to copy from that:

- **The plan and the dispatcher are passed in by name.** There is no `For<TFlow>()`:
  discovering the generated members would need reflection, which the AOT constraint forbids.
  One line, and the host stays reflection-free.
- **Substitution is by capability id**, `"payment.capture"` — the identity the plan carries,
  not the class name. It survives the capability being renamed or replaced.
- **The substituted capability is one the happy path would have passed.** If the
  substitution silently failed to apply, the test would go green for the wrong reason.
- **`run.Trace.Executed` and `run.Trace.Compensated`** are what the endpoint test could not
  see: an endpoint returns one status code whether the reservation was released before,
  after, or instead of anything else.

What `FlowTestHost` does **not** do: mock verification, auto-wiring from a container, a
journal, `AwaitSignal` or `AwaitCompletion`.

---

## 11. The six diagnostics you will meet first

The compiler is the framework teaching you. These six are the ones a newcomer hits in the
first hour, in roughly that order. Every one has a page under
[docs/diagnostics](diagnostics/README.md) arguing the case; this is the one-line version.

| Id | Fires when | Fix |
|---|---|---|
| [FLOWX1010](diagnostics/FLOWX1010.md) | a capability declares no `Authorization` | add a stance |
| [FLOWX1030](diagnostics/FLOWX1030.md) | `Authorization.Permission` names no permission | add `Permission = "…"` |
| [FLOWX1014](diagnostics/FLOWX1014.md) | a retry policy on a capability that is not `Idempotent` | make it idempotent and declare it, or handle the failure in the flow |
| [FLOWX1012](diagnostics/FLOWX1012.md) | `.CompensateWith<T>()` on a flow that is not `Durable` | `Profile = ExecutionProfile.Durable` **and** register a journal |
| [FLOWX1024](diagnostics/FLOWX1024.md) | `.Emit<T>()` whose event no `JsonSerializerContext` declares | `[JsonSerializable(typeof(T))]` on one context |
| [FLOWX1006](diagnostics/FLOWX1006.md) | a `Durable` flow's state bag holds a contract no `JsonSerializerContext` declares | the same attribute, for the contract the message names — see [§8](#part-two--the-contracts) |

### FLOWX1010 — a capability must declare an authorisation stance

There is no permissive default anywhere in FlowX. Authorisation attaches to the business
operation rather than to a route, so it holds identically over HTTP, over a bus and from an
agent — but only if it is declared.

<!-- verify: reports CS9035 FLOWX1010 -->
```csharp
[Capability("ticket.archive", Version = "1.0.0")]     // no Authorization
public sealed class ArchiveTicket : ICapability<TicketOpened, TicketOpened>
{
    public ValueTask<Result<TicketOpened>> ExecuteAsync(
        TicketOpened input, CapabilityContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(Result.Ok(input));
}

[Flow("ticket.archive", Version = "1.0.0", Owner = "support")]
[FlowDeadline("PT10S")]
public sealed partial class ArchiveTicketFlow : Flow<TicketOpened, TicketOpened>
{
    protected override void Define(IFlowBuilder<TicketOpened, TicketOpened> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow.Step<ArchiveTicket>().Return(ctx => ctx.Get<TicketOpened>());
    }
}
```

**You will see `CS9035` first**, and that is worth knowing in advance: `Authorization` is a
`required` member, so the C# compiler objects before FlowX gets a chance to. `FLOWX1010`
then fires at the flow's `.Step<ArchiveTicket>()` rather than at the attribute — a known
rough edge, recorded in `CHECKLIST.md`, not a subtlety you are missing.

**The one-line fix:** `Authorization = Authorization.Authenticated` — or `Permission`,
`Internal`, or `Public` with an `[ApprovedBy]` naming the reviewer. The IDE quick action
offers only `Authenticated` and `Internal`; `Public` is deliberately absent, because
clearing a security error with one keystroke is the outcome the rule exists to prevent.

### FLOWX1030 — a `Permission` stance with no permission named

`Authorization.Permission` is a claim that some *named* grant is required. Without the name
it publishes `"authorization": { "mode": "Permission" }` — a manifest entry that reads as
enforced and names nothing to enforce. It also kills half a security gate: `flowx diff`
reports a **changed** permission as breaking, and a stance with no value has nothing to
change.

<!-- verify: reports FLOWX1030 -->
```csharp
[Capability("ticket.close", Version = "1.0.0",
    Authorization = Authorization.Permission)]        // required — which permission?
public sealed class CloseTicket : ICapability<TicketOpened, TicketOpened>
{
    public ValueTask<Result<TicketOpened>> ExecuteAsync(
        TicketOpened input, CapabilityContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(Result.Ok(input));
}

[Flow("ticket.close", Version = "1.0.0", Owner = "support")]
[FlowDeadline("PT10S")]
public sealed partial class CloseTicketFlow : Flow<TicketOpened, TicketOpened>
{
    protected override void Define(IFlowBuilder<TicketOpened, TicketOpened> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow.Step<CloseTicket>().Return(ctx => ctx.Get<TicketOpened>());
    }
}
```

**The one-line fix:** `Permission = "ticket.write"` — or, if no named grant is really
required, `Authorization.Authenticated`, which is complete in itself. `Authorization.Policy`
takes `Policy = "…"`; the message names whichever property the declared mode reads.

### FLOWX1014 — no retry on a non-idempotent capability

Retrying a non-idempotent operation duplicates its effect. For a payment capture that is a
duplicate charge, and this is the single most expensive bug the platform prevents
structurally.

<!-- verify: reports FLOWX1014 -->
```csharp
[Capability("ticket.charge", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "billing.write",
    SideEffects = ["billing"])]                       // Idempotent defaults to false
public sealed class ChargeForTicket : ICapability<TicketOpened, TicketOpened>
{
    public ValueTask<Result<TicketOpened>> ExecuteAsync(
        TicketOpened input, CapabilityContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(Result.Ok(input));
}

public static class Policies
{
    public static readonly PolicySet Billing = PolicySet.Named("billing").Retry(attempts: 3);
}

[Flow("ticket.charge", Version = "1.0.0", Owner = "support")]
[FlowDeadline("PT10S")]
public sealed partial class ChargeFlow : Flow<TicketOpened, TicketOpened>
{
    protected override void Define(IFlowBuilder<TicketOpened, TicketOpened> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<ChargeForTicket>().WithPolicy(Policies.Billing)
            .Return(ctx => ctx.Get<TicketOpened>());
    }
}
```

**The one-line fix:** `Idempotent = true` on the capability — **and mean it**. Pass
`ctx.IdempotencyKey` downstream so the provider deduplicates, then the declaration is
honest. If you cannot, delete the retry and handle the failure in the flow. There is no
suppression for this one.

### FLOWX1012 — compensation on an ephemeral flow

Covered in [§7](#7-adding-compensation). One line: an ephemeral unwind stack lives in one
process's memory, so a crash between the effect and the failure loses the undo silently.

**The one-line fix:** `Profile = ExecutionProfile.Durable` — *and* register a journal and a
lease store on the host, or you have traded a warning for a start-up refusal. That is also
why there is no quick action: a one-click fix would produce a flow that stops working.

### FLOWX1024 — an emitted event needs a `JsonSerializerContext`

Covered in [§9](#9-emitting-an-event).

**The one-line fix:** `[JsonSerializable(typeof(TicketRaised))]` on exactly one context — or,
if the flow is `Ephemeral`, `Profile = ExecutionProfile.Durable`, because an ephemeral
execution has no transaction to stage the row in.

### Suppressing any of them

`FLOWX1012` and `FLOWX1024` are warnings; the rest are errors. The template — and this
repository — build with `TreatWarningsAsErrors`, so in practice all five stop a build.

A suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry, or the build
fails on the suppression itself ([21 §6](21-Quality-Gates.md#6-technical-debt-policy)). The
reference sample carries two, both in `samples/ecommerce/PlaceOrderFlow.cs` and both with
the argument inline: `FLOWX1012`, because an ephemeral saga is the deliberate choice for a
sample that must run without a database, and `FLOWX1024`, which follows from it — an
ephemeral flow has no transaction to stage an event in. The second carries a
`FLOWX-DEBT` id, owner and expiry; the first is argued as a decision rather than debt, on
the grounds `docs/DEBT.md` draws. They are left visible on purpose: this is the file people
copy.

---

## 12. What FlowX cannot do yet

Read this before you plan around FlowX rather than after you hit it. None of it is hidden —
each item is stated where it is relevant — but a newcomer who discovers it by walking into
it will discount everything else on this page.

**One transport.** `plugins/` contains `FlowX.Http` and nothing else. `KafkaTriggerAttribute`,
`CronTriggerAttribute`, `StreamTriggerAttribute` and `AgentTriggerAttribute` all compile and
all reach the manifest's `triggers` block — and **nothing serves them.** Only
`[HttpTrigger]` produces a registration for `app.MapFlowX()`. This block compiles, and this
page's own test asserts that it raises no diagnostic at all:

<!-- verify: compiles -->
```csharp
[Flow("ticket.import", Version = "1.0.0", Owner = "support")]
[FlowDeadline("PT10S")]
[KafkaTrigger("tickets.raised", Group = "ticket-import")]
public sealed partial class ImportTicketFlow : Flow<OpenTicket, TicketOpened>
{
    protected override void Define(IFlowBuilder<OpenTicket, TicketOpened> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<ValidateTicket>()
            .Step<RecordTicket>()
            .Return(ctx => ctx.Get<TicketOpened>());
    }
}
```

It publishes a Kafka trigger in its declared contract and will never receive a message. No
plugin implements the consumer, and no diagnostic reports a trigger that nothing serves.

**No telemetry.** Not "partial", not "basic" — none. There is no `ActivitySource` and no
`Meter` anywhere under `src/`. No spans, no metrics, no structured log scope.
[12-Observability](12-Observability.md) describes the intended design; the code emits
nothing, so plan on your own instrumentation inside capabilities.

**Four of the nine policy kinds still do nothing.** `PolicySet` has `Retry`, `Timeout`,
`CircuitBreaker`, `Bulkhead`, `Cache`, `RateLimit`, `Idempotency`, `Audit` and
`CompensationRetry`. **The first four execute**, and so does `CompensationRetry` on a
compensation: `.WithPolicy(… .Retry(3))` on a forward step now makes three attempts, a
`Timeout` is armed per attempt and clamped to the flow deadline, a `CircuitBreaker` opens
per capability, and a `Bulkhead` refuses a caller past its queue depth. `RateLimit`,
`Idempotency`, `Cache` and `Audit` are parsed, validated — `FLOWX1014` and `FLOWX1018` are
real build errors — written into the plan and the manifest, and applied by nothing;
[`FLOWX1032`](diagnostics/FLOWX1032.md) reports each one you declare. Declaring them is
still worth it: they are what the stage that implements them will find.

**And four things you may expect around a policy are missing.** There is no `[Timeout]`,
`[Retry]` or `[CircuitBreaker]` attribute — a policy attaches through `.WithPolicy(...)` on
a step and nowhere else; there is no flow-level policy surface; there is no runtime
configuration that reaches a policy parameter; and no policy emits a metric, because there
is no metrics infrastructure at all (see the paragraph above). A breaker that opens does so
silently.

**Waits work; there is no scheduler engine behind them.** A `Durable` flow that reaches an
`AwaitSignal<TSignal>(timeout)` or a `Delay(duration)` **suspends**: the invocation returns,
the instance is `Suspended` in the journal at its resume frontier holding no thread and no
lease, and it records which wait it is parked at and when it is due. `FlowHost.SignalAsync`
resumes it on a signal; `FlowTimerScan` — a sweep on an interval, not a timer per instance —
resumes it when the instant passes. An `.OnTimeout(…)` block runs when the declared duration
expires, and a wait with no block ends the flow with `flow.signal_not_received`. `FLOWX1017`
refuses either construct on a non-durable flow, correctly: an in-memory wait does not survive
a deployment, and a timer outside a journal has nowhere to record when it is due. **What that
costs you:** a wait is a lower bound, because it is resolved by a sweep —
`FlowXOptions.TimerScanInterval` is ten seconds by default. `SubFlowMode.AwaitCompletion` is
still refused outright by `FLOWX1026`, and an inline composed child that suspends is refused
at run time — give a flow that waits its own trigger, or compose it `Detached`.

**No multi-tenancy.** `TenantId` is read from validated claims at the HTTP boundary and
carried on the flow context. **Nothing consumes it**: no admission control, no quota, no
rate limit, no journal partitioning, no cache keying, no residency. Every isolation level in
[16-Multi-Tenant](16-Multi-Tenant.md) is currently the same level, and it is "none enforced
by the platform". An application on FlowX today must scope tenants inside its own
capabilities.

**No Studio, and no `flowx dev`.** The CLI has four verbs — `graph`, `manifest`, `diff`,
`verify --cost` ([22-CLI](22-CLI.md)). There is no visual designer, no live reload, no
start-up banner.

**And two smaller ones you will meet sooner than you expect.** The declared authorisation
stance is *not enforced at run time* — it reaches the manifest and the generated HTTP
endpoint does not check it, so put authentication in front of the service. And
`FLOWX.Sdk`, the metapackage the SDK document's table promises, does not exist; a project
references five packages by hand.

---

## 13. Where to go next

| If you want | Read |
|---|---|
| the model in full | [04 Core Concepts](04-Core-Concepts.md), [07 Capability Model](07-Capability-Model.md), [08 Flow Definition](08-Flow-Definition.md) |
| branching, parallel, `ForEach`, sub-flows | [08 Flow Definition](08-Flow-Definition.md) |
| what the engine actually does | [06 Execution Engine](06-Execution-Engine.md) |
| how to test each level properly | [23 Testing Strategy](23-Testing-Strategy.md) |
| every diagnostic, with the argument for its severity | [diagnostics](diagnostics/README.md) |
| a complete application | [samples/ecommerce](../samples/ecommerce/README.md) |
| the template itself | [templates/README.md](../templates/README.md) |

---

## 14. How this page is verified

`tests/FlowX.Compiler.Tests/GettingStartedTests.cs` reads this file, finds every fenced
block, and requires each one to carry an HTML comment saying how it is checked. A block
without one fails the test — which is the half that matters, because otherwise the page
could grow an unchecked snippet and stay green.

| Marker | What the test does |
|---|---|
| `verify: preamble` | compiles the block, and adds it to the sources every later compiled block gets |
| `verify: compiles` | compiles it and asserts **no** diagnostic — FlowX or C# |
| `verify: reports FLOWX1012` | compiles it and asserts the diagnostics are **exactly** those |
| `verify: excerpt <path>` | asserts the block appears verbatim in that repository file |
| `verify: prose <why>` | not C#; never accepted on a `csharp` block |

Compiled blocks are separate syntax trees with a fixed header supplying the usings and
`namespace Ordering;`, so two blocks declaring the same type collide exactly as two files
would. They are run through `FlowPlanGenerator` **and** every FlowX analyzer, and the
*generated* code is compiled too — so a snippet that makes the generator emit something that
does not bind fails here rather than in your project.

Excerpts point at files that are already built elsewhere: the template
(`templates/verify.sh` builds it with `TreatWarningsAsErrors` and asserts zero warnings) and
test projects in the solution. Template excerpts are matched after the name substitution
`dotnet new` performs, so the page quotes the file you get rather than the file in
`templates/`.

The terminal transcripts and the manifest in §2 and §4 are marked `prose`: they are output,
not source, so nothing compiles them. They were produced by running exactly the commands
shown, against the commit that added this page — with two edits, both stated here because a
transcript is only worth what its provenance is: the working directory was shortened to
`/home/you`, and the manifest in §4 is abridged to one flow and one capability. The whole
of it is what `templates/verify.sh` asserts on, and running the four commands is the
cheapest way to check this page has not gone stale.
