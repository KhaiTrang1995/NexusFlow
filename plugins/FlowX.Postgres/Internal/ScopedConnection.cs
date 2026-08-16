using Npgsql;

namespace FlowX.Postgres;

/// <summary>
/// An open connection and, when the journal is tenant-scoped, the transaction its statements
/// must run in.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the transaction has to be carried rather than left on the connection.</strong>
/// The tenant binding is <c>set_config(…, true)</c>, which lasts exactly one transaction —
/// see <see cref="TenantScope"/> for the cross-tenant read that made it transaction-local.
/// Npgsql enlists a command in its connection's current transaction automatically, so the
/// statements need nothing; what needs the object is the <em>commit</em>, and Npgsql exposes
/// no way to reach a connection's transaction after the fact. Carrying it is the only way a
/// caller can end it deliberately.
/// </para>
/// <para>
/// <strong>An unscoped journal carries no transaction and pays nothing.</strong>
/// <see cref="Transaction"/> is null, <see cref="CommitAsync"/> is a no-op, and a
/// single-statement read costs exactly what it costs today. The guarantee is bought only by
/// the deployments that need it.
/// </para>
/// <para>
/// <strong>Disposal rolls back.</strong> A write that returns without committing leaves
/// nothing behind, which is the safe direction: a half-applied step boundary is the one
/// outcome the journal's atomicity exists to prevent.
/// </para>
/// </remarks>
internal readonly struct ScopedConnection : IAsyncDisposable
{
    /// <summary>Creates a session over an open connection.</summary>
    /// <param name="connection">The open connection. Owned by this session.</param>
    /// <param name="transaction">The transaction to run in, or null when unscoped.</param>
    public ScopedConnection(NpgsqlConnection connection, NpgsqlTransaction? transaction)
    {
        Connection = connection;
        Transaction = transaction;
    }

    /// <summary>The open connection.</summary>
    public NpgsqlConnection Connection { get; }

    /// <summary>The transaction the statements run in, or null when unscoped.</summary>
    public NpgsqlTransaction? Transaction { get; }

    /// <summary>
    /// Ends the transaction successfully, if there is one.
    /// </summary>
    /// <param name="cancellationToken">Cancels the commit.</param>
    /// <returns>A task that completes when the work is durable.</returns>
    /// <remarks>
    /// A read path may call this or not; disposal would roll back a transaction that only
    /// read, which changes nothing. A write path must call it, and
    /// <c>JournalConformance</c> is what proves each one does.
    /// </remarks>
    public async ValueTask CommitAsync(CancellationToken cancellationToken)
    {
        if (Transaction is not null)
        {
            await Transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The transaction goes first: disposing the connection under an open transaction is the
    /// order that produces a rollback nobody asked for and a warning nobody reads.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Transaction is not null)
        {
            await Transaction.DisposeAsync().ConfigureAwait(false);
        }

        await Connection.DisposeAsync().ConfigureAwait(false);
    }
}
