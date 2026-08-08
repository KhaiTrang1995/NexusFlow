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
            12,
            "a process, an approval process, a report, an object, two fields, an account, " +
            "a contact, an opportunity, a lead, a record and a quote.");

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

    /// <summary>
    /// A seeded approval process governs a real quote, and the store finds it the same way it
    /// finds one somebody configured over HTTP.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The claim is that the rows are the same rows.</strong> The applier writes through
    /// <see cref="ApprovalStore.SaveProcessAsync"/> precisely so there is one spelling of a
    /// published process; this asks the reading half whether it agrees. A seed that wrote its own
    /// statement would pass every shape test in this file and then match nothing at run time,
    /// because a process that matches nothing is indistinguishable from a threshold nobody has
    /// crossed.
    /// </para>
    /// <para>
    /// Both halves are asserted. The under-threshold quote is the one that fails when a criterion
    /// is dropped on the way in — without it, a process with no criteria at all passes.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ASeededApprovalProcessGovernsAQuote()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        await ApplyAsync(crm, Document());

        var approvals = new ApprovalStore(crm.DataSource);
        var opportunity = SeedIds.For(CrmSchemaHarness.Northwind, "opportunity", "expansion");

        var (small, _) = await crm.QuoteAsync(CrmSchemaHarness.Northwind, opportunity, Cancellation);
        var (large, _) = await crm.QuoteAsync(CrmSchemaHarness.Northwind, opportunity, Cancellation);

        await crm.AsTenantAsync(
            CrmSchemaHarness.Northwind,
            "UPDATE quote SET discount = 300.0000 WHERE quote_id = @quote",
            Cancellation,
            ("quote", large));

        (await approvals.GoverningAsync(
            CrmSchemaHarness.Northwind, ApprovalSubject.Quote, small, Cancellation))
            .ShouldBeNull("the seeded threshold is 200 and this quote discounts nothing.");

        var matched = await approvals.GoverningAsync(
            CrmSchemaHarness.Northwind, ApprovalSubject.Quote, large, Cancellation);

        matched.ShouldNotBeNull();
        matched.Name.ShouldBe("big_discount");

        // The order is the chain: the manager first, and the director only after them.
        matched.Steps.Select(step => (step.Kind, step.Approver)).ShouldBe(
            [(ApproverKind.SubmittersManager, null), (ApproverKind.RoleHolder, "Director")]);
    }

    /// <summary>
    /// A seeded quote's subtotal is its lines, and its total is that less the discount.
    /// </summary>
    /// <remarks>
    /// <strong>Neither figure is in the file.</strong> A seed that stated a subtotal beside its
    /// lines could state one they do not add up to, and the quote builder would draw four rows
    /// that disagree with the total above them — with nothing anywhere to say which is wrong.
    /// Before the lines existed the file stated the subtotal and wrote no lines at all, which is
    /// the same defect with the evidence removed.
    /// </remarks>
    [Fact]
    public async Task ASeededQuotesSubtotalIsItsLines()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        await ApplyAsync(crm, Document());

        var quote = SeedIds.For(CrmSchemaHarness.Northwind, "quote", "expansion_q1");

        (await crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Northwind,
            "SELECT count(*) FROM quote_line WHERE quote_id = @quote",
            Cancellation,
            ("quote", quote)))
            .ShouldBe(2);

        // 10 × 1000 + 1 × 2000, and 12,000 less the 1,200 discount.
        (await crm.ScalarAsTenantAsync<decimal>(
            CrmSchemaHarness.Northwind,
            "SELECT subtotal FROM quote WHERE quote_id = @quote",
            Cancellation,
            ("quote", quote)))
            .ShouldBe(12_000m);

        (await crm.ScalarAsTenantAsync<decimal>(
            CrmSchemaHarness.Northwind,
            "SELECT total FROM quote WHERE quote_id = @quote",
            Cancellation,
            ("quote", quote)))
            .ShouldBe(10_800m);
    }

    /// <summary>
    /// A seeded account carries the fields the same file declared.
    /// </summary>
    /// <remarks>
    /// <strong>A file that declares a field and can never fill it in leaves a tenant half
    /// configured.</strong> The seed wrote custom objects with their values and built-in entities
    /// without, so the picklist it declares on <c>Account</c> was empty on every account it
    /// created — and the data-quality screen scored the tenant at zero per cent for a reason that
    /// was in the seed rather than in anybody's data.
    ///
    /// Written through the same merge the HTTP path uses, so a value the schema would refuse is
    /// refused here too: the second half of this test is what says so.
    /// </remarks>
    [Fact]
    public async Task ASeededAccountCarriesTheFieldsTheFileDeclared()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        await ApplyAsync(crm, Document());

        (await crm.ScalarAsTenantAsync<string>(
            CrmSchemaHarness.Northwind,
            "SELECT custom_fields ->> 'segment' FROM account WHERE account_id = @id",
            Cancellation,
            ("id", SeedIds.For(CrmSchemaHarness.Northwind, "account", "northwind"))))
            .ShouldBe("enterprise");
    }

    /// <summary>A value the declared picklist does not offer is refused, not written.</summary>
    /// <remarks>
    /// The same check the capability runs. A seed with its own statement would write it and the
    /// screen reading the field would show a value nothing else in the tenant can produce.
    /// </remarks>
    [Fact]
    public async Task ASeededValueThePicklistDoesNotOfferIsRefused()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        var document = Document();
        var account = document.Data.Accounts[0]!;

        var applied = await Applier(crm).ApplyAsync(
            document with
            {
                Data = document.Data with
                {
                    Accounts =
                    [
                        account with
                        {
                            Values = new Dictionary<string, string?> { ["segment"] = "invented" },
                        },
                    ],
                },
            },
            Cancellation);

        applied.IsSuccess.ShouldBeFalse("'invented' is not one of the three options declared.");
    }

    // ------------------------------------------------------------------------------- fixtures

    private static async Task<SeedOutcome> ApplyAsync(CrmSchemaHarness crm, SeedDocument document)
    {
        var applied = await Applier(crm).ApplyAsync(document, Cancellation);

        applied.IsSuccess.ShouldBeTrue(applied.IsSuccess ? null : applied.Error!.Message);

        return applied.Value;
    }

    internal static SeedApplier Applier(CrmSchemaHarness crm) =>
        new(
            new SeedStore(crm.DataSource),
            new CustomSchemaStore(crm.DataSource),
            new ApprovalStore(crm.DataSource),
            new ReportStore(crm.DataSource),
            new QueryStore(crm.DataSource),
            new RollupStore(crm.DataSource),
            new FormulaStore(crm.DataSource),
            new ConnectorStore(crm.DataSource),
            new LabelStore(crm.DataSource),
            new FieldPolicyStore(crm.DataSource),
            TimeProvider.System);

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
