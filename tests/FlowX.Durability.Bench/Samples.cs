using System.Globalization;
using System.Text.Json.Nodes;

namespace FlowX.Durability.Bench;

/// <summary>
/// Every operation's latency, kept individually so that a percentile is a percentile.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why not BenchmarkDotNet.</strong> It is the right instrument for B1, B3 and B12
/// and the wrong one here. It reports statistics over <em>iterations</em>, each of which is
/// the mean of many operations, so its p95 is a percentile of means — and a mean is exactly
/// what hides the tail a 15 ms p99 is written to bound. B7 and B8 need the distribution of
/// single store calls, which means keeping every one.
/// </para>
/// <para>
/// <strong>Nearest-rank, and stated because the choice moves the number.</strong> The p99 of
/// 1 000 samples is the 990th of the sorted list — an observed value, never interpolated
/// between two. Interpolation invents a latency no operation had, which is a poor thing to
/// hold a durability budget to.
/// </para>
/// </remarks>
internal sealed class Samples(string name, int capacity)
{
    private readonly List<double> _milliseconds = new(capacity);

    /// <summary>What this reservoir measures, for the report.</summary>
    public string Name { get; } = name;

    /// <summary>How many operations were recorded.</summary>
    public int Count => _milliseconds.Count;

    /// <summary>Records one operation.</summary>
    /// <param name="elapsed">How long it took.</param>
    public void Add(TimeSpan elapsed) => _milliseconds.Add(elapsed.TotalMilliseconds);

    /// <summary>Merges another reservoir's samples into this one.</summary>
    /// <param name="other">The reservoir to absorb. Left unchanged.</param>
    public void AddRange(Samples other)
    {
        ArgumentNullException.ThrowIfNull(other);

        _milliseconds.AddRange(other._milliseconds);
    }

    /// <summary>Sorts once, so every percentile below reads the same ordering.</summary>
    public void Freeze() => _milliseconds.Sort();

    /// <summary>The nearest-rank percentile, in milliseconds.</summary>
    /// <param name="percentile">A fraction between 0 and 1.</param>
    /// <returns>The observed latency at that rank, or 0 when nothing was recorded.</returns>
    /// <remarks>Call <see cref="Freeze"/> first; an unsorted list answers nonsense.</remarks>
    public double Percentile(double percentile)
    {
        if (_milliseconds.Count == 0)
        {
            return 0;
        }

        var rank = (int)Math.Ceiling(percentile * _milliseconds.Count) - 1;

        return _milliseconds[Math.Clamp(rank, 0, _milliseconds.Count - 1)];
    }

    /// <summary>The arithmetic mean, in milliseconds.</summary>
    public double Mean => _milliseconds.Count == 0 ? 0 : _milliseconds.Sum() / _milliseconds.Count;

    /// <summary>The worst observation, in milliseconds.</summary>
    public double Max => _milliseconds.Count == 0 ? 0 : _milliseconds[^1];

    /// <summary>The distribution, as the results document records it.</summary>
    /// <returns>A JSON object carrying the counts and the percentiles.</returns>
    public JsonObject ToJson() => new()
    {
        ["name"] = Name,
        ["operations"] = Count,
        ["meanMs"] = Round(Mean),
        ["p50Ms"] = Round(Percentile(0.50)),
        ["p95Ms"] = Round(Percentile(0.95)),
        ["p99Ms"] = Round(Percentile(0.99)),
        ["maxMs"] = Round(Max),
    };

    /// <summary>One line of the printed report.</summary>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Name,-34} n={Count,-7} p50={Percentile(0.50),8:F3} ms  " +
        $"p99={Percentile(0.99),8:F3} ms  max={Max,8:F3} ms");

    private static double Round(double value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);
}
