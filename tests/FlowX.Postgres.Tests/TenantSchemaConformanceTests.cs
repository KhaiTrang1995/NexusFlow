using FlowX.Conformance;
using Xunit;

namespace FlowX.Postgres.Tests;

/// <summary>
/// Runs the whole journal suite through a tenant's own schema.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The suite is inherited unmodified, and that is what makes this worth running
/// twice.</strong> <c>PostgresJournalConformanceTests</c> answers ADR-0015's commitments through
/// the ordinary connection; this answers every one of them through a per-tenant pool, into a
/// schema that was created and migrated at run time, under the <c>flowx_tenant</c> role, with
/// migration <c>0008</c>'s policies live. Six journal members, one of which — the fence — is the
/// thing every durable write depends on, all reached the way a deployment declaring
/// <see cref="TenantIsolation.Schema"/> reaches them.
/// </para>
/// <para>
/// <strong>Only the tenant is supplied, and it is supplied because the suite has no opinion
/// about tenants.</strong> <see cref="JournalConformance"/> opens every instance with no tenant
/// at all, which is correct for a contract that most stores satisfy without one — and a row with
/// no tenant is precisely what a connection scoped to a tenant is refused permission to write.
/// So the wrapper stamps the tenant onto the one member that carries it and delegates the other
/// five untouched. Nothing else about the suite or the adapter is adjusted; if it were, the
/// result would be a suite shaped around the store, which
/// <c>docs/17-Plugin-System.md §5</c> is explicit is not conformance.
/// </para>
/// </remarks>
public sealed class PostgresTenantSchemaJournalConformanceTests : JournalConformance, IAsyncLifetime
{
    private const string Tenant = "acme";

    private readonly List<PostgresTestSchema> _schemas = [];

    /// <inheritdoc />
    protected override async ValueTask<IFlowJournal> CreateJournalAsync()
    {
        var schema = await PostgresTestSchema.CreateWithTenantSchemasAsync(Cancellation);

        _schemas.Add(schema);

        return new TenantedJournal(schema.Journal.ForTenant(Tenant), Tenant);
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var schema in _schemas)
        {
            await schema.DisposeAsync();
        }

        _schemas.Clear();
    }

    /// <summary>
    /// The scoped journal, with the tenant the suite does not know about filled in.
    /// </summary>
    /// <remarks>
    /// One line of behaviour on one member. Every other call is forwarded to the real adapter
    /// unchanged, so nothing the suite asserts is being answered by this class.
    /// </remarks>
    private sealed class TenantedJournal : IFlowJournal
    {
        private readonly IFlowJournal _inner;
        private readonly string _tenantId;

        public TenantedJournal(IFlowJournal inner, string tenantId)
        {
            _inner = inner;
            _tenantId = tenantId;
        }

        public ValueTask<Result<FlowInstanceRecord>> StartAsync(
            FlowInstanceStart start, CancellationToken cancellationToken) =>
            _inner.StartAsync(start with { TenantId = _tenantId }, cancellationToken);

        public ValueTask<Result<FlowInstanceRecord>> ReadInstanceAsync(
            Guid instanceId, CancellationToken cancellationToken) =>
            _inner.ReadInstanceAsync(instanceId, cancellationToken);

        public ValueTask<Result<ResumeFrontier>> ReadResumeFrontierAsync(
            Guid instanceId, CancellationToken cancellationToken) =>
            _inner.ReadResumeFrontierAsync(instanceId, cancellationToken);

        public ValueTask<Result<JournalStep>> CommitAsync(
            StepCommit commit, CancellationToken cancellationToken) =>
            _inner.CommitAsync(commit, cancellationToken);

        public ValueTask<Result<FlowInstanceRecord>> CompleteAsync(
            Guid instanceId,
            FencingToken token,
            FlowInstanceState state,
            JournalPayload stateBag,
            FlowWake? wake,
            CancellationToken cancellationToken) =>
            _inner.CompleteAsync(instanceId, token, state, stateBag, wake, cancellationToken);

        public ValueTask<Result<FencingToken>> FenceAsync(
            Guid instanceId, FencingToken token, CancellationToken cancellationToken) =>
            _inner.FenceAsync(instanceId, token, cancellationToken);

        public ValueTask<Result<IReadOnlyList<OutboxRecord>>> ReadOutboxAsync(
            Guid instanceId, CancellationToken cancellationToken) =>
            _inner.ReadOutboxAsync(instanceId, cancellationToken);
    }
}
