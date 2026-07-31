using System.Reflection;
using Shouldly;
using Xunit;

namespace FlowX.Abstractions.Tests;

/// <summary>
/// The shape of <c>.SubFlow&lt;TFlow, TSubIn&gt;(...)</c> as a caller sees it.
/// </summary>
/// <remarks>
/// Two properties, both of which stop a whole class of mistake before any FlowX rule has
/// to be written: that the thing being composed is constrained to be a flow, and that the
/// default mode is the safe one.
/// </remarks>
public sealed class SubFlowSurfaceTests
{
    private static MethodInfo SubFlow => typeof(IFlowBuilder<,>).GetMethods()
        .Single(m => m.Name == "SubFlow");

    [Fact]
    public void ComposingSomethingThatIsNotAFlowIsAnOrdinaryCSharpError()
    {
        // Without the constraint, `.SubFlow<CapturePayment, …>()` compiles and the mistake
        // surfaces as a missing `Plan` member in generated code — a diagnostic pointing at
        // a file the author did not write. With it, the C# compiler reports it on their own
        // line, which is better than anything this catalogue could produce and costs no rule.
        var flowParameter = SubFlow.GetGenericArguments()[0];

        flowParameter.GetGenericParameterConstraints()
            .ShouldContain(typeof(Flow),
                "TFlow must be constrained to Flow. The non-generic base exists for this " +
                "and for nothing else.");
    }

    [Fact]
    public void TheDefaultModeIsTheSynchronousOne()
    {
        // `default(SubFlowMode)` is Inline, and the parameter defaults to it. Both matter:
        // Inline is the mode that waits, fails the parent when the child fails, and undoes
        // the child when the parent fails. A default that fired and forgot would mean an
        // author who wrote nothing got the mode with no error propagation and no saga.
        default(SubFlowMode).ShouldBe(SubFlowMode.Inline);

        SubFlow.GetParameters()[1].DefaultValue.ShouldBe(SubFlowMode.Inline);
    }

    [Fact]
    public void AwaitCompletionKeepsItsMemberEvenThoughItDoesNotShip()
    {
        // The enum is public surface, and an id is a forever commitment (constraint C7).
        // Deleting the member would turn a documented mode into a spelling mistake;
        // FLOWX1026 names the reason instead, which is more use than a missing member.
        Enum.GetNames<SubFlowMode>().ShouldBe(["Inline", "Detached", "AwaitCompletion"]);
    }

    [Fact]
    public void TheFlowBaseCannotBeDerivedFromOutsideTheAbstractions()
    {
        // It exists to be a constraint, not a base to inherit. A flow is still declared by
        // deriving from Flow<TIn, TOut>; this type never appears in user code.
        typeof(Flow).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .ShouldAllBe(c => c.IsFamilyAndAssembly || c.IsAssembly || c.IsPrivate);
    }
}
