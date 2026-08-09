using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// The document this repository actually ships, applied to an empty tenant.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The shipped file and not a fixture, because completeness is a property of the file
/// rather than of the format.</strong> <see cref="SeedReaderTests"/> proves the document can say
/// these things and <see cref="SeedApplierTests"/> proves the applier writes what it is given;
/// neither notices that <c>northwind.json</c> says nothing about seven of the twelve kinds
/// <c>/config</c> answers for. That was the defect: every screen was wired, every store worked,
/// and a fully seeded tenant opened its setup on seven empty lists — which nobody can tell from
/// a feature that does not work.
/// </para>
/// <para>
/// <strong>Read through <see cref="ConfigStore"/>, which is the thing the client calls.</strong>
/// Counting rows in seven tables would pass on a document whose ids do not join — a roll-up
/// naming a field of another object, a dashboard tile pointing at no report — because those are
/// rows too. The read has the joins in it, so a declaration that does not hang together
/// disappears from the answer rather than showing up as a row nobody can draw.
/// </para>
/// </remarks>
public sealed class ShippedSeedTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>Every kind the configuration read can answer for has something in it.</summary>
    /// <remarks>
    /// All twelve and not the seven that were added. A kind that is dropped from the file later
    /// is the same defect arriving from the other direction, and this is where it is caught.
    /// </remarks>
    [Fact]
    public async Task TheShippedSeedDeclaresSomethingOfEveryConfigurableKind()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        await ApplyShippedAsync(crm);

        var config = new ConfigStore(crm.DataSource);

        foreach (var kind in Enum.GetValues<ConfigKind>())
        {
            var items = await config.ListAsync(
                CrmSchemaHarness.Northwind, new ReadConfig(kind, ConfigLimits.MaxItems), Cancellation);

            items.ShouldNotBeEmpty(
                $"the shipped seed leaves a tenant with no {kind}, so its setup screen is empty " +
                "on a tenant that was seeded from the file this repository ships.");

            // A summary is what the screen draws, and the sentence is composed from the row's
            // own columns — so a declaration that joined to nothing would arrive blank here
            // rather than absent.
            items.ShouldAllBe(item => item.Summary.Length > 0);
        }
    }

    /// <summary>
    /// The demand plans reach the period roll-up, against the leads that actually arrived.
    /// </summary>
    /// <remarks>
    /// <strong>The marketing panel of every seeded tenant was empty, and the cause was three
    /// columns.</strong> <c>plan</c> has carried <c>channel</c>, <c>segment</c> and
    /// <c>target_leads</c> since migration <c>0016</c>; the seed's insert bound none of them, so
    /// the schema's <c>CHECK ((kind = 'MarketingLead') = (channel IS NOT NULL))</c> made a demand
    /// plan impossible to write and the file had no word for one either.
    /// </remarks>
    [Fact]
    public async Task TheShippedSeedCommitsDemandThePeriodRollUpCanReport()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        await ApplyShippedAsync(crm);

        var planning = new PlanningStore(crm.DataSource);

        var period = await planning.PeriodAsync(CrmSchemaHarness.Northwind, "fy26_q4", Cancellation);

        period.ShouldNotBeNull("the file declares fy26_q4.");

        var rollUp = await planning.RollUpAsync(
            CrmSchemaHarness.Northwind,
            period,
            DateOnly.FromDateTime(DateTime.UtcNow),
            [],
            Cancellation);

        rollUp.ShouldNotBeNull("the file declares a strategy for fy26_q4.");

        rollUp.Marketing.ShouldNotBeEmpty(
            "a period with no demand plan reports no demand, which reads as a marketing failure.");

        // Every channel a seeded plan names is one a seeded lead could arrive on. A plan for a
        // sixth channel is refused by DefinePlan and would report nought here for ever.
        rollUp.Marketing.ShouldAllBe(
            demand => PlanningLimits.Channels.Contains(demand.Channel, StringComparer.Ordinal));

        rollUp.Marketing.ShouldAllBe(demand => demand.TargetLeads > 0);

        // The leads the file seeds are captured as the seed runs, so the two that arrive on a
        // channel a plan names are counted against it. Asserted as a sum rather than per plan:
        // which channel is ahead is a property of the demo data, and that any of it is counted
        // at all is the property this test is about.
        rollUp.Marketing.Sum(demand => demand.ActualLeads).ShouldBeGreaterThan(
            0, "attainment is read live from lead.source and lead.captured_at.");
    }

    /// <summary>
    /// The year's plan is covered by the quarters' plans that roll into it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>FY26 reported nought committed against 4,200,000 while its own quarters held
    /// commitments.</strong> The arithmetic was right and the model was empty: <c>DefinePlan</c>
    /// has taken a parent since migration <c>0018</c>, the seed document had no word for one, and
    /// the reader refused the Portfolio kind outright — so nothing was ever written into the
    /// year's period and nothing rolled into anything.
    /// </para>
    /// <para>
    /// The quarters are asserted too. "Top-level" used to mean "has no parent at all", so the
    /// moment a quarter's plan named the year's portfolio it disappeared from its own quarter's
    /// board — trading one empty screen for two.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheShippedYearIsCoveredByTheQuartersThatRollIntoIt()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        await ApplyShippedAsync(crm);

        var planning = new PlanningStore(crm.DataSource);
        var performance = new PerformanceStore(crm.DataSource);

        var year = await planning.PeriodAsync(CrmSchemaHarness.Northwind, "fy26", Cancellation);

        year.ShouldNotBeNull("the file declares fy26.");

        var rollUp = await planning.RollUpAsync(
            CrmSchemaHarness.Northwind,
            year,
            DateOnly.FromDateTime(DateTime.UtcNow),
            [],
            Cancellation);

        rollUp.ShouldNotBeNull("the file declares a strategy for fy26.");

        rollUp.Committed.ShouldBeGreaterThan(
            0,
            "the year rolls up nothing, which is a target with no plan under it.");

        var tree = await performance.TreeAsync(
            CrmSchemaHarness.Northwind, year.PeriodId, null, Cancellation);

        var portfolio = tree.Single(node => node.Depth == 1);

        portfolio.Kind.ShouldBe(nameof(PlanKind.Portfolio));
        portfolio.Children.ShouldBe(4, "the four quarterly commitments roll into the year.");

        tree.Count(node => node.Parent == portfolio.Name).ShouldBe(
            4, "the tree draws the year without the plans underneath it.");

        // Covered: the year's own number is exactly what its children committed. The gap
        // arithmetic is the same one the period roll-up does, asked at this node.
        portfolio.Committed.ShouldBe(
            portfolio.Target,
            "the year's plan does not add up to the plans underneath it.");

        portfolio.Gap.ShouldBe(0m);

        rollUp.Committed.ShouldBe(
            portfolio.Target,
            "the period roll-up and the tree disagree about the same plan.");

        foreach (var quarter in (string[])["fy26_q3", "fy26_q4"])
        {
            var period = await planning.PeriodAsync(CrmSchemaHarness.Northwind, quarter, Cancellation);

            period.ShouldNotBeNull();

            (await performance.TreeAsync(
                CrmSchemaHarness.Northwind, period.PeriodId, null, Cancellation))
                .ShouldNotBeEmpty(
                    $"{quarter} draws no plans, so rolling them into the year emptied their own " +
                    "board.");
        }
    }

    /// <summary>
    /// A seeded roll-up carries a number, because the file links the children that feed it.
    /// </summary>
    /// <remarks>
    /// A declaration on its own lists on the setup screen and answers nothing on every parent,
    /// which is the aggregate <c>RollupCapabilities</c> spends its remarks refusing: a nought
    /// nobody investigates. The links are what make it a demonstration rather than a row.
    /// </remarks>
    [Fact]
    public async Task ASeededRollUpHasChildrenToAggregate()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        await ApplyShippedAsync(crm);

        // Six days of discovery, fourteen of migration and four of cutover.
        (await crm.ScalarAsTenantAsync<string>(
            CrmSchemaHarness.Northwind,
            "SELECT values ->> 'effort_days_total' FROM custom_record WHERE record_id = @id",
            Cancellation,
            ("id", SeedIds.For(CrmSchemaHarness.Northwind, "record", "atlas"))))
            .ShouldBe("24");

        // And the formula, which the seed runs at the write for the reason RecordWriter does:
        // a computed field left blank on every seeded row reads as a formula that is broken.
        (await crm.ScalarAsTenantAsync<string>(
            CrmSchemaHarness.Northwind,
            "SELECT values ->> 'effort_hours' FROM custom_record WHERE record_id = @id",
            Cancellation,
            ("id", SeedIds.For(CrmSchemaHarness.Northwind, "record", "atlas_migration"))))
            .ShouldBe("112", "fourteen days at eight hours.");
    }

    /// <summary>
    /// Applying the shipped file twice writes nothing the second time.
    /// </summary>
    /// <remarks>
    /// The claim <see cref="SeedApplierTests"/> makes about the format, made about the document —
    /// and the reason it is worth making twice is <c>entity_label</c>, the one table on this path
    /// whose own statement is an upsert. A seed that let it upsert would overwrite a rename an
    /// administrator had made, every time the container restarted, and report it as written.
    /// </remarks>
    [Fact]
    public async Task ApplyingTheShippedSeedTwiceWritesNothingTheSecondTime()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        var first = await ApplyShippedAsync(crm);
        var second = await ApplyShippedAsync(crm);

        first.Written.ShouldBeGreaterThan(0);
        second.Written.ShouldBe(0, "every id and every name was already taken.");
        second.Present.ShouldBe(first.Written);
    }

    // ------------------------------------------------------------------------------- fixtures

    private static async Task<SeedOutcome> ApplyShippedAsync(CrmSchemaHarness crm)
    {
        var read = SeedReader.Read(await File.ReadAllBytesAsync(ShippedSeed, Cancellation));

        read.IsSuccess.ShouldBeTrue(read.IsSuccess ? null : read.Error!.Message);
        read.Value!.Tenant.ShouldBe(CrmSchemaHarness.Northwind);

        var applied = await SeedApplierTests.Applier(crm).ApplyAsync(read.Value!, Cancellation);

        applied.IsSuccess.ShouldBeTrue(applied.IsSuccess ? null : applied.Error!.Message);

        return applied.Value;
    }

    /// <summary>The file the container mounts, found from the build output.</summary>
    private static string ShippedSeed =>
        Path.Combine(RepositoryRoot().FullName, "samples", "crm", "seed", "northwind.json");

    private static DirectoryInfo RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FlowX.slnx")))
        {
            directory = directory.Parent;
        }

        return directory ?? throw new InvalidOperationException(
            "FlowX.slnx was not found above " + AppContext.BaseDirectory);
    }
}
