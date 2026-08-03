using FlowX;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;

namespace Crm;

/// <summary>
/// Every contract this application puts on the wire, into the journal or into the outbox.
/// </summary>
/// <remarks>
/// <para>
/// Declared here for the reason <c>samples/banking</c> states at length:
/// <c>JournalPayload.Of</c> takes a <c>JsonTypeInfo&lt;T&gt;</c> and has no overload that
/// reflects over a type, which is what keeps the write path trim- and NativeAOT-safe and what
/// makes membership of a generated context a compile error rather than a convention.
/// <c>FLOWX1006</c> names the line to add.
/// </para>
/// <para>
/// <strong>The thirteen entity records are here even though no flow in this package carries
/// one.</strong> They are the surface packages 4 to 12 build against, and a contract that is
/// not in the context is one that fails at the moment somebody first tries to journal it —
/// which would be in another branch, against another author, for a reason belonging to this
/// one.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CaptureLead))]
[JsonSerializable(typeof(LeadCaptured))]
[JsonSerializable(typeof(LeadCreated))]
[JsonSerializable(typeof(LeadScored))]
[JsonSerializable(typeof(LeadAssigned))]
[JsonSerializable(typeof(ConvertLead))]
[JsonSerializable(typeof(ConversionResult))]
[JsonSerializable(typeof(LeadUnderConversion))]
[JsonSerializable(typeof(CreateAccountRequest))]
[JsonSerializable(typeof(AccountWritten))]
[JsonSerializable(typeof(CreateContactRequest))]
[JsonSerializable(typeof(ContactWritten))]
[JsonSerializable(typeof(CreateOpportunityRequest))]
[JsonSerializable(typeof(OpportunityWritten))]
[JsonSerializable(typeof(MarkLeadConvertedRequest))]
[JsonSerializable(typeof(LeadConversionRecorded))]
[JsonSerializable(typeof(LeadConverted))]
[JsonSerializable(typeof(Lead))]
[JsonSerializable(typeof(Account))]
[JsonSerializable(typeof(Contact))]
[JsonSerializable(typeof(Opportunity))]
[JsonSerializable(typeof(Quote))]
[JsonSerializable(typeof(QuoteLine))]
[JsonSerializable(typeof(SalesOrder))]
[JsonSerializable(typeof(Activity))]
[JsonSerializable(typeof(ProcessDefinition))]
[JsonSerializable(typeof(ProcessStage))]
[JsonSerializable(typeof(ProcessTransition))]
[JsonSerializable(typeof(TransitionGuard))]
[JsonSerializable(typeof(TransitionAction))]
[JsonSerializable(typeof(ConvertedTo))]
[JsonSerializable(typeof(Money))]
[JsonSerializable(typeof(RelatedRef))]
[JsonSerializable(typeof(BusMessage))]
[JsonSerializable(typeof(CrmSchemaProbe))]
[JsonSerializable(typeof(CrmSchemaReport))]
[JsonSerializable(typeof(CrmTableRowCount))]
public sealed partial class CrmJsonContext : JsonSerializerContext;

/// <summary>
/// Narrows a connection to one tenant, and is the only place in this sample that does.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two settings, and both are necessary.</strong> <c>flowx.tenant_id</c> is what
/// migration <c>0002</c>'s policies read. <c>role</c> is the half that is easy to leave out
/// and fatal to leave out: a superuser bypasses row-level security unconditionally, and a
/// table's owner bypasses it unless the table declares <c>FORCE ROW LEVEL SECURITY</c>. This
/// sample connects as the role that created the schema, so without the narrowing it would
/// install thirteen policies correctly and be isolated by none of them.
/// </para>
/// <para>
/// <strong>This is a second copy of <c>FlowX.Postgres</c>'s <c>TenantScope</c>, and the
/// duplication is deliberate rather than overlooked.</strong> That type is <c>internal</c> to
/// a plugin whose surface is not this sample's to widen, and widening it so a sample could
/// reuse fifteen lines would put an implementation detail of the journal into the published
/// contract of the package. The two must agree, and
/// <c>SchemaTests.TheSampleScopesAConnectionExactlyAsTheJournalDoes</c> is what fails when
/// they stop.
/// </para>
/// <para>
/// A null tenant produces a scope that <em>is</em> applied. It restricts the connection to
/// rows with no tenant rather than lifting the restriction, and no CRM row has one — so a
/// caller that resolved no tenant reaches nobody's data instead of everybody's.
/// </para>
/// </remarks>
public static class CrmTenantScope
{
    /// <summary>The role a scoped connection assumes. Created by migration <c>0002</c>.</summary>
    public const string RoleName = "flowx_tenant";

    /// <summary>The setting migration <c>0002</c>'s policies read.</summary>
    public const string SettingName = "flowx.tenant_id";

    private const string Bind =
        "SELECT set_config(@setting, @tenant, false), set_config('role', @role, false)";

    /// <summary>Binds a freshly opened connection to one tenant.</summary>
    /// <param name="connection">The connection to narrow.</param>
    /// <param name="tenantId">The tenant, or null for the untenanted rows.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
    public static async ValueTask ApplyAsync(
        NpgsqlConnection connection,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();

        command.CommandText = Bind;
        command.Parameters.Add(new NpgsqlParameter("setting", NpgsqlDbType.Text) { Value = SettingName });

        // The empty string rather than NULL: RESET restores a custom setting to '' rather than
        // to NULL, so the policies read both through the same nullif(…, '') and cannot
        // disagree about which of the two means "no tenant".
        command.Parameters.Add(new NpgsqlParameter("tenant", NpgsqlDbType.Text)
        {
            Value = tenantId ?? string.Empty,
        });
        command.Parameters.Add(new NpgsqlParameter("role", NpgsqlDbType.Text) { Value = RoleName });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Reads what the CRM schema is, and what of it one tenant can reach.</summary>
/// <remarks>
/// The whole of this package's data access, and it is a read. Packages 4 to 12 add the writes;
/// putting a repository here that nothing calls would be this branch guessing at their shape.
/// </remarks>
public sealed class CrmSchemaReader
{
    /// <summary>
    /// The applied CRM migration version. Deployment metadata rather than tenant data, which
    /// is why <c>0002</c> grants <c>flowx_tenant</c> a read of it and why the probe needs only
    /// one connection.
    /// </summary>
    private const string Version = "SELECT coalesce(max(version), 0) FROM crm_schema_migration";

    /// <summary>
    /// A row count per CRM table, taken on the caller's own scoped connection.
    /// </summary>
    /// <remarks>
    /// Thirteen counts and one statement, so the answer is one round trip and one snapshot
    /// rather than thirteen that can disagree with each other. Every count runs under the
    /// policies of migration <c>0002</c>, including the five tables that reach their tenant
    /// through a foreign key — which is what makes this endpoint a demonstration of the
    /// isolation rather than a report about the schema.
    /// </remarks>
    private const string Counts =
        """
                    SELECT 'account' AS table_name, count(*) AS row_count FROM account
        UNION ALL   SELECT 'activity',              count(*)              FROM activity
        UNION ALL   SELECT 'contact',               count(*)              FROM contact
        UNION ALL   SELECT 'lead',                  count(*)              FROM lead
        UNION ALL   SELECT 'opportunity',           count(*)              FROM opportunity
        UNION ALL   SELECT 'process_definition',    count(*)              FROM process_definition
        UNION ALL   SELECT 'process_stage',         count(*)              FROM process_stage
        UNION ALL   SELECT 'process_transition',    count(*)              FROM process_transition
        UNION ALL   SELECT 'quote',                 count(*)              FROM quote
        UNION ALL   SELECT 'quote_line',            count(*)              FROM quote_line
        UNION ALL   SELECT 'sales_order',           count(*)              FROM sales_order
        UNION ALL   SELECT 'transition_action',     count(*)              FROM transition_action
        UNION ALL   SELECT 'transition_guard',      count(*)              FROM transition_guard
        ORDER BY table_name
        """;

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates the reader over the data source the host registered.</summary>
    /// <param name="dataSource">Where the CRM tables are. Its schema is already selected.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> is null.</exception>
    public CrmSchemaReader(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _dataSource = dataSource;
    }

    /// <summary>Reports the schema version and one tenant's row counts.</summary>
    /// <param name="tenantId">The tenant to read as.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>What the probe found.</returns>
    public async ValueTask<CrmSchemaReport> ReportAsync(
        string? tenantId,
        CancellationToken cancellationToken)
    {
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var closing = connection.ConfigureAwait(false);

        await CrmTenantScope.ApplyAsync(connection, tenantId, cancellationToken)
            .ConfigureAwait(false);

        int version;

        using (var command = connection.CreateCommand())
        {
            command.CommandText = Version;

            version = Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        var tables = new List<CrmTableRowCount>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = Counts;

            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using var closingReader = reader.ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tables.Add(new CrmTableRowCount(reader.GetString(0), reader.GetInt64(1)));
            }
        }

        return new CrmSchemaReport(version, tables);
    }
}
