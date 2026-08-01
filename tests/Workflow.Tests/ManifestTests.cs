using System.Text.Json;
using FlowX.Generated;
using Shouldly;
using Xunit;

namespace Workflow.Tests;

/// <summary>
/// The manifest this sample's build produced.
/// </summary>
/// <remarks>
/// Asserted against the generated constant rather than a file, because that is what the build
/// actually emits — <c>flowx manifest</c> only copies it to disk. If the two ever disagree,
/// the CLI is wrong, not this.
/// </remarks>
public sealed class ManifestTests
{
    private static readonly JsonDocument Manifest = JsonDocument.Parse(FlowXManifest.Json);

    private static JsonElement Flow(string id) => Manifest.RootElement
        .GetProperty("flows").EnumerateArray()
        .Single(f => f.GetProperty("id").GetString() == id);

    [Fact]
    public void BothFlowsArePublishedWithTheirProfileAndDeadline()
    {
        var parent = Flow("employee.onboard");

        parent.GetProperty("version").GetString().ShouldBe("2.0.0");
        parent.GetProperty("profile").GetString().ShouldBe("Durable");
        parent.GetProperty("deadline").GetString().ShouldBe("PT60S");

        var child = Flow("workspace.provision");

        child.GetProperty("profile").GetString().ShouldBe("Durable");
        child.GetProperty("deadline").GetString().ShouldBe("PT20S");
    }

    /// <summary>The national insurance number is marked, and the manifest says so.</summary>
    /// <remarks>
    /// The same array the generator puts on the flow as <c>SensitiveMembers</c>, which is what
    /// keeps the value out of the event body a broker would fan out and out of every journal
    /// row this durable flow commits for as long as it is retained.
    /// </remarks>
    [Fact]
    public void TheNationalIdIsRecordedAsSensitive()
    {
        Flow("employee.onboard").GetProperty("input").GetProperty("sensitive")
            .EnumerateArray().Select(e => e.GetString())
            .ShouldBe(["NationalId"]);

        OnboardEmployeeFlow.SensitiveMembers.ShouldBe(["NationalId"]);
    }

    /// <summary>
    /// The suspension point publishes what it waits for, and how long it declared to wait.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the assertion that stops
    /// <a href="../../docs/adr/ADR-0021-manifest-publishes-the-wait.md">ADR-0021</a>'s two
    /// fields repeating <c>authorization.value</c>'s history.</strong> That field was declared
    /// by the schema, read by a Breaking <c>flowx diff</c> rule, and written by nothing — so
    /// the rule could not fire on any manifest FlowX produced, and neither
    /// <c>ManifestSchemaTests</c> nor <c>DiffCodeDocumentationTests</c> could see it. What was
    /// missing was exactly this: an assertion that a <em>real compilation</em> puts the field
    /// in the document, under the name the rule reads.
    /// </para>
    /// <para>
    /// <c>timeout</c> is the interesting half. The flow declares
    /// <c>Waits.Countersignature</c>, a named property rather than a literal, and the plan
    /// carries that expression verbatim — so the value below exists only because the compiler
    /// followed the constant one hop to <c>TimeSpan.FromDays(7)</c> and rendered it. Without
    /// that hop this application would publish no <c>timeout</c> at all, and the field would
    /// have a producer on paper and none in practice.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheSuspensionPointPublishesItsSignalAndItsDeclaredWait()
    {
        var wait = Flow("offer.accept").GetProperty("steps").EnumerateArray()
            .Single(s => s.GetProperty("kind").GetString() == "AwaitSignal");

        wait.GetProperty("signal").GetString().ShouldBe(Signals.OfferCountersigned);
        wait.GetProperty("timeout").GetString().ShouldBe("P7D");

        Waits.Countersignature.ShouldBe(
            TimeSpan.FromDays(7),
            "The published duration is the declared one folded, so a change to the constant " +
            "that did not reach the manifest would leave these two disagreeing.");
    }

    /// <summary>
    /// The flow that waits declares an ordinary <c>[HttpTrigger]</c>, like every other.
    /// </summary>
    /// <remarks>
    /// It did not until WP-64: the generated endpoint answered <c>200</c> with a projected
    /// output a suspended flow does not have, so the attribute was left off and
    /// <c>Program.cs</c> mapped two routes by hand. The manifest is where that shows —
    /// <c>offer.accept</c> had no <c>triggers</c> block at all, which read as "this flow
    /// cannot be started from outside the process" for a flow whose whole point is being
    /// started from outside the process.
    /// </remarks>
    [Fact]
    public void TheFlowThatWaitsPublishesItsAddress()
    {
        var trigger = Flow("offer.accept").GetProperty("triggers").EnumerateArray().Single();

        trigger.GetProperty("kind").GetString().ShouldBe("Http");
        trigger.GetProperty("method").GetString().ShouldBe("POST");
        trigger.GetProperty("route").GetString().ShouldBe("/api/v1/offers");
    }

    /// <summary>The output contract has no secrets.</summary>
    [Fact]
    public void TheOutputContractHasNoSecrets() =>
        Flow("employee.onboard").GetProperty("output").TryGetProperty("sensitive", out _).ShouldBeFalse();

    /// <summary>
    /// The manifest publishes the graph as a <em>tree</em>, where the plan is a flat array.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The two are different views of one flow and neither is the other's copy.</strong>
    /// <c>docs/06-Execution-Engine.md §3</c> is about the plan: a <c>Switch</c> node carrying a
    /// target per case, a <c>Jump</c> closing each case block, an index that advances by one or
    /// to a target. That is what the engine walks, and it is what makes the loop provably
    /// terminate. The manifest is what a consumer reads, so it nests each arm inside the node
    /// that owns it and does not publish the <c>Jump</c>s at all — a jump is layout, not
    /// structure, and a document that listed them would be publishing the compiler's
    /// bookkeeping.
    /// </para>
    /// <para>
    /// This test asserts both, next to each other, because a reader who has seen only one
    /// will reasonably assume the other has the same shape.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheManifestNestsWhatThePlanFlattens()
    {
        // The document: seven top-level nodes, three of which carry their arms inside them.
        Flow("employee.onboard").GetProperty("steps").EnumerateArray()
            .Select(s => s.GetProperty("kind").GetString())
            .ShouldBe(
                [
                    "Capability",  // offer.validate
                    "Switch",      // the two cases and the Fail arm nest inside this
                    "Capability",  // identity.create
                    "Parallel",    // three branches nest inside this
                    "ForEach",     // the body, including its own Condition, nests inside this
                    "SubFlow",     // named, not inlined
                    "Condition",   // both arms nest inside this
                    "Capability",  // welcome.send
                    "Emit",
                ]);

        // The plan: twenty-five nodes in one array, jumps and all.
        OnboardEmployeeFlow.Plan.Graph.Steps.Select(s => s.Kind.ToString()).ShouldBe(
            [
                "Capability", "Switch", "Capability", "Jump", "Capability", "Jump", "Fail",
                "Capability", "Parallel", "Capability", "Capability", "Capability",
                "ForEach", "Branch", "Capability", "Jump", "Capability", "Capability",
                "SubFlow", "Branch", "Capability", "Jump", "Capability", "Capability", "Emit",
            ]);

        // And every target points forward, which is the whole termination argument.
        foreach (var step in OnboardEmployeeFlow.Plan.Graph.Steps)
        {
            if (step.Target is { } target)
            {
                target.ShouldBeGreaterThan(step.Index, "Step " + step.Index + " jumps backwards.");
            }
        }
    }

    /// <summary>The conditional inside the loop is published inside the loop.</summary>
    /// <remarks>
    /// The nesting is the part a table of builder methods cannot show. A consumer reading this
    /// document can tell that the approval decision is taken per item rather than once for the
    /// whole collection, which is a different flow.
    /// </remarks>
    [Fact]
    public void TheConditionalInsideTheLoopIsPublishedInsideTheLoop()
    {
        var loop = Flow("employee.onboard").GetProperty("steps").EnumerateArray()
            .Single(s => s.GetProperty("kind").GetString() == "ForEach");

        var body = loop.GetProperty("branches")[0];

        body.EnumerateArray().Select(s => s.GetProperty("kind").GetString())
            .ShouldBe(["Condition", "Capability"]);

        body[0].GetProperty("branches").EnumerateArray()
            .Select(arm => arm[0].GetProperty("capability").GetString())
            .ShouldBe(["equipment.approve@1.0.0", "equipment.auto_clear@1.0.0"]);

        body[1].GetProperty("compensation").GetString().ShouldBe("equipment.return@1.0.0");
    }

    /// <summary>
    /// Every compensable step publishes the capability that undoes it, wherever it sits.
    /// </summary>
    /// <remarks>
    /// Walked recursively, because the document nests: four of this flow's six compensations
    /// are inside a case block, a fork branch or a loop body, and a check that read only the
    /// top level would pass while publishing a saga with one undo in it.
    /// </remarks>
    [Fact]
    public void EveryCompensationIsPublishedWhereverItSits()
    {
        Compensations(Flow("employee.onboard").GetProperty("steps")).Order(StringComparer.Ordinal).ShouldBe(
            [
                "access.revoke@1.0.0",
                "equipment.return@1.0.0",
                "hardware.cancel@1.0.0",
                "identity.disable@1.0.0",
                "payroll.close@1.0.0",
                "supplier.void@1.0.0",
            ]);

        // The child's two are in the child's own entry, not in its parent's.
        Compensations(Flow("workspace.provision").GetProperty("steps")).Order(StringComparer.Ordinal)
            .ShouldBe(["workspace.cancel_pass@1.0.0", "workspace.release_desk@1.0.0"]);
    }

    /// <summary>Every step kind under a node, however deeply nested.</summary>
    private static IEnumerable<string> Kinds(JsonElement steps)
    {
        foreach (var step in steps.EnumerateArray())
        {
            if (step.GetProperty("kind").GetString() is { } kind)
            {
                yield return kind;
            }

            if (step.TryGetProperty("branches", out var branches))
            {
                foreach (var branch in branches.EnumerateArray())
                {
                    foreach (var nested in Kinds(branch))
                    {
                        yield return nested;
                    }
                }
            }
        }
    }

    /// <summary>Every capability under a node, however deeply nested.</summary>
    private static IEnumerable<string> Capabilities(JsonElement steps)
    {
        foreach (var step in steps.EnumerateArray())
        {
            if (step.TryGetProperty("capability", out var capability) &&
                capability.GetString() is { } id)
            {
                yield return id;
            }

            if (step.TryGetProperty("branches", out var branches))
            {
                foreach (var branch in branches.EnumerateArray())
                {
                    foreach (var nested in Capabilities(branch))
                    {
                        yield return nested;
                    }
                }
            }
        }
    }

    /// <summary>Every compensation under a node, however deeply nested.</summary>
    private static IEnumerable<string> Compensations(JsonElement steps)
    {
        foreach (var step in steps.EnumerateArray())
        {
            if (step.TryGetProperty("compensation", out var compensation) &&
                compensation.GetString() is { } id)
            {
                yield return id;
            }

            if (step.TryGetProperty("branches", out var branches))
            {
                foreach (var branch in branches.EnumerateArray())
                {
                    foreach (var nested in Compensations(branch))
                    {
                        yield return nested;
                    }
                }
            }
        }
    }

    /// <summary>
    /// The event is published, and needs no suppression to be.
    /// </summary>
    /// <remarks>
    /// <c>samples/ecommerce</c> has to suppress <c>FLOWX1024</c> on its <c>Emit</c>, because
    /// an <c>Ephemeral</c> flow keeps no transaction for the outbox row to join. This one is
    /// <c>Durable</c>, so the rule is satisfied rather than silenced — the same line of code
    /// with a different profile above it.
    /// </remarks>
    [Fact]
    public void TheEventIsPublishedWithoutASuppression()
    {
        Flow("employee.onboard").GetProperty("emits").EnumerateArray()
            .Select(e => e.GetString())
            .ShouldBe(["employee.onboarded"]);
    }

    /// <summary>
    /// The only trigger is the parent's, and the composed child has none.
    /// </summary>
    /// <remarks>
    /// A flow reached by being composed is not an endpoint. Publishing an address for
    /// <c>workspace.provision</c> would advertise a way to allocate a desk outside the saga
    /// that is supposed to release it.
    /// </remarks>
    [Fact]
    public void OnlyTheParentDeclaresATrigger()
    {
        Flow("employee.onboard").GetProperty("triggers").EnumerateArray()
            .Select(t => t.GetProperty("method").GetString() + " " + t.GetProperty("route").GetString())
            .ShouldBe(["POST /api/v1/onboarding"]);

        Flow("workspace.provision").TryGetProperty("triggers", out _).ShouldBeFalse(
            "A flow reached by being composed is not an endpoint. Publishing an address for " +
            "workspace.provision would advertise a way to allocate a desk outside the saga " +
            "that is supposed to release it.");
    }

    /// <summary>The source pointer is relative to the project.</summary>
    /// <remarks>
    /// An absolute path would differ between two machines that compiled identical source,
    /// breaking the determinism ADR-0005 requires of this document — and shipping the build
    /// agent's directory layout to whoever reads it.
    /// </remarks>
    [Fact]
    public void TheSourcePointerIsRelativeToTheProject()
    {
        var source = Flow("employee.onboard").GetProperty("source").GetString().ShouldNotBeNull();

        source.ShouldStartWith("OnboardEmployeeFlow.cs:");
        source.ShouldNotStartWith("/");
        source.ShouldNotContain(":\\");
    }

    /// <summary>
    /// Nothing in the manifest suspends, which is the sample's absent half stated in the
    /// document a consumer reads.
    /// </summary>
    /// <remarks>
    /// <c>AwaitSignal</c> is a kind the manifest can publish — <c>ManifestWriter</c> has a case
    /// for it. Its absence here is therefore a fact about this flow rather than about the
    /// document's vocabulary, and it is the one the old README got wrong.
    /// </remarks>
    [Fact]
    public void NoStepInEitherFlowSuspends()
    {
        foreach (var id in new[] { "employee.onboard", "workspace.provision" })
        {
            Kinds(Flow(id).GetProperty("steps"))
                .ShouldNotContain("AwaitSignal", id + " declares a suspension point.");
        }
    }
}
