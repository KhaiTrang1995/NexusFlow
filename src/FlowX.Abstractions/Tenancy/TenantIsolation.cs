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
/// <strong>Three of the four are implemented, and the fourth is refused because it is not a
/// runtime mode.</strong> <see cref="None"/>, <see cref="Row"/> and <see cref="Schema"/> are
/// what a process can enforce between two tenants it is serving at the same time.
/// <see cref="Database"/> is not: it names a deployment per tenant, and the process serving
/// one of those has a single tenant and declares <see cref="None"/>. A runtime asked for it
/// says so through <see cref="TenantErrors.IsolationNotSupported"/> rather than quietly
/// serving <see cref="Schema"/> and letting a deployment believe it bought more separation
/// than it did. That is the same stance <c>AuthorizationErrors.StanceNotEnforceable</c> takes
/// for a stance the engine cannot decide, and it fails closed for the same reason.
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
    /// Shared compute, a schema per tenant. <c>docs/16 §2</c>'s L2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Row"/> plus a change of address. Everything <see cref="Row"/> requires still
    /// holds — a tenant is mandatory, it is derived from claims, and the connection still
    /// narrows itself to a role the policies apply to — and on top of that the connection comes
    /// from a pool of that tenant's own, whose <c>search_path</c> was fixed when the socket was
    /// opened. That is the load-bearing difference: a schema chosen per borrow is session state
    /// on a shared connection and would outlive the borrower, so the pools are separate instead.
    /// </para>
    /// <para>
    /// It costs a connection pool per tenant, bounded, and a schema per tenant to migrate. A
    /// tenant arriving at run time has its schema created and brought to the current version on
    /// first use, behind the same advisory lock a rolling update already relies on.
    /// </para>
    /// </remarks>
    Schema = 2,

    /// <summary>
    /// A dedicated deployment, and with it dedicated stores. <c>docs/16 §2</c>'s L3/L4.
    /// Refused at startup, and the refusal is the answer rather than a gap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>There is no second tenant in the process to be kept apart from.</strong> A
    /// dedicated deployment serves one tenant, so the pod running it points its connection
    /// string at that tenant's store and declares <see cref="None"/>; the separation is the
    /// topology, and the runtime's part in it is to have no part in it. Read as an in-process
    /// level — a connection string per tenant inside one host — it is <c>docs/16 §2</c>'s L2,
    /// which puts "schema <em>or</em> database per tenant" on one row, and <see cref="Schema"/>
    /// serves that.
    /// </para>
    /// <para>
    /// Two consequences follow that no amount of implementation would remove. Every store
    /// multiplies rather than just the journal: <c>flow_lease</c> is taken before the instance
    /// row exists and therefore before its tenant's store could be selected, and across
    /// databases there is no shared table to take it in. And nothing could enumerate the
    /// tenants — a registry lives in one database and cannot see the others — so the recovery
    /// scan and the timer sweep would have no set to sweep, which is isolation bought at the
    /// price of silently losing recovery.
    /// </para>
    /// </remarks>
    Database = 3,
}
