# FlowXMinimal

The smallest FlowX application that runs. One flow, one capability, one route, and nothing
installed — no database, no broker, no authentication.

```bash
dotnet run
```

```bash
curl -sS -X POST http://localhost:5000/api/v1/tickets \
  -H 'Idempotency-Key: t-1' \
  -H 'Content-Type: application/json' \
  -d '{"subject":"The printer is on fire"}'
```

Send it twice with the same `Idempotency-Key` and the ticket id does not change. Send it with a
blank subject and the answer is `ticket.subject_required` — a value the capability returned, not
an exception it threw.

## The three files

| File | What is in it |
|---|---|
| `Ticket.cs` | the contracts, the capability and the flow |
| `Program.cs` | composition — three registrations and one wiring call |
| `FlowXMinimal.csproj` | five package references, of which two are the compiler |

Nothing in `Program.cs` names the route. It is on the flow, the compiler reads it, and
`app.UseFlowX()` serves whatever is there — including the subscription or the schedule you get
the moment you add a `[BusTrigger]` or a `[CronTrigger]`.

## Where to go next

`dotnet new flowx` is the same idea with authentication, an agent surface over MCP, and a second
capability so the flow has more than one step. `docs/24-Getting-Started.md` walks it.
