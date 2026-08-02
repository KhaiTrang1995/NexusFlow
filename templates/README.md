# `dotnet new flowx`

The template a new user runs first. It generates one working vertical slice — a flow, two
capabilities, their contracts, an authentication scheme, the composition root, and the same
flow served over two transports — not an empty folder tree.

```bash
templates/local-feed.sh                              # pre-release only; see below
dotnet new install templates/FlowX.Templates
dotnet new flowx -o Ordering
cd Ordering && dotnet run
```

```bash
curl -X POST http://localhost:5000/api/v1/tickets \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: ticket-1' \
  -H 'Authorization: Bearer support-token' \
  -d '{"subject":"Printer on fire","reporter":"ops","contactPhone":"+44 7700 900000"}'
```

The token is required. `ticket.validate` admits any authenticated caller and `ticket.record`
requires `ticket.write`, and the engine decides both before the step runs — so
`reader-token` reaches the second step and is refused there, which is the difference between
a door and a permission model. `Authentication.cs` is a stand-in for an OIDC handler.

And the same flow is an agent tool, because it declares an `[AgentTrigger]` as well:

```bash
curl -X POST http://localhost:5000/mcp \
  -H 'Content-Type: application/json' \
  -H 'Authorization: Bearer support-token' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
```

The tool's name, description and required permissions come out of `flowx.manifest.json`, and
`tools/call` is decided by the same stances the endpoint is. Two transports, one
authorisation decision, and nothing in `Program.cs` naming either flow.

## What is here

| Path | What it is |
|---|---|
| `FlowX.Templates/FlowX.Templates.csproj` | The template pack. **Not in `FlowX.slnx`** — see the comment in the file. |
| `FlowX.Templates/content/FlowX.Web/` | The template itself: what `dotnet new flowx` copies. |
| `local-feed.sh` | Builds the FlowX packages a generated project references, into `.artifacts/local-feed`. |
| `verify.sh` | The acceptance test: generate, build with warnings as errors, run, drive the endpoint. |

## The one pre-release step

Nothing is published to NuGet. The generated `.csproj` is nevertheless the shape that will
ship — plain `PackageReference`s, with the compiler as an analyzer asset — because the
alternative, project references pointing back into a clone of this repository, is a
different project shape that would have to be rewritten the day packages exist.

So the pre-release cost is one command, `templates/local-feed.sh`, which packs the eight
packages into `.artifacts/local-feed` and registers it as a NuGet source. When the
packages publish, that script and this section are deleted and **the template does not
change**.

It also evicts those eight ids from the global NuGet cache before packing. The version
never changes between runs, and NuGet caches by id and version — so without the eviction a
second `verify.sh` restores the *first* run's assemblies and reports green against a
generator from an earlier commit. Only the FlowX ids are removed.

`templates/local-feed.sh --remove` unregisters the source and deletes the feed.

## Verifying it

`templates/verify.sh` is the whole acceptance test, and it is what CI should run. It
packs the feed, packs and installs the template pack, generates a project into a
temporary directory, asserts the name substitution took, builds with
`TreatWarningsAsErrors` and asserts zero warnings, checks what the generator emitted —
including that the generated endpoint carries the route and the idempotency rule the flow
declared, that `Program.cs` restates neither, and that the manifest's source pointers are
relative rather than the build agent's directory layout — then runs the application and
drives both transports: over HTTP, an anonymous refusal, an under-privileged refusal, the
happy path, a repeat with the same idempotency key, a business failure arriving as RFC 7807
problem details, and a request with no `Idempotency-Key`; over `/mcp`, that `tools/list`
publishes the flow's tool with the permission its capability declares, and that
`tools/call` refuses the *same* under-privileged token the endpoint refuses. It exits with
the number of failed checks.

It is a shell script rather than an xunit project on purpose. The thing under test is
`dotnet new`, `dotnet build` and a process listening on a socket, and a test that shells
out to all three from inside the repository's own test run would make the suite depend on
a NuGet feed built from that same run.

## Why one shape and no options

There is no `--profile` and no `--transport`.

`--profile durable` would generate a project that builds and then refuses its own first
request.

*This paragraph used to give a different reason — that `FLOWX1028` reported a declared
`Durable` profile as a warning, and `TreatWarningsAsErrors` turned it into a build failure.
That stopped being true at **WP-52**, which made `FlowX.Runtime` read `ExecutionProfile`
and narrowed `FLOWX1028` to `Streaming`. The option is still not offered, for a reason
that outlived the old one.*

Durability is opted into by **registering stores**, not by an attribute: a `Durable` flow
on a host with no journal and no lease store is refused before its first step with
`flow.durability_not_configured`. The only journal that ships is `plugins/FlowX.Postgres`.
So `--profile durable` would have to scaffold a connection string, a migration step and a
running PostgreSQL into the one command whose whole value is that `dotnet run` works — or
scaffold the attribute alone and hand a new user a project that compiles and cannot serve a
request. Neither is a template.

*This section used to end "`--transport` has one value. `plugins/` contains `FlowX.Http` and
nothing else."* `plugins/` now contains four — `FlowX.Http`, `FlowX.Mcp`, `FlowX.Postgres`
and `FlowX.Redis` — and the template takes two of them, because a transport that needs no
infrastructure is a package reference and an attribute. There is still no `--transport`
option, and the reason is the same one durability has: the two that are left need a broker
or a database running, which is the one thing the command whose value is `dotnet run` cannot
scaffold. The samples are where those are wired — `samples/ecommerce` for the bus and the
change feed, `samples/banking` for the journal and multi-tenancy.

There is no `--tenancy` either. `TenantIsolation.Row` makes a tenant mandatory and enforces
it in PostgreSQL, so a generated project declaring it would refuse every request until a
database existed and a token carried a claim — the same trap `--profile durable` is.

The remaining knobs the .NET template engine gives for free — `-n`, `-o`, and the
directory-name default — are the ones that were verified.
