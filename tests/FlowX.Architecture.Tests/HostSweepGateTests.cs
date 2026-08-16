using Mono.Cecil;
using Mono.Cecil.Cil;
using Shouldly;
using Xunit;

namespace FlowX.Architecture.Tests;

/// <summary>
/// Every background sweep asks whether this host was deployed to run it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a rule rather than a review.</strong> Six sweeps each decided for themselves
/// whether they <em>could</em> run and none could be told whether they <em>should</em>, so
/// the three-role topology <c>docs/18-Cloud-Native.md §1</c> draws had nothing behind it —
/// the environment variable both that document and <c>docs/28-Azure-Hosting.md</c> named,
/// <c>FLOWX_TRIGGERS</c>, appeared nowhere in the source. Six gates were added. The seventh
/// sweep, written a year from now by somebody who has not read this, is what this rule is
/// for: without it that sweep runs on every host including the ones deliberately opted out,
/// and the symptom is duplicated work in production rather than a failing build.
/// </para>
/// <para>
/// <strong>Read off the IL, not the source.</strong> A comment can say a service is gated;
/// only the compiled method can prove it reads the field. The rule looks for a load of the
/// <c>_deployed</c> field inside <c>ExecuteAsync</c>, which is what the gate compiles to
/// however the condition is spelt.
/// </para>
/// <para>
/// <strong>What it cannot check.</strong> That the gate is on the <em>right</em> flag — a
/// service reading its neighbour's flag satisfies this rule. <c>HostSweepsTests</c> covers
/// the flag semantics, and the mapping is one line per service in plain sight. What this
/// catches is the case that has no reviewer at all: a new sweep with no gate whatsoever.
/// </para>
/// </remarks>
public sealed class HostSweepGateTests
{
    /// <summary>The field every gated service reads before it starts its loop.</summary>
    private const string GateField = "_deployed";

    /// <summary>The method a <c>BackgroundService</c> does its work in.</summary>
    private const string Entry = "ExecuteAsync";

    private const string BackgroundService = "Microsoft.Extensions.Hosting.BackgroundService";

    /// <summary>
    /// Every hosted sweep consults its deployment gate before doing anything.
    /// </summary>
    [Fact]
    public void EveryBackgroundSweepAsksWhetherThisHostRunsIt()
    {
        var sweeps = CompiledAssemblies.Read("FlowX.Hosting").Types
            .Where(static type => type.BaseType?.FullName == BackgroundService)
            .OrderBy(static type => type.Name, StringComparer.Ordinal)
            .ToList();

        sweeps.ShouldNotBeEmpty(
            "No BackgroundService was found in FlowX.Hosting. Either the sweeps moved or this "
            + "rule is reading the wrong assembly, and a rule that inspects nothing passes "
            + "whatever the code does.");

        var ungated = new List<string>();

        foreach (var sweep in sweeps)
        {
            // FlowXStartupValidation is a BackgroundService that runs once at start-up and
            // sweeps nothing. It validates the declared triggers against the catalogues, which
            // every host must do whatever it was deployed to run -- an api host with a
            // misdeclared trigger is still misconfigured.
            if (sweep.Name == "FlowXStartupValidation")
            {
                continue;
            }

            if (!sweep.Fields.Any(static field => field.Name == GateField))
            {
                ungated.Add($"{sweep.Name} has no {GateField} field");

                continue;
            }

            var entry = sweep.Methods.FirstOrDefault(static method => method.Name == Entry);

            if (entry is null)
            {
                ungated.Add($"{sweep.Name} has no {Entry} to inspect");

                continue;
            }

            var body = Body(sweep, entry);

            if (body is null)
            {
                ungated.Add($"{sweep.Name}.{Entry} has no body to inspect");

                continue;
            }

            var reads = body.Instructions.Any(static instruction =>
                instruction.OpCode == OpCodes.Ldfld
                && instruction.Operand is FieldReference field
                && field.Name == GateField);

            if (!reads)
            {
                ungated.Add($"{sweep.Name}.{Entry} never reads {GateField}");
            }
        }

        ungated.ShouldBeEmpty(
            "A background sweep runs on every host, including the ones a deployment opted out "
            + "of with FlowXOptions.Sweeps. It will duplicate the work the three-role split "
            + "exists to separate, and the symptom is not a failing build — it is two nodes "
            + "firing the same schedule in production:"
            + Environment.NewLine + string.Join(Environment.NewLine, ungated));
    }

    /// <summary>
    /// The IL that actually runs, which for an <c>async</c> method is not the method.
    /// </summary>
    /// <param name="declaring">The service the method belongs to.</param>
    /// <param name="method">The entry point named in the source.</param>
    /// <returns>The body to inspect, or null when there is none.</returns>
    /// <remarks>
    /// An <c>async</c> method compiles to a stub that starts a state machine, and every
    /// statement written in the source ends up in that machine's <c>MoveNext</c>. Reading the
    /// stub finds no field access at all — which is exactly what this rule reported on its
    /// first run, against six services that were correctly gated. The rule was wrong, not the
    /// code, and the correction is recorded here rather than quietly applied.
    /// </remarks>
    private static MethodBody? Body(TypeDefinition declaring, MethodDefinition method)
    {
        var machine = method.CustomAttributes
            .FirstOrDefault(static attribute =>
                attribute.AttributeType.Name == "AsyncStateMachineAttribute")
            ?.ConstructorArguments[0].Value as TypeReference;

        if (machine is null)
        {
            return method.HasBody ? method.Body : null;
        }

        var moveNext = declaring.NestedTypes
            .FirstOrDefault(nested => nested.FullName == machine.FullName)
            ?.Methods.FirstOrDefault(static nested => nested.Name == "MoveNext");

        return moveNext?.HasBody == true ? moveNext.Body : null;
    }
}
