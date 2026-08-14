using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlowX.ColdStart.Bench;

// V5 — "cold start ≤ 200 ms, NativeAOT-compatible", the criterion docs/01-Vision.md §7 has
// stated since the vision was written and that nothing had ever timed. See
// docs/benchmarks/V5-cold-start.md for the recorded run.
//
// Opt-in by construction rather than by an environment variable: this is an Exe with no test
// framework in it, so `dotnet test` cannot run it and only an explicit `dotnet run` does. It
// publishes an 18 MB binary and starts thirty processes, which is not something to do inside
// somebody's test loop.
if (!OperatingSystem.IsLinux() || RuntimeInformation.OSArchitecture != Architecture.X64)
{
    await Console.Error
        .WriteLineAsync(
            "This rig publishes linux-x64 and starts the binary it published, so it has to " +
            "run on linux-x64. Refusing rather than reporting a number for a different target.")
        .ConfigureAwait(false);

    return 3;
}

ColdStartOptions options;

try
{
    options = ColdStartOptions.Parse(args);
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
    Repository.SetCurrentDirectory();

    var wall = Stopwatch.StartNew();
    var published = await SamplePublish.RunAsync(options, cancellationToken).ConfigureAwait(false);

    Console.WriteLine(
        string.Create(
            CultureInfo.InvariantCulture,
            $"    {published.BinaryPath}: {published.BinaryBytes / 1024.0 / 1024.0:F1} MiB " +
            $"({published.DirectoryBytes / 1024.0 / 1024.0:F1} MiB including symbols), " +
            $"published in {published.Seconds:F1} s."));

    Console.WriteLine();
    Console.WriteLine($"==> {options.Warmup} discarded starts, then {options.Runs} measured");

    var firstStart = 0.0;

    for (var i = 0; i < options.Warmup; i++)
    {
        var discarded = await ColdStartRun.MeasureAsync(options, cancellationToken).ConfigureAwait(false);

        if (i == 0)
        {
            firstStart = discarded.ResponseMs;
        }

        Console.WriteLine(
            string.Create(CultureInfo.InvariantCulture, $"    warm-up {i + 1}: {discarded.ResponseMs:F1} ms"));
    }

    var listening = new Samples("process.listening", "ms", options.Runs);
    var responding = new Samples("flow.firstResponse", "ms", options.Runs);
    var resident = new Samples("rss.atFirstResponse", "KiB", options.Runs);

    for (var i = 0; i < options.Runs; i++)
    {
        var run = await ColdStartRun.MeasureAsync(options, cancellationToken).ConfigureAwait(false);

        listening.Add(run.ListenMs);
        responding.Add(run.ResponseMs);
        resident.Add(run.RssKb);

        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"    run {i + 1,2}: listening {run.ListenMs,6:F1} ms, " +
                $"first response {run.ResponseMs,6:F1} ms, rss {run.RssKb / 1024.0,6:F1} MiB"));
    }

    listening.Freeze();
    responding.Freeze();
    resident.Freeze();
    wall.Stop();

    // The criterion is a ceiling, so it is judged against the worst rank the run resolves —
    // p99 of 30 samples, which is the maximum and is named as such in the document rather
    // than dressed up as a percentile that 30 runs cannot distinguish from one.
    const double CeilingMs = 200;
    var subject = responding.Percentile(0.99);
    var met = subject <= CeilingMs;

    var verdict = options.Mode switch
    {
        PublishMode.Aot when met => "MET",
        PublishMode.Aot => "NOT MET",

        // Not a verdict on V5 at all: the criterion names NativeAOT, and this arm published
        // something else. Recorded so the fallback cannot be quoted as the criterion.
        _ => "NOT V5 — ReadyToRun, not NativeAOT",
    };

    var report = new JsonObject
    {
        ["criterion"] = "V5",
        ["what"] = "Process start to the first successful response on a served flow endpoint, "
            + "for the NativeAOT-published reference sample.",
        ["subject"] = new JsonObject
        {
            ["sample"] = ColdStartOptions.SampleProject,
            ["endpoint"] = "POST " + ColdStartOptions.FlowPath,
            ["publishMode"] = options.Mode == PublishMode.Aot ? "NativeAOT" : "ReadyToRun",
            ["publishCommand"] = published.Command,
            ["binary"] = published.BinaryPath,
            ["binaryBytes"] = published.BinaryBytes,
            ["publishDirectoryBytes"] = published.DirectoryBytes,
            ["publishSeconds"] = published.Seconds,
        },
        ["ceilingMs"] = CeilingMs,
        ["judgedOn"] = "flow.firstResponse p99",
        ["judgedValueMs"] = Math.Round(subject, 3, MidpointRounding.AwayFromZero),
        ["marginFactor"] = Math.Round(CeilingMs / subject, 2, MidpointRounding.AwayFromZero),
        ["verdict"] = verdict,
        ["phases"] = new JsonArray(listening.ToJson(), responding.ToJson(), resident.ToJson()),
        ["run"] = new JsonObject
        {
            ["measured"] = options.Runs,
            ["discardedWarmups"] = options.Warmup,
            ["firstStartAfterPublishMs"] = Math.Round(firstStart, 1, MidpointRounding.AwayFromZero),
            ["pollIntervalMs"] = options.PollInterval.TotalMilliseconds,
            ["timeoutSeconds"] = options.Timeout.TotalSeconds,
        },
        ["machine"] = new JsonObject
        {
            ["processorCount"] = Environment.ProcessorCount,
            ["os"] = RuntimeInformation.OSDescription,
            ["architecture"] = RuntimeInformation.OSArchitecture.ToString(),
            ["dotnet"] = RuntimeInformation.FrameworkDescription,
            ["shared"] = true,
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
    Console.WriteLine(listening.ToString());
    Console.WriteLine(responding.ToString());
    Console.WriteLine(resident.ToString());
    Console.WriteLine();
    Console.WriteLine(
        string.Create(
            CultureInfo.InvariantCulture,
            $"V5: {subject:F1} ms at p99 against a {CeilingMs:F0} ms ceiling — {verdict}."));
    Console.WriteLine($"Results written to {options.JsonPath}.");

    return 0;
}
catch (OperationCanceledException)
{
    await Console.Error.WriteLineAsync("Cancelled; nothing was measured.").ConfigureAwait(false);

    return 130;
}
catch (Exception failure)
{
    // Loudly, and with no results file: an unmeasured criterion that reports a number is
    // worse than the "unreported" V5 has carried since the vision was written.
    await Console.Error.WriteLineAsync($"The rig failed, and recorded nothing: {failure.Message}")
        .ConfigureAwait(false);

    return 3;
}
