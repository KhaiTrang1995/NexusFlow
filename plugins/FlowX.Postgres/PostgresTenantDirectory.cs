using Npgsql;

namespace FlowX.Postgres;

/// <summary>
/// The tenant set a per-tenant schedule fans out over, read from <c>tenant_schema</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>An adapter and nothing else.</strong> <see cref="PostgresTenantStores"/> already keeps
/// the registry — it has to, because a schema is what it hands out — and this exposes the one
/// question the host asks of it under a contract the host can name without referencing this
/// package (ADR-0009). Giving <c>PostgresTenantStores</c> the interface directly would have put a
/// <c>FlowX.Abstractions</c> obligation on a class whose job is connection pools.
/// </para>
/// <para>
/// <strong>A store that cannot be reached is a <see cref="Result"/> rather than a throw</strong>,
/// which is the whole reason this is not a two-line lambda. The registry read opens a connection,
/// and a schedule sweep that took an exception from it would lose the pass rather than the
/// schedule: <c>FlowScheduleScan</c> counts a directory that did not answer as one failed
/// schedule and fires the rest.
/// </para>
/// </remarks>
public sealed class PostgresTenantDirectory : ITenantDirectory
{
    /// <summary>The code <see cref="KnownTenantsAsync"/> raises when the registry is unreachable.</summary>
    public const string UnreachableCode = "postgres.tenant_directory_unavailable";

    private readonly PostgresTenantStores _stores;

    /// <summary>Creates a directory over the registry the tenant stores keep.</summary>
    /// <param name="stores">The per-tenant pools, and the registry that lists them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stores"/> is null.</exception>
    public PostgresTenantDirectory(PostgresTenantStores stores)
    {
        ArgumentNullException.ThrowIfNull(stores);

        _stores = stores;
    }

    /// <inheritdoc />
    public async ValueTask<Result<IReadOnlyList<string>>> KnownTenantsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var tenants = await _stores.KnownTenantsAsync(cancellationToken).ConfigureAwait(false);

            return Result.Ok(tenants);
        }
        catch (NpgsqlException unreachable)
        {
            return Result.Fail<IReadOnlyList<string>>(new Error(
                UnreachableCode,
                "The tenant registry did not answer, so this pass does not know which tenants a " +
                $"per-tenant schedule fires for: {unreachable.Message} No firing is attempted, " +
                "because firing the tenants a partial answer named would record the occurrence " +
                "as done for them and skip it for the rest.",
                ErrorCategory.Unavailable));
        }
    }
}
