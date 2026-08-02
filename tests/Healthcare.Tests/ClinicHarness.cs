using System.Security.Claims;
using FlowX;
using FlowX.Hosting;
using FlowX.Postgres;
using FlowX.Runtime;
using FlowX.Testing;
using Npgsql;
using Xunit;

namespace Healthcare.Tests;

/// <summary>
/// One deployment of the healthcare sample, on a real PostgreSQL schema of its own.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No store here is a double, and that is the whole point of this project.</strong>
/// <c>samples/banking</c>'s harness stands the reference journal in for PostgreSQL and argues
/// convincingly that what it asserts is store-independent. Nothing this project asserts is:
/// cross-tenant denial is migration <c>0008</c>'s row-level security under a role that cannot
/// bypass it, redaction at rest is what is actually in a <c>json</c> column, and erasure is
/// four statements in one transaction against migration <c>0012</c>'s column. Every one of
/// those would pass vacuously against a dictionary.
/// </para>
/// <para>
/// <strong>A schema per harness, dropped on disposal.</strong> Two tests running side by side
/// must not be able to see each other's instances, or an isolation assertion would pass or
/// fail depending on what else the runner happened to be doing.
/// </para>
/// <para>
/// The composition below is what <c>Program.cs</c> builds, minus the HTTP surface:
/// <see cref="FlowHost"/> over a <see cref="FlowDurability"/> over the PostgreSQL journal and
/// lease store, with the sample's own capabilities, the sample's own audit sink and the
/// sample's own policy stores. What the tests vary is the tenant, the region and the clinic's
/// consent register.
/// </para>
/// </remarks>
internal sealed class ClinicHarness : IAsyncDisposable
{
    private readonly PostgresJournalOptions _options;

    private ClinicHarness(
        NpgsqlDataSource dataSource,
        PostgresJournalOptions options,
        string region)
    {
        _options = options;

        DataSource = dataSource;
        Journal = new PostgresFlowJournal(dataSource);
        Leases = new PostgresLeaseStore(dataSource);
        // Nothing reads this schema's outbox, and saying so is what lets an erasure complete.
        // The default is one publisher, which would hold every instance for a publisher that
        // does not exist — truthfully, and uselessly. WithAPublisherDeclared is the test that
        // the default behaves that way.
        Erasure = new PostgresSubjectErasure(
            dataSource, stores: null, new RetentionConsumers { Publisher = false });
        Region = region;
    }

    /// <summary>The data source, with <c>search_path</c> already pointing at this schema.</summary>
    public NpgsqlDataSource DataSource { get; }

    /// <summary>The journal every step boundary is committed to.</summary>
    public PostgresFlowJournal Journal { get; }

    /// <summary>Where an instance's exclusive ownership comes from.</summary>
    public PostgresLeaseStore Leases { get; }

    /// <summary>The store under test in <c>SubjectErasureTests</c>.</summary>
    public PostgresSubjectErasure Erasure { get; }

    /// <summary>An erasure over the same schema that believes a publisher drains its outbox.</summary>
    /// <remarks>
    /// The default consumer set, which is what a host that has said nothing gets. One test uses
    /// it, and it is a property rather than the default here because every other test would then
    /// be a test about the outbox.
    /// </remarks>
    public PostgresSubjectErasure ErasureWithAPublisherDeclared =>
        new(DataSource, stores: null, RetentionConsumers.Default);

    /// <summary>The region this deployment declares.</summary>
    public string Region { get; }

    /// <summary>The clinic's consent register. Tests seed and withdraw through it.</summary>
    public InMemoryConsentRegister Consents { get; } = new();

    /// <summary>The clinic's patient index.</summary>
    public InMemoryPatientIndex Patients { get; } = new();

    /// <summary>The clinic's clinical record store.</summary>
    public InMemoryRecordStore Records { get; } = new(TimeProvider.System);

    /// <summary>The clinical audit trail three of the flow's steps write to.</summary>
    public InMemoryClinicalAudit Audit { get; } = new();

    /// <summary>Creates a schema, migrates it, and builds the deployment over it.</summary>
    /// <param name="cancellationToken">Cancels the setup.</param>
    /// <param name="region">The region this deployment declares.</param>
    /// <returns>The prepared harness.</returns>
    /// <exception cref="InvalidOperationException">
    /// A database was promised by the environment and is not reachable.
    /// </exception>
    public static async ValueTask<ClinicHarness> CreateAsync(
        CancellationToken cancellationToken,
        string region = "eu-central-1")
    {
        if (!ClinicDatabase.IsAvailable)
        {
            if (ClinicDatabase.IsPromised)
            {
                throw ClinicDatabase.Unreachable();
            }

            Assert.Skip(ClinicDatabase.Reason);
        }

        var options = new PostgresJournalOptions
        {
            Schema = "flowx_h_" + Guid.NewGuid().ToString("n"),
        };

        var dataSource = ServiceCollectionExtensions.BuildDataSource(
            ClinicDatabase.ConnectionString!, options);

        var harness = new ClinicHarness(dataSource, options, region);

        await new PostgresMigrator(dataSource, options)
            .MigrateAsync(cancellationToken)
            .ConfigureAwait(false);

        return harness;
    }

    /// <summary>Runs one admission as a clinician of a clinic.</summary>
    /// <param name="intake">What the endpoint would have deserialised.</param>
    /// <param name="tenantId">Which clinic, as the caller's <c>tid</c> claim would say.</param>
    /// <param name="admissionKey">The caller's idempotency key.</param>
    /// <param name="ct">Cancels the run.</param>
    /// <param name="grants">
    /// What the caller holds. Defaults to every grant the flow names, so that a test about
    /// consent is not also a test about authorisation.
    /// </param>
    public ValueTask<FlowExecutionResult<IntakeResult>> AdmitAsync(
        PatientIntake intake,
        string tenantId,
        string admissionKey,
        CancellationToken ct,
        params string[] grants) =>
        Host().RunAsync(
            PatientIntakeFlow.Plan,
            Dispatcher(),
            new FlowInvocation(
                "corr-" + admissionKey,
                admissionKey,
                tenantId,
                Deadline: null,
                Principal: Clinician(tenantId, grants)),
            intake,
            PatientIntakeFlow.Projection,
            ct);

    /// <summary>A clinician of one clinic, holding the grants a test names.</summary>
    /// <remarks>
    /// <para>
    /// <strong>The <c>tid</c> claim is the whole reason this is not
    /// <c>TestPrincipal.Holding</c>.</strong> The tenant on the invocation is an
    /// <em>assertion</em>, not a fact: <c>ClaimTenantResolver</c> derives the tenant from
    /// validated claims and refuses a call whose asserted tenant the claims do not support
    /// (ADR-0046). A harness that supplied a principal with no <c>tid</c> would have every one
    /// of these tests refused with <c>tenant.cross_tenant_denied</c> — which is the resolver
    /// working, and is why the claim is here rather than the check being relaxed.
    /// </para>
    /// <para>
    /// The grants are listed by each test rather than granted wholesale, so a capability added
    /// with a new permission fails here — naming the grant it needs — instead of being waved
    /// through by a caller who holds everything.
    /// </para>
    /// </remarks>
    private static ClaimsPrincipal Clinician(string tenantId, string[] grants) =>
        new(new ClaimsIdentity(
            [
                new Claim("tid", tenantId),
                new Claim(
                    "scope",
                    string.Join(" ", grants.Length > 0 ? grants : ["consent:read", "records:write"])),
            ],
            TestPrincipal.AuthenticationType));

    /// <summary>The tenant-scoped journal a host would use for one clinic.</summary>
    /// <param name="tenantId">Which clinic.</param>
    /// <remarks>
    /// The same object <c>FlowHost.JournalFor</c> hands the engine: an unprivileged role and
    /// <c>flowx.tenant_id</c> set, so the database is what decides what it can reach.
    /// </remarks>
    public IFlowJournal JournalFor(string tenantId) => Journal.ForTenant(tenantId);

    /// <summary>Reads one column of one instance row, bypassing every wall this sample has.</summary>
    /// <param name="instanceId">The instance to read.</param>
    /// <param name="column">The column to read.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The value as text, or null.</returns>
    /// <remarks>
    /// <strong>Raw SQL as the schema's owner, deliberately.</strong> A test that asserted
    /// "the identifier is not in the journal" through the adapter would be asserting that the
    /// adapter does not hand it back, which is a different and much weaker claim. This reads
    /// the bytes on disk as an operator with a psql prompt would, which is the threat model
    /// <c>[Sensitive]</c> is actually about.
    /// </remarks>
    public async ValueTask<string?> ColumnAsync(Guid instanceId, string column, CancellationToken ct)
    {
        var connection = await DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        using var command = connection.CreateCommand();

        // The column name is a literal from this file and never from a test's input; every
        // value below is a parameter.
        command.CommandText = $"SELECT {column}::text FROM flow_instance WHERE instance_id = @id";
        command.Parameters.AddWithValue("id", instanceId);

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    /// <summary>Every step result recorded for an instance, as text, in commit order.</summary>
    /// <param name="instanceId">The instance to read.</param>
    /// <param name="ct">Cancels the read.</param>
    public async ValueTask<IReadOnlyList<string?>> StepResultsAsync(Guid instanceId, CancellationToken ct)
    {
        var connection = await DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        using var command = connection.CreateCommand();

        command.CommandText =
            "SELECT result::text FROM flow_step WHERE instance_id = @id ORDER BY sequence";
        command.Parameters.AddWithValue("id", instanceId);

        var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        var results = new List<string?>();

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(
                await reader.IsDBNullAsync(0, ct).ConfigureAwait(false) ? null : reader.GetString(0));
        }

        return results;
    }

    /// <summary>Every staged event body for an instance, as text.</summary>
    /// <param name="instanceId">The instance to read.</param>
    /// <param name="ct">Cancels the read.</param>
    public async ValueTask<IReadOnlyList<string?>> EventBodiesAsync(Guid instanceId, CancellationToken ct)
    {
        var connection = await DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        using var command = connection.CreateCommand();

        command.CommandText = "SELECT payload::text FROM outbox_event WHERE instance_id = @id";
        command.Parameters.AddWithValue("id", instanceId);

        var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        var bodies = new List<string?>();

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            bodies.Add(
                await reader.IsDBNullAsync(0, ct).ConfigureAwait(false) ? null : reader.GetString(0));
        }

        return bodies;
    }

    /// <summary>The instance a run opened, found by the correlation the harness derived.</summary>
    /// <param name="admissionKey">The key the run was made with.</param>
    /// <param name="ct">Cancels the read.</param>
    public async ValueTask<Guid> InstanceAsync(string admissionKey, CancellationToken ct)
    {
        var connection = await DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        using var command = connection.CreateCommand();

        command.CommandText =
            "SELECT instance_id FROM flow_instance WHERE correlation_id = @correlation";
        command.Parameters.AddWithValue("correlation", "corr-" + admissionKey);

        return (Guid)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    /// <summary>Marks an instance as still running, so an erasure has to withhold it.</summary>
    /// <param name="instanceId">The instance to reopen.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <remarks>
    /// Raw SQL, because the adapter will not move a completed instance back to <c>Running</c>
    /// and should not: what is being arranged is the state a node's death leaves behind, not a
    /// transition the journal offers.
    /// </remarks>
    public async ValueTask ReopenAsync(Guid instanceId, CancellationToken ct)
    {
        var connection = await DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        using var command = connection.CreateCommand();

        command.CommandText = "UPDATE flow_instance SET state = 'Running' WHERE instance_id = @id";
        command.Parameters.AddWithValue("id", instanceId);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            var connection = await DataSource.OpenConnectionAsync().ConfigureAwait(false);
            await using var closing = connection.ConfigureAwait(false);

            using var command = connection.CreateCommand();

            // The schema name is this harness's own, built from a Guid in CreateAsync.
            command.CommandText = $"DROP SCHEMA IF EXISTS \"{_options.Schema}\" CASCADE";

            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            await DataSource.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The host, composed exactly as <c>Program.cs</c> composes it.</summary>
    private FlowHost Host()
    {
        var options = new FlowXOptions
        {
            ApplicationName = "Healthcare",
            NodeName = "test-node",
            TenantIsolation = TenantIsolation.Row,
        };

        options.Residency.Region = Region;
        options.Residency.Requirements[ClinicTokens.BerlinTenant] = "eu-central-1";
        options.Residency.Requirements[ClinicTokens.MunichTenant] = "eu-central-1";
        options.Residency.Requirements[ClinicTokens.DublinTenant] = "eu-west-1";

        return new FlowHost(
            new FlowEngine(
                SystemClock.Instance,
                rateLimiter: new PostgresRateLimiterStore(DataSource),
                audit: Audit),
            options,
            new FlowDurability(Journal, Leases));
    }

    private PatientIntakeFlow.Dispatcher Dispatcher() => new(
        deduplicatePatient: new DeduplicatePatient(Patients),
        purgeRecord: new PurgeRecord(Records),
        storeRecord: new StoreRecord(Records),
        validateIntake: new ValidateIntake(),
        verifyConsent: new VerifyConsent(Consents));
}

/// <summary>Intakes the tests admit, so no test has to spell one out to talk about another.</summary>
internal static class Intakes
{
    /// <summary>A national identifier that is a patient in the tests that need one.</summary>
    public const string BerlinPatientId = "DE-1949-0523-0071";

    /// <summary>A second patient, so an erasure can be shown to reach one and not the other.</summary>
    public const string OtherPatientId = "DE-1961-0813-0044";

    /// <summary>A well-formed intake with a standing treatment consent.</summary>
    /// <param name="nationalId">Whose record. Defaults to <see cref="BerlinPatientId"/>.</param>
    public static PatientIntake Treatment(string? nationalId = null) => new(
        nationalId ?? BerlinPatientId,
        "Wilhelm Brandt",
        new DateOnly(1949, 5, 23),
        CarePurpose.Treatment,
        InMemoryConsentRegister.TreatmentReference);
}
