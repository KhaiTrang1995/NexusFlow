using FlowX.Observability;
using Shouldly;
using Xunit;

namespace FlowX.Postgres.Tests;

/// <summary>
/// One tenant's schema genuinely broken, against a real database, and what the two node-wide
/// sweeps do about it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The tenant is broken by dropping its schema, not by a double.</strong> A fake store
/// that returns a failure would assert what this code does with a value it was handed; the
/// failure a deployment actually meets arrives as a <c>PostgresException</c> thrown from a
/// query against a schema that is no longer there, and it arrives through
/// <see cref="PostgresTenantStores"/>'s own pool for a tenant the registry still lists. Only
/// the real arrangement proves the fan-out survives the shape the exception really has.
/// </para>
/// <para>
/// <strong>Tenant ids are unique per test.</strong> The log assertions read a process-wide
/// <see cref="System.Diagnostics.DiagnosticListener"/>, so a fixed pair of names would let one
/// test read another's records while both were running.
/// </para>
/// </remarks>
public sealed class TenantSweepIsolationTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>
    /// A recovery scan whose first tenant cannot be reached still returns the other tenants'
    /// abandoned instances.
    /// </summary>
    /// <remarks>
    /// The defect this closes: the fan-out returned the first tenant's failure and abandoned
    /// the page, so one dropped schema, one revoked grant or one unreachable pool stopped
    /// recovery for every other tenant on the node — for as long as the repair took.
    /// </remarks>
    [Fact]
    public async Task ARecoveryScanStepsOverABrokenTenantAndStillReturnsTheOthers()
    {
        await using var schema = await PostgresTestSchema.CreateWithTenantSchemasAsync(Cancellation);

        var (broken, healthy) = Tenants();

        var abandoned = await AbandonAsync(schema, healthy);
        _ = await AbandonAsync(schema, broken);

        await DropSchemaAsync(schema, broken);

        var listed = await schema.RecoveryScan.ListAbandonedAsync(AbandonedQuery(), Cancellation);

        listed.IsSuccess.ShouldBeTrue(
            "one tenant's schema is gone. Refusing the whole page for it strands every other " +
            $"tenant's abandoned instances until somebody repairs that one.\n{Failure(listed)}");

        listed.Value.Select(candidate => candidate.InstanceId).ShouldContain(abandoned);

        listed.Value.ShouldAllBe(
            candidate => candidate.TenantId == healthy,
            "the broken tenant contributed nothing, and nothing else did either.");
    }

    /// <summary>
    /// A timer sweep whose first tenant cannot be reached still wakes the other tenants'
    /// parked instances.
    /// </summary>
    /// <remarks>
    /// Asserted separately rather than assumed from the recovery scan: they are two classes
    /// over disjoint sets of rows, and a parked instance nobody wakes fails as a business
    /// outcome — the offer is never withdrawn, the reminder is never sent.
    /// </remarks>
    [Fact]
    public async Task ATimerSweepStepsOverABrokenTenantAndStillReturnsTheOthers()
    {
        await using var schema = await PostgresTestSchema.CreateWithTenantSchemasAsync(Cancellation);

        var (broken, healthy) = Tenants();

        var parked = await ParkAsync(schema, healthy);
        _ = await ParkAsync(schema, broken);

        await DropSchemaAsync(schema, broken);

        var listed = await schema.TimerSweep.ListDueAsync(DueQuery(), Cancellation);

        listed.IsSuccess.ShouldBeTrue(
            $"a tenant that cannot be swept must not keep every other tenant asleep.\n{Failure(listed)}");

        listed.Value.Select(candidate => candidate.InstanceId).ShouldContain(parked);
    }

    /// <summary>
    /// The tenant that was stepped over is named on the log pillar, with the sweep that
    /// stepped over it and a code to alert on.
    /// </summary>
    /// <remarks>
    /// <strong>This is the half that stops the fix being a second defect.</strong> Stepping
    /// silently over a broken tenant for ever is worse than failing loudly, and nothing above
    /// this class would say so: <c>FlowRecoveryService</c> discards the scan's report and
    /// swallows what the sweep throws, so the only place this can be reported from is here.
    /// </remarks>
    [Fact]
    public async Task TheTenantThatWasSteppedOverIsReportedWithTheSweepAndTheCode()
    {
        await using var schema = await PostgresTestSchema.CreateWithTenantSchemasAsync(Cancellation);

        var (broken, healthy) = Tenants();

        _ = await AbandonAsync(schema, healthy);
        _ = await AbandonAsync(schema, broken);

        await DropSchemaAsync(schema, broken);

        var refusals = new List<FlowLogRecord>();

        using (Subscribe(broken, refusals))
        {
            _ = await schema.RecoveryScan.ListAbandonedAsync(AbandonedQuery(), Cancellation);
        }

        var refusal = refusals.ShouldHaveSingleItem();

        refusal.Operation.ShouldBe(
            "ListAbandonedAsync",
            "an operator has to be able to tell a recovery scan that lost a tenant from a " +
            "timer sweep that lost one: they strand different work.");

        refusal.ErrorCode.ShouldBe("postgres.tenant_sweep_unavailable");
        refusal.ErrorCategory.ShouldBe(nameof(ErrorCategory.Unavailable));
        refusal.Level.ShouldBe(FlowLogLevel.Warning);
    }

    /// <summary>
    /// A tenant that fails every pass is asked once per pass, takes none of the page from the
    /// tenants that answer, and is reported every time rather than once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Three passes, because "for ever" is the interesting case.</strong> A tenant that
    /// is broken until somebody fixes it must not accumulate anything: not retries inside a
    /// pass, not page budget taken from the tenants that can answer, and not silence. The
    /// retry is the next sweep on the caller's own interval — <c>FlowRecoveryService</c> waits
    /// ten seconds ±25 % whatever the sweep returned — so there is nothing here to back off.
    /// </para>
    /// <para>
    /// <strong>An event per pass rather than an event per breakage.</strong> A record that
    /// arrived once when the schema went and never again would leave an operator who started
    /// watching afterwards with no signal at all, and a broken tenant that has been silently
    /// stepped over for a week reads the same as a tenant that recovered.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ATenantThatFailsEveryPassCostsTheOthersNothingAndIsReportedEveryPass()
    {
        await using var schema = await PostgresTestSchema.CreateWithTenantSchemasAsync(Cancellation);

        var (broken, healthy) = Tenants();

        var first = await AbandonAsync(schema, healthy);
        var second = await AbandonAsync(schema, healthy);
        _ = await AbandonAsync(schema, broken);

        await DropSchemaAsync(schema, broken);

        var refusals = new List<FlowLogRecord>();

        using (Subscribe(broken, refusals))
        {
            for (var pass = 1; pass <= 3; pass++)
            {
                var listed = await schema.RecoveryScan.ListAbandonedAsync(
                    AbandonedQuery(), Cancellation);

                listed.IsSuccess.ShouldBeTrue($"pass {pass}.\n{Failure(listed)}");

                listed.Value.Select(candidate => candidate.InstanceId).ShouldBe(
                    [first, second],
                    ignoreOrder: true,
                    $"the healthy tenant's page is the page it would have had with no broken " +
                    $"tenant present, on pass {pass} as on the first.");

                refusals.Count.ShouldBe(
                    pass,
                    "the broken tenant is asked exactly once per pass — never retried inside " +
                    "one, and never given up on.");
            }
        }
    }

    /// <summary>
    /// A sweep in which no tenant answered fails, rather than reporting an empty backlog.
    /// </summary>
    /// <remarks>
    /// The failure this exists to avoid is the one schema isolation introduced in the first
    /// place: an empty list means "there is no abandoned work", and answering it when nothing
    /// could be looked at reports a healthy deployment at the moment none of it can be swept.
    /// </remarks>
    [Fact]
    public async Task ASweepThatReachedNoTenantAtAllFailsRatherThanReportingAnEmptyBacklog()
    {
        await using var schema = await PostgresTestSchema.CreateWithTenantSchemasAsync(Cancellation);

        var (broken, alsoBroken) = Tenants();

        _ = await AbandonAsync(schema, broken);
        _ = await AbandonAsync(schema, alsoBroken);

        await DropSchemaAsync(schema, broken);
        await DropSchemaAsync(schema, alsoBroken);

        var listed = await schema.RecoveryScan.ListAbandonedAsync(AbandonedQuery(), Cancellation);

        listed.IsFailure.ShouldBeTrue(
            "no schema was reachable, so the sweep looked at nothing. An empty page here is a " +
            "claim that there is nothing to recover.");

        listed.Error.Code.ShouldBe("postgres.tenant_sweep_unavailable");
        listed.Error.Category.ShouldBe(ErrorCategory.Unavailable);
    }

    /// <summary>
    /// A sweep scoped to one tenant still fails when that tenant is the broken one.
    /// </summary>
    /// <remarks>
    /// The scoped sweep is not a fan-out — there is nobody to carry on to — so its answer must
    /// be the one it always gave. Without this, "step over the failure" would quietly turn a
    /// caller that asked about one tenant into a caller told that tenant has nothing due.
    /// </remarks>
    [Fact]
    public async Task ASweepScopedToTheBrokenTenantStillFails()
    {
        await using var schema = await PostgresTestSchema.CreateWithTenantSchemasAsync(Cancellation);

        var (broken, healthy) = Tenants();

        _ = await AbandonAsync(schema, healthy);
        _ = await AbandonAsync(schema, broken);

        await DropSchemaAsync(schema, broken);

        var listed = await schema.RecoveryScan.ListAbandonedAsync(
            AbandonedQuery() with { TenantId = broken }, Cancellation);

        listed.IsFailure.ShouldBeTrue(
            "the caller named a tenant, and that tenant's schema is gone. Answering with an " +
            "empty page would report it as having nothing to recover.");
    }

    // -----------------------------------------------------------------------------------
    // Fixtures
    // -----------------------------------------------------------------------------------

    /// <summary>Two tenant ids no other test in this run is using.</summary>
    private static (string Broken, string Healthy) Tenants()
    {
        var identity = Guid.NewGuid().ToString("n")[..8];

        return ("broken-" + identity, "healthy-" + identity);
    }

    /// <summary>
    /// Breaks one tenant for real: its schema goes, its registry row and its pool stay.
    /// </summary>
    /// <remarks>
    /// This is what a mis-run migration, a schema dropped by hand and a revoked grant all look
    /// like from inside the sweep — a query that raises against a tenant the registry still
    /// lists and whose data source is already built, so nothing re-provisions it back into
    /// existence on the way past.
    /// </remarks>
    private static async Task DropSchemaAsync(PostgresTestSchema schema, string tenantId) =>
        await schema.ExecuteAsync(
            $"DROP SCHEMA \"{schema.TenantStores!.SchemaFor(tenantId)}\" CASCADE",
            tenantId: null,
            Cancellation);

    /// <summary>Collects the refusals one tenant's schema produced, and nobody else's.</summary>
    private static IDisposable Subscribe(string tenantId, List<FlowLogRecord> refusals) =>
        FlowXLog.Subscribe((name, record) =>
        {
            if (string.Equals(name, FlowXLog.JournalRefused, StringComparison.Ordinal)
                && string.Equals(record.TenantId, tenantId, StringComparison.Ordinal))
            {
                lock (refusals)
                {
                    refusals.Add(record);
                }
            }
        });

    private static AbandonedInstanceQuery AbandonedQuery() => new()
    {
        IdleBefore = DateTimeOffset.UtcNow.AddMinutes(-1),
        Limit = 64,
    };

    private static DueInstanceQuery DueQuery() => new()
    {
        DueBefore = DateTimeOffset.UtcNow,
        Limit = 64,
    };

    private static string Failure<T>(Result<T> result) =>
        result.IsFailure ? result.Error.ToString() : string.Empty;

    /// <summary>Leaves one tenant an abandoned instance, in that tenant's own schema.</summary>
    private static Task<Guid> AbandonAsync(PostgresTestSchema schema, string tenantId) =>
        schema.AbandonAsync(
            FlowInstanceState.Running, TimeSpan.FromHours(1), tenantId, Cancellation).AsTask();

    /// <summary>Parks one tenant's instance at a wait that is already due, in its own schema.</summary>
    private static async Task<Guid> ParkAsync(PostgresTestSchema schema, string tenantId)
    {
        var instance = Guid.CreateVersion7();
        var journal = schema.Writer(tenantId);

        var started = await journal.StartAsync(
            new FlowInstanceStart
            {
                InstanceId = instance,
                FlowId = "offer.accept",
                FlowVersion = "1.0.0",
                TenantId = tenantId,
                Token = new FencingToken(1),
            },
            Cancellation);

        started.IsSuccess.ShouldBeTrue(Failure(started));

        var parked = await journal.CompleteAsync(
            instance,
            new FencingToken(1),
            FlowInstanceState.Suspended,
            JournalPayload.Empty,
            new FlowWake(StepScope.Root, 1, DateTimeOffset.UtcNow.AddSeconds(-30)),
            Cancellation);

        parked.IsSuccess.ShouldBeTrue(Failure(parked));

        return instance;
    }
}
