using FlowX;

namespace Crm;

/// <summary>
/// One plan, whole.
/// </summary>
/// <remarks>
/// <c>crm.read</c>, the same grant the tree above it takes. A plan is data about the accounts a
/// representative already reads; making the detail administrative would mean the tree could name
/// a plan its owner could not open.
/// </remarks>
[Capability("crm.plan.read", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.read",
    Idempotent = true)]
public sealed class ReadCrmPlan : ICapability<ReadPlan, PlanDetail>
{
    private readonly PlanDetailStore _plans;

    /// <summary>Creates the capability.</summary>
    /// <param name="plans">Reads the plan.</param>
    /// <exception cref="ArgumentNullException"><paramref name="plans"/> is null.</exception>
    public ReadCrmPlan(PlanDetailStore plans)
    {
        ArgumentNullException.ThrowIfNull(plans);

        _plans = plans;
    }

    /// <inheritdoc />
    public async ValueTask<Result<PlanDetail>> ExecuteAsync(
        ReadPlan input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (string.IsNullOrWhiteSpace(input.Name))
        {
            return Result.Fail<PlanDetail>(PlanReadErrors.PlanNotFound(input.Name ?? string.Empty));
        }

        var plan = await _plans.ReadAsync(ctx.TenantId, input.Name, ct).ConfigureAwait(false);

        return plan is null
            ? Result.Fail<PlanDetail>(PlanReadErrors.PlanNotFound(input.Name))
            : Result.Ok(plan);
    }
}
