using System.Composition;
using System.Reflection;
using FlowX.Compiler.Diagnostics;
using Microsoft.CodeAnalysis.CodeFixes;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.CodeFixes.Tests;

/// <summary>
/// Fitness functions for the fixes assembly, and the seam it was split along.
/// </summary>
/// <remarks>
/// A code fix fails silently by construction. If it is not exported, not discovered, or
/// claims an id nothing raises, the developer sees a diagnostic with no lightbulb and
/// concludes there is no fix — there is no error message anywhere in that story. These
/// tests are the only thing between "the fix exists" and "the fix is reachable".
/// </remarks>
public sealed class CodeFixFitnessTests
{
    private static readonly Assembly Fixes = typeof(FlowMustBePartialCodeFixProvider).Assembly;

    private static readonly Type[] Providers =
        [.. Fixes.GetTypes().Where(static t => !t.IsAbstract && typeof(CodeFixProvider).IsAssignableFrom(t))];

    /// <summary>
    /// The fixes assembly must not link against the compiler.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The seam this work package is built on, stated as an assertion because it is
    /// invisible in the source: adding a <c>ProjectReference</c> to FlowX.Compiler looks
    /// harmless, compiles, and passes every other test here.
    /// </para>
    /// <para>
    /// It breaks at install time instead. Both projects are
    /// <c>DevelopmentDependency</c> analyzer assets and a development dependency does not
    /// flow transitively, so a consumer of the fixes package would get an assembly whose
    /// reference the compiler host cannot resolve — and a compiler extension that fails
    /// to load is dropped without a message. The failure would surface as "the quick
    /// actions do not appear on my machine", which is not a bug report anyone can act on.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheFixesAssemblyDoesNotLinkAgainstTheCompiler() =>
        Fixes.GetReferencedAssemblies()
            .Select(static a => a.Name)
            .ShouldNotContain(
                "FlowX.Compiler",
                "See this test's remarks: the reference resolves locally and fails for consumers.");

    /// <summary>
    /// Every id a provider claims must be a diagnostic the compiler actually raises.
    /// </summary>
    /// <remarks>
    /// The price of the previous test. Because the fixes cannot read
    /// <see cref="FlowXDiagnostics"/>, the ids are string literals over there and the two
    /// lists could drift — a typo, or a rule renamed in the catalogue — leaving a fix
    /// registered for an id nothing reports. This project references both assemblies and
    /// is therefore the only place the drift is observable. Same arrangement, and same
    /// reason, as <c>PolicyStageFitnessTests</c> in FlowX.Compiler.Tests.
    /// </remarks>
    [Fact]
    public void EveryFixableIdIsARealDiagnostic()
    {
        var known = FlowXDiagnostics.All.Select(static d => d.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var provider in Providers)
        {
            var ids = Instantiate(provider).FixableDiagnosticIds;

            ids.ShouldNotBeEmpty($"{provider.Name} fixes nothing.");

            foreach (var id in ids)
            {
                known.ShouldContain(
                    id,
                    $"{provider.Name} offers a fix for {id}, which is not in FlowXDiagnostics.All. " +
                    "A fix for an id nothing raises is a fix nobody will ever see.");
            }
        }
    }

    /// <summary>
    /// Every provider must be MEF-discoverable, or it does not exist as far as the IDE
    /// is concerned.
    /// </summary>
    /// <remarks>
    /// Both attributes are required and neither is checked by the compiler. Omitting
    /// <c>[Shared]</c> is the subtler of the two: the provider is still discovered, but a
    /// new instance is constructed per request, which is a performance bug that no test
    /// asserting on fixed source text would ever notice.
    /// </remarks>
    [Fact]
    public void EveryProviderIsExportedAndShared()
    {
        Providers.ShouldNotBeEmpty("The fixes assembly contains no CodeFixProvider at all.");

        foreach (var provider in Providers)
        {
            provider.GetCustomAttribute<ExportCodeFixProviderAttribute>()
                .ShouldNotBeNull($"{provider.Name} has no [ExportCodeFixProvider], so MEF will not find it.");

            provider.GetCustomAttribute<SharedAttribute>()
                .ShouldNotBeNull($"{provider.Name} has no [Shared], so the host constructs one per request.");
        }
    }

    /// <summary>
    /// A fixed diagnostic must have a documentation page whose stated repair is the one
    /// the fix performs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The weaker half of a test that would be worth more. The valuable assertion is the
    /// other direction — every diagnostic that <em>claims</em> to be mechanically fixable
    /// has a provider — and it is not written here because "claims to be" has no
    /// machine-readable definition in this codebase. Every page carries a "How to fix it"
    /// section (<c>EveryDiagnosticPageShowsBothTheProblemAndTheFix</c> requires one), so
    /// the section's presence separates nothing; a <c>DiagnosticDescriptor</c> carries no
    /// field saying so; and inventing a second list of "fixable" ids here is precisely
    /// the hand-maintained thing that drifts.
    /// </para>
    /// <para>
    /// The honest version costs a change to the descriptor catalogue — a custom tag on
    /// the descriptors whose repair is mechanical — which belongs to whoever owns
    /// <c>FlowXDiagnostics</c>, not to a consumer of it. Until then this asserts what it
    /// can: nothing is fixed that is not documented.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryFixedDiagnosticIsDocumented()
    {
        var directory = Repository.Directory("docs/diagnostics");

        foreach (var id in Providers.SelectMany(p => Instantiate(p).FixableDiagnosticIds).Distinct(StringComparer.Ordinal))
        {
            var page = Path.Combine(directory, id + ".md");

            File.Exists(page).ShouldBeTrue($"{id} has a code fix but no documentation page.");
            File.ReadAllText(page).ShouldContain("## How to fix it", Case.Sensitive);
        }
    }

    private static CodeFixProvider Instantiate(Type provider) =>
        (CodeFixProvider)Activator.CreateInstance(provider)!;
}
