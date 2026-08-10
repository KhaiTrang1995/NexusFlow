using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlowX.Durability.Bench;
using FlowX.Postgres;

// B7 and B8 — the two durability budgets docs/14-Performance.md declares and, until this
// project existed, nothing measured. See docs/benchmarks/B7-B8-durability.md for the recorded
// run and scripts/check-durability-latency.py for the verdict.
//
// Gated on FLOWX_POSTGRES_CONNECTION the way tests/FlowX.Postgres.Tests and tests/FlowX.Chaos
// are: skip with a reason when nobody asked, fail rather than skip when somebody asked and
// the database is not there.
var gate = LatencyDatabase.Gate();

if (gate is { } refusal)
{
    await (refusal.Skipped ? Console.Out : Console.Error)
        .WriteLineAsync(refusal.Reason)
        .ConfigureAwait(false);

    return refusal.Skipped ? 0 : 1;
}

BenchOptions options;

try
{
    options = BenchOptions.Parse(args, LatencyDatabase.ConnectionString!);
}
catch (ArgumentException failure)
{
    await Console.Error.WriteLineAsync(failure.Message).ConfigureAwait(false);

    return 2;
}

using var lifetime = new CancellationTokenSource();

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    lifetime.Cancel();
};

var cancellationToken = lifetime.Token;

try
{
    if (await LatencyDatabase
            .TooManyWritersAsync(options.ConnectionString, options.Writers, cancellationToken)
            .ConfigureAwait(false) is { } crowded)
    {
        await Console.Error.WriteLineAsync(crowded).ConfigureAwait(false);

        return 2;
    }

    var journalOptions = new PostgresJournalOptions { Schema = options.Schema };

    await using var dataSource = ServiceCollectionExtensions.BuildDataSource(
        options.ConnectionString, journalOptions);

    Console.WriteLine($"Schema {options.Schema}: migrating.");

    _ = await new PostgresMigrator(dataSource, journalOptions)
        .MigrateAsync(cancellationToken)
        .ConfigureAwait(false);

    var journal = new PostgresFlowJournal(dataSource);

    var wall = Stopwatch.StartNew();

    Console.WriteLine();
    Console.WriteLine("==> B7: durable step commit");

    var commit = await CommitArm.RunAsync(journal, options, cancellationToken).ConfigureAwait(false);

    Console.WriteLine();
    Console.WriteLine("==> B8: flow instance rehydration");

    var resume = await ResumeArm.RunAsync(journal, options, cancellationToken).ConfigureAwait(false);

    wall.Stop();

    var report = new JsonObject
    {
        ["budgets"] = new JsonArray(commit.ToJson(), resume.ToJson()),
        ["machine"] = new JsonObject
        {
            ["processorCount"] = Environment.ProcessorCount,
            ["postgres"] = await LatencyDatabase
                .ServerVersionAsync(options.ConnectionString, cancellationToken).ConfigureAwait(false),

            // The one server setting that decides whether B7 was measured at all: with
            // synchronous_commit off, PostgreSQL acknowledges before the write is on disk and
            // the number describes a commit that is not durable.
            ["synchronousCommit"] = await LatencyDatabase
                .SynchronousCommitAsync(options.ConnectionString, cancellationToken).ConfigureAwait(false),
            ["wallClockSeconds"] = Math.Round(wall.Elapsed.TotalSeconds, 1, MidpointRounding.AwayFromZero),
        },
    };

    var directory = Path.GetDirectoryName(Path.GetFullPath(options.JsonPath));

    if (!string.IsNullOrEmpty(directory))
    {
        _ = Directory.CreateDirectory(directory);
    }

    await File.WriteAllTextAsync(
            options.JsonPath,
            report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken)
        .ConfigureAwait(false);

    Console.WriteLine();
    Console.WriteLine(commit.Service);
    Console.WriteLine(commit.Response);
    Console.WriteLine(
        string.Create(
            CultureInfo.InvariantCulture,
            $"    offered {commit.OfferedRate}/s, achieved {commit.AchievedRate:F1}/s, " +
            $"{commit.Refusals.Count} refusals"));
    Console.WriteLine(resume.Total);
    Console.WriteLine(
        string.Create(
            CultureInfo.InvariantCulture,
            $"    {resume.StepsRead} step rows read, {resume.Failures} failures"));
    Console.WriteLine();
    Console.WriteLine($"Results written to {options.JsonPath}.");

    if (!options.KeepSchema)
    {
        await LatencyDatabase
            .DropSchemaAsync(options.ConnectionString, options.Schema, cancellationToken)
            .ConfigureAwait(false);
    }

    return 0;
}
catch (OperationCanceledException)
{
    await Console.Error.WriteLineAsync("Cancelled; nothing was measured.").ConfigureAwait(false);

    return 130;
}
catch (Exception failure)
{
    await Console.Error.WriteLineAsync($"The rig failed: {failure}").ConfigureAwait(false);

    return 3;
}
