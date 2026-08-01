using System.Reflection;
using FlowX.Compiler.Emit;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// The compiler's copy of the policy stage ordering must match the real one.
/// </summary>
/// <remarks>
/// <para>
/// <c>ManifestWriter</c> hard-codes which stage each policy kind runs in, because it
/// targets netstandard2.0 and cannot reference <c>FlowX.Abstractions</c> — the same
/// constraint that makes the model layer its own thing.
/// </para>
/// <para>
/// An unpinned copy of a safety ordering is exactly the kind of duplication that drifts
/// silently: ADR-0011 says the ordering guarantees <em>are</em> the safety property, so a
/// manifest that reported <c>Cache</c> in <c>Resilience</c> would describe a system that
/// caches before it authorises. This test reads the real mapping out of
/// <c>PolicySet</c> by calling every builder method and inspecting what it produced,
/// then compares.
/// </para>
/// </remarks>
public sealed class PolicyStageFitnessTests
{
    [Fact]
    public void PolicyStagesMatchTheAbstraction()
    {
        var actual = ReadRealMapping();

        foreach (var (kind, stage) in ManifestWriter.KnownPolicyStages)
        {
            actual.ShouldContainKey(kind,
                $"ManifestWriter knows a policy kind '{kind}' that PolicySet does not " +
                "declare. Remove it, or add the builder method.");

            actual[kind].ShouldBe(stage,
                $"ManifestWriter puts '{kind}' in {stage}; PolicySet puts it in " +
                $"{actual[kind]}. ADR-0011 makes the ordering the safety property, so " +
                "a manifest that disagrees describes a system that does not exist.");
        }
    }

    [Fact]
    public void EveryPolicyTheAbstractionOffersHasAKnownStage()
    {
        // The other direction. A policy added to PolicySet and not here would be silently
        // dropped from the manifest — present in the code, absent from the description of
        // the code, which is the failure the manifest exists to prevent.
        foreach (var kind in ReadRealMapping().Keys)
        {
            ManifestWriter.KnownPolicyStages.ShouldContainKey(kind,
                $"PolicySet offers '{kind}' but ManifestWriter has no stage for it, so it " +
                "would be dropped from the manifest. Add it to PolicyStages.");
        }
    }

    /// <summary>
    /// The emitter's copy of the one kind that wraps a compensation must match the real one.
    /// </summary>
    /// <remarks>
    /// <c>FlowEmitter</c> decides which half of a declared set goes on the step and which half
    /// goes on its compensation by comparing against this name, and it cannot reference
    /// <c>CompensationPolicy</c> for the reason <c>ManifestWriter</c> cannot reference
    /// <c>PolicySet</c>. A drift here would not fail a build: it would quietly put the
    /// compensation retry on the forward chain, where nothing reads it, and the undo would go
    /// back to a single attempt with the manifest still advertising five.
    /// </remarks>
    [Fact]
    public void TheEmittersCompensationRetryKindMatchesTheRealOne()
    {
        FlowEmitter.CompensationRetryKind.ShouldBe(
            CompensationPolicy.CompensationRetryKind,
            "The emitter splits a policy set on this name. If it stops matching, every " +
            "declared compensation retry is silently emitted onto the wrong chain.");

        ManifestWriter.KnownPolicyStages.ShouldContainKey(FlowEmitter.CompensationRetryKind);
    }

    /// <summary>
    /// The kind-to-stage mapping, read by invoking each builder method on an empty set.
    /// </summary>
    /// <remarks>
    /// Reflection over the real type rather than a second hand-written list, which would
    /// only move the drift problem into the test.
    /// </remarks>
    private static Dictionary<string, string> ReadRealMapping()
    {
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);

        var builders = typeof(PolicySet)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.ReturnType == typeof(PolicySet) && m.DeclaringType == typeof(PolicySet));

        foreach (var builder in builders)
        {
            var arguments = builder.GetParameters()
                .Select(p => p.HasDefaultValue ? p.DefaultValue : Sample(p.ParameterType))
                .ToArray();

            var produced = (PolicySet)builder.Invoke(PolicySet.Named("probe"), arguments)!;

            foreach (var policy in produced.Policies)
            {
                mapping[policy.Kind] = policy.Stage.ToString();
            }
        }

        return mapping;
    }

    /// <summary>A usable argument for a builder parameter that has no default.</summary>
    private static object? Sample(Type type)
    {
        if (type == typeof(TimeSpan))
        {
            return TimeSpan.FromSeconds(1);
        }

        if (type == typeof(int))
        {
            return 1;
        }

        if (type == typeof(double))
        {
            return 0.5d;
        }

        if (type == typeof(string))
        {
            return "probe";
        }

        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
