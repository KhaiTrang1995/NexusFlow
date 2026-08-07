using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// Applying a seed, and applying the same one again.
/// </summary>
/// <remarks>
/// <strong>Idempotency is the property worth a database to prove.</strong> Everything else about
/// this module is a shape the reader can check without one; "running it twice writes nothing the
/// second time" is a claim about rows, and the only honest way to test it is to write some.
/// </remarks>
public sealed class SeedApplierTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A whole document lands: metadata, then rows.</summary>
    [Fact]
    public async Task AWholeDocumentIsApplied()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        var applied = await ApplyAsync(crm, Document());

        applied.Written.ShouldBe(
            9,
            "a process, an object, two fields, an account, a contact, an opportunity, a lead " +
            "and a record.");

        (await crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Northwind, "SELECT count(*) FROM account", Cancellation))
            .ShouldBe(1);

        (await crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Northwind, "SELECT count(*) FROM opportunity", Cancellation))
            .ShouldBe(1);

        (await crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Northwind, "SELECT count(*) FROM process_stage", Cancellation))
            .ShouldBe(3, "the stages are the process, and half of them is a pipeline with no exit.");
    }

    /// <summary>
    /// Applying the same file twice writes nothing the second time, and moves nothing.
    /// </summary>
    /// <remarks>
    /// <strong>The whole design rests on this.</strong> The id comes from the alias, so the
    /// second run's inserts collide with the first run's rows. If it did not hold, every restart
    /// of a container with a seed mounted would double its data.
    /// </remarks>
    [Fact]
    public async Task ApplyingItTwiceWritesNothingTheSecondTime()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        var first = await ApplyAsync(crm, Document());
        var second = await ApplyAsync(crm, Document());

        first.Written.ShouldBeGreaterThan(0);
        second.Written.ShouldBe(0, "every id was already taken.");
        second.Present.ShouldBe(first.Written);

        (await crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Northwind, "SELECT count(*) FROM account", Cancellation))
            .ShouldBe(1);
    }

    /// <summary>The same alias in two tenants is two rows, with two ids.</summary>
    /// <remarks>
    /// Sharing them would put one tenant's primary key in another tenant's table, and row-level
    /// security would then hide the row rather than refuse the write — a record that exists and
    /// cannot be read.
    /// </remarks>
    [Fact]
    public async Task TwoTenantsSeededFromOneFileDoNotShareARow()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        await ApplyAsync(crm, Document());
        await ApplyAsync(crm, Document() with { Tenant = CrmSchemaHarness.Contoso });

        var northwind = await crm.ScalarAsTenantAsync<Guid>(
            CrmSchemaHarness.Northwind, "SELECT account_id FROM account", Cancellation);

        var contoso = await crm.ScalarAsTenantAsync<Guid>(
            CrmSchemaHarness.Contoso, "SELECT account_id FROM account", Cancellation);

        northwind.ShouldNotBe(contoso);
        northwind.ShouldBe(SeedIds.For(CrmSchemaHarness.Northwind, "account", "northwind"));
    }

    /// <summary>
    /// A second active process for the same entity kind is refused rather than published.
    /// </summary>
    [Fact]
    public async Task ASecondActiveProcessIsRefused()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        await ApplyAsync(crm, Document());

        var rival = Document();
        var applied = await Applier(crm).ApplyAsync(
            rival with
            {
                Metadata = rival.Metadata with
                {
                    Processes = [rival.Metadata.Processes[0]! with { Alias = "rival", Version = 2 }],
                },
            },
            Cancellation);

        applied.IsSuccess.ShouldBeFalse();
        applied.Error!.Code.ShouldBe("crm.seed_process_conflict");
    }

    /// <summary>A record whose values do not satisfy its fields is refused.</summary>
    /// <remarks>
    /// The seed meets the validation a written record meets over HTTP. A path into the database
    /// that skipped it would be the one way to get a record without its required field.
    /// </remarks>
    [Fact]
    public async Task ARecordMissingARequiredFieldIsRefused()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        var document = Document();
        var applied = await Applier(crm).ApplyAsync(
            document with
            {
                Data = document.Data with
                {
                    Records = [new SeedRecord("atlas", "project", new Dictionary<string, string?>())],
                },
            },
            Cancellation);

        applied.IsSuccess.ShouldBeFalse();
    }

    // ------------------------------------------------------------------------------- fixtures

    private static async Task<SeedOutcome> ApplyAsync(CrmSchemaHarness crm, SeedDocument document)
    {
        var applied = await Applier(crm).ApplyAsync(document, Cancellation);

        applied.IsSuccess.ShouldBeTrue(applied.IsSuccess ? null : applied.Error!.Message);

        return applied.Value;
    }

    private static SeedApplier Applier(CrmSchemaHarness crm) =>
        new(new SeedStore(crm.DataSource), new CustomSchemaStore(crm.DataSource), TimeProvider.System);

    /// <summary>The document the tests apply, read from the reader's own fixture.</summary>
    /// <remarks>
    /// Parsed rather than constructed, so the two files cannot describe different documents —
    /// and so a change to the shape breaks both at once rather than one of them quietly.
    /// </remarks>
    private static SeedDocument Document()
    {
        var read = SeedReader.Read(System.Text.Encoding.UTF8.GetBytes(SeedReaderTests.Fixture));

        read.IsSuccess.ShouldBeTrue();

        return read.Value!;
    }
}
