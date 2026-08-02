using FlowX;
using Npgsql;

namespace Crm;

/// <summary>Errors this application can produce.</summary>
/// <remarks>
/// Declared in one place so the codes are greppable and so two capabilities cannot invent two
/// spellings of the same condition. Each one reaches the manifest, the generated OpenAPI
/// responses and the RFC 7807 <c>type</c> URI.
/// </remarks>
public static class CrmErrors
{
    /// <summary>The CRM tables are not there, or are at a version this build does not know.</summary>
    /// <param name="found">What the schema reports.</param>
    /// <param name="expected">What this build writes against.</param>
    public static Error SchemaOutOfDate(int found, int expected) =>
        new Error(
            "crm.schema_out_of_date",
            $"The CRM schema is at version {found} and this build writes against {expected}. " +
            "Run the migrator, or deploy the build that matches.",
            ErrorCategory.Unavailable)
            .With("found", found)
            .With("expected", expected);

    /// <summary>PostgreSQL refused the read.</summary>
    /// <remarks>
    /// <c>Unavailable</c> rather than <c>Internal</c>: the caller can retry this, and the
    /// category is what decides whether the engine will.
    /// </remarks>
    public static Error SchemaUnreachable() =>
        new Error(
            "crm.schema_unreachable",
            "The CRM schema did not answer.",
            ErrorCategory.Unavailable);
}

/// <summary>
/// Counts what one tenant has, on a connection narrowed to that tenant.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The one capability in this package, and it is a read.</strong> §10 gives packages 4
/// to 12 the flows of §4 by name; a foundation that also shipped a write would be two branches
/// disagreeing about who owns <c>CreateAccount</c>.
/// </para>
/// <para>
/// <strong>It takes its tenant from <see cref="CapabilityContext.TenantId"/> and from nowhere
/// else.</strong> <see cref="CrmSchemaProbe"/> carries no members at all, so there is no field
/// on the request a caller could use to ask about somebody else's rows — and even if there
/// were, the connection this reads on is narrowed to the tenant the claims resolved to, and
/// the policies decide from there. What is demonstrated over HTTP is exactly what
/// <c>TenantIsolationTests</c> asserts against the tables.
/// </para>
/// <para>
/// <c>Authenticated</c> rather than a permission: knowing whether the schema this deployment
/// serves is present is not a business fact, and gating it behind a grant would mean a
/// deployment could not answer "are you healthy" without provisioning one. It is not
/// <c>Public</c> — the counts are a tenant's, and an anonymous caller has no tenant to be
/// narrowed to.
/// </para>
/// </remarks>
[Capability("crm.schema.count", Version = "1.0.0",
    Authorization = Authorization.Authenticated,
    Idempotent = true)]
public sealed class CountCrmRows : ICapability<CrmSchemaProbe, CrmSchemaReport>
{
    private readonly CrmSchemaReader _reader;

    /// <summary>Creates the capability over the schema reader.</summary>
    /// <param name="reader">Reads the schema version and the tenant's counts.</param>
    /// <exception cref="ArgumentNullException"><paramref name="reader"/> is null.</exception>
    public CountCrmRows(CrmSchemaReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        _reader = reader;
    }

    /// <inheritdoc />
    public async ValueTask<Result<CrmSchemaReport>> ExecuteAsync(
        CrmSchemaProbe input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        CrmSchemaReport report;

        try
        {
            report = await _reader.ReportAsync(ctx.TenantId, ct).ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            // The message is not carried through. A connection failure's text names hosts,
            // ports and sometimes roles, and an RFC 7807 body is the wrong place for any of
            // them; the operator has the log.
            return CrmErrors.SchemaUnreachable();
        }

        return report.SchemaVersion == CrmMigrator.TargetVersion
            ? report
            : CrmErrors.SchemaOutOfDate(report.SchemaVersion, CrmMigrator.TargetVersion);
    }
}
