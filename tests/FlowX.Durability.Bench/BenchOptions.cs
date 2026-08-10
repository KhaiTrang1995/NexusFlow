using System.Globalization;

namespace FlowX.Durability.Bench;

/// <summary>What one run of the rig was asked to do.</summary>
/// <remarks>
/// The defaults are the budget's own parameters where the budget states them. B7 says
/// "15 ms @ 5 000 commits/s/node", so <see cref="Rate"/> defaults to 5 000 and a run that
/// cannot sustain it says so rather than quietly measuring an easier question.
/// </remarks>
internal sealed record BenchOptions
{
    /// <summary>How to reach PostgreSQL.</summary>
    public required string ConnectionString { get; init; }

    /// <summary>The schema the run owns, and drops on the way out.</summary>
    public string Schema { get; init; } =
        "bench_" + Guid.CreateVersion7().ToString("n")[..12];

    /// <summary>Commits per second the rig offers in the B7 arm.</summary>
    public int Rate { get; init; } = 5000;

    /// <summary>Concurrent writers offering that rate.</summary>
    /// <remarks>
    /// One node, several in-flight commits — which is what "per node" means and what makes
    /// PostgreSQL's group commit apply at all. A single writer cannot reach 5 000 commits/s
    /// against a durable fsync however fast the disk is, and a rig that used one would be
    /// measuring round-trip serialisation rather than the budget.
    /// </remarks>
    public int Writers { get; init; } = 32;

    /// <summary>How many commits the B7 arm measures, after the warm-up.</summary>
    public int Commits { get; init; } = 20000;

    /// <summary>How many commits are issued and discarded before measurement starts.</summary>
    /// <remarks>
    /// Connection establishment, statement preparation and the first WAL segment are all
    /// paid once. Leaving them in would put a handful of tens-of-milliseconds outliers into
    /// a 20 000-sample tail, which moves the p99 and describes nothing that happens again.
    /// </remarks>
    public int Warmup { get; init; } = 2000;

    /// <summary>Instances the B8 arm rehydrates.</summary>
    public int Resumes { get; init; } = 2000;

    /// <summary>Committed steps each rehydrated instance carries.</summary>
    /// <remarks>
    /// The frontier read returns every committed step, so its cost grows with history and a
    /// single number for B8 is only meaningful beside the depth it was measured at.
    /// </remarks>
    public int HistoryDepth { get; init; } = 20;

    /// <summary>Where the results document is written.</summary>
    public string JsonPath { get; init; } = Path.Combine(".artifacts", "durability-latency.json");

    /// <summary>Whether to leave the schema behind for inspection.</summary>
    public bool KeepSchema { get; init; }

    /// <summary>Reads the options from a command line.</summary>
    /// <param name="args">The arguments, as given.</param>
    /// <param name="connectionString">The gated connection string.</param>
    /// <returns>The options.</returns>
    /// <exception cref="ArgumentException">An argument is unknown or not a positive integer.</exception>
    public static BenchOptions Parse(string[] args, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(args);

        var options = new BenchOptions { ConnectionString = connectionString };

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--rate":
                    options = options with { Rate = Positive(args, ++i) };
                    break;
                case "--writers":
                    options = options with { Writers = Positive(args, ++i) };
                    break;
                case "--commits":
                    options = options with { Commits = Positive(args, ++i) };
                    break;
                case "--warmup":
                    options = options with { Warmup = Positive(args, ++i) };
                    break;
                case "--resumes":
                    options = options with { Resumes = Positive(args, ++i) };
                    break;
                case "--history-depth":
                    options = options with { HistoryDepth = Positive(args, ++i) };
                    break;
                case "--json":
                    options = options with { JsonPath = Value(args, ++i) };
                    break;
                case "--schema":
                    options = options with { Schema = Value(args, ++i) };
                    break;
                case "--keep-schema":
                    options = options with { KeepSchema = true };
                    break;
                default:
                    throw new ArgumentException(
                        $"Unknown argument '{args[i]}'. Accepted: --rate, --writers, --commits, " +
                        "--warmup, --resumes, --history-depth, --json, --schema, --keep-schema.");
            }
        }

        return options;
    }

    private static string Value(string[] args, int index) =>
        index < args.Length
            ? args[index]
            : throw new ArgumentException($"'{args[index - 1]}' needs a value.");

    private static int Positive(string[] args, int index)
    {
        var text = Value(args, index);

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && value > 0
            ? value
            : throw new ArgumentException($"'{args[index - 1]}' needs a positive integer, not '{text}'.");
    }
}
