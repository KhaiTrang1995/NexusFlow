namespace FlowX.Redis;

/// <summary>Where in a Redis key space the lease store keeps its keys.</summary>
/// <remarks>
/// <para>
/// A prefix rather than a database number, because Redis Cluster has exactly one logical
/// database and a store configured by database index is a store that cannot be deployed to
/// the cluster mode most managed Redis offerings run. The database index is still available
/// for the single-node case, and defaults to whatever the connection selected.
/// </para>
/// </remarks>
public sealed record RedisLeaseOptions
{
    /// <summary>The prefix every key this store writes begins with. Defaults to <c>flowx</c>.</summary>
    /// <remarks>
    /// Namespacing matters more here than in a relational store: a schema is a first-class
    /// object in PostgreSQL and a key prefix is a convention in Redis, so two deployments
    /// sharing one Redis instance share one key space unless somebody says otherwise.
    /// </remarks>
    public string KeyPrefix { get; init; } = "flowx";

    /// <summary>
    /// Which logical database to use, or <c>-1</c> for the one the connection selected.
    /// </summary>
    /// <remarks>
    /// Left at <c>-1</c> by default. Redis Cluster rejects <c>SELECT</c> for anything other
    /// than database 0, so a non-default value here is a single-node-only configuration and
    /// should be a deliberate one.
    /// </remarks>
    public int Database { get; init; } = -1;
}
