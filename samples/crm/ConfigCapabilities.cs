using FlowX;

namespace Crm;

/// <summary>
/// What a tenant has declared, of one kind.
/// </summary>
/// <remarks>
/// <para>
/// <strong><c>crm.admin</c>, because this is the administrator's view of the tenant.</strong> The
/// sentences it composes describe what refuses a write, who has to approve what, and where this
/// server sends. None of that is secret from an administrator and none of it is any of a
/// representative's business — and a setup screen that anybody could read is a map of the
/// controls for anybody who wants to work around them.
/// </para>
/// <para>
/// Idempotent, and it writes nothing.
/// </para>
/// </remarks>
[Capability("crm.config.list", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.admin",
    Idempotent = true)]
public sealed class ReadCrmConfig : ICapability<ReadConfig, ConfigList>
{
    private readonly ConfigStore _config;

    /// <summary>Creates the capability.</summary>
    /// <param name="config">Lists the declarations.</param>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> is null.</exception>
    public ReadCrmConfig(ConfigStore config)
    {
        ArgumentNullException.ThrowIfNull(config);

        _config = config;
    }

    /// <inheritdoc />
    public async ValueTask<Result<ConfigList>> ExecuteAsync(
        ReadConfig input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var items = await _config.ListAsync(ctx.TenantId, input, ct).ConfigureAwait(false);

        return Result.Ok(new ConfigList(input.Kind, items));
    }
}
