using FlowX;

namespace Crm;

/// <summary>
/// Answers whether this deployment's CRM schema is present, and what of it the caller's tenant
/// can reach.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One step, and that is the point.</strong> This package is §10's third — the
/// schema, the entity records and the row-level security everything else stands on — and the
/// flows of §4 belong to packages 4 to 12. What is needed here is that
/// <c>dotnet run --project samples/crm</c> serves something, so the project is an application
/// rather than a library and the wiring in <c>Program.cs</c> is exercised rather than
/// described.
/// </para>
/// <para>
/// <strong><c>Ephemeral</c>, deliberately.</strong> A durable profile would journal a health
/// probe: an instance row, a step row and a lease, per call, for a read that changes nothing
/// and is worth nothing on replay. The flows that need durability are the saga in §8.1 and the
/// waits in §8.2 and §8.4, and they declare it.
/// </para>
/// <para>
/// <strong><c>POST</c> for a read, and it is the transport rather than the design.</strong>
/// The generated endpoint deserialises the flow's input from the request body, so a <c>GET</c>
/// route would bind nothing. <see cref="CrmSchemaProbe"/> is empty and the body is <c>{}</c>.
/// </para>
/// </remarks>
[Flow("crm.schema.probe", Version = "1.0.0", Profile = ExecutionProfile.Ephemeral, Owner = "crm-platform")]
[FlowDeadline("PT10S")]
[HttpTrigger("POST", "/api/v1/crm/schema-probes")]
public sealed partial class ProbeCrmSchemaFlow : Flow<CrmSchemaProbe, CrmSchemaReport>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<CrmSchemaProbe, CrmSchemaReport> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<CountCrmRows>()
            .Return(ctx => ctx.Get<CrmSchemaReport>());
    }
}
