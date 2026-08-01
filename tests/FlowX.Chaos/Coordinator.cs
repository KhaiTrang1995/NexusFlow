using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlowX.Postgres;
using Npgsql;

namespace FlowX.Chaos;

/// <summary>
/// Sets a run up, spawns every other process in it, waits for the fleet to converge, and
/// renders the verdict.
/// </summary>
/// <remarks>
/// <para>
/// This process never runs a flow and never dies. Its one job that matters is bookkeeping:
/// it registers every instance before any worker exists, it observes the exit code of every
/// worker it spawned — <strong>137 is the evidence that the kill was real</strong> — and it
/// refuses to report a run in which nothing was killed as anything but inconclusive.
/// </para>
/// </remarks>
internal static class Coordinator
{
    /// <summary>Runs every arm and writes the results.</summary>
    /// <param name="options">The run's parameters.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The process exit code: 0 when every arm produced a usable measurement.</returns>
    public static async Task<int> RunAsync(ChaosOptions options, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var arms = new List<ArmResult>();

        foreach (var position in options.Positions)
        {
            arms.Add(await RunArmAsync(
                options with
                {
                    Position = position,
                    Schema = "flowx_chaos_" + Guid.NewGuid().ToString("n", CultureInfo.InvariantCulture),
                },
                cancellationToken).ConfigureAwait(false));
        }

        var report = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["recordedAt"] = started.ToString("O", CultureInfo.InvariantCulture),
            ["elapsedSeconds"] = Math.Round((DateTimeOffset.UtcNow - started).TotalSeconds, 1),
            ["resumeBudgetSeconds"] = 45,
            ["parameters"] = new JsonObject
            {
                ["flows"] = options.Flows,
                ["killEvery"] = options.KillEvery,
                ["killStep"] = options.KillStep,
                ["steps"] = LedgerDispatcher.StepCount,
                ["workers"] = options.Workers,
                ["concurrency"] = options.Concurrency,
                ["recoveryNodes"] = options.RecoveryNodes,
                ["maxConcurrentRecoveries"] = options.MaxConcurrentRecoveries,
                ["leaseTtlSeconds"] = (int)options.LeaseTtl.TotalSeconds,
                ["scanIntervalSeconds"] = (int)options.ScanInterval.TotalSeconds,
            },
            ["machine"] = new JsonObject
            {
                ["processorCount"] = Environment.ProcessorCount,
                ["loadAverage"] = LoadAverage(),
                ["postgres"] = await ServerVersionAsync(options, cancellationToken).ConfigureAwait(false),
            },
            ["arms"] = new JsonArray([.. arms.Select(arm => (JsonNode)arm.ToJson())]),
        };

        var json = report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        await File.WriteAllTextAsync(options.JsonPath, json, cancellationToken).ConfigureAwait(false);

        Print(options, arms);

        Console.WriteLine();
        Console.WriteLine($"Results written to {options.JsonPath}.");

        return 0;
    }

    private static async Task<ArmResult> RunArmAsync(
        ChaosOptions options,
        CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine($"==> Arm: kill {ChaosOptions.Slug(options.Position)}, schema {options.Schema}");

        var journalOptions = new PostgresJournalOptions { Schema = options.Schema };

        await using var dataSource = ServiceCollectionExtensions.BuildDataSource(
            options.ConnectionString, journalOptions);

        _ = await new PostgresMigrator(dataSource, journalOptions)
            .MigrateAsync(cancellationToken)
            .ConfigureAwait(false);

        await ChaosDatabase.CreateAsync(dataSource, cancellationToken).ConfigureAwait(false);
        await ChaosDatabase.RegisterInstancesAsync(dataSource, options.Flows, cancellationToken)
            .ConfigureAwait(false);

        var recovery = StartRecoveryNodes(options);

        Dictionary<int, int> exitCodes;

        try
        {
            exitCodes = await DriveWorkersAsync(dataSource, options, cancellationToken)
                .ConfigureAwait(false);

            await ConvergeAsync(dataSource, options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await StopRecoveryAsync(dataSource, recovery, cancellationToken).ConfigureAwait(false);
        }

        var result = await ChaosAnalysis
            .ReadAsync(dataSource, options.Position, exitCodes, cancellationToken)
            .ConfigureAwait(false);

        if (!options.KeepSchema)
        {
            await ChaosDatabase
                .DropSchemaAsync(options.ConnectionString, options.Schema, cancellationToken)
                .ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Keeps the requested number of worker processes alive until every instance has been
    /// claimed and every worker has stopped, and records how each one ended.
    /// </summary>
    private static async Task<Dictionary<int, int>> DriveWorkersAsync(
        NpgsqlDataSource dataSource,
        ChaosOptions options,
        CancellationToken cancellationToken)
    {
        var exitCodes = new Dictionary<int, int>();
        var running = new List<Process>();
        var spawned = 0;
        var deadline = DateTimeOffset.UtcNow + options.ConvergeTimeout;

        try
        {
            while (DateTimeOffset.UtcNow < deadline)
            {
                var unclaimed = await ChaosAnalysis
                    .UnclaimedAsync(dataSource, cancellationToken)
                    .ConfigureAwait(false);

                while (unclaimed > 0 && running.Count < options.Workers)
                {
                    running.Add(Spawn(options, ChaosRole.Worker, $"worker-{++spawned}"));
                }

                if (running.Count == 0)
                {
                    if (unclaimed == 0)
                    {
                        break;
                    }

                    continue;
                }

                for (var i = running.Count - 1; i >= 0; i--)
                {
                    if (!running[i].HasExited)
                    {
                        continue;
                    }

                    var code = running[i].ExitCode;
                    exitCodes[code] = exitCodes.GetValueOrDefault(code) + 1;

                    running[i].Dispose();
                    running.RemoveAt(i);
                }

                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (var process in running)
            {
                Terminate(process);
                process.Dispose();
            }
        }

        Console.WriteLine(
            $"    workers: {spawned} spawned, exit codes " +
            string.Join(", ", exitCodes.OrderBy(pair => pair.Key)
                .Select(pair => $"{pair.Key}×{pair.Value}")));

        return exitCodes;
    }

    /// <summary>Waits for every opened instance to reach a terminal state.</summary>
    private static async Task ConvergeAsync(
        NpgsqlDataSource dataSource,
        ChaosOptions options,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + options.ConvergeTimeout;
        var reported = -1L;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var unfinished = await ChaosAnalysis
                .UnfinishedAsync(dataSource, cancellationToken)
                .ConfigureAwait(false);

            if (unfinished == 0)
            {
                Console.WriteLine("    every opened instance reached a terminal state.");
                return;
            }

            if (unfinished != reported)
            {
                Console.WriteLine($"    waiting for recovery: {unfinished} instance(s) still running");
                reported = unfinished;
            }

            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        }

        Console.WriteLine("    convergence timed out; the remaining instances are reported as lost.");
    }

    private static List<Process> StartRecoveryNodes(ChaosOptions options)
    {
        var nodes = new List<Process>(options.RecoveryNodes);

        for (var i = 1; i <= options.RecoveryNodes; i++)
        {
            nodes.Add(Spawn(options, ChaosRole.Recovery, $"recovery-{i}"));
        }

        return nodes;
    }

    private static async Task StopRecoveryAsync(
        NpgsqlDataSource dataSource,
        List<Process> recovery,
        CancellationToken cancellationToken)
    {
        _ = await ChaosDatabase
            .ExecuteAsync(dataSource, "UPDATE chaos_control SET stop = true WHERE id = 1", cancellationToken)
            .ConfigureAwait(false);

        foreach (var node in recovery)
        {
            try
            {
                if (!node.WaitForExit(30_000))
                {
                    Terminate(node);
                }
            }
            catch (Exception failure)
            {
                Console.WriteLine($"    a recovery node would not stop: {failure.Message}");
            }
            finally
            {
                node.Dispose();
            }
        }
    }

    private static void Terminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception failure)
        {
            Console.WriteLine($"    could not stop a child process: {failure.Message}");
        }
    }

    private static Process Spawn(ChaosOptions options, ChaosRole role, string node)
    {
        var start = new ProcessStartInfo { UseShellExecute = false };
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("This process has no path to re-launch itself from.");

        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.Ordinal))
        {
            start.FileName = executable;
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "FlowX.Chaos.dll"));
        }
        else
        {
            start.FileName = executable;
        }

        foreach (var argument in options.ChildArguments(role, node))
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start a {role} process.");
    }

    private static JsonArray LoadAverage()
    {
        try
        {
            var parts = File.ReadAllText("/proc/loadavg").Split(' ');

            return new JsonArray([.. parts.Take(3)
                .Select(part => JsonValue.Create(
                    double.Parse(part, CultureInfo.InvariantCulture)))]);
        }
        catch (Exception failure) when (failure is IOException or FormatException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static async Task<string> ServerVersionAsync(
        ChaosOptions options,
        CancellationToken cancellationToken)
    {
        await using var dataSource = NpgsqlDataSource.Create(options.ConnectionString);

        var version = await ChaosDatabase
            .ScalarAsync(dataSource, "SELECT version()", cancellationToken)
            .ConfigureAwait(false);

        return version as string ?? "unknown";
    }

    private static void Print(ChaosOptions options, IReadOnlyList<ArmResult> arms)
    {
        Console.WriteLine();
        Console.WriteLine("==> QR2 chaos rig");
        Console.WriteLine(
            $"    {options.Flows} flows × {LedgerDispatcher.StepCount} non-idempotent steps, " +
            $"kill at step {options.KillStep} every {options.KillEvery} arrivals, " +
            $"{options.Workers} worker process(es) × {options.Concurrency}, " +
            $"{options.RecoveryNodes} recovery node(s), lease TTL {options.LeaseTtl.TotalSeconds:0}s");

        foreach (var arm in arms)
        {
            Console.WriteLine();
            Console.WriteLine($"    --- kill {ChaosOptions.Slug(arm.Position)} ---");
            Console.WriteLine($"    flows requested / claimed / opened : {arm.FlowsRequested} / {arm.FlowsClaimed} / {arm.FlowsOpened}");
            Console.WriteLine($"    completed / other terminal / LOST  : {arm.FlowsCompleted} / {arm.FlowsTerminalOther} / {arm.LostInstances}");
            Console.WriteLine($"    claimed but never opened           : {arm.ClaimedButNeverOpened}");
            Console.WriteLine($"    process kills (exit codes)         : {arm.ProcessKills} ({Codes(arm.KillExitCodes)})");
            Console.WriteLine($"    effect applications                : {arm.EffectApplications}");
            Console.WriteLine($"    duplicate applications             : {arm.DuplicateApplications}");
            Console.WriteLine($"      of which AGAINST THE GUARANTEE   : {arm.DuplicatesAgainstGuarantee}");
            Console.WriteLine($"      of which in the documented window: {arm.DuplicatesInDocumentedWindow}");
            Console.WriteLine($"      of which between live workers    : {arm.DuplicatesBetweenLiveWorkers}");
            Console.WriteLine($"    orphan effects / double-resumed    : {arm.OrphanEffects} / {arm.InstancesResumedByMoreThanOneNode}");
            Console.WriteLine($"    recovered instances                : {arm.RecoveredInstances}");
            Console.WriteLine($"    resume p50 / p95 / p99 / max (s)   : {Seconds(arm.Resume(50))} / {Seconds(arm.Resume(95))} / {Seconds(arm.Resume(99))} / {Seconds(arm.ResumeSeconds.Count == 0 ? null : arm.ResumeSeconds.Max())}");
            Console.WriteLine($"    takeover p50 / p95 / p99 (s)       : {Seconds(arm.Takeover(50))} / {Seconds(arm.Takeover(95))} / {Seconds(arm.Takeover(99))}");
        }
    }

    private static string Codes(IReadOnlyDictionary<int, int> codes) =>
        codes.Count == 0
            ? "none"
            : string.Join(", ", codes.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}×{pair.Value}"));

    private static string Seconds(double? value) =>
        value is null ? "—" : value.Value.ToString("0.0", CultureInfo.InvariantCulture);
}
