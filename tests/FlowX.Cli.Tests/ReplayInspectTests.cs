using Shouldly;
using Xunit;

namespace FlowX.Cli.Tests;

/// <summary>
/// <c>flowx replay --mode inspect</c> against a real journal.
/// </summary>
/// <remarks>
/// <para>
/// <strong>These tests are the compensating control
/// [ADR-0020](../../docs/adr/ADR-0020-cli-reads-the-journal-as-rows.md) names.</strong> The
/// decision accepts that the CLI knows PostgreSQL column names and that no compiler check
/// ties <c>Replay/JournalReader.cs</c> to <c>0001_initial_schema.sql</c>. A column rename
/// therefore breaks the verb at run time, and this class is the only thing that notices —
/// which is why <see cref="ReplayFixture"/> refuses to skip when a database was promised.
/// </para>
/// <para>
/// <strong>Two things the verb must not paper over, both asserted here.</strong>
/// <c>flow_instance.input</c> is NULL on every row ever written, and a <c>Parallel</c> fork
/// attributes one branch's captured id to its sibling's row. An <c>inspect</c> output that
/// rendered either as though it were reliable would be the CLI lying about the store, so
/// <see cref="ANullInputIsRenderedAsUnknownRatherThanAsEmpty"/> and
/// <see cref="AStepInsideAForkCarriesTheAttributionCaveat"/> pin the honest rendering.
/// </para>
/// </remarks>
[Collection(CliConsoleGroup.Name)]
public sealed class ReplayInspectTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static (int ExitCode, string Out, string Error) Inspect(ReplayFixture fixture, params string[] extra)
    {
        string[] args =
        [
            "replay",
            "--mode", "inspect",
            "--connection", CliPostgresDatabase.ConnectionString!,
            "--schema", fixture.Schema,
            .. extra,
        ];

        return CliRunner.Run(args);
    }

    [Fact]
    public async Task InspectRendersACompletedInstanceInCommitOrder()
    {
        await using var fixture = await ReplayFixture.CreateAsync(Cancellation);

        var instance = await fixture.StartAsync("order.place", "acme", Cancellation);

        await fixture.CommitAsync(
            instance, StepScope.Root, 0, "order.validate", JournalOutcome.Success,
            Cancellation, duration: TimeSpan.FromMilliseconds(2));

        await fixture.CommitAsync(
            instance, StepScope.Root, 1, "inventory.reserve", JournalOutcome.Success,
            Cancellation, duration: TimeSpan.FromMilliseconds(41));

        await fixture.CompleteAsync(instance, FlowInstanceState.Completed, Cancellation);

        var (exitCode, output, _) = Inspect(fixture, "--instance", instance.ToString());

        exitCode.ShouldBe(0);

        output.ShouldContain("order.place@1.0.0", Case.Sensitive);
        output.ShouldContain(instance.ToString(), Case.Sensitive);
        output.ShouldContain("tenant acme", Case.Sensitive);
        output.ShouldContain("Completed", Case.Sensitive);

        output.ShouldContain("order.validate@1.0.0", Case.Sensitive);
        output.ShouldContain("inventory.reserve@1.0.0", Case.Sensitive);
        output.ShouldContain("2ms", Case.Sensitive);
        output.ShouldContain("41ms", Case.Sensitive);

        output.IndexOf("order.validate", StringComparison.Ordinal)
            .ShouldBeLessThan(
                output.IndexOf("inventory.reserve", StringComparison.Ordinal),
                "Steps render in commit order, which is what flow_step.sequence records. " +
                "An operator reading a history out of order during an incident draws the " +
                "wrong causal conclusion from it.");
    }

    [Fact]
    public async Task AnUnknownInstanceExitsNonZeroAndSaysWhereItLooked()
    {
        await using var fixture = await ReplayFixture.CreateAsync(Cancellation);

        var absent = Guid.CreateVersion7();

        var (exitCode, _, stderr) = Inspect(fixture, "--instance", absent.ToString());

        exitCode.ShouldBe(
            3,
            "Not found, the same code a missing manifest gets. An operator who mistyped an " +
            "instance id needs 'I looked and it is not there', and a script needs it " +
            "distinguishable from 'you called me wrong'.");

        stderr.ShouldContain(absent.ToString(), Case.Sensitive);
        stderr.ShouldContain(
            fixture.Schema,
            Case.Sensitive,
            "Naming the schema is what turns this from a dead end into a diagnosis: the " +
            "overwhelmingly likely cause is reading the wrong one.");
    }

    [Fact]
    public async Task AForEachRendersItsScopesDistinctly()
    {
        await using var fixture = await ReplayFixture.CreateAsync(Cancellation);

        var instance = await fixture.StartAsync("order.ship", tenantId: null, Cancellation);

        // The same step id, three times, once per element — which is exactly why the journal
        // key carries the scope and (instance, step) is not unique.
        await fixture.CommitAsync(
            instance, StepScope.Root.Element(0), 4, "label.print", JournalOutcome.Success, Cancellation);

        await fixture.CommitAsync(
            instance, StepScope.Root.Element(1), 4, "label.print", JournalOutcome.Success, Cancellation);

        await fixture.CommitAsync(
            instance, StepScope.Root.Element(1).Element(2), 4, "label.stamp", JournalOutcome.Success,
            Cancellation);

        var (exitCode, output, _) = Inspect(fixture, "--instance", instance.ToString());

        exitCode.ShouldBe(0);

        output.ShouldContain("step 4[0]", Case.Sensitive);
        output.ShouldContain("step 4[1]", Case.Sensitive);
        output.ShouldContain(
            "step 4[1/2]",
            Case.Sensitive,
            "A nested loop's scope is '1/2', and flattening it would make an element of an " +
            "inner loop indistinguishable from an element of the outer one.");

        output.Split("step 4").Length.ShouldBe(
            4,
            "Three rows, three renderings. A history that collapsed a ForEach's iterations " +
            "into one line would hide the element that actually failed.");
    }

    [Fact]
    public async Task AFailedAttemptAndItsRetryAreBothShown()
    {
        await using var fixture = await ReplayFixture.CreateAsync(Cancellation);

        var instance = await fixture.StartAsync("order.pay", tenantId: null, Cancellation);

        await fixture.CommitAsync(
            instance, StepScope.Root, 2, "payment.capture", JournalOutcome.Failure,
            Cancellation, attempt: 1, duration: TimeSpan.FromMilliseconds(1610));

        await fixture.CommitAsync(
            instance, StepScope.Root, 2, "payment.capture", JournalOutcome.Success,
            Cancellation, attempt: 2, duration: TimeSpan.FromMilliseconds(120));

        var (exitCode, output, _) = Inspect(fixture, "--instance", instance.ToString());

        exitCode.ShouldBe(0);

        output.ShouldContain("attempt 1", Case.Sensitive);
        output.ShouldContain("attempt 2", Case.Sensitive);

        output.ShouldContain(
            "FAIL",
            Case.Sensitive,
            "The failed attempt stays visible after the retry succeeded. A history that " +
            "showed only the winning attempt would hide the retry storm that is usually the " +
            "thing being investigated.");

        output.ShouldContain("1.61s", Case.Sensitive);
        output.ShouldContain("120ms", Case.Sensitive);
    }

    [Fact]
    public async Task ANullInputIsRenderedAsUnknownRatherThanAsEmpty()
    {
        await using var fixture = await ReplayFixture.CreateAsync(Cancellation);

        var instance = await fixture.StartAsync("order.place", tenantId: null, Cancellation);

        await fixture.CommitAsync(
            instance, StepScope.Root, 0, "order.validate", JournalOutcome.Success, Cancellation);

        var (exitCode, output, _) = Inspect(fixture, "--instance", instance.ToString());

        exitCode.ShouldBe(0);

        output.ShouldContain(
            "Input: unknown",
            Case.Sensitive,
            "flow_instance.input is NULL, and NULL does not distinguish 'this flow was " +
            "started with no input' from 'the input was never captured'. Rendering the " +
            "first would be the CLI making a claim the store does not support.");

        output.ShouldNotContain(
            "Input: {}",
            Case.Sensitive,
            "An empty object is a positive claim about what the flow received. It is the " +
            "specific lie this assertion exists to prevent.");

        output.ShouldNotContain("Input: none", Case.Sensitive);
        output.ShouldContain("NULL", Case.Sensitive, "Say which column, so the reader can check.");
    }

    [Fact]
    public async Task AStepInsideAForkCarriesTheAttributionCaveat()
    {
        await using var fixture = await ReplayFixture.CreateAsync(Cancellation);

        var manifest = TestManifests.WriteParallelManifest();
        var instance = await fixture.StartAsync("order.screen", tenantId: null, Cancellation);

        await fixture.CommitAsync(
            instance, StepScope.Root, 1, "screen.sanctions", JournalOutcome.Success, Cancellation,
            capture: new NondeterminismCapture { NewIds = [Guid.CreateVersion7()] });

        await fixture.CommitAsync(
            instance, StepScope.Root, 2, "screen.fraud", JournalOutcome.Success, Cancellation);

        var (exitCode, output, _) = Inspect(
            fixture, "--instance", instance.ToString(), "--manifest", manifest);

        exitCode.ShouldBe(0);

        output.ShouldContain(
            "Caveats",
            Case.Sensitive,
            "A fork's capture is attributed to whichever branch committed first, so a value " +
            "shown against one branch may have been minted by its sibling. Rendering it " +
            "without saying so is the CLI lying about the store.");

        output.ShouldContain("ADR-0015", Case.Sensitive, "Point at the record that states the limit.");
        output.ShouldContain(
            "Parallel",
            Case.Sensitive,
            "Name the construct, so the caveat can be acted on rather than merely noticed.");

        output.ShouldContain(
            "step 1",
            Case.Sensitive,
            "The caveat is scoped to the steps it actually applies to, which is what the " +
            "manifest join buys. An unscoped warning on every history teaches the reader to " +
            "ignore it.");
    }

    [Fact]
    public async Task AHistoryWithNoManifestSaysTheForkCheckDidNotRun()
    {
        await using var fixture = await ReplayFixture.CreateAsync(Cancellation);

        var instance = await fixture.StartAsync("order.screen", tenantId: null, Cancellation);

        await fixture.CommitAsync(
            instance, StepScope.Root, 1, "screen.sanctions", JournalOutcome.Success, Cancellation,
            capture: new NondeterminismCapture { NewIds = [Guid.CreateVersion7()] });

        var (exitCode, output, _) = Inspect(fixture, "--instance", instance.ToString());

        exitCode.ShouldBe(0, "A history is still worth reading without the plan beside it.");

        output.ShouldContain(
            "no manifest",
            Case.Sensitive,
            "Without the plan the CLI cannot tell which steps are branches of a Parallel, so " +
            "it cannot scope the attribution caveat. Saying nothing would let the reader " +
            "conclude the check ran and found nothing.");
    }

    [Fact]
    public async Task ACaptureIsRenderedUnderTheStepThatRecordedIt()
    {
        await using var fixture = await ReplayFixture.CreateAsync(Cancellation);

        var instance = await fixture.StartAsync("order.place", tenantId: null, Cancellation);
        var minted = Guid.CreateVersion7();

        await fixture.CommitAsync(
            instance, StepScope.Root, 0, "order.validate", JournalOutcome.Success, Cancellation,
            capture: new NondeterminismCapture
            {
                UtcNow = new DateTimeOffset(2026, 7, 30, 9, 14, 0, 1, TimeSpan.Zero),
                NewIds = [minted],
                RandomSeed = 4711,
            });

        var (exitCode, output, _) = Inspect(fixture, "--instance", instance.ToString());

        exitCode.ShouldBe(0);

        output.ShouldContain("Non-deterministic values captured", Case.Sensitive);
        output.ShouldContain("2026-07-30T09:14:00.001", Case.Sensitive);
        output.ShouldContain(minted.ToString(), Case.Sensitive);
        output.ShouldContain("4711", Case.Sensitive, "A seed of record is what makes a run reproducible.");
    }

    [Fact]
    public async Task InspectEmitsParsableJsonThatCarriesTheSameCaveats()
    {
        await using var fixture = await ReplayFixture.CreateAsync(Cancellation);

        var instance = await fixture.StartAsync("order.place", "acme", Cancellation);

        await fixture.CommitAsync(
            instance, StepScope.Root, 0, "order.validate", JournalOutcome.Success, Cancellation);

        var (exitCode, stdout, _) = Inspect(
            fixture, "--instance", instance.ToString(), "--format", "json");

        exitCode.ShouldBe(0);

        using var document = System.Text.Json.JsonDocument.Parse(stdout);
        var root = document.RootElement;

        root.GetProperty("flow").GetString().ShouldBe("order.place@1.0.0");
        root.GetProperty("steps").GetArrayLength().ShouldBe(1);

        root.GetProperty("inputKnown").GetBoolean().ShouldBeFalse(
            "The JSON has to carry the same doubt the text does. A machine reader that saw " +
            "only `\"input\": null` would be free to treat it as an empty payload, which is " +
            "the exact conclusion the text output refuses to let a human draw.");

        root.GetProperty("caveats").GetArrayLength().ShouldBeGreaterThan(
            0, "The unscoped-fork caveat applies: no manifest was given.");
    }
}
