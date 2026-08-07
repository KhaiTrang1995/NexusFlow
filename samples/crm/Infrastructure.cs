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
/// <para>
/// <strong>Enums cross the wire as their names, not as their ordinals.</strong> The default
/// writes <c>CasePriority.Urgent</c> as <c>3</c>, and the number is a promise this sample cannot
/// keep: <c>CaseStatus</c> already gained <c>Escalated</c>, and had that member been inserted
/// rather than appended, every stored and in-flight <c>3</c> would have quietly changed meaning.
/// A name cannot be reordered. It also makes the generated OpenAPI document say what a caller may
/// actually send, rather than an integer with no vocabulary attached.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ReadPlan))]
[JsonSerializable(typeof(PlanDetail))]
[JsonSerializable(typeof(ReadConfig))]
[JsonSerializable(typeof(ConfigList))]
[JsonSerializable(typeof(SeedDocument))]
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
[JsonSerializable(typeof(OpportunityStageChanged))]
[JsonSerializable(typeof(TransitionApplied))]
[JsonSerializable(typeof(IssueQuote))]
[JsonSerializable(typeof(QuoteIssued))]
[JsonSerializable(typeof(QuoteRequestLine))]
[JsonSerializable(typeof(ApproveDiscount))]
[JsonSerializable(typeof(ApproveQuoteDiscount))]
[JsonSerializable(typeof(DiscountApproved))]
[JsonSerializable(typeof(PlaceOrder))]
[JsonSerializable(typeof(OrderPlaced))]
[JsonSerializable(typeof(AdvanceOpportunity))]
[JsonSerializable(typeof(OpportunityAdvanced))]
[JsonSerializable(typeof(CreateTask))]
[JsonSerializable(typeof(TaskCreated))]
[JsonSerializable(typeof(TasksEscalated))]
[JsonSerializable(typeof(StaleOpportunitiesSwept))]
[JsonSerializable(typeof(ScheduledFire))]
[JsonSerializable(typeof(CompanyProfile))]
[JsonSerializable(typeof(EnrichmentRequested))]
[JsonSerializable(typeof(EnrichmentAttempt))]
[JsonSerializable(typeof(EnrichmentWebhook))]
[JsonSerializable(typeof(ApplyEnrichment))]
[JsonSerializable(typeof(LeadEnriched))]
[JsonSerializable(typeof(SummariseAccount))]
[JsonSerializable(typeof(AccountSummary))]
[JsonSerializable(typeof(CrmSchemaProbe))]
[JsonSerializable(typeof(CrmSchemaReport))]
[JsonSerializable(typeof(CrmTableRowCount))]

// The run-time schema. Every one of these is a contract of a Durable flow, so FLOWX1006 is what
// requires them here — the journal has to record the state bag without reflection.
//
// IReadOnlyDictionary<string, string?> is the shape a caller sends values in, and it is declared
// once. A jsonb column's contents cannot be a generated contract, because the whole point is
// that an administrator decides what is in it; a dictionary of text is the widest thing that
// still has a JsonTypeInfo, and CustomValues is what gives it a type on the way in.
[JsonSerializable(typeof(DefineObject))]
[JsonSerializable(typeof(ObjectDefined))]
[JsonSerializable(typeof(DefineField))]
[JsonSerializable(typeof(FieldDefined))]
[JsonSerializable(typeof(DefineRelationship))]
[JsonSerializable(typeof(RelationshipDefined))]
[JsonSerializable(typeof(CreateRecord))]
[JsonSerializable(typeof(RecordCreated))]
[JsonSerializable(typeof(LinkRecords))]
[JsonSerializable(typeof(RecordsLinked))]
[JsonSerializable(typeof(SetCustomFields))]
[JsonSerializable(typeof(CustomFieldsSet))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string?>))]
[JsonSerializable(typeof(CustomFieldOption))]
[JsonSerializable(typeof(WriteObjectRecord))]
[JsonSerializable(typeof(WriteEntityFields))]
[JsonSerializable(typeof(DefineValidationRule))]
[JsonSerializable(typeof(ValidationRuleDefined))]
[JsonSerializable(typeof(DefineRollup))]
[JsonSerializable(typeof(RollupFilter))]
[JsonSerializable(typeof(RollupDefined))]
[JsonSerializable(typeof(DefineListView))]
[JsonSerializable(typeof(ListViewDefined))]
[JsonSerializable(typeof(QueryRecords))]
[JsonSerializable(typeof(ReadObjectRecords))]
[JsonSerializable(typeof(RecordView))]
[JsonSerializable(typeof(RecordPage))]
[JsonSerializable(typeof(RecordFilter))]
[JsonSerializable(typeof(SearchEverything))]
[JsonSerializable(typeof(SearchHit))]
[JsonSerializable(typeof(SearchResults))]
[JsonSerializable(typeof(DefineFormula))]
[JsonSerializable(typeof(FormulaDefined))]
[JsonSerializable(typeof(RecordOrder))]
[JsonSerializable(typeof(DescribeSchema))]
[JsonSerializable(typeof(DescribeFor))]
[JsonSerializable(typeof(DescribedField))]
[JsonSerializable(typeof(DescribedView))]
[JsonSerializable(typeof(DescribedObject))]
[JsonSerializable(typeof(DescribedEntity))]
[JsonSerializable(typeof(SchemaDescription))]
[JsonSerializable(typeof(SyncChanges))]
[JsonSerializable(typeof(ReadChanges))]
[JsonSerializable(typeof(RecordChange))]
[JsonSerializable(typeof(ChangePage))]
[JsonSerializable(typeof(DeleteRecord))]
[JsonSerializable(typeof(RecordDeleted))]
[JsonSerializable(typeof(SubmitImport))]
[JsonSerializable(typeof(SubmitExport))]
[JsonSerializable(typeof(SubmitJob))]
[JsonSerializable(typeof(ReadJob))]
[JsonSerializable(typeof(ReadJobFor))]
[JsonSerializable(typeof(JobSubmitted))]
[JsonSerializable(typeof(JobRowError))]
[JsonSerializable(typeof(JobStatus))]
[JsonSerializable(typeof(JobsSwept))]
[JsonSerializable(typeof(List<RecordView>))]
[JsonSerializable(typeof(DefineReport))]
[JsonSerializable(typeof(ReportDefined))]
[JsonSerializable(typeof(RunReport))]
[JsonSerializable(typeof(ReportGroup))]
[JsonSerializable(typeof(ReportResult))]
[JsonSerializable(typeof(DefineDashboard))]
[JsonSerializable(typeof(DashboardDefined))]
[JsonSerializable(typeof(RunDashboard))]
[JsonSerializable(typeof(DashboardResult))]
[JsonSerializable(typeof(SetLabel))]
[JsonSerializable(typeof(LabelSet))]
[JsonSerializable(typeof(DescribedColumn))]
[JsonSerializable(typeof(ViewLayout))]
[JsonSerializable(typeof(DefinePeriod))]
[JsonSerializable(typeof(PeriodDefined))]
[JsonSerializable(typeof(SetStrategy))]
[JsonSerializable(typeof(StrategySet))]
[JsonSerializable(typeof(DefinePlan))]
[JsonSerializable(typeof(PlanDefined))]
[JsonSerializable(typeof(AnswerQualification))]
[JsonSerializable(typeof(QualificationRecorded))]
[JsonSerializable(typeof(SetPlanStep))]
[JsonSerializable(typeof(PlanStepSet))]
[JsonSerializable(typeof(ReadRollUp))]
[JsonSerializable(typeof(AccountCoverage))]
[JsonSerializable(typeof(OpportunityReadiness))]
[JsonSerializable(typeof(LeadAttainment))]
[JsonSerializable(typeof(PeriodRollUp))]
[JsonSerializable(typeof(ForViewer))]

// The reporting line, the depth an account plan actually has, and the numbers reviewed.
[JsonSerializable(typeof(SetOrgMember))]
[JsonSerializable(typeof(OrgMemberSet))]
[JsonSerializable(typeof(SetObjective))]
[JsonSerializable(typeof(ObjectiveSet))]
[JsonSerializable(typeof(SetStakeholder))]
[JsonSerializable(typeof(StakeholderSet))]
[JsonSerializable(typeof(SetRisk))]
[JsonSerializable(typeof(RiskSet))]
[JsonSerializable(typeof(DefineKpi))]
[JsonSerializable(typeof(KpiDefined))]
[JsonSerializable(typeof(ReviewKpi))]
[JsonSerializable(typeof(ForReview))]
[JsonSerializable(typeof(KpiReviewed))]
[JsonSerializable(typeof(ReadScorecard))]
[JsonSerializable(typeof(KpiResult))]
[JsonSerializable(typeof(Scorecard))]

// The plan tree at any level, how the people and the deals are doing, and the board that
// assembles them in one request.
[JsonSerializable(typeof(ReadPlanTree))]
[JsonSerializable(typeof(PlanNode))]
[JsonSerializable(typeof(PlanTree))]
[JsonSerializable(typeof(ReadSalesPerformance))]
[JsonSerializable(typeof(SellerPerformance))]
[JsonSerializable(typeof(SalesPerformance))]
[JsonSerializable(typeof(ReadDealPerformance))]
[JsonSerializable(typeof(DealPerformance))]
[JsonSerializable(typeof(ReadBoard))]
[JsonSerializable(typeof(ForPerformance))]
[JsonSerializable(typeof(ViewerScope))]
[JsonSerializable(typeof(ExecutiveBoard))]

// Who owns which accounts, what number each person carries, and what a part-year seller carries.
[JsonSerializable(typeof(RoutingRule))]
[JsonSerializable(typeof(DefineTerritory))]
[JsonSerializable(typeof(TerritoryDefined))]
[JsonSerializable(typeof(RouteSubject))]
[JsonSerializable(typeof(RoutedTo))]
[JsonSerializable(typeof(ReadCoverage))]
[JsonSerializable(typeof(TerritoryCoverage))]
[JsonSerializable(typeof(Coverage))]
[JsonSerializable(typeof(SetQuota))]
[JsonSerializable(typeof(QuotaSet))]
[JsonSerializable(typeof(ReadQuotaAttainment))]
[JsonSerializable(typeof(QuotaAttainment))]
[JsonSerializable(typeof(QuotaAttainmentReport))]

// Approvals an administrator configures, and the register of what was said.
[JsonSerializable(typeof(ApprovalCriterion))]
[JsonSerializable(typeof(ApprovalStepDefinition))]
[JsonSerializable(typeof(DefineApprovalProcess))]
[JsonSerializable(typeof(ApprovalProcessDefined))]
[JsonSerializable(typeof(SubmitForApproval))]
[JsonSerializable(typeof(ApprovalBy))]
[JsonSerializable(typeof(ApprovalSubmitted))]
[JsonSerializable(typeof(DecideApproval))]
[JsonSerializable(typeof(ApprovalDecided))]
[JsonSerializable(typeof(ReadApprovalInbox))]
[JsonSerializable(typeof(WaitingApproval))]
[JsonSerializable(typeof(ApprovalInbox))]

// The service desk: the week it is open, what it promises, and the cases it promises about.
[JsonSerializable(typeof(SetBusinessHours))]
[JsonSerializable(typeof(OpeningHoursOfDay))]
[JsonSerializable(typeof(BusinessHoursSet))]
[JsonSerializable(typeof(DefineSlaPolicy))]
[JsonSerializable(typeof(SlaPolicyDefined))]
[JsonSerializable(typeof(OpenCase))]
[JsonSerializable(typeof(CaseOpened))]
[JsonSerializable(typeof(CommentOnCase))]
[JsonSerializable(typeof(CaseCommented))]
[JsonSerializable(typeof(ServiceBy))]
[JsonSerializable(typeof(ReadCaseWorklist))]
[JsonSerializable(typeof(QueuedCase))]
[JsonSerializable(typeof(CaseWorklist))]

// Campaigns, what they touched, what they cost, and who gets the credit.
[JsonSerializable(typeof(DefineCampaign))]
[JsonSerializable(typeof(CampaignDefined))]
[JsonSerializable(typeof(RecordTouch))]
[JsonSerializable(typeof(TouchRecorded))]
[JsonSerializable(typeof(RecordCampaignCost))]
[JsonSerializable(typeof(CampaignCostRecorded))]
[JsonSerializable(typeof(ReadCampaignPerformance))]
[JsonSerializable(typeof(CampaignPerformance))]
[JsonSerializable(typeof(CampaignReport))]
[JsonSerializable(typeof(ReadDealAttribution))]
[JsonSerializable(typeof(AttributedCredit))]
[JsonSerializable(typeof(DealAttribution))]
[JsonSerializable(typeof(CampaignBy))]

// A page of a built-in entity. RecordPage and RecordView are already here for the custom query,
// which this surface deliberately reuses rather than answering in a second shape.
[JsonSerializable(typeof(ReadEntityPage))]
[JsonSerializable(typeof(ReadEntityRecords))]

// The connector registry, its queue and what goes on the wire.
[JsonSerializable(typeof(DefineConnector))]
[JsonSerializable(typeof(ConnectorDefined))]
[JsonSerializable(typeof(SetConnectorEnabled))]
[JsonSerializable(typeof(ConnectorEnablementSet))]
[JsonSerializable(typeof(PublishToConnector))]
[JsonSerializable(typeof(DeliveryQueued))]
[JsonSerializable(typeof(DeliveriesSwept))]
[JsonSerializable(typeof(ConnectorEnvelope))]
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
    /// One count per table and one statement, so the answer is one round trip and one snapshot
    /// rather than twenty-nine that can disagree with each other. Every count runs under the
    /// policies of migrations <c>0002</c> and <c>0005</c>, including the five tables that reach their tenant
    /// through a foreign key — which is what makes this endpoint a demonstration of the
    /// isolation rather than a report about the schema.
    /// </remarks>
    private const string Counts =
        """
                    SELECT 'account' AS table_name, count(*) AS row_count FROM account
        UNION ALL   SELECT 'activity',              count(*)              FROM activity
        UNION ALL   SELECT 'connector',             count(*)              FROM connector
        UNION ALL   SELECT 'connector_delivery',    count(*)              FROM connector_delivery
        UNION ALL   SELECT 'contact',               count(*)              FROM contact
        UNION ALL   SELECT 'custom_field',          count(*)              FROM custom_field
        UNION ALL   SELECT 'custom_filter_criterion', count(*)            FROM custom_filter_criterion
        UNION ALL   SELECT 'custom_field_history',  count(*)              FROM custom_field_history
        UNION ALL   SELECT 'custom_field_option',   count(*)              FROM custom_field_option
        UNION ALL   SELECT 'custom_formula',        count(*)              FROM custom_formula
        UNION ALL   SELECT 'custom_link',           count(*)              FROM custom_link
        UNION ALL   SELECT 'custom_list_view',      count(*)              FROM custom_list_view
        UNION ALL   SELECT 'custom_object',         count(*)              FROM custom_object
        UNION ALL   SELECT 'custom_record',         count(*)              FROM custom_record
        UNION ALL   SELECT 'custom_relationship',   count(*)              FROM custom_relationship
        UNION ALL   SELECT 'custom_rollup',         count(*)              FROM custom_rollup
        UNION ALL   SELECT 'custom_unique_value',   count(*)              FROM custom_unique_value
        UNION ALL   SELECT 'custom_validation_rule', count(*)             FROM custom_validation_rule
        UNION ALL   SELECT 'lead',                  count(*)              FROM lead
        UNION ALL   SELECT 'lead_enrichment',       count(*)              FROM lead_enrichment
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
