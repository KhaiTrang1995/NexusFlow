# FlowXStarter

A FlowX application: one flow, two capabilities, their contracts, and the HTTP endpoint
that runs them.

```bash
dotnet run
```

```bash
curl -X POST http://localhost:5000/api/v1/tickets \
  -H 'Content-Type: application/json' \
  -H 'Authorization: Bearer support-token' \
  -H 'Idempotency-Key: ticket-1' \
  -d '{"subject":"Printer on fire","reporter":"ops","contactPhone":"+44 7700 900000"}'
```

```json
{ "ticketId": "ticket-1", "subject": "Printer on fire" }
```

Send it again with the same `Idempotency-Key` and the store is written once. Send it
without a subject and you get RFC 7807 problem details carrying the error code the
capability returned. Send it without the `Idempotency-Key` and you get `400` — the flow
declares `Idempotent = true`, and the endpoint enforces what the flow declared.

Drop the `Authorization` header and you get `403 authorization.not_authenticated`, because
`ticket.validate` admits only authenticated callers. Send `Bearer reader-token` instead and
you get `403 authorization.permission_denied` — that caller *is* authenticated, and
`ticket.record` wants the `ticket.write` permission, which only `support-token` carries.
Both tokens are two constants in `Authentication.cs`; see [Authentication](#authentication).

## What is here

| File | What it holds |
|---|---|
| `Contracts.cs` | The records on the wire and between steps. No behaviour. |
| `Capabilities.cs` | Two capabilities and the one port they depend on. All the business rules. |
| `OpenTicketFlow.cs` | The control flow: order, and where recovery would go. |
| `Program.cs` | Composition. Registrations, and `MapFlowX()` for every declared endpoint. |
| `Infrastructure.cs` | The in-memory adapter and the JSON context. |
| `Authentication.cs` | Two demonstration tokens. A stand-in for your identity provider — delete it. |

The plan, the step dispatcher, the projection and the manifest are generated from
`OpenTicketFlow.Define` at build time. They are on disk under `obj/generated`, as
ordinary C# with line directives back to your source — set a breakpoint on a step and it
lands in `Capabilities.cs`.

So is the endpoint. `FlowXEndpoints.g.cs` holds the route registration `app.MapFlowX()`
calls, written from the `[HttpTrigger]` on the flow — the same reading of that attribute
that produced the `triggers` block of the manifest. Change the method or the route on the
flow, rebuild, and the served address moves with it: `Program.cs` never named it. The
file exists only because this project references `FlowX.Http`; a project without an HTTP
transport gets no such file.

## The next thing to change

Add a capability: a class with one `ExecuteAsync` method and a `[Capability]` attribute,
registered in `Program.cs`, added to the flow with `.Step<T>()`. The compiler will tell
you if the contracts do not line up (`FLOWX1020`), if the step is not a capability
(`FLOWX1002`), or if it does not declare who may call it (`FLOWX1010`).

`Authorization` is required on every capability. There is no permissive default anywhere
in FlowX, which is why the two here declare a stance and one of them names a permission.

## Authentication

The engine enforces the declared stance on every step, and it decides against
`HttpContext.User` — so an application whose capabilities declare a stance needs something
that produces a principal, or every request is refused at its first step. That something is
`Authentication.cs`, and it is deliberately the smallest thing that works: two tokens in a
dictionary, resolved to claims.

**It is not a security control and it is meant to be deleted.** There is no signature, no
issuer, no audience and no expiry, so anyone who can reach the port can present a token.
Replacing it is one line in `Program.cs`:

```csharp
builder.Services.AddAuthentication().AddJwtBearer(/* your authority and audience */);
```

Nothing else in the project changes, because nothing else in the project knows how the
principal was obtained. The stances stay on the capabilities, where they hold over any
transport — a rule attached to the HTTP endpoint would not hold when the same flow is run
from a broker or an agent.

A permission is read from a `permission`, `permissions`, `scope` or `scp` claim, with
`scope` and `scp` treated as space-delimited lists, which is what an OAuth 2.0 access token
carries. `roles` is deliberately not consulted: a role is a bundle somebody maps to
permissions, and that mapping is not something the engine can see.

## Before this is a real service

* Replace `Authentication.cs` with your identity provider, as above. Until you do, the two
  tokens are the only credentials this application accepts and they are public knowledge.
* This flow is `Ephemeral`, which is the right profile for it and is not free.
  `Profile = ExecutionProfile.Durable` **is** honoured by the runtime — a durable flow
  journals its step boundaries and resumes after a node dies — but only on a host that
  registers a journal and a lease store. Without them the flow is refused before its first
  step with `flow.durability_not_configured`, so the attribute and the registration change
  together. The only journal that ships is PostgreSQL, which is why this project does not
  declare it: `dotnet run` would need a database.
* Retry, timeout and circuit-breaker policies reach the plan and are not executed. What
  does execute is the flow deadline, and — on a flow that declares compensation —
  `CompensationRetry`.
