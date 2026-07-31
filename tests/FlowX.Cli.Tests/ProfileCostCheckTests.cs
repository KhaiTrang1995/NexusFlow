using FlowX.Cli.Manifest;
using FlowX.Cli.Verification;
using Shouldly;
using Xunit;

namespace FlowX.Cli.Tests;

/// <summary>
/// The rule behind <c>flowx verify --cost</c>: a flow declaring <c>Durable</c> and using
/// nothing durability provides.
/// </summary>
/// <remarks>
/// The interesting half of these is the flows the check must <em>not</em> report. A cost
/// rule that fires on a correct saga is a cost rule somebody deletes from the pipeline,
/// and then it catches nothing at all — so every way a flow can legitimately be durable
/// is asserted here alongside the one way it cannot.
/// </remarks>
public sealed class ProfileCostCheckTests
{
    private static ManifestDocument Manifest(params ManifestFlow[] flows) => new()
    {
        Application = new ManifestApplication { Name = "Sample.App", Version = "1.0.0" },
        Flows = [.. flows],
    };

    private static ManifestFlow Flow(string id, string? profile, params ManifestStep[] steps) => new()
    {
        Id = id,
        Version = "1.0.0",
        Profile = profile,
        Steps = [.. steps],
    };

    private static ManifestStep Step(string kind = "Capability") => new()
    {
        Id = 0,
        Kind = kind,
        Capability = kind == "Capability" ? "order.validate@1.0.0" : null,
    };

    [Fact]
    public void ADurableFlowUsingNoneOfDurabilityIsReported()
    {
        var report = ProfileCostCheck.Run(Manifest(Flow("report.daily", "Durable", Step())));

        report.Passed.ShouldBeFalse();
        report.DurableFlows.ShouldBe(1);
        report.Flagged.ShouldBe(1);

        var finding = report.Findings[0];

        finding.Code.ShouldBe("FLOWX-VERIFY-001");
        finding.Subject.ShouldBe("flow report.daily@1.0.0");
        finding.Summary.ShouldContain("no compensation, no signal and no timer", Case.Sensitive);
        finding.Consequence.ShouldContain("journal write", Case.Sensitive,
            "A finding that says what changed and not what it costs gets skimmed past.");
    }

    [Fact]
    public void AnEphemeralFlowIsNeverReportedHoweverBareItIs()
    {
        var report = ProfileCostCheck.Run(Manifest(Flow("order.price", "Ephemeral", Step())));

        report.Passed.ShouldBeTrue();
        report.DurableFlows.ShouldBe(0,
            "Ephemeral is the default and costs nothing extra. The check has no opinion on it.");
    }

    [Fact]
    public void AFlowWithNoProfileAtAllIsNotReported()
    {
        // The attribute's default is Ephemeral, so an absent profile is not a durable one.
        var report = ProfileCostCheck.Run(Manifest(Flow("order.price", profile: null, Step())));

        report.DurableFlows.ShouldBe(0);
        report.Passed.ShouldBeTrue();
    }

    [Fact]
    public void ACompensationClearsIt()
    {
        var compensating = new ManifestStep
        {
            Id = 0,
            Kind = "Capability",
            Capability = "inventory.reserve@1.0.0",
            Compensation = "inventory.release@1.0.0",
        };

        var report = ProfileCostCheck.Run(Manifest(Flow("order.place", "Durable", compensating)));

        report.Passed.ShouldBeTrue("A saga is the reason the Durable profile exists.");
        report.DurableFlows.ShouldBe(1, "It was examined and cleared, not skipped.");
    }

    [Theory]
    [InlineData("AwaitSignal")]
    [InlineData("Delay")]
    public void ASignalOrATimerClearsIt(string kind)
    {
        // Delay is in flowx.manifest.schema.json and this repository's generator has no
        // case for it yet. The CLI reads the schema, not the current emitter, so the day
        // it does the check is already right.
        var report = ProfileCostCheck.Run(Manifest(Flow("order.await", "Durable", Step(kind))));

        report.Passed.ShouldBeTrue();
    }

    [Fact]
    public void ACompensationNestedInABranchClearsIt()
    {
        var nested = new ManifestStep
        {
            Id = 0,
            Kind = "Condition",
            Branches =
            [
                [
                    new ManifestStep
                    {
                        Id = 1,
                        Kind = "Capability",
                        Capability = "payment.capture@1.0.0",
                        Compensation = "payment.refund@1.0.0",
                    },
                ],
            ],
        };

        var report = ProfileCostCheck.Run(Manifest(Flow("order.place", "Durable", nested)));

        report.Passed.ShouldBeTrue(
            "Compensating inside a When arm is compensating. Reading only the top level " +
            "would report every conditional saga there is.");
    }

    private static ManifestStep Compose(string flowId, string? mode = null) =>
        new() { Id = 0, Kind = "SubFlow", Flow = flowId, Mode = mode };

    [Theory]
    [InlineData(null)]
    [InlineData("Inline")]
    public void AnInlineSubFlowThatCompensatesClearsTheParent(string? mode)
    {
        // Inline is the DSL's default, so an absent mode reads as Inline: the child's
        // steps run inside the parent's execution and its compensations are the parent's.
        var child = Flow("payment.take", "Durable", new ManifestStep
        {
            Id = 0,
            Kind = "Capability",
            Capability = "payment.capture@1.0.0",
            Compensation = "payment.refund@1.0.0",
        });

        var report = ProfileCostCheck.Run(
            Manifest(Flow("order.place", "Durable", Compose("payment.take", mode)), child));

        report.DurableFlows.ShouldBe(2);
        report.Passed.ShouldBeTrue("Both flows were examined, and neither is bare.");
    }

    [Fact]
    public void ADetachedSubFlowThatCompensatesDoesNotClearTheParent()
    {
        var child = Flow("audit.record", "Durable", new ManifestStep
        {
            Id = 0,
            Kind = "Capability",
            Capability = "audit.write@1.0.0",
            Compensation = "audit.undo@1.0.0",
        });

        var report = ProfileCostCheck.Run(
            Manifest(Flow("order.place", "Durable", Compose("audit.record", "Detached")), child));

        report.Findings.Select(f => f.Subject).ShouldBe(["flow order.place@1.0.0"],
            "A detached child has its own deadline, lifecycle and profile. What it does " +
            "is no argument for the parent being durable.");
    }

    [Fact]
    public void AnAwaitCompletionSubFlowClearsTheParentByItself()
    {
        // The child is present and bare, so nothing about it can clear the parent and the
        // abstention for an unresolvable child cannot fire either. Only the mode is left.
        var report = ProfileCostCheck.Run(Manifest(
            Flow("order.place", "Durable", Compose("payment.take", "AwaitCompletion")),
            Flow("payment.take", "Durable", Step())));

        report.Findings.Select(f => f.Subject).ShouldBe(["flow payment.take@1.0.0"],
            "Suspending until a child finishes is durability by itself.");
    }

    [Fact]
    public void AnUnresolvableSubFlowMakesTheCheckAbstain()
    {
        // The child was compiled into another assembly, so this manifest names it and does
        // not describe it. The parent may well compensate down there.
        var report = ProfileCostCheck.Run(
            Manifest(Flow("order.place", "Durable", Compose("shipping.arrange"))));

        report.Passed.ShouldBeTrue(
            "Accusing a flow the check cannot fully see is what gets the check switched " +
            "off. A missed finding costs storage; a false one costs the rule.");
    }

    [Fact]
    public void ACompositionCycleTerminatesAndIsStillReported()
    {
        var a = Flow("a", "Durable", Compose("b"));
        var b = Flow("b", "Durable", Compose("a"));

        var report = ProfileCostCheck.Run(Manifest(a, b));

        report.Flagged.ShouldBe(2, "Two flows composing each other and doing nothing else.");
    }

    [Fact]
    public void FindingsAreOrderedByFlowIdSoTwoRunsProduceTheSameReport()
    {
        var report = ProfileCostCheck.Run(Manifest(
            Flow("z.last", "Durable", Step()),
            Flow("a.first", "Durable", Step())));

        report.Findings.Select(f => f.Subject)
            .ShouldBe(["flow a.first@1.0.0", "flow z.last@1.0.0"]);
    }

    [Fact]
    public void TheReportCarriesTheApplicationSoAReaderKnowsWhatWasChecked()
    {
        var report = ProfileCostCheck.Run(Manifest(Flow("report.daily", "Durable", Step())));

        report.Application.ShouldBe("Sample.App");
        report.Version.ShouldBe("1.0.0");
    }

    [Fact]
    public void AnEmptyManifestPassesAndSaysNothingWasChecked()
    {
        var report = ProfileCostCheck.Run(new ManifestDocument());

        report.Passed.ShouldBeTrue();
        report.DurableFlows.ShouldBe(0);
    }

    [Fact]
    public void RejectsANullManifest()
        => Should.Throw<ArgumentNullException>(() => ProfileCostCheck.Run(null!));
}
