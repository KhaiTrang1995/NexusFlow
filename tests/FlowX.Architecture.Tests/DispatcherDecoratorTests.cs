using Mono.Cecil;
using Shouldly;
using Xunit;

namespace FlowX.Architecture.Tests;

/// <summary>
/// Every <c>IStepDispatcher</c> decorator forwards every member of the interface.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The one rule on this interface a compiler cannot state.</strong> Half of
/// <c>IStepDispatcher</c> is default implementations, and they exist for a good reason: a
/// dispatcher for an ephemeral flow with no cache and no audit has nothing to describe, and
/// requiring it to write six members returning <c>JournalPayload.Empty</c> would be noise. The
/// cost is that a <em>decorator</em> — a type whose whole contract is "the inner one, plus
/// something" — silently answers for the inner one when it forgets a member. It compiles, it
/// passes, and the feature behind that member turns off.
/// </para>
/// <para>
/// <strong>Written because it happened.</strong> <c>StepTelemetry</c> forwarded eleven of
/// fourteen members and inherited <c>DescribeAudit</c>, <c>DescribeCacheKey</c> and
/// <c>DescribeCacheEntry</c>. It is installed by <c>FlowHost</c> whenever anything is listening,
/// which is a process-global condition — so attaching an exporter made every audit record go out
/// with a null document and made <c>CacheKey</c> read <c>null</c> and treat every cached step as
/// uncached. Neither failed anything. It surfaced as an intermittent sample test, because a
/// <c>MeterListener</c> in a neighbouring test class was alive while an audited transfer ran in
/// parallel.
/// </para>
/// <para>
/// Asked of the IL rather than the source: a forward can be an expression body, a block, or an
/// explicit interface implementation, and only one of the three is greppable.
/// </para>
/// </remarks>
public sealed class DispatcherDecoratorTests
{
    private const string DispatcherInterface = "FlowX.Runtime.IStepDispatcher";

    /// <summary>
    /// A decorator declares an implementation for every member the interface declares.
    /// </summary>
    /// <remarks>
    /// Name comparison rather than full signature, because <c>IStepDispatcher</c> overloads
    /// nothing — and a rule that matched signatures would report a forward whose parameter the
    /// interface had renamed, which is not a defect.
    /// </remarks>
    [Fact]
    public void EveryDispatcherDecoratorForwardsEveryMember()
    {
        var expected = InterfaceMembers();

        expected.Count.ShouldBeGreaterThan(
            10,
            "the interface was read from the built assembly, so an empty set here means the " +
            "scan found the wrong type rather than that the interface shrank.");

        var decorators = Decorators();

        decorators.ShouldNotBeEmpty(
            "StepTelemetry and SubstitutingDispatcher are both decorators, so a run that finds " +
            "none is a broken scan passing vacuously.");

        var missing = decorators
            .SelectMany(decorator => expected
                .Where(member => !decorator.Members.Contains(member))
                .Select(member => decorator.Name + " does not forward " + member))
            .ToList();

        missing.ShouldBeEmpty(
            "A decorator that omits a member inherits the interface's default and answers for " +
            "the dispatcher it wraps — which turns the feature behind that member off without " +
            "failing anything. Forward it, even when the decorator adds nothing to it.");
    }

    /// <summary>Every instance member <c>IStepDispatcher</c> declares.</summary>
    private static HashSet<string> InterfaceMembers()
    {
        using var runtime = CompiledAssemblies.Read("FlowX.Runtime");

        var contract = IlSurvey
            .AllTypes(runtime)
            .Single(static type => type.FullName == DispatcherInterface);

        return contract
            .Methods
            .Where(static method => !method.IsStatic)
            .Select(static method => method.Name)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Types that implement the interface and hold one, which is what a decorator is.
    /// </summary>
    /// <remarks>
    /// A generated dispatcher composes a child flow's dispatcher too, but holds it by its
    /// concrete type — so the field's type is what separates "wraps a dispatcher" from
    /// "dispatches to a composed flow", and the rule does not need a name list to maintain.
    /// </remarks>
    private static List<Decorator> Decorators()
    {
        var found = new List<Decorator>();

        foreach (var assembly in CompiledAssemblies.ShippingAssemblies)
        {
            // Read eagerly rather than yielding the TypeDefinition: Cecil's members are read
            // through the module, and handing one back outliving its `using` reads a closed file.
            using var module = CompiledAssemblies.Read(assembly);

            found.AddRange(
                IlSurvey
                    .AllTypes(module)
                    .Where(IsDecorator)
                    .Select(static type => new Decorator(
                        type.FullName,
                        type.Methods
                            .Where(static method => !method.IsStatic && !IlSurvey.IsCompilerGenerated(method))
                            .Select(static method => Simple(method.Name))
                            .ToHashSet(StringComparer.Ordinal))));
        }

        return found;
    }

    private static bool IsDecorator(TypeDefinition type) =>
        type.Interfaces.Any(static i => i.InterfaceType.FullName == DispatcherInterface)
        && type.Fields.Any(
            static field => !field.IsStatic && field.FieldType.FullName == DispatcherInterface);

    /// <summary>A decorator's name and the members it declares itself.</summary>
    private sealed record Decorator(string Name, IReadOnlySet<string> Members);

    /// <summary>The member name without the interface an explicit implementation prefixes.</summary>
    private static string Simple(string name)
    {
        var dot = name.LastIndexOf('.');

        return dot < 0 ? name : name[(dot + 1)..];
    }
}
