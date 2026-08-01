using FlowX.Runtime;
using Npgsql;
using NpgsqlTypes;

namespace FlowX.Chaos;

/// <summary>
/// The flow under test, and the dispatcher whose steps apply an effect a database can count.
/// </summary>
/// <remarks>
/// <para>
/// Three steps, all declared <c>isIdempotent: false</c>, because that is what they are: each
/// one inserts a row into <c>chaos_effect</c> with no key that could collapse a second
/// insertion into the first. A charge, a shipment and a notification behave this way; the rig
/// makes the behaviour visible instead of assuming it.
/// </para>
/// <para>
/// <strong>The kill is taken inside this dispatcher, and that is the only place it can be
/// taken.</strong> <c>FlowEngine</c> commits a step's row immediately after
/// <c>IStepDispatcher.ExecuteAsync</c> returns, so the window between an effect and its commit
/// is the return path of this method. A kill anywhere else is a kill at a moment the engine
/// has already made safe.
/// </para>
/// </remarks>
internal sealed class LedgerDispatcher : IStepDispatcher
{
    private const string ApplySql =
        """
        INSERT INTO chaos_effect (instance_id, step_index, phase, node, pid)
        VALUES (@instance, @step, @phase, @node, @pid)
        """;

    private const string FrontierSql =
        "SELECT count(DISTINCT step_id) FROM flow_step WHERE instance_id = @instance AND scope = ''";

    private const string ResumeSql =
        """
        INSERT INTO chaos_resume (instance_id, node, pid, frontier, first_step)
        VALUES (@instance, @node, @pid, @frontier, @step)
        """;

    private const string KillSql =
        """
        INSERT INTO chaos_kill (node, pid, instance_id, step_index, position)
        VALUES (@node, @pid, @instance, @step, @position)
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly ChaosOptions _options;
    private readonly bool _resuming;
    private readonly HashSet<Guid> _seen = [];
    private readonly Lock _gate = new();

    private int _arrivals;

    /// <summary>Builds a dispatcher over one process's data source.</summary>
    /// <param name="dataSource">Where the ledger lives.</param>
    /// <param name="options">The run's parameters, including the kill schedule.</param>
    /// <param name="resuming">
    /// Whether this process is a recovery node. A resuming dispatcher records the frontier it
    /// was handed, which is what turns "this step ran twice" into "this step ran twice after
    /// its commit was already in the journal" — the difference between a documented window
    /// and a broken guarantee.
    /// </param>
    public LedgerDispatcher(NpgsqlDataSource dataSource, ChaosOptions options, bool resuming)
    {
        _dataSource = dataSource;
        _options = options;
        _resuming = resuming;
    }

    /// <summary>The flow the rig runs: three non-idempotent steps, declared <c>Durable</c>.</summary>
    /// <returns>The plan.</returns>
    public static ExecutionPlan Plan() => ExecutionPlan.Create(
        FlowDescriptor.Create(
            "chaos.order", "1.0.0", ExecutionProfile.Durable, TimeSpan.FromMinutes(30)),
        StepGraph.Create([
            StepNode.ForCapability(0, Capability("chaos.charge")),
            StepNode.ForCapability(1, Capability("chaos.ship")),
            StepNode.ForCapability(2, Capability("chaos.notify")),
        ]));

    /// <summary>How many steps the plan has.</summary>
    public static int StepCount => 3;

    /// <inheritdoc />
    public async ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var instance = Guid.Parse(ctx.FlowInstanceId!);

        if (_resuming)
        {
            await RecordResumeAsync(instance, stepIndex, ct).ConfigureAwait(false);
        }

        // The two halves of the window, in the order the engine sees them. AfterCommit fires
        // before the effect, so the previous step's row is in the journal and this step's
        // effect has not happened; BeforeCommit fires after it, so the effect has happened and
        // this step's row is not in the journal.
        if (_options.Position == KillPosition.AfterCommit)
        {
            await MaybeKillAsync(instance, stepIndex, ct).ConfigureAwait(false);
        }

        await ApplyAsync(instance, stepIndex, ct).ConfigureAwait(false);

        if (_options.Position == KillPosition.BeforeCommit)
        {
            await MaybeKillAsync(instance, stepIndex, ct).ConfigureAwait(false);
        }

        return StepOutcome.Success;
    }

    /// <inheritdoc />
    public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(StepOutcome.Success);

    /// <inheritdoc />
    public bool Evaluate(int stepIndex, FlowContext ctx) =>
        throw new NotSupportedException("The chaos plan has no branch step.");

    /// <inheritdoc />
    public int Select(int stepIndex, FlowContext ctx) =>
        throw new NotSupportedException("The chaos plan has no switch step.");

    /// <inheritdoc />
    public IterationSource BeginIteration(int stepIndex, FlowContext ctx) =>
        throw new NotSupportedException("The chaos plan has no iteration.");

    /// <inheritdoc />
    public FlowContext EnterIteration(int stepIndex, in IterationSource source, int iteration, FlowContext ctx) =>
        throw new NotSupportedException("The chaos plan has no iteration.");

    private static CapabilityDescriptor Capability(string id) =>
        CapabilityDescriptor.Create(id, "1.0.0", isIdempotent: false, "ledger.write");

    /// <summary>The effect: one row, every time, with nothing that could deduplicate it.</summary>
    private async Task ApplyAsync(Guid instance, int stepIndex, CancellationToken ct)
    {
        await using var command = _dataSource.CreateCommand(ApplySql);

        _ = command.Parameters.AddWithValue("instance", NpgsqlDbType.Uuid, instance);
        _ = command.Parameters.AddWithValue("step", NpgsqlDbType.Integer, stepIndex);
        _ = command.Parameters.AddWithValue("phase", _resuming ? "resumed" : "initial");
        _ = command.Parameters.AddWithValue("node", _options.NodeName);
        _ = command.Parameters.AddWithValue("pid", NpgsqlDbType.Integer, Environment.ProcessId);

        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records, once per instance, the committed prefix this node was handed before it ran
    /// anything.
    /// </summary>
    private async Task RecordResumeAsync(Guid instance, int stepIndex, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_seen.Add(instance))
            {
                return;
            }
        }

        long frontier;

        await using (var query = _dataSource.CreateCommand(FrontierSql))
        {
            _ = query.Parameters.AddWithValue("instance", NpgsqlDbType.Uuid, instance);

            var value = await query.ExecuteScalarAsync(ct).ConfigureAwait(false);
            frontier = value is null or DBNull ? 0 : (long)value;
        }

        await using var command = _dataSource.CreateCommand(ResumeSql);

        _ = command.Parameters.AddWithValue("instance", NpgsqlDbType.Uuid, instance);
        _ = command.Parameters.AddWithValue("node", _options.NodeName);
        _ = command.Parameters.AddWithValue("pid", NpgsqlDbType.Integer, Environment.ProcessId);
        _ = command.Parameters.AddWithValue("frontier", NpgsqlDbType.Integer, (int)frontier);
        _ = command.Parameters.AddWithValue("step", NpgsqlDbType.Integer, stepIndex);

        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Counts arrivals at the scheduled kill point and, on the last one, commits the evidence
    /// and dies.
    /// </summary>
    private async Task MaybeKillAsync(Guid instance, int stepIndex, CancellationToken ct)
    {
        if (_resuming || stepIndex != _options.KillStep)
        {
            return;
        }

        if (Interlocked.Increment(ref _arrivals) != _options.KillEvery)
        {
            return;
        }

        await using (var command = _dataSource.CreateCommand(KillSql))
        {
            _ = command.Parameters.AddWithValue("pid", NpgsqlDbType.Integer, Environment.ProcessId);
            _ = command.Parameters.AddWithValue("node", _options.NodeName);
            _ = command.Parameters.AddWithValue("instance", NpgsqlDbType.Uuid, instance);
            _ = command.Parameters.AddWithValue("step", NpgsqlDbType.Integer, stepIndex);
            _ = command.Parameters.AddWithValue("position", ChaosOptions.Slug(_options.Position));

            _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        ProcessKill.KillSelf();
    }
}
