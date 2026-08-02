namespace FlowX;

/// <summary>
/// Which tenants this deployment serves, for the one decision that has to enumerate them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A schedule is the only trigger that needs a set rather than a value.</strong> A
/// change arrives from a tenant's schema and a message arrives with a tenant's field on it, so
/// each already knows whose work it is; a cron occurrence knows only an instant, and
/// <c>[CronTrigger(PerTenant = true)]</c> means "one firing per tenant", which cannot be
/// evaluated without the list. That is the whole reason this exists, and it is why the contract
/// is one method.
/// </para>
/// <para>
/// <strong>It is not a tenant registry and must not become one.</strong> Nothing here creates,
/// provisions, deletes or describes a tenant — <c>PostgresTenantStores</c> does that, one store
/// down, and a second place to ask "does this tenant exist" is a second answer. What this
/// returns is the set a fan-out iterates, read fresh on every pass so a tenant another node
/// provisioned a moment ago gets its firing rather than waiting for a restart.
/// </para>
/// <para>
/// <strong>A failure is a <see cref="Result"/> and not an exception</strong> (ADR-0007). A
/// directory that cannot be reached must not fire a schedule for a set it guessed at: the sweep
/// records the failure and tries again, which is one late firing rather than a firing in the
/// wrong tenant or in none.
/// </para>
/// </remarks>
public interface ITenantDirectory
{
    /// <summary>The tenants a per-tenant fan-out should visit, in a stable order.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// The tenants, or an <see cref="Error"/> when the set could not be read. An empty list is
    /// an ordinary answer and means no fan-out fires anything this pass.
    /// </returns>
    ValueTask<Result<IReadOnlyList<string>>> KnownTenantsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The tenants a deployment names in its own configuration.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The answer for <see cref="TenantIsolation.Row"/>, where nothing else knows.</strong>
/// At <see cref="TenantIsolation.Schema"/> the set is a table — every tenant has a schema and
/// <c>tenant_schema</c> lists them — but row isolation deliberately has no such registry: a
/// tenant exists there the moment a token carrying its claim arrives, and a tenant with no rows
/// yet is indistinguishable from one that was never provisioned. Deriving the list from
/// <c>SELECT DISTINCT tenant_id</c> would therefore skip exactly the tenant whose first
/// scheduled job has not run.
/// </para>
/// <para>
/// So the deployment states it. An empty list is the default and fires nothing, which is the
/// truthful behaviour for a host that was never told who it serves.
/// </para>
/// </remarks>
public sealed class DeclaredTenantDirectory : ITenantDirectory
{
    private readonly Result<IReadOnlyList<string>> _tenants;

    /// <summary>Creates a directory over a fixed, configured set.</summary>
    /// <param name="tenants">The tenants this deployment serves.</param>
    /// <exception cref="ArgumentNullException"><paramref name="tenants"/> is null.</exception>
    public DeclaredTenantDirectory(IReadOnlyList<string> tenants)
    {
        ArgumentNullException.ThrowIfNull(tenants);

        _tenants = Result.Ok<IReadOnlyList<string>>([.. tenants]);
    }

    /// <inheritdoc />
    public ValueTask<Result<IReadOnlyList<string>>> KnownTenantsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(_tenants);
    }
}
