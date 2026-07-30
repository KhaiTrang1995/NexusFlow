using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;

namespace FlowX.Benchmarks;

/// <summary>Entry point for the benchmark harness.</summary>
public static class Program
{
    /// <summary>
    /// Runs the benchmarks. With no arguments every benchmark runs; pass
    /// <c>--filter</c> to narrow, as with any BenchmarkDotNet host.
    /// </summary>
    public static void Main(string[] args)
    {
        var config = ManualConfig.Create(DefaultConfig.Instance)
            // JSON is what scripts/check-benchmark-budgets.py reads. Markdown is what
            // a human reads in the CI log; both are produced so the gate and the
            // explanation of the gate never come from different runs.
            .AddExporter(JsonExporter.Full)
            .AddExporter(MarkdownExporter.GitHub)
            .AddJob(Job.Default
                .WithWarmupCount(3)
                .WithIterationCount(10))
            .WithOptions(ConfigOptions.DisableLogFile);

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
    }
}
