# Dependency Licence Register

> **Constraint:** C6 — *Apache-2.0, no copyleft dependencies*, implication *"vets every
> transitive dependency"* ([05 §2](05-Architecture.md)).
> **Decision:** [ADR-0012](adr/ADR-0012-apache-2-license.md).
> **Gate:** `DependencyLicencesAreCompatible` in `tests/FlowX.Architecture.Tests`.

---

## Why this file exists

ADR-0012 says *"no dependency may carry a licence incompatible with Apache-2.0
redistribution, verified by an automated licence scan in CI"*, and *"no GPL/AGPL
dependencies, ever, even transitively"*. Until this file existed, neither sentence was
enforced by anything: the scan named in [15 §10](15-Security.md) and in the ADR's own
consequences was never written, and the constraint held only as an observation about the
dependency set of the day. ADR-0012 said the scan *"should happen before the first
published package"*.

This document is the machine-readable half of that scan. Every package this repository
declares, and every package NuGet resolves behind those declarations, has a row here
naming its licence and how that licence was determined. The gate fails when a package has
no row, when a row names a licence nobody classified, when a row disagrees with what the
package itself declares, or when a row cites a licence this project cannot redistribute.

It is a **register**, not an allow-list of names somebody trusts. A row is a claim with
evidence attached, and the gate checks the evidence.

---

## 1. What "compatible" means here

Taken from ADR-0012, not invented. Three verdicts:

| Verdict | Meaning | Where it may appear |
|---|---|---|
| `permissive` | Redistributable inside an Apache-2.0 work under attribution terms alone. No obligation propagates to a consumer of a FlowX package. | anywhere |
| `restricted` | Not copyleft, but not redistributable on permissive terms — a proprietary EULA or a source-available licence with field-of-use conditions. | **only** where the package contributes no assembly at all (see §2) |
| `forbidden` | Copyleft. ADR-0012: *"no GPL/AGPL dependencies, ever, even transitively"*, and constraint C6 says *no copyleft*, which reaches the weak-copyleft licences too. | nowhere |

### 1.1 Licence classification

The `Fingerprint` column is a string the gate looks for inside a package's bundled licence
file when the package does not declare an SPDX expression. It is how a hand-read verdict
stays checkable.

| Licence | Verdict | Fingerprint |
|---|---|---|
| `MIT` | permissive | The MIT License |
| `Apache-2.0` | permissive | Apache License |
| `BSD-2-Clause` | permissive | Redistribution and use in source and binary forms |
| `BSD-3-Clause` | permissive | Redistribution and use in source and binary forms |
| `ISC` | permissive | Permission to use, copy, modify, and/or distribute |
| `PostgreSQL` | permissive | Permission to use, copy, modify, and distribute |
| `Apache-2.0 OR MPL-2.0` | permissive | — |
| `MS-DOTNET-LIBRARY` | restricted | MICROSOFT .NET LIBRARY |
| `SONAR-SOURCE-AVAILABLE-1.0` | restricted | SONAR Source-Available License |
| `BUSL-1.1` | restricted | Business Source License |
| `GPL-2.0-only` | forbidden | GNU GENERAL PUBLIC LICENSE |
| `GPL-3.0-only` | forbidden | GNU GENERAL PUBLIC LICENSE |
| `AGPL-3.0-only` | forbidden | GNU AFFERO GENERAL PUBLIC LICENSE |
| `LGPL-2.1-only` | forbidden | GNU LESSER GENERAL PUBLIC LICENSE |
| `LGPL-3.0-only` | forbidden | GNU LESSER GENERAL PUBLIC LICENSE |
| `MPL-2.0` | forbidden | Mozilla Public License |
| `EPL-2.0` | forbidden | Eclipse Public License |
| `CDDL-1.0` | forbidden | COMMON DEVELOPMENT AND DISTRIBUTION LICENSE |
| `SSPL-1.0` | forbidden | Server Side Public License |

Several of these rows classify a licence no dependency here carries, and they stay
deliberately. The gate's rule is that an *unclassified* licence is a failure, so without
these rows a GPL package would fail with "nobody has classified GPL-3.0-only" — true, but
weaker than "ADR-0012 forbids this outright, and no exception exists". A definition that
only names what is already present is a description, not a definition.

`Apache-2.0 OR MPL-2.0` is a **disjunction**, and that is the whole of why it has its own
row rather than being split into two. SPDX `OR` means the licensee chooses; this project
takes the Apache-2.0 half and is bound by nothing in MPL-2.0, so the expression is
permissive by ADR-0012's test even though one of its two branches is `forbidden` on the
row above. Only `RabbitMQ.Client` carries it. A **conjunction** — `A AND B` — would be a
different question and has no row, because it obliges both and the verdict is then the
worse of the two.

`MS-PL`, `MS-RL` and `CC-BY-SA` are absent because nothing depends on them and they would
each need a considered verdict rather than a guess. Adding one is the point at which
somebody has to think, which is where the gate wants them.

---

## 2. The one narrow exception, and why it is narrow

A `restricted` licence is tolerated **only** where both of these hold:

1. **The package contributes no assembly.** NuGet's resolved graph records it with no
   `compile`/`runtime` assets at all, or with nothing but the `_._` empty-file placeholder.
   Such a package participates in the build and reaches no consumer, because there is
   nothing of it to reach them with.
2. **Every `PackageReference` naming it carries `PrivateAssets="all"`.** Contributing no
   assembly is not sufficient on its own. NuGet writes a plain `PackageReference` into the
   dependency group of every package packed from that project whether or not it carries an
   assembly, so dropping `PrivateAssets` would have a consumer restore it regardless. A
   package nobody declares is exempt from this half — whether a transitive package appears
   in a dependency group is decided by the package that pulls it in, not here.

Both are read off the repository by the gate: the first from `obj/project.assets.json`,
the second from the project files. Neither is a human assertion in a table that somebody
could copy onto the next package needing an excuse. There is no free-text exemption field
here, and that is on purpose: the debt register ([DEBT.md](DEBT.md)) is the mechanism for a
rule switched off deliberately, and a licence this project cannot redistribute is not debt
— it is a package that has to go.

Two rows use the exception today, both verified:

- **`SonarAnalyzer.CSharp`** — SONAR Source-Available License v1.0, a non-OSI licence with
  a non-compete restriction. Declared in `Directory.Build.props` with `PrivateAssets="all"`,
  and its resolved entry has no `compile` and no `runtime` assets in any project: it is an
  analyzer that runs in the compiler. It does not appear in any packed `.nuspec`.
- **`Microsoft.NETCore.Platforms` 1.1.0** — MICROSOFT .NET LIBRARY licence, a proprietary
  EULA, not MIT. Arrives transitively via `NETStandard.Library` 2.0.3, which the two Roslyn
  components pull in by targeting `netstandard2.0`. Its only assets are
  `lib/netstandard1.0/_._`. `FlowX.Compiler` packs with an empty dependency group.

Both are worth stating out loud, because both are the kind of thing a reader assumes is
MIT. Neither reaches a consumer of a FlowX package.

---

## 3. What this gate cannot see

Stated here rather than left to be discovered, on the same principle as
[15 §11](15-Security.md). A gate that overstates its coverage is worse than no gate.

1. **A `.nuspec` that misstates its own licence.** Every machine-checked row compares the
   register against the package's declaration. If the package's declaration is wrong, both
   are wrong together. Only a human reading the source tree catches that.
2. **Licences of code vendored *inside* a package.** A package's SPDX expression covers the
   package; a `THIRD-PARTY-NOTICES.txt` inside it may carry others. The gate does not read
   those, and several packages here ship one.
3. **The transitive closure of a project that has not been restored.** The gate reads the
   real graph out of `obj/project.assets.json`; a project with no assets file has only its
   *declared* references checked. See §5.
4. **Anything NuGet does not restore.** The .NET shared framework itself (MIT); the npm,
   pip and `dotnet tool` packages CI installs — `@mermaid-js/mermaid-cli`, `checkov`,
   `dotnet-stryker`, `reportgenerator`, `dotnet-sonarscanner`; GitHub Actions pulled by
   tag. None of them reaches a published FlowX package, and none of them is scanned.

   A `PackageDownload` **is** read, and was not always. It is not a `PackageReference` —
   it is how the SDK fetches a build-time pack, and it lands in the assets file's
   `downloadDependencies` rather than in `libraries`. One is live here:
   `runtime.linux-x64.Microsoft.DotNet.ILCompiler`, which the ecommerce sample's NativeAOT
   publish links *into the executable*. Which of the two places the SDK records a pack in
   is a decision that has changed between SDK feature bands, and nothing in this repository
   pins one — there is no `global.json`, and CI asks for `10.0.x`. Reading both is what
   stops the answer depending on whose machine asked.
5. **Dual-licensed packages, in one direction only.** A disjunction is representable: the
   whole expression is one id in §1.1 with the verdict the branch this project takes
   deserves, which is how `Apache-2.0 OR MPL-2.0` is classified. A **conjunction**
   (`A AND B`) still has none and would fail as unclassified — the safe direction, and
   still a gap. What the gate never does either way is decide *which* branch of a
   disjunction applies; a person did that once, in §1.1's prose, and the gate holds the
   expression to it verbatim.
6. **What a licence obliges beyond redistribution.** "Permissive" here means the
   Apache-2.0 redistribution question. Attribution and NOTICE-file obligations
   (ADR-0012's last consequence) are a separate matter and nothing enforces them.

---

## 4. Packages

`Determined` says where the licence in the row came from, and the gate checks each kind
differently:

- `nuspec` — the package declares an SPDX expression. The gate reads it out of every
  restored version and fails if it differs from this row. A licence change on a version
  bump is therefore caught even though this table records no version.
- `read` — the package declares no expression, only a licence *file* or a deprecated
  `licenseUrl`. A person read it. The gate checks the named file is still there and still
  contains the classification's fingerprint.
- `first-party` — built from this repository. The gate checks the row against
  `Directory.Build.props`'s `PackageLicenseExpression`.

Versions are deliberately **not** recorded. A version column turns every weekly Dependabot
bump into an edit here, and a table edited on every PR is a table nobody reads — while the
`nuspec` check above already re-reads the licence of whatever version is actually restored.

| Package | Licence | Determined | Evidence |
|---|---|---|---|
| `Azure.Core` | `MIT` | nuspec | — |
| `Azure.Core.Amqp` | `MIT` | nuspec | — |
| `Azure.Messaging.ServiceBus` | `MIT` | nuspec | — |
| `BenchmarkDotNet` | `MIT` | nuspec | — |
| `BenchmarkDotNet.Annotations` | `MIT` | nuspec | — |
| `Confluent.Kafka` | `Apache-2.0` | nuspec | — |
| `CommandLineParser` | `MIT` | read | `License.md` |
| `coverlet.collector` | `MIT` | nuspec | — |
| `DiffEngine` | `MIT` | nuspec | — |
| `EmptyFiles` | `MIT` | nuspec | — |
| `FlowX.Abstractions` | `Apache-2.0` | first-party | — |
| `FlowX.Compiler` | `Apache-2.0` | first-party | — |
| `FlowX.Compiler.CodeFixes` | `Apache-2.0` | first-party | — |
| `FlowX.Hosting` | `Apache-2.0` | first-party | — |
| `FlowX.Http` | `Apache-2.0` | first-party | — |
| `FlowX.Mcp` | `Apache-2.0` | first-party | — |
| `Gee.External.Capstone` | `MIT` | nuspec | — |
| `Humanizer.Core` | `MIT` | nuspec | — |
| `Iced` | `MIT` | nuspec | — |
| `Json.More.Net` | `MIT` | nuspec | — |
| `JsonPointer.Net` | `MIT` | nuspec | — |
| `JsonSchema.Net` | `MIT` | nuspec | — |
| `Microsoft.AspNetCore.TestHost` | `MIT` | nuspec | — |
| `librdkafka.redist` | `BSD-2-Clause` | read | `LICENSES.txt` |
| `Microsoft.Azure.Amqp` | `MIT` | nuspec | — |
| `Microsoft.Bcl.AsyncInterfaces` | `MIT` | nuspec | — |
| `Microsoft.CodeAnalysis.Analyzers` | `MIT` | nuspec | — |
| `Microsoft.CodeAnalysis.Common` | `MIT` | nuspec | — |
| `Microsoft.CodeAnalysis.CSharp` | `MIT` | nuspec | — |
| `Microsoft.CodeAnalysis.CSharp.Workspaces` | `MIT` | nuspec | — |
| `Microsoft.CodeAnalysis.Workspaces.Common` | `MIT` | nuspec | — |
| `Microsoft.CodeCoverage` | `MIT` | nuspec | — |
| `Microsoft.Diagnostics.NETCore.Client` | `MIT` | nuspec | — |
| `Microsoft.Diagnostics.Runtime` | `MIT` | nuspec | — |
| `Microsoft.Diagnostics.Tracing.TraceEvent` | `MIT` | nuspec | — |
| `Microsoft.DotNet.ILCompiler` | `MIT` | nuspec | — |
| `Microsoft.DotNet.PlatformAbstractions` | `MIT` | read | `LICENSE.TXT` |
| `Microsoft.Extensions.Configuration` | `MIT` | nuspec | — |
| `Microsoft.Extensions.Configuration.Abstractions` | `MIT` | nuspec | — |
| `Microsoft.Extensions.Configuration.Binder` | `MIT` | nuspec | — |
| `Microsoft.Extensions.Configuration.CommandLine` | `MIT` | nuspec | — |
| `Microsoft.Extensions.Configuration.EnvironmentVariables` | `MIT` | nuspec | — |
| `Microsoft.Extensions.Configuration.FileExtensions` | `MIT` | nuspec | — |
| `Microsoft.Extensions.Configuration.Json` | `MIT` | nuspec | — |
| `Microsoft.Extensions.Configuration.UserSecrets` | `MIT` | nuspec | — |
| `Microsoft.Extensions.DependencyInjection` | `MIT` | nuspec | — |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | `MIT` | nuspec | — |
| `Microsoft.Extensions.Diagnostics` | `MIT` | nuspec | — |
| `Microsoft.Extensions.Diagnostics.Abstractions` | `MIT` | nuspec | — |
| `Microsoft.Extensions.Diagnostics.HealthChecks` | `MIT` | nuspec | — |
| `Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions` | `MIT` | nuspec | — |
| `Microsoft.Extensions.FileProviders.Abstractions` | `MIT` | nuspec | — |
| `Microsoft.Extensions.FileProviders.Physical` | `MIT` | nuspec | — |
| `Microsoft.Extensions.FileSystemGlobbing` | `MIT` | nuspec | — |
| `Microsoft.Extensions.Hosting` | `MIT` | nuspec | — |
| `Microsoft.Extensions.Hosting.Abstractions` | `MIT` | nuspec | — |
| `Microsoft.Extensions.Logging` | `MIT` | nuspec | — |
| `Microsoft.Extensions.Logging.Abstractions` | `MIT` | nuspec | — |
| `Microsoft.Extensions.Logging.Configuration` | `MIT` | nuspec | — |
| `Microsoft.Extensions.Logging.Console` | `MIT` | nuspec | — |
| `Microsoft.Extensions.Logging.Debug` | `MIT` | nuspec | — |
| `Microsoft.Extensions.Logging.EventLog` | `MIT` | nuspec | — |
| `Microsoft.Extensions.Logging.EventSource` | `MIT` | nuspec | — |
| `Microsoft.Extensions.Options` | `MIT` | nuspec | — |
| `Microsoft.Extensions.Options.ConfigurationExtensions` | `MIT` | nuspec | — |
| `Microsoft.Extensions.Primitives` | `MIT` | nuspec | — |
| `Microsoft.NET.ILLink.Tasks` | `MIT` | nuspec | — |
| `Microsoft.NET.Test.Sdk` | `MIT` | nuspec | — |
| `Microsoft.NETCore.Platforms` | `MS-DOTNET-LIBRARY` | read | `dotnet_library_license.txt` |
| `Microsoft.Testing.Extensions.TrxReport.Abstractions` | `MIT` | nuspec | — |
| `Microsoft.Testing.Platform` | `MIT` | nuspec | — |
| `Microsoft.Testing.Platform.MSBuild` | `MIT` | nuspec | — |
| `Microsoft.TestPlatform.ObjectModel` | `MIT` | nuspec | — |
| `Microsoft.TestPlatform.TestHost` | `MIT` | nuspec | — |
| `Microsoft.VisualStudio.Threading.Analyzers` | `MIT` | nuspec | — |
| `Mono.Cecil` | `MIT` | nuspec | — |
| `NETStandard.Library` | `MIT` | read | `LICENSE.TXT` |
| `Newtonsoft.Json` | `MIT` | nuspec | — |
| `Npgsql` | `PostgreSQL` | nuspec | — |
| `Perfolizer` | `MIT` | nuspec | — |
| `RabbitMQ.Client` | `Apache-2.0 OR MPL-2.0` | nuspec | — |
| `RESPite` | `MIT` | nuspec | — |
| `runtime.linux-x64.Microsoft.DotNet.ILCompiler` | `MIT` | nuspec | — |
| `Shouldly` | `BSD-3-Clause` | nuspec | — |
| `StackExchange.Redis` | `MIT` | nuspec | — |
| `SonarAnalyzer.CSharp` | `SONAR-SOURCE-AVAILABLE-1.0` | read | `licenses/LICENSE.txt` |
| `System.Buffers` | `MIT` | read | `LICENSE.TXT` |
| `System.CodeDom` | `MIT` | nuspec | — |
| `System.ClientModel` | `MIT` | nuspec | — |
| `System.Collections.Immutable` | `MIT` | nuspec | — |
| `System.Composition` | `MIT` | nuspec | — |
| `System.Composition.AttributedModel` | `MIT` | nuspec | — |
| `System.Composition.Convention` | `MIT` | nuspec | — |
| `System.Composition.Hosting` | `MIT` | nuspec | — |
| `System.Composition.Runtime` | `MIT` | nuspec | — |
| `System.Composition.TypedParts` | `MIT` | nuspec | — |
| `System.Diagnostics.EventLog` | `MIT` | nuspec | — |
| `System.IO.Hashing` | `MIT` | nuspec | — |
| `System.IO.Pipelines` | `MIT` | nuspec | — |
| `System.Management` | `MIT` | nuspec | — |
| `System.Memory.Data` | `MIT` | nuspec | — |
| `System.Memory` | `MIT` | read | `LICENSE.TXT` |
| `System.Numerics.Vectors` | `MIT` | read | `LICENSE.TXT` |
| `System.Reflection.Metadata` | `MIT` | nuspec | — |
| `System.Reflection.MetadataLoadContext` | `MIT` | nuspec | — |
| `System.Reflection.TypeExtensions` | `MIT` | nuspec | — |
| `System.Runtime.CompilerServices.Unsafe` | `MIT` | nuspec | — |
| `System.Text.Encoding.CodePages` | `MIT` | nuspec | — |
| `System.Threading.Channels` | `MIT` | nuspec | — |
| `System.Threading.RateLimiting` | `MIT` | nuspec | — |
| `System.Threading.Tasks.Extensions` | `MIT` | read | `LICENSE.TXT` |
| `xunit.analyzers` | `Apache-2.0` | nuspec | — |
| `xunit.runner.visualstudio` | `Apache-2.0` | nuspec | — |
| `xunit.v3` | `Apache-2.0` | nuspec | — |
| `xunit.v3.assert` | `Apache-2.0` | nuspec | — |
| `xunit.v3.common` | `Apache-2.0` | nuspec | — |
| `xunit.v3.core` | `Apache-2.0` | nuspec | — |
| `xunit.v3.extensibility.core` | `Apache-2.0` | nuspec | — |
| `xunit.v3.runner.common` | `Apache-2.0` | nuspec | — |
| `xunit.v3.runner.inproc.console` | `Apache-2.0` | nuspec | — |

### 4.1 What a consumer of a published FlowX package actually restores

The table above is everything the *repository* touches, most of it a test or benchmark
dependency. The redistributed set — what appears in a packed `.nuspec` and so lands in a
user's application — is much smaller, and every entry is permissive:

| Package | Its non-FlowX dependencies |
|---|---|
| `FlowX.Abstractions` | none, by gate (`AbstractionsHasNoDependencies`, ADR-0009) |
| `FlowX.Core` | none |
| `FlowX.Runtime` | none |
| `FlowX.Hosting` | `Microsoft.Extensions.{DependencyInjection.Abstractions, Diagnostics.HealthChecks, Hosting.Abstractions, Options}` — all `MIT` |
| `FlowX.Http` | none |
| `FlowX.Mcp` | none — it packs against `FlowX.Http` and the shared framework, which is what lets `dotnet new flowx` take it without adding a licence to vet |
| `FlowX.Testing` | none |
| `FlowX.Compiler`, `FlowX.Compiler.CodeFixes` | none — both pack with an empty dependency group |
| `FlowX.Postgres` | `Npgsql` (`PostgreSQL`), `Microsoft.Extensions.DependencyInjection.Abstractions` (`MIT`) |
| `FlowX.Kafka` | `Confluent.Kafka` (`Apache-2.0`), which brings `librdkafka.redist` (`BSD-2-Clause`); `Microsoft.Extensions.DependencyInjection.Abstractions` (`MIT`) |
| `FlowX.RabbitMq` | `RabbitMQ.Client` (`Apache-2.0 OR MPL-2.0`, taken as Apache-2.0), which brings `System.Threading.RateLimiting` (`MIT`); `Microsoft.Extensions.DependencyInjection.Abstractions` (`MIT`) |
| `FlowX.AzureServiceBus` | `Azure.Messaging.ServiceBus` (`MIT`), which brings `Azure.Core`, `Azure.Core.Amqp`, `Microsoft.Azure.Amqp`, `System.ClientModel` and `System.Memory.Data` — all `MIT`; `Microsoft.Extensions.DependencyInjection.Abstractions` (`MIT`) |
| `flowx` (CLI tool) | bundles `System.Reflection.MetadataLoadContext.dll` (`MIT`) in `tools/` |

**`RabbitMQ.Client` is the one row here whose licence a reader should not skim.** It
declares `Apache-2.0 OR MPL-2.0`, and MPL-2.0 on its own is `forbidden` above. The
disjunction is what makes it permissible: SPDX `OR` is the licensor offering a choice, this
project takes Apache-2.0, and no MPL obligation attaches. §1.1 carries the argument and the
gate compares the whole expression against the `.nuspec`, so a future version that dropped
the Apache-2.0 branch would fail here rather than pass as "still dual-licensed".

**`Npgsql`, added by WP-53, is the other non-Microsoft package in that set.** Its licence is
the PostgreSQL Licence — a BSD/MIT-style permissive licence with attribution terms and no
copyleft obligation, not the database's own terms by another name. It is permissive by
ADR-0012's test, and it is worth naming because a scan built around an allow-list of
`MIT`/`Apache-2.0`/`BSD-*` would have rejected it for being unfamiliar rather than for
being incompatible.

---

## 5. Projects whose transitive closure is not vetted

These have no `obj/project.assets.json` after an ordinary `dotnet build` of `FlowX.slnx`,
because they are deliberately outside the solution. Their **declared** references are still
checked against §4; what those references drag in behind them is not.

| Project | Why it is outside the solution | Declared references |
|---|---|---|
| `scripts/generator-cost-probe/GeneratorCostProbe.csproj` | a measurement harness, not a build target | `Microsoft.CodeAnalysis.CSharp` |
| `templates/FlowX.Templates/FlowX.Templates.csproj` | a template pack: it carries files, not code | none |
| `templates/FlowX.Templates/content/FlowX.Web/FlowXStarter.csproj` | template *input*, not repository source | five first-party `FlowX.*` packages |

`ci.yml` restores the first of these before running the architecture gates, so in CI its
closure *is* vetted. The template's cannot be restored at all until the packages it names
exist on a feed — which is WP-70, the work item that made this gate urgent. When that
lands, restoring it here becomes possible and this row should shrink.

`EveryProjectIsCoveredByTheLicenceGate` asserts that no project falls outside both
readings, and that the declaration-only set stays within this list.

---

## 6. Adding a dependency

1. Add the `PackageReference` and build. `DependencyLicencesAreCompatible` fails, naming
   the package.
2. Find its licence. `dotnet restore` has already put the `.nuspec` under
   `~/.nuget/packages/<id>/<version>/`; read the `<license>` element.
3. Add a row to §4. If the package declares an SPDX expression, use `nuspec` and copy the
   expression exactly. If it declares only a file or a `licenseUrl`, read the file, use
   `read`, and cite the file's path inside the package.
4. If the licence is not in §1.1, classify it there — and if the verdict is `forbidden`,
   the answer is a different package, not a new row.
