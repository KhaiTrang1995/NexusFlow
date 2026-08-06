using System.Text.Json;
using FlowX;

namespace Crm;

/// <summary>An opportunity moved, and the configured process has not run yet.</summary>
/// <param name="OpportunityId">Which opportunity.</param>
/// <param name="Trigger">What happened, in the administrator's vocabulary.</param>
public sealed record OpportunityStageChanged(Guid OpportunityId, string Trigger);

/// <summary>What the configured process did, or why it did nothing.</summary>
/// <param name="OpportunityId">The opportunity.</param>
/// <param name="TransitionId">The transition taken, or null when none was allowed.</param>
/// <param name="ActionsRun">How many actions ran.</param>
public sealed record TransitionApplied(Guid OpportunityId, Guid? TransitionId, int ActionsRun);

/// <summary>Refusals the transition engine can produce.</summary>
public static class TransitionErrors
{
    /// <summary>The opportunity is not in this tenant, or is gone.</summary>
    /// <param name="opportunityId">What was named.</param>
    public static Error OpportunityNotFound(Guid opportunityId) =>
        new Error(
            "crm.opportunity_not_found",
            "That opportunity is not in this tenant.",
            ErrorCategory.NotFound)
            .With("opportunityId", opportunityId);

    /// <summary>Its stage belongs to no definition this deployment can read.</summary>
    /// <param name="stageId">The stage.</param>
    public static Error ProcessNotFound(Guid stageId) =>
        new Error(
            "crm.process_not_found",
            "That opportunity's stage belongs to no readable process definition.",
            ErrorCategory.NotFound)
            .With("stageId", stageId);
}

/// <summary>
/// Runs whatever the administrator configured for the transition an opportunity just made.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the capability §7 rests on.</strong> Every branch below is compiled: five
/// action kinds, a closed enumeration, a manifest that publishes all five. What the
/// administrator supplies is which of them run, in what order, with what parameters — and the
/// guards that decide whether the transition happens at all.
/// </para>
/// <para>
/// <strong>What it will not do is grow a sixth kind from the database.</strong> There is no
/// dispatch on a string, no reflection, no plugin loader. A new kind of side effect is a code
/// change, a build and a deployment, and §7.3 says so in as many words. That is the price of
/// compile-time orchestration and it is paid here rather than hidden.
/// </para>
/// <para>
/// <strong>Actions are attempted in order and a failure does not stop the rest.</strong> An
/// administrator's typo in one action's parameters should not silently drop the approval
/// request after it. What each action did is in the row it wrote; what ran is the count this
/// returns.
/// </para>
/// </remarks>
[Capability("crm.process.run_transition", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = false,
    SideEffects = ["crm.opportunity.written", "crm.activity.written"])]
public sealed class RunConfiguredTransition : ICapability<BusMessage, TransitionApplied>
{
    private readonly ProcessStore _store;
    private readonly ConnectorStore _connectors;

    /// <summary>Creates the capability.</summary>
    /// <param name="store">Reads the definition and writes what the actions do.</param>
    /// <param name="connectors">
    /// Resolves the connector a <see cref="ActionKind.SendNotification"/> names, and queues what
    /// it sends.
    /// </param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public RunConfiguredTransition(ProcessStore store, ConnectorStore connectors)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(connectors);

        _store = store;
        _connectors = connectors;
    }

    /// <inheritdoc />
    public async ValueTask<Result<TransitionApplied>> ExecuteAsync(
        BusMessage input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (Read(input) is not { } changed)
        {
            return Result.Fail<TransitionApplied>(IntakeErrors.EventHasNoBody(input.EventId));
        }

        if (await _store.ReadStageAsync(ctx.TenantId, changed.OpportunityId, ct).ConfigureAwait(false)
            is not { } stage)
        {
            return Result.Fail<TransitionApplied>(
                TransitionErrors.OpportunityNotFound(changed.OpportunityId));
        }

        if (await _store.ReadForStageAsync(ctx.TenantId, stage, ct).ConfigureAwait(false)
            is not { } snapshot)
        {
            return Result.Fail<TransitionApplied>(TransitionErrors.ProcessNotFound(stage));
        }

        if (await _store.ReadFactsAsync(ctx.TenantId, changed.OpportunityId, ct).ConfigureAwait(false)
            is not { } facts)
        {
            return Result.Fail<TransitionApplied>(
                TransitionErrors.OpportunityNotFound(changed.OpportunityId));
        }

        // The decision, and it is pure: nothing below reads the database again.
        var taken = ProcessRules.Match(snapshot.Candidates, stage, changed.Trigger, facts);

        if (taken is null)
        {
            // Not an error. Nothing the administrator configured applies here, and a recorded
            // "nothing happened" is what lets the change cursor move past this change.
            return Result.Ok(new TransitionApplied(changed.OpportunityId, null, 0));
        }

        await _store.MoveAsync(ctx.TenantId, changed.OpportunityId, taken.Transition.To, ctx.UtcNow, ct)
            .ConfigureAwait(false);

        var ran = 0;

        foreach (var action in taken.Actions)
        {
            if (await RunAsync(ctx, changed.OpportunityId, facts, action, ct).ConfigureAwait(false))
            {
                ran++;
            }
        }

        return Result.Ok(new TransitionApplied(changed.OpportunityId, taken.Transition.Id, ran));
    }

    /// <summary>Reads the change's body.</summary>
    /// <param name="message">What the change feed handed over.</param>
    /// <returns>The event, or null when the body was absent.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    public static OpportunityStageChanged? Read(BusMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return message.Payload is { Length: > 0 } body
            ? JsonSerializer.Deserialize(body, CrmJsonContext.Default.OpportunityStageChanged)
            : null;
    }

    /// <summary>
    /// The five kinds, as a switch the compiler sees the whole of.
    /// </summary>
    private async ValueTask<bool> RunAsync(
        CapabilityContext ctx,
        Guid opportunityId,
        ProcessFacts facts,
        TransitionAction action,
        CancellationToken ct)
    {
        switch (action.Kind)
        {
            case ActionKind.CreateTask:
                await _store.CreateTaskAsync(
                    ctx.TenantId,
                    ActionIds.Task(opportunityId, action.Id),
                    opportunityId,
                    ActionParameters.Text(action.Parameters, "subject", "Follow up"),
                    facts.Owner ?? Guid.Empty,
                    ctx.UtcNow.AddDays(ActionParameters.Number(action.Parameters, "dueInDays", 3)),
                    ct).ConfigureAwait(false);

                return true;

            case ActionKind.RequestApproval:
                // An approval is a task somebody owes, and saying so is more honest than a
                // second table that holds the same three columns under a different name. What
                // makes it an approval is the subject and who owes it.
                await _store.CreateTaskAsync(
                    ctx.TenantId,
                    ActionIds.Task(opportunityId, action.Id),
                    opportunityId,
                    ActionParameters.Text(action.Parameters, "subject", "Approval required"),
                    facts.Owner ?? Guid.Empty,
                    ctx.UtcNow.AddDays(ActionParameters.Number(action.Parameters, "dueInDays", 1)),
                    ct).ConfigureAwait(false);

                return true;

            case ActionKind.SetField:
                await _store.SetProbabilityAsync(
                    ctx.TenantId,
                    opportunityId,
                    Math.Clamp(ActionParameters.Number(action.Parameters, "probability", 0), 0, 100),
                    ct).ConfigureAwait(false);

                return true;

            case ActionKind.SendNotification:
                // The one action that leaves this system, and the reason the connector registry
                // exists. The administrator names a connector they registered; this queues what
                // it is to send and the sweep delivers it. Nothing is posted here: a transition
                // that made an outbound request inline would hold its transaction open across
                // somebody else's gateway and fail the transition when they are slow.
                //
                // A connector that is not registered, or is switched off, leaves this returning
                // false — the same answer the whole action gave before, and the honest one. It
                // is not an error: an administrator turning an integration off must not start
                // failing every transition that mentions it.
                return await NotifyAsync(ctx, opportunityId, action, ct).ConfigureAwait(false);

            case ActionKind.EmitEvent:
                // Reaches a broker the deployment chooses, and this sample wires none for the
                // configured process. In the enumeration because the manifest publishes all five
                // and an administrator may configure it; what it does here is nothing, and
                // saying so is better than a stub that looks like it worked.
                return false;

            default:
                // Unreachable: ProcessPublishing.Validate refuses a kind outside the enumeration
                // before it can be stored, and the enumeration is closed at build time.
                return false;
        }
    }

    /// <summary>Queues what a configured notification sends, if its connector is usable.</summary>
    /// <remarks>
    /// The payload is what an administrator can act on without joining anything: which
    /// opportunity, and the trigger that moved it. Widening it is a decision about what leaves
    /// the tenant, which is why it is written here rather than assembled from the action's
    /// parameters.
    /// </remarks>
    private async ValueTask<bool> NotifyAsync(
        CapabilityContext ctx,
        Guid opportunityId,
        TransitionAction action,
        CancellationToken ct)
    {
        var name = ActionParameters.Text(action.Parameters, "connector", string.Empty);

        if (name.Length == 0 ||
            await _connectors.ReadByNameAsync(ctx.TenantId, name, ct).ConfigureAwait(false)
                is not { IsEnabled: true } registered)
        {
            return false;
        }

        await _connectors
            .QueueAsync(
                ctx.TenantId,
                ActionIds.Delivery(opportunityId, action.Id),
                registered.Connector.Id,
                ActionParameters.Text(action.Parameters, "subject", "An opportunity moved"),
                ConnectorPayload.ToJson(new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["opportunityId"] = opportunityId.ToString(),
                    ["subject"] = ActionParameters.Text(action.Parameters, "subject", string.Empty),
                }),
                ctx.UtcNow,
                ct)
            .ConfigureAwait(false);

        return true;
    }
}

/// <summary>Derives the ids a configured action writes under.</summary>
/// <remarks>
/// <strong>From the opportunity and the action, so a re-run writes the same row.</strong> A
/// change feed re-offers a change whose flow did not reach an outcome, and an action minting a
/// fresh id each time would leave a second task behind on every redelivery.
/// </remarks>
public static class ActionIds
{
    /// <summary>
    /// The delivery a given <see cref="ActionKind.SendNotification"/> on a given opportunity
    /// queues.
    /// </summary>
    /// <param name="opportunityId">The opportunity.</param>
    /// <param name="actionId">The configured action.</param>
    /// <returns>The derived id.</returns>
    public static Guid Delivery(Guid opportunityId, Guid actionId) =>
        Derive(opportunityId, actionId, salt: 1);

    /// <summary>The activity a given action on a given opportunity creates.</summary>
    /// <param name="opportunityId">The opportunity.</param>
    /// <param name="actionId">The configured action.</param>
    /// <returns>The derived id.</returns>
    public static Guid Task(Guid opportunityId, Guid actionId) =>
        Derive(opportunityId, actionId, salt: 0);

    /// <remarks>
    /// Salted, so one action that both creates a task and notifies cannot derive the same id for
    /// the two — they are different rows in different tables and a collision would be invisible
    /// until one of them was missing.
    /// </remarks>
    private static Guid Derive(Guid opportunityId, Guid actionId, byte salt)
    {
        Span<byte> seed = stackalloc byte[33];

        opportunityId.TryWriteBytes(seed);
        actionId.TryWriteBytes(seed[16..]);
        seed[32] = salt;

        Span<byte> hash = stackalloc byte[32];

        System.Security.Cryptography.SHA256.HashData(seed, hash);

        var id = hash[..16];

        id[6] = (byte)((id[6] & 0x0F) | 0x80);
        id[8] = (byte)((id[8] & 0x3F) | 0x80);

        return new Guid(id);
    }
}

/// <summary>
/// Runs the configured process when an opportunity's stage changes.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Started by the change feed, not by a broker, and §8.3 says why.</strong> The event
/// is already durable when this runs — it was staged in the moving step's own transaction — so
/// a broker outage cannot lose a configured action. The cursor advances past a change only once
/// its flow has reached a recorded outcome.
/// </para>
/// <para>
/// <strong>Its tenant is attested rather than resolved.</strong> A change carries no caller;
/// the platform reads the tenant from the emitting instance's row and the capability's stance is
/// <c>Internal</c> in consequence.
/// </para>
/// </remarks>
[Flow("crm.process.transition", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT60S")]
[ChangeTrigger("opportunity.stage.changed", Group = "process")]
public sealed partial class RunWorkflowTransitionFlow : Flow<BusMessage, TransitionApplied>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<BusMessage, TransitionApplied> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<RunConfiguredTransition>()
            .Return(ctx => ctx.Get<TransitionApplied>());
    }
}
