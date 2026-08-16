using Npgsql;

namespace FlowX.Postgres;

/// <summary>
/// The tenant a connection is bound to, and the one statement that binds it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two settings, and both are necessary.</strong> <c>flowx.tenant_id</c> is what
/// migration <c>0006</c>'s policies read, and it is the obvious half. <c>role</c> is the half
/// that is easy to leave out and fatal to leave out: <strong>a superuser bypasses row-level
/// security unconditionally, and a table's owner bypasses it unless the table declares
/// <c>FORCE ROW LEVEL SECURITY</c></strong>. A deployment whose journal connects as the role
/// that created the schema — which is the ordinary arrangement, and is exactly what
/// <c>FLOWX_POSTGRES_CONNECTION</c> points at in this repository's own test runs — would
/// install every policy correctly and be isolated by none of them. Assuming
/// <see cref="RoleName"/> is what puts the connection under the policies it just configured.
/// </para>
/// <para>
/// <strong>Transaction-local, and that is a correction.</strong> Both settings were written
/// with <c>set_config(…, false)</c> — session-scoped — once per connection open, on the
/// argument that a scoped journal rebinds on every open, so a pooled connection cannot carry
/// one execution's tenant into the next. That argument holds for Npgsql's pool, where a client
/// connection <em>is</em> a server session. **It is false in front of a transaction-pooling
/// proxy**, where one server connection is shared between clients and consecutive statements
/// from one client can land on different ones.
/// </para>
/// <para>
/// Reproduced against PgBouncer 1.22 and PostgreSQL 16 at <c>default_pool_size = 1</c>:
/// client A binds <c>tenant-A</c>, client B binds <c>tenant-B</c>, and A's next statement
/// reads <c>tenant-B</c>. The same three steps direct to PostgreSQL return the empty setting,
/// which is correct. That is a cross-tenant read, so the round-trip saving the old shape
/// bought is not a saving worth having.
/// </para>
/// <para>
/// <strong>What it costs, and who pays.</strong> <c>set_config(…, true)</c> lasts one
/// transaction, so a scoped connection now opens one and the bind runs inside it —
/// <see cref="ApplyAsync"/> takes the transaction rather than the connection so that a
/// bind with nothing to belong to cannot be written. An <b>unscoped</b> deployment is
/// untouched: <see cref="None"/> is never applied, no transaction is opened, and its reads
/// keep their single round trip.
/// </para>
/// </remarks>
internal readonly struct TenantScope
{
    /// <summary>
    /// The role a scoped connection assumes: present, unprivileged, and unable to bypass
    /// row-level security.
    /// </summary>
    /// <remarks>
    /// Created by migration <c>0006</c> and granted only what the journal's own statements
    /// need. It deliberately has <c>NOLOGIN</c>: nothing connects <em>as</em> it, and it is
    /// reachable only by a session that has already authenticated as the application's own
    /// role and then narrowed itself.
    /// </remarks>
    public const string RoleName = "flowx_tenant";

    /// <summary>The setting migration <c>0006</c>'s policies read.</summary>
    public const string SettingName = "flowx.tenant_id";

    private const string Bind =
        "SELECT set_config(@setting, @tenant, true), set_config('role', @role, true)";

    private readonly bool _scoped;

    private TenantScope(bool scoped, string? tenantId)
    {
        _scoped = scoped;
        TenantId = tenantId;
    }

    /// <summary>The unscoped state: no role change, no setting, no statement.</summary>
    public static TenantScope None => default;

    /// <summary>The tenant this scope binds to, or null for untenanted rows.</summary>
    public string? TenantId { get; }

    /// <summary>Whether anything has to be applied to a connection at all.</summary>
    public bool IsScoped => _scoped;

    /// <summary>
    /// Binds to one tenant, or to the untenanted rows when <paramref name="tenantId"/> is null.
    /// </summary>
    /// <param name="tenantId">The tenant, or null.</param>
    /// <returns>The scope.</returns>
    /// <remarks>
    /// A null tenant produces a scope that <em>is</em> applied, and that is the distinction
    /// this whole type turns on. It restricts the connection to rows with no tenant rather
    /// than lifting the restriction — so a runtime that resolved nothing reaches nobody's
    /// data instead of everybody's. <see cref="None"/> is the only thing that lifts it, and
    /// only a deployment declaring <see cref="TenantIsolation.None"/> can obtain one.
    /// </remarks>
    public static TenantScope For(string? tenantId) => new(true, tenantId);

    /// <summary>Applies this scope inside the transaction that will run the work.</summary>
    /// <param name="transaction">
    /// The open transaction the caller's statements will run in. Taking the transaction
    /// rather than the connection is the point: <c>set_config(…, true)</c> lasts for one
    /// transaction, so a call with no transaction to belong to would bind a setting that is
    /// discarded before the next statement runs. The type makes that call unwritable.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ArgumentNullException"><paramref name="transaction"/> is null.</exception>
    public async ValueTask ApplyAsync(
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        using var command = transaction.Connection!.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = Bind;
        command.Parameters.Add(Db.Text("setting", SettingName));

        // The empty string rather than NULL, because set_config's own signature takes text and
        // RESET restores a custom setting to '' rather than to NULL — so the policies read
        // both through the same nullif(...,'') and cannot disagree about which of the two
        // means "no tenant".
        command.Parameters.Add(Db.Text("tenant", TenantId ?? string.Empty));
        command.Parameters.Add(Db.Text("role", RoleName));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
