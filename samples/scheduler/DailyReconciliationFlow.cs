using FlowX;

namespace Scheduler;

/// <summary>
/// Reconciles the ledger against the bank, every night, once per tenant, started by nothing but
/// a cron expression.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This declaration is the sample.</strong> Five properties beside the expression, and
/// every one of them is executed by the platform rather than by anything in this project: the
/// zone makes the 02:00 a wall-clock time and therefore DST-correct; the missed-fire policy
/// decides what a fleet that was down owes; the overlap policy decides what happens when a
/// night's run outlasts the next night's occurrence; the jitter spreads five hundred tenants
/// across two minutes instead of all of them hitting one bank at 02:00:00; and the fan-out
/// makes one occurrence one instance <em>per tenant</em>, each with its own journal row and its
/// own failure mode. There is no hosted service in this project, no timer, and no line in
/// <c>Program.cs</c> that mentions 02:00.
/// </para>
/// <para>
/// <strong>Its input is <see cref="ScheduledFire"/> and it has to be.</strong> A firing carries
/// no body, and <c>FLOWX1007</c> / <c>FLOWX1011</c> forbid the flow reading a clock to work out
/// which occurrence it is, so the instant arrives as data and is journalled on
/// <c>flow_instance.input</c> like any other trigger's payload
/// (<c>docs/adr/ADR-0033-a-scheduled-flows-input-is-its-occurrence.md</c>). This is also the
/// one thing <c>samples/scheduler/README.md</c> printed that never compiled: it declared
/// <c>Flow&lt;ReconciliationRequest, ReconciliationReport&gt;</c>, and <c>FLOWX1038</c> refuses
/// it. Nobody sends a reconciliation request; a night arrives.
/// </para>
/// <para>
/// <strong><c>Durable</c> is load-bearing.</strong> The id every node derives for a firing is
/// exclusive only because something refuses the second start, and what refuses it is
/// <c>flow_instance</c>'s primary key. An ephemeral flow with this attribute would reconcile
/// once per replica, every night, with nothing anywhere recording that it had
/// (<c>docs/adr/ADR-0031-an-occurrence-names-the-instance-it-starts.md</c>).
/// </para>
/// <para>
/// <strong>The declared expression is the business number, and a demonstration cannot wait for
/// it.</strong> <c>0 2 * * *</c> in <c>Europe/Berlin</c> is what the manifest publishes and what
/// <c>flowx diff</c> compares. <c>Program.cs</c> registers a <em>second</em>, denser schedule
/// from the environment rather than overriding this one, so the declaration a reader sees stays
/// the declaration the manifest carries.
/// </para>
/// </remarks>
[Flow("reconciliation.daily", Version = "1.0.0",
    Profile = ExecutionProfile.Durable, Owner = "finance-ops")]
[CronTrigger("0 2 * * *",
    TimeZone = "Europe/Berlin",         // DST-correct: a wall-clock time, not an instant.
    Overlap = OverlapPolicy.Skip,       // last night still going? this night is skipped.
    MissedFire = MissedFirePolicy.RunOnce,
    Jitter = "PT120S",                  // spread across tenants, derived so the fleet agrees.
    PerTenant = true)]                  // one occurrence, one instance per active tenant.
[FlowDeadline("PT10M")]
public sealed partial class DailyReconciliationFlow : Flow<ScheduledFire, ReconciliationReport>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<ScheduledFire, ReconciliationReport> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<LoadLedgerSnapshot>()

            // The external read, and the only step with a policy on it. It is also the step
            // that decides whether a night overruns — which is what makes the Overlap
            // declaration above a real decision rather than a default nobody reaches.
            .Step<LoadBankStatement>().WithPolicy(Policies.ExternalRead)

            // Two matchers over the same pair of inputs, joined with AllSettled: a heuristic
            // that fails must not throw away an exact-reference match that succeeded. Each
            // branch writes its own contract, because the state bag is keyed by type and
            // FLOWX1013 refuses two branches sharing a slot.
            //
            // The mapping lambda is what lets one capability bind two earlier outputs. It reads
            // only the context, which is the rule FLOWX1011 enforces on every builder lambda.
            .Parallel(
                p => p
                    .Branch(exact => exact.Step<MatchByReference, Reconcilable>(ctx =>
                        new Reconcilable(ctx.Get<LedgerSnapshot>(), ctx.Get<BankStatement>())))
                    .Branch(fuzzy => fuzzy.Step<MatchByAmountAndDate, Reconcilable>(ctx =>
                        new Reconcilable(ctx.Get<LedgerSnapshot>(), ctx.Get<BankStatement>()))),
                merge: MergeStrategy.AllSettled)

            .Step<ProduceReport, MatchSet>(ctx => new MatchSet(
                ctx.Get<LedgerSnapshot>(),
                ctx.Get<ReferenceMatches>(),
                ctx.Get<HeuristicMatches>()))

            // Staged in the same transaction as the step row above it, so a subscriber cannot
            // see a reconciliation the journal does not record.
            .Emit(ctx => new ReconciliationCompleted(
                ctx.Input.OccurrenceAt,
                ctx.Get<ReconciliationReport>().TenantId,
                ctx.Get<ReconciliationReport>().Unmatched))

            .Return(ctx => ctx.Get<ReconciliationReport>());
    }
}
