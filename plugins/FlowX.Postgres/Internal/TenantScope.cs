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
/// <strong>Session-scoped, and re-applied on every open rather than trusted to be
/// cleared.</strong> <c>DISCARD ALL</c> — which Npgsql sends when it returns a dirty
/// connection to the pool — does reset both, but correctness here does not rest on that:
/// a scoped journal writes both values on every connection it opens, so a pooled connection
/// cannot carry one execution's tenant into the next. The alternative, <c>SET LOCAL</c> inside
/// an explicit transaction, was rejected for the two reads that have no transaction of their
/// own: it would buy the same guarantee for two extra round trips per read.
/// </para>
/// <para>
/// <strong>One round trip.</strong> Both settings are written by a single statement issued
/// immediately after the connection is opened, so scoping costs a scoped deployment one
/// message and costs an unscoped one nothing at all — <see cref="None"/> is not applied.
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
        "SELECT set_config(@setting, @tenant, false), set_config('role', @role, false)";

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

    /// <summary>Applies this scope to a freshly opened connection.</summary>
    /// <param name="connection">The connection to bind.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async ValueTask ApplyAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();

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
