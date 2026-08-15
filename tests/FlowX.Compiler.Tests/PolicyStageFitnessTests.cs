using System.Reflection;
using FlowX.Compiler.Analysis;
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

    /// <summary>
    /// The compiler's copy of which stances the runtime cannot decide matches the runtime's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>FLOWX1037</c> names a set of stance names, and <c>StepAuthorization</c> names the
    /// same set as enum members. The two are separate because <c>FlowX.Compiler</c> targets
    /// netstandard2.0, loads into the compiler process and cannot reference the runtime it
    /// compiles for — the same constraint <c>PolicyStagesMatchTheAbstraction</c> exists
    /// under.
    /// </para>
    /// <para>
    /// <strong>The drift this catches is the one that matters most.</strong> A stance that
    /// becomes decidable while the rule still reports it is a build error over a working
    /// feature; a stance that stops being decidable while the rule stays quiet is a published
    /// authorisation contract that nothing enforces and nothing reports — which is precisely
    /// the defect P4's authorisation work was written to end.
    /// </para>
    /// </remarks>
    [Fact]
    public void AuthorizationStancesMatchTheAbstraction()
    {
        var runtime = Enum.GetValues<Authorization>()
            .Where(StepAuthorization.IsRefusedAtBuildTime)
            .Select(static stance => stance.ToString())
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        var compiler = FlowAnalyzer.StancesTheRuntimeCannotDecide
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        compiler.ShouldBe(
            runtime,
            "FLOWX1037 reports [" + string.Join(", ", compiler) + "] and StepAuthorization " +
            "refuses [" + string.Join(", ", runtime) + "] at build time. A stance in one list " +
            "and not the other is either a build error over a stance that now works, or a " +
            "stance nothing enforces and nothing reports.");
    }

    /// <summary>
    /// Every stance is decided by the engine or reported by the compiler, and none is both.
    /// </summary>
    /// <remarks>
    /// The partition, asserted from the compiler's side. Its counterpart in
    /// <c>tests/FlowX.Runtime.Tests</c> asserts it from the runtime's, and between them a
    /// member of <see cref="Authorization"/> cannot fall through both.
    /// </remarks>
    [Fact]
    public void NoStanceIsBothDecidedAndReported()
    {
        var both = Enum.GetValues<Authorization>()
            .Where(static stance => StepAuthorization.IsDecidedAtRunTime(stance)
                                 && StepAuthorization.IsRefusedAtBuildTime(stance))
            .ToArray();

        both.ShouldBeEmpty(
            "A stance the engine decides and the compiler also refuses would fail a build " +
            "over a capability that works.");
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
    /// Every kind <c>PolicySet</c> offers is applied by the runtime, and the compiler's copy
    /// of that list says so — in both directions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>DeclaredPolicyAnalyzer.ExecutedKinds</c> was the list <c>FLOWX1032</c> was the
    /// complement of. That rule is deleted, because the complement is empty; the list is not,
    /// and it is a hand-written copy for <c>ManifestWriter.KnownPolicyStages</c>'s reason: the
    /// compiler targets netstandard2.0 and can reference none of <c>StepPolicy</c>,
    /// <c>StepAudit</c> or <c>CompensationPolicy</c>. An unpinned copy of "what runs" drifts in
    /// both directions and each is bad in its own way — a kind the list claims and no resolver
    /// reads is a declaration the runtime silently drops, and a kind a resolver gained and the
    /// list did not is a copy that has stopped describing the engine.
    /// </para>
    /// <para>
    /// The real list is read off the three resolvers rather than typed out again:
    /// <c>StepPolicy</c> publishes the nine descriptor kinds it reads as constants — the six
    /// stage-4 ones, the stage-1 <c>RateLimit</c>, the stage-3 <c>Idempotency</c> and the
    /// stage-5 <c>Cache</c> — <c>CompensationPolicy</c> publishes the one it reads, and
    /// <c>StepAudit</c> publishes the stage-7 one. A kind implemented without a constant would
    /// slip past this — which is why they are constants, and why each new resolver publishes
    /// one on the day it starts reading a kind.
    /// </para>
    /// <para>
    /// <strong>The reverse direction is what replaced the rule.</strong> While a kind could be
    /// inert, "every kind <c>PolicySet</c> offers is executed" was false by design and could
    /// not be asserted. It is true now, so it is asserted here — which is the gate that will
    /// notice the next builder method added without a resolver behind it.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryKindPolicySetOffersIsAppliedByTheRuntime()
    {
        string[] executed =
        [
            StepPolicy.RateLimitKind,
            StepPolicy.QuotaKind,
            StepPolicy.ValidateKind,
            StepPolicy.IdempotencyKind,
            StepPolicy.TimeoutKind,
            StepPolicy.RetryKind,
            StepPolicy.CircuitBreakerKind,
            StepPolicy.BulkheadKind,
            StepPolicy.HedgeKind,
            StepPolicy.FallbackKind,
            StepPolicy.CacheKind,
            StepAudit.AuditKind,
            CompensationPolicy.CompensationRetryKind,
        ];

        DeclaredPolicyAnalyzer.ExecutedKinds.OrderBy(k => k, StringComparer.Ordinal).ShouldBe(
            executed.OrderBy(k => k, StringComparer.Ordinal),
            "ExecutedKinds is the compiler's copy of what the runtime applies. If it disagrees " +
            "with what StepPolicy, StepAudit and CompensationPolicy actually read, the copy " +
            "has stopped describing the engine it was written to describe.");

        var declarable = ReadRealMapping();

        // And every one of them is a kind PolicySet can actually produce. A constant naming a
        // descriptor kind nothing emits would claim a policy that does not exist, which reads
        // as correctness and is not.
        foreach (var kind in executed)
        {
            declarable.ShouldContainKey(kind,
                $"'{kind}' is treated as executed, and no PolicySet builder emits it.");
        }

        // The other direction, and the one that replaced FLOWX1032. Every kind an author can
        // declare is applied by one of the three resolvers — so there is no inert declaration
        // left for a rule to report, which is why the rule is deleted rather than narrowed.
        declarable.Keys.OrderBy(k => k, StringComparer.Ordinal).ShouldBe(
            executed.OrderBy(k => k, StringComparer.Ordinal),
            "PolicySet offers a builder whose kind no resolver reads, so a step declaring it " +
            "reaches the plan and the manifest and nothing applies it. That is the state " +
            "FLOWX1032 existed to report; the rule is deleted, so this gate is what is left " +
            "to notice it.");
    }

    /// <summary>
    /// The compiler's copy of what <c>PolicySet</c>'s own well-known sets contain must match
    /// the real ones, in both directions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>PolicySetReader</c> resolves a policy set by walking its initialiser, and a set
    /// declared in a referenced assembly has no initialiser to walk. <c>PolicySet</c>'s own
    /// sets are the exception: the compiler ships beside the assembly that declares them and
    /// their composition is part of FlowX's published surface, so the reader carries a table
    /// rather than inferring one. <c>PolicySet.CompensationDefault</c> is the whole reason it
    /// is worth carrying — <c>docs/06-Execution-Engine.md</c> §7 rule 2 recommends it for
    /// compensation, and it is a metadata symbol in every consuming compilation.
    /// </para>
    /// <para>
    /// <strong>The second direction is the load-bearing one.</strong> A well-known set added
    /// to <c>PolicySet</c> and not to the reader would resolve to nothing in every consuming
    /// build: no plan chain, no manifest entry, and FLOWX1036 reported against FlowX's own
    /// API. That is exactly the state <c>CompensationDefault</c> shipped in.
    /// </para>
    /// </remarks>
    [Fact]
    public void PolicySetContentsAreThePinnedOnes()
    {
        var actual = ReadWellKnownSets();

        foreach (var (name, kinds) in PolicySetReader.WellKnownSets)
        {
            actual.ShouldContainKey(name,
                $"PolicySetReader carries a well-known set '{name}' that PolicySet does not " +
                "declare as a public static member. Remove it, or add the member.");

            actual[name].ShouldBe(kinds,
                $"PolicySetReader says PolicySet.{name} declares [{string.Join(", ", kinds)}]; " +
                $"it declares [{string.Join(", ", actual[name])}]. Every consuming build " +
                "resolves the set from that table, so a stale entry emits the wrong chain " +
                "into the plan and publishes the wrong kinds in the manifest.");
        }

        foreach (var name in actual.Keys)
        {
            PolicySetReader.WellKnownSets.ShouldContainKey(name,
                $"PolicySet declares a well-known set '{name}' and PolicySetReader has no " +
                "entry for it, so every .WithPolicy(PolicySet." + name + ") in a consuming " +
                "assembly resolves to nothing: no plan chain, no manifest entry, and " +
                "FLOWX1036 raised against FlowX's own API.");
        }
    }

    /// <summary>
    /// Every public static <c>PolicySet</c> member of <c>PolicySet</c>, and the kinds it
    /// declares — ordinally sorted, as <c>PolicySetReader</c> returns them.
    /// </summary>
    private static Dictionary<string, string[]> ReadWellKnownSets()
    {
        var sets = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var members = typeof(PolicySet)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(PolicySet))
            .Select(p => (p.Name, Value: p.GetValue(null)))
            .Concat(typeof(PolicySet)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.FieldType == typeof(PolicySet))
                .Select(f => (f.Name, Value: f.GetValue(null))));

        foreach (var (name, value) in members)
        {
            var set = (PolicySet)value!;

            sets[name] = [.. set.Policies
                .Select(policy => policy.Kind)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(kind => kind, StringComparer.Ordinal)];
        }

        return sets;
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

        foreach (var declared in builders)
        {
            // A generic builder is closed over a type this test picks, because the kind and
            // the stage a descriptor carries are the same whatever the type argument is.
            // PolicySet.Fallback<TValue> is the first: the value it captures is typed and the
            // policy it emits is not.
            var builder = declared.IsGenericMethodDefinition
                ? declared.MakeGenericMethod(typeof(int))
                : declared;

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
