using System.Globalization;
using System.Text.Json.Nodes;
using Npgsql;

namespace FlowX.Chaos;

/// <summary>
/// What one arm of a run turned out to be, read back out of the database that outlived every
/// process in it.
/// </summary>
/// <param name="Position">Which half of the kill window this arm killed in.</param>
/// <param name="FlowsRequested">How many instances the arm registered before it started.</param>
/// <param name="FlowsClaimed">How many a worker claimed.</param>
/// <param name="FlowsOpened">How many reached a <c>flow_instance</c> row.</param>
/// <param name="FlowsCompleted">How many ended in <c>Completed</c>.</param>
/// <param name="FlowsTerminalOther">How many ended in some other terminal state.</param>
/// <param name="LostInstances">
/// Instances with a journal row that never reached a terminal state. QR2's figure is zero.
/// </param>
/// <param name="ClaimedButNeverOpened">
/// Instances a worker claimed and was killed before it could open — no journal row, and so
/// nothing for a recovery scan to find. Distinct from a lost instance because no durable
/// execution ever existed; reported because the rig asked for it and it did not run.
/// </param>
/// <param name="ProcessKills">How many worker processes were SIGKILLed.</param>
/// <param name="KillExitCodes">What the operating system reported those processes exited with.</param>
/// <param name="EffectApplications">Rows in the side-effect ledger.</param>
/// <param name="DuplicateApplications">
/// How many ledger rows are a second or later application of the same step of the same
/// instance.
/// </param>
/// <param name="DuplicatesAgainstGuarantee">
/// Of those, how many were applied by a node that had already been handed a committed row for
/// that step. This is the number QR2 requires to be zero, and the only one that is a defect.
/// </param>
/// <param name="DuplicatesInDocumentedWindow">
/// Of those, how many were applied where no commit for that step existed — the window
/// ADR-0006 and docs/11 §4 say outright is not closed.
/// </param>
/// <param name="DuplicatesBetweenLiveWorkers">
/// Duplicates where both applications came from a first run rather than a recovery — two live
/// nodes executing one instance, which the lease is supposed to make impossible.
/// </param>
/// <param name="OrphanEffects">Ledger rows for an instance with no journal row at all.</param>
/// <param name="InstancesResumedByMoreThanOneNode">
/// Instances more than one recovery node dispatched a step for.
/// </param>
/// <param name="RecoveredInstances">Instances a recovery node carried to a terminal state.</param>
/// <param name="ResumeSeconds">Kill-to-terminal, in seconds, one entry per recovered instance.</param>
/// <param name="TakeoverSeconds">
/// Kill-to-first-step-on-the-new-node, in seconds. The half of the resume latency that is
/// detection — a lease TTL of staleness plus a sweep interval — with the queueing behind a
/// bounded <c>MaxConcurrentRecoveries</c> excluded. Reported beside the resume figure because
/// the difference between them is a capacity setting rather than a property of the runtime.
/// </param>
internal sealed record ArmResult(
    KillPosition Position,
    long FlowsRequested,
    long FlowsClaimed,
    long FlowsOpened,
    long FlowsCompleted,
    long FlowsTerminalOther,
    long LostInstances,
    long ClaimedButNeverOpened,
    long ProcessKills,
    IReadOnlyDictionary<int, int> KillExitCodes,
    long EffectApplications,
    long DuplicateApplications,
    long DuplicatesAgainstGuarantee,
    long DuplicatesInDocumentedWindow,
    long DuplicatesBetweenLiveWorkers,
    long OrphanEffects,
    long InstancesResumedByMoreThanOneNode,
    long RecoveredInstances,
    IReadOnlyList<double> ResumeSeconds,
    IReadOnlyList<double> TakeoverSeconds)
{
    /// <summary>The percentile of the resume distribution, by nearest rank.</summary>
    /// <param name="percentile">Between 0 and 100.</param>
    /// <returns>The value, or null when nothing was recovered.</returns>
    public double? Resume(double percentile) => Percentile(ResumeSeconds, percentile);

    /// <summary>The percentile of the takeover distribution, by nearest rank.</summary>
    /// <param name="percentile">Between 0 and 100.</param>
    /// <returns>The value, or null when nothing was taken over.</returns>
    public double? Takeover(double percentile) => Percentile(TakeoverSeconds, percentile);

    private static double? Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var sorted = values.Order().ToList();
        var rank = (int)Math.Ceiling(percentile / 100 * sorted.Count);

        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }

    /// <summary>Renders the arm as JSON for the checker.</summary>
    /// <returns>The object.</returns>
    public JsonObject ToJson() => new()
    {
        ["killPosition"] = ChaosOptions.Slug(Position),
        ["flowsRequested"] = FlowsRequested,
        ["flowsClaimed"] = FlowsClaimed,
        ["flowsOpened"] = FlowsOpened,
        ["flowsCompleted"] = FlowsCompleted,
        ["flowsTerminalOther"] = FlowsTerminalOther,
        ["lostInstances"] = LostInstances,
        ["claimedButNeverOpened"] = ClaimedButNeverOpened,
        ["processKills"] = ProcessKills,
        ["killExitCodes"] = new JsonObject(
            KillExitCodes.Select(pair => KeyValuePair.Create<string, JsonNode?>(
                pair.Key.ToString(CultureInfo.InvariantCulture), pair.Value))),
        ["effectApplications"] = EffectApplications,
        ["duplicateApplications"] = DuplicateApplications,
        ["duplicatesAgainstGuarantee"] = DuplicatesAgainstGuarantee,
        ["duplicatesInDocumentedWindow"] = DuplicatesInDocumentedWindow,
        ["duplicatesBetweenLiveWorkers"] = DuplicatesBetweenLiveWorkers,
        ["orphanEffects"] = OrphanEffects,
        ["instancesResumedByMoreThanOneNode"] = InstancesResumedByMoreThanOneNode,
        ["recoveredInstances"] = RecoveredInstances,
        ["resumeSecondsP50"] = Resume(50),
        ["resumeSecondsP95"] = Resume(95),
        ["resumeSecondsP99"] = Resume(99),
        ["resumeSecondsMax"] = ResumeSeconds.Count == 0 ? null : ResumeSeconds.Max(),
        ["takeoverSecondsP50"] = Takeover(50),
        ["takeoverSecondsP95"] = Takeover(95),
        ["takeoverSecondsP99"] = Takeover(99),
        ["takeoverSecondsMax"] = TakeoverSeconds.Count == 0 ? null : TakeoverSeconds.Max(),
        ["resumeSeconds"] = new JsonArray([.. ResumeSeconds.Order().Select(v => JsonValue.Create(v))]),
    };
}

/// <summary>
/// The queries that turn an arm's tables into the six numbers QR2 asks for.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The classification of a duplicate is the whole argument of this file.</strong> Two
/// applications of one step are not automatically a defect: <c>ADR-0006</c> and
/// <c>docs/11-Distributed-Runtime.md §4</c> say plainly that a node dying after an effect and
/// before its commit re-executes that step, and a rig that reported those as failures would be
/// reporting a documented design decision as a bug.
/// </para>
/// <para>
/// So each recovery node records the committed prefix it was handed — <c>chaos_resume.frontier</c>,
/// counted from <c>flow_step</c> before it ran anything — and a duplicate is judged against it.
/// An application at a step index <em>below</em> that frontier means a node re-ran a step whose
/// row was already in the journal, which is the guarantee failing. An application at or above
/// it means the earlier effect had no commit, which is the documented window. Neither judgement
/// depends on the kill schedule, so the numbers stay meaningful at any concurrency.
/// </para>
/// </remarks>
internal static class ChaosAnalysis
{
    private const string TerminalStates = "('Completed', 'Failed', 'TimedOut', 'CompensationFailed')";

    /// <summary>Reads one arm's outcome.</summary>
    /// <param name="dataSource">The arm's data source.</param>
    /// <param name="position">Which half of the window this arm killed in.</param>
    /// <param name="exitCodes">The exit codes the coordinator observed from killed workers.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The arm's result.</returns>
    public static async Task<ArmResult> ReadAsync(
        NpgsqlDataSource dataSource,
        KillPosition position,
        IReadOnlyDictionary<int, int> exitCodes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        var requested = await Count(dataSource, "SELECT count(*) FROM chaos_instance", cancellationToken)
            .ConfigureAwait(false);

        var claimed = await Count(
            dataSource,
            "SELECT count(*) FROM chaos_instance WHERE started_at IS NOT NULL",
            cancellationToken).ConfigureAwait(false);

        var opened = await Count(dataSource, "SELECT count(*) FROM flow_instance", cancellationToken)
            .ConfigureAwait(false);

        var completed = await Count(
            dataSource,
            "SELECT count(*) FROM flow_instance WHERE state = 'Completed'",
            cancellationToken).ConfigureAwait(false);

        var terminalOther = await Count(
            dataSource,
            $"SELECT count(*) FROM flow_instance WHERE state IN {TerminalStates} AND state <> 'Completed'",
            cancellationToken).ConfigureAwait(false);

        var lost = await Count(
            dataSource,
            $"SELECT count(*) FROM flow_instance WHERE state NOT IN {TerminalStates}",
            cancellationToken).ConfigureAwait(false);

        var neverOpened = await Count(
            dataSource,
            """
            SELECT count(*) FROM chaos_instance c
             WHERE c.started_at IS NOT NULL
               AND NOT EXISTS (SELECT 1 FROM flow_instance i WHERE i.instance_id = c.instance_id)
            """,
            cancellationToken).ConfigureAwait(false);

        var kills = await Count(dataSource, "SELECT count(*) FROM chaos_kill", cancellationToken)
            .ConfigureAwait(false);

        var applications = await Count(dataSource, "SELECT count(*) FROM chaos_effect", cancellationToken)
            .ConfigureAwait(false);

        var duplicates = await Count(
            dataSource,
            """
            SELECT coalesce(sum(applications - 1), 0) FROM (
                SELECT count(*) AS applications
                  FROM chaos_effect
                 GROUP BY instance_id, step_index
                HAVING count(*) > 1) AS repeated
            """,
            cancellationToken).ConfigureAwait(false);

        var againstGuarantee = await Count(
            dataSource,
            """
            SELECT count(*)
              FROM chaos_effect e
              JOIN LATERAL (
                    SELECT r.frontier
                      FROM chaos_resume r
                     WHERE r.instance_id = e.instance_id
                       AND r.resumed_at <= e.applied_at
                     ORDER BY r.resumed_at DESC
                     LIMIT 1) AS taken ON true
             WHERE e.phase = 'resumed'
               AND e.step_index < taken.frontier
            """,
            cancellationToken).ConfigureAwait(false);

        var betweenLiveWorkers = await Count(
            dataSource,
            """
            SELECT coalesce(sum(applications - 1), 0) FROM (
                SELECT count(*) AS applications
                  FROM chaos_effect
                 WHERE phase = 'initial'
                 GROUP BY instance_id, step_index
                HAVING count(*) > 1) AS repeated
            """,
            cancellationToken).ConfigureAwait(false);

        var orphans = await Count(
            dataSource,
            """
            SELECT count(*) FROM chaos_effect e
             WHERE NOT EXISTS (SELECT 1 FROM flow_instance i WHERE i.instance_id = e.instance_id)
            """,
            cancellationToken).ConfigureAwait(false);

        var multiplyResumed = await Count(
            dataSource,
            """
            SELECT count(*) FROM (
                SELECT instance_id FROM chaos_resume
                 GROUP BY instance_id
                HAVING count(DISTINCT pid) > 1) AS contended
            """,
            cancellationToken).ConfigureAwait(false);

        var latencies = await ResumeSecondsAsync(dataSource, cancellationToken).ConfigureAwait(false);
        var takeovers = await TakeoverSecondsAsync(dataSource, cancellationToken).ConfigureAwait(false);

        return new ArmResult(
            position,
            requested,
            claimed,
            opened,
            completed,
            terminalOther,
            lost,
            neverOpened,
            kills,
            exitCodes,
            applications,
            duplicates,
            againstGuarantee,
            duplicates - againstGuarantee,
            betweenLiveWorkers,
            orphans,
            multiplyResumed,
            latencies.Count,
            latencies,
            takeovers);
    }

    /// <summary>How many instances are still short of a terminal state.</summary>
    /// <param name="dataSource">The arm's data source.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The count.</returns>
    public static Task<long> UnfinishedAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken) =>
        Count(
            dataSource,
            $"SELECT count(*) FROM flow_instance WHERE state NOT IN {TerminalStates}",
            cancellationToken);

    /// <summary>How many registered instances no worker has claimed yet.</summary>
    /// <param name="dataSource">The arm's data source.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The count.</returns>
    public static Task<long> UnclaimedAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken) =>
        Count(
            dataSource,
            "SELECT count(*) FROM chaos_instance WHERE started_at IS NULL",
            cancellationToken);

    /// <summary>
    /// Kill-to-terminal for every instance a killed worker was holding, in seconds.
    /// </summary>
    /// <remarks>
    /// The population is defined by the worker rather than by the recovery node: an instance
    /// whose owner was killed and which that owner never marked finished is one this fleet
    /// had to recover, including the ones whose last step had already committed and which
    /// therefore reach a terminal state without running a single step again. Measuring only
    /// what a recovery dispatcher saw would quietly drop the fastest cases.
    /// </remarks>
    private static async Task<IReadOnlyList<double>> ResumeSecondsAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"""
             SELECT extract(epoch FROM (i.updated_at - k.killed_at))
               FROM chaos_instance c
               JOIN chaos_kill k ON k.node = c.worker_node
               JOIN flow_instance i ON i.instance_id = c.instance_id
              WHERE c.finished_at IS NULL
                AND i.state IN {TerminalStates}
             """);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var values = new List<double>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add((double)reader.GetDecimal(0));
        }

        return values;
    }

    /// <summary>Kill-to-takeover for every instance a recovery node ran a step for.</summary>
    private static async Task<IReadOnlyList<double>> TakeoverSecondsAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT extract(epoch FROM (min(r.resumed_at) - k.killed_at))
              FROM chaos_resume r
              JOIN chaos_instance c ON c.instance_id = r.instance_id
              JOIN chaos_kill k ON k.node = c.worker_node
             GROUP BY r.instance_id, k.killed_at
            """);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var values = new List<double>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add((double)reader.GetDecimal(0));
        }

        return values;
    }

    private static Task<long> Count(
        NpgsqlDataSource dataSource,
        string sql,
        CancellationToken cancellationToken) =>
        ChaosDatabase.CountAsync(dataSource, sql, cancellationToken);
}
