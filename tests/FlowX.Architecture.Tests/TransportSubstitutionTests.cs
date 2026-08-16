using Shouldly;
using Xunit;

namespace FlowX.Architecture.Tests;

/// <summary>
/// WP-71's unchanged-file assertion: moving a chain between transports changes attributes and
/// one adapter step, and changes no business logic.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What the work package asked for, and what its structure allows.</strong> WP-71 was
/// specified as an assertion that a business file is *unchanged* when a transport moves —
/// a diff. <c>samples/event-driven</c> is built the other way round: one set of capabilities
/// under four flows, one per transport
/// (docs/adr/ADR-0062-transport-portability-is-a-property-of-the-capability-chain.md),
/// so there is nothing for a diff to compare — a shared file cannot differ from itself, and an
/// assertion that it does not would pass for ever while saying nothing. A diff is also the
/// weaker instrument in a second way: it can only judge a change while that change is in a
/// diff, and it has to be told which commit was the transport switch.
/// </para>
/// <para>
/// <strong>So the boundary is asserted instead.</strong> The four arms must write one chain,
/// character for character below the adapter step; they must differ only in the trigger
/// attribute; their per-transport files must hold nothing but the declaration; and every
/// capability the chain names must be declared once, outside those files. That is the same
/// promise stated as a property rather than as an event: if business logic is ever attached to
/// one transport, it lands in one of the four places this checks, whatever commit does it.
/// </para>
/// <para>
/// <strong>What it cannot catch.</strong> It reads declarations, so it does not know that the
/// four arms *behave* alike — <c>tests/EventDriven.Tests</c> owns that, comparing the four
/// compiled plans and then running one billing reference through a real broker, a real change
/// feed and a real cron occurrence. It does not read the adapter capability's body, so a
/// transport-shaped rule hidden inside <c>ReadInvoiceRequest</c> is invisible here (though
/// <see cref="TransportIsolationTests"/> would see a transport *type* reached from a flow).
/// And it says nothing about transports the sample does not have: Kafka is WP-72's
/// obligation, and adding it means adding a fifth arm that has to satisfy all four assertions
/// below.
/// </para>
/// </remarks>
public sealed class TransportSubstitutionTests
{
    private const string Sample = "samples/event-driven";

    /// <summary>
    /// The flows that carry one chain over four transports, by their shared id prefix.
    /// </summary>
    /// <remarks>
    /// <c>invoice.request</c> is deliberately not one of them: it is the emitter that gives the
    /// two event-driven arms something to consume, and it shares a step with them without
    /// sharing their chain. Matching on the prefix rather than on "every flow in the sample" is
    /// what keeps a fifth flow with a different job from being read as a fifth transport.
    /// </remarks>
    private const string ArmPrefix = "invoice.issue.";

    private const string Guard = "ArgumentNullException.ThrowIfNull";

    /// <summary>The four arms write one chain, and differ by at most one adapter step.</summary>
    [Fact]
    public void TheTransportArmsWriteOneChainBelowTheAdapterStep()
    {
        var arms = Arms();
        var shared = SharedChain(arms);
        var findings = new List<string>();

        foreach (var arm in arms)
        {
            var adapter = arm.Chain.Take(arm.Chain.Count - shared.Count).Select(static call => call.Text).ToList();

            if (adapter.Count > 1)
            {
                findings.Add($"{arm.Where} writes {string.Join(" ", adapter)} above what the arms agree on");
            }
            else if (adapter.Count == 1 && !adapter[0].StartsWith("Step<", StringComparison.Ordinal))
            {
                findings.Add($"{arm.Where} adapts its trigger with {adapter[0]} rather than a step");
            }
        }

        findings.ShouldBeEmpty(
            "These flows carry the same capability chain over different transports and no "
            + "longer write the same chain. The shared part is "
            + $"'{string.Join(" ", shared)}', and portability is a property of that chain one "
            + "adapter step in (V2, quality goal Q4):" + Environment.NewLine
            + string.Join(Environment.NewLine, findings));
    }

    /// <summary>Everything the arms declare about themselves, bar the trigger, is one declaration.</summary>
    [Fact]
    public void TheTransportArmsDifferOnlyInTheirTriggerAttribute()
    {
        var arms = Arms();
        var first = arms[0];
        var findings = new List<string>();

        foreach (var arm in arms.Skip(1))
        {
            if (!Identity(arm).SequenceEqual(Identity(first), StringComparer.Ordinal))
            {
                findings.Add(
                    $"{arm.Where} declares {string.Join(" ", Identity(arm))}, "
                    + $"{first.Where} declares {string.Join(" ", Identity(first))}");
            }

            if (arm.BaseNames[^1] != first.BaseNames[^1])
            {
                findings.Add($"{arm.Where} returns {arm.BaseNames[^1]}, {first.Where} returns {first.BaseNames[^1]}");
            }
        }

        findings.ShouldBeEmpty(
            "The arms of one chain differ in something other than the trigger they answer. The "
            + "flow id is expected to differ and the input contract is fixed by the trigger; "
            + "the version, the profile, the owner, the deadline and the output contract are "
            + "the chain's, and a per-transport value for any of them is a flow that is no "
            + "longer the same flow behind a different door:" + Environment.NewLine
            + string.Join(Environment.NewLine, findings));
    }

    /// <summary>A per-transport file declares the flow and nothing else.</summary>
    [Fact]
    public void NoPerTransportFileHoldsBusinessLogic()
    {
        var types = SampleSurvey.TypesIn(Sample);
        var findings = new List<string>();

        foreach (var arm in Arms())
        {
            var neighbours = types.Where(type => type.File == arm.File && type.Name != arm.Name).ToList();

            findings.AddRange(neighbours.Select(type => $"{arm.File} also declares {type.Name}"));

            findings.AddRange(arm.Members
                .Where(static member => member != "method Define")
                .Select(member => $"{arm.Where} declares {member}"));

            findings.AddRange(arm.StatementsBesideTheChain
                .Where(static statement => !statement.StartsWith(Guard, StringComparison.Ordinal))
                .Select(statement => $"{arm.Where} runs `{statement}` in Define"));
        }

        findings.ShouldBeEmpty(
            "A file that exists because of one transport holds something other than the "
            + "declaration that binds the chain to it. Whatever is in it is logic one transport "
            + "has and the others do not, which is the claim this sample exists to disprove:"
            + Environment.NewLine + string.Join(Environment.NewLine, findings));
    }

    /// <summary>Every capability the arms step through is declared once, outside their files.</summary>
    [Fact]
    public void TheChainsCapabilitiesAreSharedRatherThanCopiedPerTransport()
    {
        var types = SampleSurvey.TypesIn(Sample);
        var arms = Arms();
        var armFiles = arms.Select(static arm => arm.File).ToHashSet(StringComparer.Ordinal);
        var findings = new List<string>();

        foreach (var step in arms.SelectMany(static arm => arm.Steps).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var declarations = types.Where(type => type.Name == step).ToList();

            if (declarations.Count != 1)
            {
                findings.Add($"{step} is declared {declarations.Count} times: "
                    + string.Join(", ", declarations.Select(static type => type.Where)));

                continue;
            }

            if (armFiles.Contains(declarations[0].File))
            {
                findings.Add($"{step} is declared in {declarations[0].File}, a per-transport file");
            }
        }

        findings.ShouldBeEmpty(
            "A capability these flows step through is copied per transport or lives in a "
            + "transport's own file. One chain under four transports is the sample's claim; two "
            + "copies of a capability are two chains that will diverge:" + Environment.NewLine
            + string.Join(Environment.NewLine, findings));
    }

    /// <summary>
    /// There are still four arms, four transports and a chain worth sharing.
    /// </summary>
    /// <remarks>
    /// Every assertion above is satisfied by an empty set of arms, and three of them by one
    /// arm. What makes them mean anything is that four flows really do reach one chain over
    /// four different trigger kinds, which is the state
    /// PLAN.md's V2 row records and the thing a fifth transport has to join.
    /// </remarks>
    [Fact]
    public void TheArmSurveyCanStillSeeItsSubject()
    {
        var arms = Arms();

        arms.Count.ShouldBe(
            4,
            $"{Sample} no longer reaches one chain over four transports, so every assertion in "
            + "this class is measuring something smaller than V2's claim.");

        arms.SelectMany(Triggers).Distinct(StringComparer.Ordinal).Count().ShouldBe(
            4,
            "The four arms no longer carry four distinct trigger kinds, so 'the same chain over "
            + "four transports' is now the same chain over fewer:" + Environment.NewLine
            + string.Join(Environment.NewLine, arms.Select(arm => $"{arm.Where} {string.Join(" ", Triggers(arm))}")));

        var shared = SharedChain(arms);

        shared.Count.ShouldBeGreaterThan(
            3,
            "The arms now agree on fewer than four calls. A chain that short is agreed on "
            + "trivially, and the comparison above stops being evidence of anything.");

        arms.Count(arm => arm.Chain.Count > shared.Count).ShouldBeGreaterThan(
            0,
            "No arm carries an adapter step any more, so the one-step allowance the comparison "
            + "makes is never exercised and a chain that grows a step on one transport would "
            + "read as the adapter it is allowed.");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// The calls every arm ends with, in order — the chain they carry between them.
    /// </summary>
    /// <remarks>
    /// A common suffix rather than one arm's chain taken as the truth. No arm is the reference
    /// implementation: the sample's claim is that the four agree, and when they stop agreeing a
    /// gate that had picked a favourite reports the three that did not change. What is reported
    /// instead is where agreement stops, which is the fact.
    /// </remarks>
    private static List<string> SharedChain(List<TypeSite> arms)
    {
        if (arms.Count == 0)
        {
            return [];
        }

        var chains = arms.Select(static arm => arm.Chain.Select(static call => call.Text).ToList()).ToList();
        var shared = 0;

        while (shared < chains.Min(static chain => chain.Count)
            && chains.TrueForAll(chain => chain[^(shared + 1)] == chains[0][^(shared + 1)]))
        {
            shared++;
        }

        return [.. chains[0].TakeLast(shared)];
    }

    private static List<TypeSite> Arms() =>
        SampleSurvey.TypesIn(Sample)
            .Where(static type => type.FlowId?.StartsWith(ArmPrefix, StringComparison.Ordinal) == true)
            .OrderBy(static type => type.FlowId, StringComparer.Ordinal)
            .ToList();

    /// <summary>Attribute names ending in <c>Trigger</c> — what the arm answers.</summary>
    private static IEnumerable<string> Triggers(TypeSite arm) =>
        arm.Attributes.Select(Named).Where(static name =>
            name.EndsWith("Trigger", StringComparison.Ordinal)
            || name.EndsWith("TriggerAttribute", StringComparison.Ordinal));

    /// <summary>
    /// Everything an arm declares about itself apart from its trigger and its own id.
    /// </summary>
    /// <remarks>
    /// The id is replaced rather than dropped: two arms declaring the same id would be a
    /// different defect and one the manifest catches, and blanking the whole attribute would
    /// take the version, the profile and the owner out of the comparison with it.
    /// </remarks>
    private static IEnumerable<string> Identity(TypeSite arm) =>
        arm.Attributes
            .Where(attribute => !Triggers(arm).Contains(Named(attribute), StringComparer.Ordinal))
            .Select(attribute => attribute.Replace($"\"{arm.FlowId}\"", "<id>", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);

    private static string Named(string attribute)
    {
        var arguments = attribute.IndexOf('(', StringComparison.Ordinal);

        return arguments < 0 ? attribute : attribute[..arguments];
    }
}
