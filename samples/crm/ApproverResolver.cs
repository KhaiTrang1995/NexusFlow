using Npgsql;
using NpgsqlTypes;

namespace Crm;

/// <summary>
/// Works out who a step is waiting on.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A service and not a method on the store, because two capabilities need it and neither
/// may call the other.</strong> <c>CapabilitiesDoNotCallCapabilities</c> is the gate, and the
/// answer everywhere else in this sample has been the same: extract the shared decision, leave the
/// authorisation stance on each caller.
/// </para>
/// <para>
/// <strong>A step can resolve to more than one person.</strong> A role is held by several, and a
/// model that allowed only one would make the second an exception somebody works around by
/// inventing a second process.
/// </para>
/// </remarks>
public sealed class ApproverResolver
{
    private const string ManagerOf =
        "SELECT reports_to FROM org_member WHERE user_id = @user";

    private const string HoldersOfRole =
        "SELECT user_id FROM org_member WHERE role = @role ORDER BY user_id";

    private const string MemberExists =
        "SELECT count(*) FROM org_member WHERE user_id = @user";

    private readonly NpgsqlDataSource _source;

    /// <summary>Builds the resolver over the application's data source.</summary>
    /// <param name="source">The pool <c>AddFlowXPostgres</c> built.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public ApproverResolver(NpgsqlDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
    }

    /// <summary>Who may decide a step.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="step">Which step.</param>
    /// <param name="submittedBy">Who asked, for a step that names the submitter's manager.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// The people. Empty means nobody can decide it — which is why it is checked when a request is
    /// submitted rather than when somebody first tries.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="step"/> is null.</exception>
    public async ValueTask<IReadOnlyList<string>> ResolveAsync(
        string? tenantId,
        StoredStep step,
        string submittedBy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(step);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        switch (step.Kind)
        {
            case ApproverKind.Named:
                // Checked to exist rather than taken on trust: a step naming somebody who left is
                // a request that waits for ever, and the name alone cannot tell you which.
                return step.Approver is { Length: > 0 } named
                    && await ExistsAsync(connection, named, cancellationToken).ConfigureAwait(false)
                        ? [named]
                        : [];

            case ApproverKind.SubmittersManager:
                var manager = await ManagerAsync(connection, submittedBy, cancellationToken)
                    .ConfigureAwait(false);

                return manager is { Length: > 0 } ? [manager] : [];

            default:
                return step.Approver is { Length: > 0 } role
                    ? await HoldersAsync(connection, role, cancellationToken).ConfigureAwait(false)
                    : [];
        }
    }

    private static async ValueTask<bool> ExistsAsync(
        NpgsqlConnection connection,
        string userId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var closing = command.ConfigureAwait(false);

        command.CommandText = MemberExists;
        command.Parameters.Add(new NpgsqlParameter("user", NpgsqlDbType.Text) { Value = userId });

        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! > 0;
    }

    private static async ValueTask<string?> ManagerAsync(
        NpgsqlConnection connection,
        string userId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var closing = command.ConfigureAwait(false);

        command.CommandText = ManagerOf;
        command.Parameters.Add(new NpgsqlParameter("user", NpgsqlDbType.Text) { Value = userId });

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private static async ValueTask<IReadOnlyList<string>> HoldersAsync(
        NpgsqlConnection connection,
        string role,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var closing = command.ConfigureAwait(false);

        command.CommandText = HoldersOfRole;
        command.Parameters.Add(new NpgsqlParameter("role", NpgsqlDbType.Text) { Value = role });

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        var holders = new List<string>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            holders.Add(reader.GetString(0));
        }

        return holders;
    }

    private ValueTask<NpgsqlConnection> OpenAsync(string? tenantId, CancellationToken cancellationToken) =>
        CrmTenantScope.OpenAsync(_source, tenantId, cancellationToken);
}
