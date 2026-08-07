using FlowX;

namespace Crm;

/// <summary>
/// The active process for one entity kind.
/// </summary>
/// <remarks>
/// <c>crm.read</c> and not <c>crm.admin</c>: the stages are what a pipeline board draws and what
/// a representative moves a deal between. A process only an administrator could read would be a
/// board only an administrator could see.
/// </remarks>
[Capability("crm.process.read", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.read",
    Idempotent = true)]
public sealed class ReadCrmProcess : ICapability<ReadProcess, ProcessView>
{
    private readonly ProcessViewStore _processes;

    /// <summary>Creates the capability.</summary>
    /// <param name="processes">Reads the definition.</param>
    /// <exception cref="ArgumentNullException"><paramref name="processes"/> is null.</exception>
    public ReadCrmProcess(ProcessViewStore processes)
    {
        ArgumentNullException.ThrowIfNull(processes);

        _processes = processes;
    }

    /// <inheritdoc />
    public async ValueTask<Result<ProcessView>> ExecuteAsync(
        ReadProcess input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var process = await _processes.ReadAsync(ctx.TenantId, input.AppliesTo, ct).ConfigureAwait(false);

        return process is null
            ? Result.Fail<ProcessView>(ProcessViewErrors.NoActiveProcess(input.AppliesTo))
            : Result.Ok(process);
    }
}
