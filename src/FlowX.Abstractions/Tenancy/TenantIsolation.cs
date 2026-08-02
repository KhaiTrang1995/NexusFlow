namespace FlowX;

/// <summary>
/// How far apart a deployment keeps its tenants' data.
/// </summary>
/// <remarks>
/// <para>
/// The four levels of <c>docs/16-Multi-Tenant.md §2</c>, as a closed set the runtime can
/// branch on. They are ordered by strength, and the ordering is load-bearing: a deployment
/// moving a tenant up the scale changes configuration and topology, never flow or capability
/// code, which is the design's main payoff and the reason the level is a property of the
/// <em>deployment</em> rather than of anything a flow author writes.
/// </para>
/// <para>
/// <strong>Two of the four are implemented, and naming the other two is deliberate rather
/// than aspirational.</strong> <see cref="None"/> and <see cref="Row"/> are what
/// <c>docs/16 §5</c> specifies in enough detail to build — the second as worked DDL. The
/// remaining two need <c>ITenantStoreResolver</c> and a per-tenant connection pool, which
/// that section describes and no record decides; a runtime asked for one of them says so
/// through <see cref="TenantErrors.IsolationNotSupported"/> rather than quietly serving
/// <see cref="Row"/> and letting a deployment believe it bought more separation than it did.
/// That is the same stance <c>AuthorizationErrors.StanceNotEnforceable</c> takes for a
/// stance the engine cannot decide, and it fails closed for the same reason.
/// </para>
/// </remarks>
public enum TenantIsolation
{
    /// <summary>
    /// Single-tenant. No tenant is resolved, none is required, and nothing is scoped.
    /// </summary>
    /// <remarks>
    /// The default, and it costs exactly nothing: the host reaches no resolver, the journal
    /// applies no scope, and the connection issues no extra statement. A single-tenant
    /// deployment must not pay for a feature it did not ask for, which is the bargain
    /// <c>ExecutionPlan.HasAuthorizedSteps</c> struck for the authorisation stage and this
    /// strikes again one layer up.
    /// </remarks>
    None = 0,

    /// <summary>
    /// Shared database, one row-level tenant column, enforced by PostgreSQL row-level
    /// security. <c>docs/16 §2</c>'s L1.
    /// </summary>
    /// <remarks>
    /// A tenant becomes mandatory: an untenanted call is refused at admission rather than
    /// defaulted, because a default tenant is the shape of a cross-tenant read. The store
    /// binds each connection to the resolved tenant and the database's policy decides — so
    /// a capability with a bug still cannot reach another tenant's rows.
    /// </remarks>
    Row = 1,

    /// <summary>
    /// Shared compute, a schema or database per tenant. <c>docs/16 §2</c>'s L2. Not built.
    /// </summary>
    Schema = 2,

    /// <summary>
    /// A dedicated deployment, and with it dedicated stores. <c>docs/16 §2</c>'s L3/L4.
    /// Not built — and largely a deployment concern rather than a runtime one.
    /// </summary>
    Database = 3,
}
