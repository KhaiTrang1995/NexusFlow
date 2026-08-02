using System.Globalization;
using System.Security.Claims;
using FlowX.Hosting;
using FlowX.Runtime;
using Npgsql;
using Shouldly;
using Xunit;

namespace FlowX.Postgres.Tests;

/// <summary>
/// <c>docs/16 §4</c>'s sixth fairness mechanism, against a real database and two real limiter
/// clients: one tenant writing hard does not consume another tenant's journal throughput, and
/// the budget it is writing against is the fleet's rather than its node's.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two limiter clients, because the claim is about two nodes.</strong> A write budget
/// each process kept for itself would admit n × the declared rate, which is the anti-conservative
/// limiter
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0040-a-rate-limit-is-shared-or-it-is-not-a-rate-limit.md">ADR-0040</a>
/// refuses — and for a <em>write</em> budget it would mean the shared store the mechanism names
/// is not protected at all. The two hosts here hold independently constructed
/// <see cref="PostgresRateLimiterStore"/> instances over their own connection pools, which is
/// the same relationship two processes have and the only one a test process can create. It is
/// <c>RateLimiterConformance</c>'s arrangement, applied one layer up to the thing that consumes
/// the limiter.
/// </para>
/// <para>
/// <strong>The refusal is a synchronisation point rather than a timing guess.</strong> The
/// second node is not asserted to be slow; it is asserted to have been <em>told no by the bucket
/// the first node emptied</em>, and the quiet tenant's flow is then run while it is still
/// waiting. A wall-clock comparison would have been a flake wearing an assertion.
/// </para>
/// </remarks>
public sealed class TenantWriteBudgetTests
{
    private const string Noisy = "acme";
    private const string Quiet = "globex";

    /// <summary>Start, one step commit, complete — the rows a one-step durable flow writes.</summary>
    private const int RowsPerFlow = 3;

    private static readonly CapabilityDescriptor Validate =
        CapabilityDescriptor.Create("order.validate", "1.0.0", isIdempotent: true);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>
    /// A tenant that has spent its journal write budget on one node is out of budget on every
    /// node — and the tenant next to it commits anyway.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The budget is one flow's worth of rows per window and the window outlasts the test, so
    /// there is nothing to refill and nothing to wait for: the noisy tenant is stuck for as long
    /// as the assertions take. That is the only state in which "the quiet tenant still commits"
    /// means anything.
    /// </para>
    /// <para>
    /// <strong>The row counts are the half that cannot be faked.</strong> A mechanism that
    /// refused the noisy tenant's flow rather than pacing it, or that let it through and merely
    /// reported a refusal, would show here as a different number of rows in <c>flow_instance</c>
    /// and <c>flow_step</c> — read out of the database rather than out of the runtime's own
    /// accounting.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ANoisyTenantsSpentWriteBudgetIsSpentOnEveryNodeAndOnNoOtherTenant()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        // A second client's pool, sharing nothing with the first but the server.
        await using var second = ServiceCollectionExtensions.BuildDataSource(
            PostgresTestDatabase.ConnectionString!, schema.Options);

        var durability = new FlowDurability(schema.Journal, schema.Leases);

        var nodeOne = NewHost(durability, "node-1", new PostgresRateLimiterStore(schema.DataSource));

        var watched = new WatchedLimiter(new PostgresRateLimiterStore(second));
        var nodeTwo = NewHost(durability, "node-2", watched);

        var first = await nodeOne.RunAsync(
            Plan(), new PassingDispatcher(), AsTenant(Noisy), Cancellation);

        first.IsSuccess.ShouldBeTrue(
            $"the first flow is inside the budget.\n{first.Error?.ToString() ?? string.Empty}");

        using var stuck = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);

        var blocked = nodeTwo
            .RunAsync(Plan(), new PassingDispatcher(), AsTenant(Noisy), stuck.Token)
            .AsTask();

        try
        {
            await watched.Refused.Task.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);

            var quiet = await nodeTwo.RunAsync(
                Plan(), new PassingDispatcher(), AsTenant(Quiet), Cancellation);

            quiet.IsSuccess.ShouldBeTrue(
                "the quiet tenant has spent none of its own budget, and the noisy tenant's " +
                "exhaustion is not its to pay for.\n" +
                (quiet.Error?.ToString() ?? string.Empty));

            blocked.IsCompleted.ShouldBeFalse(
                "and the noisy tenant is still waiting on a bucket the other node emptied, so " +
                "this was measured under load rather than after it drained");

            (await InstancesAsync(schema, Noisy)).ShouldBe(
                1,
                "the noisy tenant opened one instance and no more. A budget each node kept for " +
                "itself would have let the second node open a second one.");

            (await StepsAsync(schema, Noisy)).ShouldBe(1);

            (await InstancesAsync(schema, Quiet)).ShouldBe(
                1,
                "and the quiet tenant's rows are in the database, not merely reported as " +
                "written");

            (await StepsAsync(schema, Quiet)).ShouldBe(1);
        }
        finally
        {
            await stuck.CancelAsync();

            try
            {
                await blocked;
            }
            catch (OperationCanceledException)
            {
                // The paced flow was released by the cancellation this test supplied, which is
                // the only thing that bounds the wait. Nothing to assert about it.
            }
        }
    }

    // -----------------------------------------------------------------------------------
    // Fixtures
    // -----------------------------------------------------------------------------------

    private static FlowHost NewHost(
        FlowDurability durability,
        string nodeName,
        IRateLimiterStore limiter)
    {
        var options = new FlowXOptions
        {
            ApplicationName = "Sample.App",
            NodeName = nodeName,
            ShutdownDrainTimeout = TimeSpan.FromSeconds(5),
            TenantIsolation = TenantIsolation.Row,
        };

        // One flow's worth of rows per tenant per window, drawn in one block, over a window that
        // outlasts the test. So the first flow spends the whole budget and nothing refills.
        options.Fairness.JournalWritesPerWindow = RowsPerFlow;
        options.Fairness.JournalWriteBlock = RowsPerFlow;
        options.Fairness.JournalWriteWindow = TimeSpan.FromMinutes(30);

        return new FlowHost(
            new FlowEngine(SystemClock.Instance), options, durability, tenants: null, limiter);
    }

    private static ExecutionPlan Plan() => ExecutionPlan.Create(
        FlowDescriptor.Create(
            "order.place", "1.0.0", ExecutionProfile.Durable, TimeSpan.FromMinutes(5)),
        StepGraph.Create([StepNode.ForCapability(0, Validate)]));

    private static FlowInvocation AsTenant(string tenantId) =>
        new(
            "corr",
            Guid.NewGuid().ToString(),
            Principal: new ClaimsPrincipal(
                new ClaimsIdentity([new Claim("tid", tenantId)], "test")));

    private static Task<int> InstancesAsync(PostgresTestSchema schema, string tenantId) =>
        CountAsync(schema, $"FROM flow_instance WHERE tenant_id = '{tenantId}'");

    /// <summary>
    /// A step row carries no tenant of its own, so it is counted through the instance that
    /// owns it.
    /// </summary>
    private static Task<int> StepsAsync(PostgresTestSchema schema, string tenantId) =>
        CountAsync(
            schema,
            "FROM flow_step s JOIN flow_instance i ON i.instance_id = s.instance_id " +
            $"WHERE i.tenant_id = '{tenantId}'");

    private static async Task<int> CountAsync(PostgresTestSchema schema, string from)
    {
        var scalar = await schema.ScalarAsync($"SELECT count(*) {from}", Cancellation);

        return Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The real limiter, with a signal for the moment the shared bucket says no.
    /// </summary>
    /// <remarks>
    /// Every decision is still PostgreSQL's — this adds no arithmetic and no state the store does
    /// not have. It exists so the test can wait for the refusal instead of guessing how long one
    /// takes, which is the difference between an assertion and a flake.
    /// </remarks>
    private sealed class WatchedLimiter(IRateLimiterStore inner) : IRateLimiterStore
    {
        /// <summary>Completes the first time the shared bucket refuses a draw.</summary>
        public TaskCompletionSource Refused { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<Result<RateLimitVerdict>> TryAcquireAsync(
            string key, int permits, TimeSpan window, CancellationToken cancellationToken)
        {
            var verdict = await inner
                .TryAcquireAsync(key, permits, window, cancellationToken)
                .ConfigureAwait(false);

            if (verdict.IsSuccess && !verdict.Value.Admitted)
            {
                Refused.TrySetResult();
            }

            return verdict;
        }
    }

    private sealed class PassingDispatcher : IStepDispatcher
    {
        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct) =>
            ValueTask.FromResult(StepOutcome.Success);

        public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct) =>
            ValueTask.FromResult(StepOutcome.Success);

        public bool Evaluate(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException("This plan has no branch step.");

        public int Select(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException("This plan has no switch step.");

        public IterationSource BeginIteration(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException("This plan has no iteration.");

        public FlowContext EnterIteration(
            int stepIndex, in IterationSource source, int iteration, FlowContext ctx) =>
            throw new NotSupportedException("This plan has no iteration.");
    }
}
