using Shouldly;
using Xunit;

namespace FlowX.Architecture.Tests;

/// <summary>
/// The isolation gates: a tenant's rows stay a tenant's rows, at the schema and at the seam
/// that reaches it.
/// </summary>
/// <remarks>
/// <para>
/// <c>SecurityFitnessTests</c> asks what a <em>capability</em> declares. These ask what the
/// <em>data</em> declares, which is a different question with a worse failure mode: an
/// authorisation stance that is missing stops a request, while a row-level-security policy that
/// is missing serves one. Nothing in the build notices the second. The C# compiles, the
/// analyzers pass, the integration suite runs as one tenant and gets exactly the rows it
/// expected — and the table is readable by everybody.
/// </para>
/// <para>
/// Both gates below are written in the safe direction. They ask "does everything that is
/// tenant-scoped carry its protection", never "is everything that carries protection
/// tenant-scoped": a table protected more than it needs to be costs a query plan, and a table
/// protected less than it needs to be costs the tenancy guarantee.
/// </para>
/// </remarks>
public sealed class TenancyFitnessTests
{
    /// <summary>
    /// Files permitted to take a connection from the pool without narrowing it to a tenant.
    /// </summary>
    /// <remarks>
    /// Both entries are code that runs with no tenant in scope and would be wrong to scope.
    /// <c>CrmMigrator</c> issues DDL as the role that owns the schema — narrowing it to
    /// <c>flowx_tenant</c> would make every migration fail on its first <c>CREATE</c>. The
    /// health check reads <c>crm_schema_migration</c>, which is deployment metadata rather than
    /// anybody's data, on a request that has no caller and therefore no claims to derive a
    /// tenant from. <c>Infrastructure.cs</c> is where the narrowing itself is implemented and
    /// is excluded by construction rather than by this list.
    /// </remarks>
    private static readonly string[] MayOpenUnscoped =
    [
        "samples/crm/CrmHealth.cs",
        "samples/crm/Migrations/CrmMigrator.cs",
    ];

    /// <summary>The file that implements the narrowing, and so is allowed to perform it.</summary>
    private const string ScopeImplementation = "samples/crm/Infrastructure.cs";

    /// <summary>
    /// A table with a <c>tenant_id</c> column enforces row-level security on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The column is the declaration that a table holds more than one tenant's rows. Having
    /// declared it, the table has exactly one way to keep them apart, and a table that skips it
    /// is not partially protected — it is a plain table that happens to record who each row
    /// belongs to while serving all of them to anybody.
    /// </para>
    /// <para>
    /// Keyed on the column rather than on a list of table names so that the gate covers the
    /// table added next month, which is the only table this can usefully protect. The tables
    /// without the column are not swept in: <c>ratelimit_bucket</c>, <c>idempotency_record</c>
    /// and <c>flowx_cache_entry</c> carry the tenant inside a composed key rather than in a
    /// column — the scope enums that compose those keys are held to their tenant default by
    /// <c>SecurityFitnessTests.EveryScopeEnumDefaultsToTenant</c> — and <c>flow_lease</c>,
    /// <c>retention_policy</c>, <c>change_cursor</c> and <c>stream_checkpoint</c> are node and
    /// deployment infrastructure that no tenant addresses.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryTenantScopedTableEnforcesRowLevelSecurity()
    {
        var offenders = MigrationSurvey.Tables
            .Where(static table => table.HasTenantColumn)
            .Where(static table => !MigrationSurvey.RowLevelSecured.Contains(table.Name))
            .Select(static table => table.Where)
            .Order(StringComparer.Ordinal)
            .ToArray();

        offenders.ShouldBeEmpty(
            "These tables declare a tenant_id column and never enable row-level security on " +
            "it, so every tenant reads every row and no query fails to say so. Add " +
            "`ALTER TABLE <name> ENABLE ROW LEVEL SECURITY` and a policy keyed on " +
            "current_setting('flowx.tenant_id') in the migration that creates the table:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// A table protected by row-level security is reachable by the role the policies are written for.
    /// </summary>
    /// <remarks>
    /// Row-level security and the grant are two halves of one decision and fail in opposite
    /// directions. Without the policy the role reads everything; without the grant the role
    /// reads nothing, and the application fails closed at run time with a permission error on a
    /// table that looks correctly configured. This is the half that a schema review passes and
    /// a deployment does not.
    /// </remarks>
    [Fact]
    public void EveryRowLevelSecuredTableIsReachableByTheTenantRole()
    {
        var offenders = MigrationSurvey.Tables
            .Where(static table => MigrationSurvey.RowLevelSecured.Contains(table.Name))
            .Where(static table => !MigrationSurvey.GrantedToTenantRole.Contains(table.Name))
            .Select(static table => table.Where)
            .Order(StringComparer.Ordinal)
            .ToArray();

        offenders.ShouldBeEmpty(
            $"These tables enable row-level security but never grant {MigrationSurvey.TenantRole} " +
            "access, so the policies are correct and the role cannot reach the table at all:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// Every CRM connection is narrowed to a tenant by the one method that knows how.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This replaced thirty byte-identical copies of open-bind-dispose-on-failure, one per
    /// store. The duplication mattered because of what the copies were protecting: between the
    /// pool handing back a connection and the bind committing, the connection is live as the
    /// schema-owning role with no <c>flowx.tenant_id</c> set, which is the one state in which
    /// the policies of migration <c>0002</c> are inert. A copy that omitted the <c>catch</c>
    /// would return that connection to a caller, or to the pool, still unbound.
    /// </para>
    /// <para>
    /// The gate is on <c>OpenConnectionAsync</c> rather than on the bind, because the failure
    /// being prevented is a store that opens a connection and never binds it at all — which no
    /// check on the binding call can see.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryCrmStoreOpensThroughTheTenantScope()
    {
        var offenders = SourceSurvey.SourceFiles("samples/crm")
            .Select(SourceSurvey.RelativePath)
            .Where(static path => path != ScopeImplementation)
            .Where(static path => !MayOpenUnscoped.Contains(path, StringComparer.Ordinal))
            .Where(static path => File.ReadAllText(
                    Path.Combine(RepositoryLayout.Root.FullName, path))
                .Contains(".OpenConnectionAsync(", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        offenders.ShouldBeEmpty(
            "These files take a connection from the pool directly instead of through " +
            "CrmTenantScope.OpenAsync, so nothing guarantees the connection is narrowed to a " +
            "tenant before a statement runs on it, or disposed if narrowing fails:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// The migration survey still reads the schema the two gates above are written against.
    /// </summary>
    /// <remarks>
    /// Both gates pass on an empty set, and an empty set is what a renamed directory, a moved
    /// migration or a <c>CREATE TABLE</c> written in an unfamiliar style produces. Without
    /// this, "every tenant-scoped table is protected" quietly becomes "no table is
    /// tenant-scoped" and the build stays green while the guarantee leaves.
    /// </remarks>
    [Fact]
    public void TheMigrationSurveyStillSeesTheSchema()
    {
        MigrationSurvey.Tables.Count.ShouldBeGreaterThanOrEqualTo(
            65, "the migration survey stopped finding the tables the tenancy gates are about.");

        // Sixty tables declare a tenant_id column today. The floors below sit a little under
        // each real figure so that deleting one table is a passing build and losing the parser
        // is a failing one.
        MigrationSurvey.Tables.Count(static table => table.HasTenantColumn)
            .ShouldBeGreaterThanOrEqualTo(
                55, "the survey stopped recognising tenant_id columns, so the row-level " +
                    "security gate is passing vacuously.");

        MigrationSurvey.RowLevelSecured.Count.ShouldBeGreaterThanOrEqualTo(
            55, "the survey stopped recognising ENABLE ROW LEVEL SECURITY.");

        MigrationSurvey.GrantedToTenantRole.Count.ShouldBeGreaterThanOrEqualTo(
            55, $"the survey stopped recognising GRANT … TO {MigrationSurvey.TenantRole}.");
    }

    /// <summary>
    /// The unscoped-open allowance still names files that open a connection.
    /// </summary>
    /// <remarks>
    /// An allowlist entry that no longer describes anything is an excuse nobody is using and
    /// the next reader has to re-derive. Same reason as
    /// <c>SecretAllowlistTests.TheAllowlistExcusesNoPathAndNoRule</c>.
    /// </remarks>
    [Fact]
    public void TheUnscopedOpenAllowanceNamesOnlyFilesThatOpenAConnection()
    {
        var stale = MayOpenUnscoped
            .Where(static path => !File.Exists(Path.Combine(RepositoryLayout.Root.FullName, path))
                || !File.ReadAllText(Path.Combine(RepositoryLayout.Root.FullName, path))
                    .Contains(".OpenConnectionAsync(", StringComparison.Ordinal))
            .ToArray();

        stale.ShouldBeEmpty(
            "These files are excused from opening through CrmTenantScope and no longer open a " +
            "connection at all. Remove them from the allowance:" +
            Environment.NewLine + string.Join(Environment.NewLine, stale));
    }
}
