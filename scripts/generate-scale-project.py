#!/usr/bin/env python3
"""Emit a synthetic FlowX project of N flows, for the P1 scale criterion.

    ./scripts/generate-scale-project.py --flows 200 --out /tmp/scale-200

The roadmap's P1 exit criterion is "a 200-flow synthetic solution builds with <= 8 %
overhead". Nothing in the repository is that shape: the reference sample has one flow,
and B12 measured it honestly while saying so. This produces the missing subject.

Three properties are deliberate, because each one decides whether the resulting number
means anything.

**The flows are not copies of each other.** The generator's cost scales with what it has
to analyse — steps, compensations, emits, the verbatim text of a Return projection, the
policy expression on a step. Two hundred one-step flows would understate every one of
those. Flows cycle through four shapes of three to five steps, with compensations, emits,
an emit-on-failure branch and a policy set, so the analysed surface per flow resembles
something a team would actually write.

**Capability bodies stay minimal.** This is the choice most likely to look wrong. Padding
each capability with plausible business logic would make the project bigger and the
overhead ratio smaller, because the denominator would grow while the generator's work did
not. Every line of non-flow source added here is a line that flatters the result. Minimal
bodies put the generator's share of the build at its realistic *maximum*, so a pass is a
conservative pass.

**Every type is distinct.** Contracts, capabilities and events are per-flow rather than
shared, so the semantic model cannot amortise the second flow against the first. A
solution where 200 flows share a dozen capabilities would be cheaper to compile and is not
what "200-flow solution" is asked to mean.

Both of those last two are assumptions about what real projects look like, and ADR-0014 §6
records that neither has been measured — they point in opposite directions and nobody knows
which wins. Two opt-in knobs exist to find out, and they are off by default because the
default subject is hashed into docs/benchmarks/generator-cost-baseline.json:

    --capability-body-lines N   pad every capability body with N extra statements
    --capability-pool K         declare K sets of capabilities and share them across flows

With both at their defaults the emitted project is byte-identical to what this script has
always produced. scripts/measure-catalogue-shape.py drives the grid; the results are in
docs/benchmarks/B13-error-catalogue-resolution.md §6.

The output builds two ways — with the generator, and with its previously emitted sources
compiled as ordinary files — using the same `FlowXGeneratorDisabled` condition
`samples/ecommerce/Ecommerce.csproj` carries. That condition is what makes a like-for-like
comparison possible at all; see docs/benchmarks/B12.md.
"""

from __future__ import annotations

import argparse
import pathlib
import shutil
import sys

# Step count, and which 1-based step indexes carry a compensation. The four shapes are
# cycled rather than randomised: a harness whose subject changes between runs cannot
# attribute a moved number to a code change, which is the only reason to run it twice.
SHAPES = (
    {"steps": 3, "compensate": (2,), "emit": False, "emit_on_failure": False, "policy": None},
    {"steps": 4, "compensate": (2, 3), "emit": True, "emit_on_failure": False, "policy": None},
    {"steps": 5, "compensate": (3,), "emit": True, "emit_on_failure": False, "policy": 1},
    {"steps": 4, "compensate": (2,), "emit": True, "emit_on_failure": True, "policy": None},
)

NAMESPACE = "ScaleSynthetic"

SUPPORT_SOURCE = """using FlowX;

namespace ScaleSynthetic;

/// <summary>The one dependency the synthetic capabilities take.</summary>
/// <remarks>
/// A capability that resolves a dependency and awaits it binds differently from one that
/// returns a constant, and roughly half of them do. It is an ordinary interface: a
/// transport type here would be FLOWX1003 and a capability here would be FLOWX1004, and
/// either would make the project fail to build for a reason that has nothing to do with
/// scale.
/// </remarks>
public interface IScaleStore
{
    /// <summary>Reads a value.</summary>
    ValueTask<int> ReadAsync(string key, CancellationToken ct);

    /// <summary>Writes a value under an idempotency key.</summary>
    ValueTask WriteAsync(string key, int value, string idempotencyKey, CancellationToken ct);
}

/// <summary>Errors the synthetic capabilities can produce.</summary>
public static class ScaleErrors
{
    /// <summary>The amount is not positive.</summary>
    public static Error InvalidAmount(int amount) =>
        new Error("scale.invalid_amount", $"Amount must be positive; got {amount}.", ErrorCategory.Validation)
            .With("amount", amount);

    /// <summary>The upstream refused.</summary>
    public static Error Rejected(string key) =>
        new Error("scale.rejected", $"'{key}' was rejected.", ErrorCategory.Conflict)
            .With("key", key);
}
"""

# MSBuild walks up from the project directory looking for these, and the synthetic project
# lives outside the repository precisely so that it inherits nothing. An empty pair stops
# the walk, so a Directory.Build.props somewhere above the temp directory cannot silently
# change the compilation being measured — which would move the number without moving the
# code, the failure mode a benchmark harness exists to avoid.
EMPTY_BUILD_FILE = "<Project>\n  <!-- Intentionally empty. See generate-scale-project.py. -->\n</Project>\n"

CSPROJ_TEMPLATE = """<Project Sdk="Microsoft.NET.Sdk">

  <!--
    Generated by scripts/generate-scale-project.py. Do not edit: it is rewritten on
    every run of scripts/measure-scale-overhead.sh.
  -->

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>preview</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>{namespace}</RootNamespace>
    <AssemblyName>{assembly}</AssemblyName>
    <IsPackable>false</IsPackable>

    <!--
      The repository's own analysis settings are deliberately NOT reproduced here. Trim,
      AOT and code-style analysers cost time in both arms equally, so every second they
      add lands in the denominator and shrinks the overhead ratio. Leaving them off keeps
      the generator's share of the build at its maximum, which is the conservative
      direction for a budget this is trying to fail if it can.
    -->
    <EnableNETAnalyzers>false</EnableNETAnalyzers>
    <AnalysisLevel>none</AnalysisLevel>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>

    <!--
      The generated sources have to reach disk, because the control arm compiles them as
      ordinary files. Without this there is no second arm.
    -->
    <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
    <CompilerGeneratedFilesOutputPath>$(MSBuildProjectDirectory)/obj/generated</CompilerGeneratedFilesOutputPath>

    <!--
      FLOWX1024 fires once per .Emit step and says the outbox does not exist yet. That is
      true, it is documented, and 150 repetitions of it here would bury anything the
      harness actually needs to see.
    -->
    <NoWarn>FLOWX1024</NoWarn>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="{abstractions}" />
    <ProjectReference Include="{core}" />
    <ProjectReference Include="{runtime}" />

    <!--
      Referenced as an ANALYZER, exactly as a consumer would reference it, so the
      measurement times the generator running inside a real build rather than a harness
      calling Roslyn directly.
    -->
    <ProjectReference Include="{compiler}"
                      OutputItemType="Analyzer"
                      ReferenceOutputAssembly="false"
                      Condition="'$(FlowXGeneratorDisabled)' != 'true'" />
  </ItemGroup>

  <ItemGroup>
    <!--
      The manifest records where each flow was declared, relative to this property. The
      repository supplies it through Directory.Build.props; this project inherits nothing,
      so it declares it itself or the recorded paths become absolute.
    -->
    <CompilerVisibleProperty Include="ProjectDir" />
  </ItemGroup>

  <!--
    The control arm, as in samples/ecommerce/Ecommerce.csproj: drop the analyzer and
    compile the sources it produced last time. Both arms then reach the same final
    compilation and differ only in whether the generator ran.
  -->
  <ItemGroup Condition="'$(FlowXGeneratorDisabled)' == 'true'">
    <Compile Include="$(FlowXPreGeneratedDir)/**/*.g.cs" />
  </ItemGroup>

</Project>
"""


# Optional body padding, off by default so the gated subject is unchanged. See
# docs/benchmarks/B13-error-catalogue-resolution.md §6: ADR-0014 §6 says "real capability
# bodies are larger, which costs more per capability" and calls it an extrapolation. These
# are statements, not failure paths — the count of Error-typed expressions stays fixed, so
# the knob varies body size and nothing else, which is the only way to attribute a moved
# number to it. A real body would grow both, so this understates.
PADDING_TEMPLATES = (
    "var pad{n} = input.Key.Length + input.Amount + {n};",
    "var pad{n} = pad{prev} % 2 == 0 ? pad{prev} / 2 : (pad{prev} * 3) + 1;",
    "var pad{n} = pad{prev} > 100 ? pad{prev} - 100 : pad{prev} + {n};",
    "var pad{n} = System.Math.Max(pad{prev}, input.Amount) - System.Math.Min(pad{prev}, {n});",
    "var pad{n} = input.Key.Length > {n} ? pad{prev} ^ {n} : pad{prev} & {n};",
)


def padding(lines: int) -> str:
    """`lines` statements of ordinary arithmetic, indented into a capability body."""
    if lines < 1:
        return ""

    rendered = []

    for n in range(lines):
        template = PADDING_TEMPLATES[0] if n == 0 else PADDING_TEMPLATES[n % len(PADDING_TEMPLATES)]
        rendered.append("        " + template.format(n=n, prev=max(n - 1, 0)))

    return "\n" + "\n".join(rendered) + "\n"


def capability(
    *,
    type_name: str,
    identity: str,
    input_type: str,
    output_type: str,
    idempotent: bool,
    authorization: str,
    side_effects: tuple[str, ...],
    injected: bool,
    construct: str,
    body_lines: int = 0,
) -> str:
    """One capability class: attribute, contracts, and the smallest honest body."""
    attribute_lines = [
        f'[Capability("{identity}", Version = "1.0.0",',
        f"    Authorization = {authorization},",
    ]

    if authorization == "Authorization.Permission":
        attribute_lines.append('    Permission = "scale.write",')

    attribute_lines.append(f"    Idempotent = {'true' if idempotent else 'false'}")

    if side_effects:
        rendered = ", ".join(f'"{effect}"' for effect in side_effects)
        attribute_lines[-1] += ","
        attribute_lines.append(f"    SideEffects = [{rendered}])]")
    else:
        attribute_lines[-1] += ")]"

    attribute = "\n".join(attribute_lines)

    if injected:
        body = f"""public sealed class {type_name} : ICapability<{input_type}, {output_type}>
{{
    private readonly IScaleStore _store;

    /// <summary>Creates the capability.</summary>
    public {type_name}(IScaleStore store)
    {{
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }}

    /// <inheritdoc />
    public async ValueTask<Result<{output_type}>> ExecuteAsync(
        {input_type} input,
        CapabilityContext ctx,
        CancellationToken ct)
    {{
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);
{padding(body_lines)}
        var current = await _store.ReadAsync(input.Key, ct).ConfigureAwait(false);

        if (current < 0)
        {{
            return ScaleErrors.Rejected(input.Key);
        }}

        await _store.WriteAsync(input.Key, current, ctx.IdempotencyKey, ct).ConfigureAwait(false);

        return {construct};
    }}
}}"""
    else:
        body = f"""public sealed class {type_name} : ICapability<{input_type}, {output_type}>
{{
    /// <inheritdoc />
    public ValueTask<Result<{output_type}>> ExecuteAsync(
        {input_type} input,
        CapabilityContext ctx,
        CancellationToken ct)
    {{
        ArgumentNullException.ThrowIfNull(input);
{padding(body_lines)}
        if (input.Amount <= 0)
        {{
            return ValueTask.FromResult(Result.Fail<{output_type}>(ScaleErrors.InvalidAmount(input.Amount)));
        }}

        return ValueTask.FromResult(Result.Ok({construct}));
    }}
}}"""

    return f"""/// <summary>Synthetic capability <c>{identity}</c>.</summary>
{attribute}
{body}"""


def type_parts(index: int, body_lines: int = 0) -> list[str]:
    """The contracts and capabilities belonging to flow shape `index`, as source lines."""
    shape = SHAPES[index % len(SHAPES)]
    prefix = f"F{index:04d}"
    identity = f"f{index:04d}"
    steps = shape["steps"]
    compensated = set(shape["compensate"])

    # Every step's input is the previous step's output, so the flow context always holds
    # what the next step needs. A step whose input no prior step produced would compile
    # into a Get<T> for a slot nothing filled — a runtime fault the contract-compatibility
    # work exists to catch, and not a property this harness should be exercising.
    stage_types = [f"{prefix}Request"] + [f"{prefix}Stage{n}" for n in range(1, steps + 1)]

    parts: list[str] = [
        "using FlowX;",
        "",
        f"namespace {NAMESPACE};",
        "",
        "/// <summary>What a caller asks this flow for.</summary>",
        f"public sealed record {prefix}Request(string Key, int Amount, "
        "[property: Sensitive] string Token);",
        "",
    ]

    for n in range(1, steps + 1):
        parts.append(f"/// <summary>Intermediate state after step {n}.</summary>")
        parts.append(f"public sealed record {prefix}Stage{n}(string Key, int Amount);")
        parts.append("")

    parts.append("/// <summary>What the caller gets back.</summary>")
    parts.append(f"public sealed record {prefix}Result(string Key, int Amount);")
    parts.append("")

    if shape["emit"]:
        parts.append("/// <summary>Published when the flow succeeds.</summary>")
        parts.append(f"public sealed record {prefix}Completed(string Key, int Amount);")
        parts.append("")

    if shape["emit_on_failure"]:
        parts.append("/// <summary>Published when the flow fails.</summary>")
        parts.append(f"public sealed record {prefix}Abandoned(string Key);")
        parts.append("")

    for n in range(1, steps + 1):
        input_type = stage_types[n - 1]
        output_type = stage_types[n]

        # The idempotency and side-effect declarations are not decoration: FLOWX1014 and
        # FLOWX1018 are checked against them, and the policy attached below is only legal
        # because the step it lands on declares itself idempotent and effect-free.
        idempotent = n != steps
        side_effects = () if n == 1 else ("scale-ledger",)
        authorization = (
            "Authorization.Authenticated"
            if n == 1
            else "Authorization.Permission"
            if n == steps
            else "Authorization.Internal"
        )

        parts.append(
            capability(
                type_name=f"{prefix}Step{n}",
                identity=f"{identity}.step{n}",
                input_type=input_type,
                output_type=output_type,
                idempotent=idempotent,
                authorization=authorization,
                side_effects=side_effects,
                injected=n % 2 == 0,
                construct=f"new {output_type}(input.Key, input.Amount)",
                body_lines=body_lines,
            )
        )
        parts.append("")

    for n in sorted(compensated):
        # A compensation takes the same input as the step it undoes, because that is what
        # the generated CompensateAsync reads out of the context.
        undone_input = stage_types[n - 1]

        parts.append(
            capability(
                type_name=f"{prefix}Undo{n}",
                identity=f"{identity}.undo{n}",
                input_type=undone_input,
                output_type=undone_input,
                idempotent=True,
                authorization="Authorization.Internal",
                side_effects=("scale-ledger",),
                injected=True,
                construct=f"new {undone_input}(input.Key, input.Amount)",
                body_lines=body_lines,
            )
        )
        parts.append("")

    return parts


def flow_block(index: int, type_index: int | None = None) -> str:
    """The flow class for `index`, over the capability types declared by `type_index`.

    The two indexes are the same in the default project, where every flow owns its own
    capabilities. They differ under --capability-pool, which is what lets flow count and
    capability-type count move independently — the ratio ADR-0014 §6 says nobody has
    measured.
    """
    type_index = index if type_index is None else type_index
    shape = SHAPES[type_index % len(SHAPES)]
    prefix = f"F{type_index:04d}"
    identity = f"f{index:04d}"
    steps = shape["steps"]
    compensated = set(shape["compensate"])
    stage_types = [f"{prefix}Request"] + [f"{prefix}Stage{n}" for n in range(1, steps + 1)]

    chain: list[str] = []

    for n in range(1, steps + 1):
        line = f"            .Step<{prefix}Step{n}>()"

        if n in compensated:
            line += f".CompensateWith<{prefix}Undo{n}>()"

        if shape["policy"] == n:
            # Retry on a step declared non-idempotent is FLOWX1014, so this is attached to
            # step 1, which declares itself idempotent and effect-free. The expression is
            # written inline rather than hoisted into a shared static, because inline is
            # what PolicySetReader parses and therefore what the generator is charged for.
            line += (
                "\n                .WithPolicy(PolicySet.Named(\"resilient\")"
                ".Timeout(TimeSpan.FromSeconds(2)).Retry(3))"
            )

        chain.append(line)

    if shape["emit"]:
        chain.append(
            f"            .Emit<{prefix}Completed>(ctx => new {prefix}Completed(\n"
            f"                ctx.Get<{stage_types[steps]}>().Key,\n"
            f"                ctx.Get<{stage_types[steps]}>().Amount))"
        )

    if shape["emit_on_failure"]:
        chain.append(
            f"            .EmitOnFailure<{prefix}Abandoned>(ctx => new {prefix}Abandoned(\n"
            f"                ctx.Get<{stage_types[0]}>().Key))"
        )

    chain.append(
        f"            .Return(ctx => new {prefix}Result(\n"
        f"                ctx.Get<{stage_types[steps]}>().Key,\n"
        f"                ctx.Get<{stage_types[steps]}>().Amount));"
    )

    return f"""/// <summary>Synthetic flow <c>{identity}.process</c>, shape {type_index % len(SHAPES)}.</summary>
[Flow("{identity}.process", Version = "1.0.0", Profile = ExecutionProfile.Ephemeral, Owner = "team-{index % 8}")]
[FlowDeadline("PT30S")]
public sealed partial class F{index:04d}Flow : Flow<{prefix}Request, {prefix}Result>
{{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<{prefix}Request, {prefix}Result> flow)
    {{
        ArgumentNullException.ThrowIfNull(flow);

        flow
{chr(10).join(chain)}
    }}
}}"""


def flow_source(index: int, body_lines: int = 0) -> str:
    """The complete source file for one synthetic flow that owns its capabilities."""
    return "\n".join(type_parts(index, body_lines) + [flow_block(index)]) + "\n"


FILE_HEADER = ["using FlowX;", "", f"namespace {NAMESPACE};", ""]


def generate(
    flows: int,
    out: pathlib.Path,
    repo: pathlib.Path,
    body_lines: int = 0,
    capability_pool: int = 0,
) -> None:
    """Write the whole project, replacing anything already at `out`."""
    if flows < 1:
        raise SystemExit("--flows must be at least 1.")

    if body_lines < 0:
        raise SystemExit("--capability-body-lines cannot be negative.")

    if capability_pool < 0:
        raise SystemExit("--capability-pool cannot be negative.")

    for project in ("FlowX.Abstractions", "FlowX.Core", "FlowX.Runtime", "FlowX.Compiler"):
        path = repo / "src" / project / f"{project}.csproj"

        if not path.is_file():
            # Failing here beats emitting a project that cannot restore and letting the
            # measurement script report a build failure as though it were a slow build.
            raise SystemExit(f"Not a FlowX repository: {path} does not exist.")

    if out.exists():
        shutil.rmtree(out)

    (out / "Flows").mkdir(parents=True)

    (out / "Directory.Build.props").write_text(EMPTY_BUILD_FILE, encoding="utf-8")
    (out / "Directory.Build.targets").write_text(EMPTY_BUILD_FILE, encoding="utf-8")
    (out / "Support.cs").write_text(SUPPORT_SOURCE, encoding="utf-8")

    (out / "ScaleSynthetic.csproj").write_text(
        CSPROJ_TEMPLATE.format(
            namespace=NAMESPACE,
            assembly="ScaleSynthetic",
            abstractions=repo / "src/FlowX.Abstractions/FlowX.Abstractions.csproj",
            core=repo / "src/FlowX.Core/FlowX.Core.csproj",
            runtime=repo / "src/FlowX.Runtime/FlowX.Runtime.csproj",
            compiler=repo / "src/FlowX.Compiler/FlowX.Compiler.csproj",
        ),
        encoding="utf-8",
    )

    pool = flows if capability_pool in (0, None) else min(capability_pool, flows)

    if pool == flows:
        # The default project, unchanged to the byte: one file per flow holding its own
        # contracts, its own capabilities and its flow class. scripts/check-generator-cost.py
        # compares a SHA of these sources against a committed baseline and refuses the
        # comparison when it moves, so this path must stay exactly as it was.
        steps = 0
        compensations = 0

        for index in range(flows):
            shape = SHAPES[index % len(SHAPES)]
            steps += shape["steps"]
            compensations += len(shape["compensate"])
            (out / "Flows" / f"F{index:04d}Flow.cs").write_text(
                flow_source(index, body_lines), encoding="utf-8")

        capability_types = steps + compensations
    else:
        # Capability reuse. `pool` distinct sets of contracts and capabilities are declared
        # once, and every flow draws its steps from set `index % pool`. Flow count and
        # capability-type count then move independently, which is the whole point: ADR-0014
        # §6 records that real projects reuse capabilities, that this would give a real
        # 200-flow solution proportionally fewer capability types than the synthetic one,
        # and that the effect has never been measured.
        (out / "Capabilities").mkdir(parents=True)

        steps = 0
        compensations = 0

        for index in range(pool):
            shape = SHAPES[index % len(SHAPES)]
            steps += shape["steps"]
            compensations += len(shape["compensate"])
            (out / "Capabilities" / f"F{index:04d}Types.cs").write_text(
                "\n".join(type_parts(index, body_lines)) + "\n", encoding="utf-8")

        for index in range(flows):
            (out / "Flows" / f"F{index:04d}Flow.cs").write_text(
                "\n".join(FILE_HEADER + [flow_block(index, index % pool)]) + "\n",
                encoding="utf-8")

        capability_types = steps + compensations

    print(
        f"{flows} flows, {steps} capability steps, {compensations} compensations, "
        f"{capability_types} capability types ({capability_types / flows:.2f} per flow), "
        f"{body_lines} padding lines per capability body, in {out}"
    )


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--flows", type=int, default=200, help="how many flows to emit")
    parser.add_argument("--out", type=pathlib.Path, required=True, help="target directory")
    parser.add_argument(
        "--repo",
        type=pathlib.Path,
        default=pathlib.Path(__file__).resolve().parent.parent,
        help="FlowX repository root the project references",
    )
    parser.add_argument(
        "--capability-body-lines",
        type=int,
        default=0,
        help="pad every capability body with this many extra statements (default 0). "
             "Varies body size without varying the number of failure paths.",
    )
    parser.add_argument(
        "--capability-pool",
        type=int,
        default=0,
        help="share capability types across flows: every flow draws its steps from one of "
             "this many declared sets (default 0, meaning one set per flow). Lowers the "
             "capability-types-per-flow ratio without changing the flow count.",
    )

    args = parser.parse_args(argv)
    generate(
        args.flows,
        args.out.resolve(),
        args.repo.resolve(),
        body_lines=args.capability_body_lines,
        capability_pool=args.capability_pool,
    )

    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
