using System.Globalization;
using System.Text.Json.Nodes;

namespace FlowX.ColdStart.Bench;

/// <summary>
/// Every observation, kept individually so that a percentile is a percentile.
/// </summary>
/// <remarks>
/// <para>
/// The same reservoir tests/FlowX.Durability.Bench uses, and for the same reason: a
/// percentile of means is not a percentile, so nothing here averages before it ranks.
/// </para>
/// <para>
/// <strong>Nearest-rank.</strong> The p95 of 30 runs is the 29th of the sorted list — an
/// observed start-up, never interpolated between two. Interpolation invents a start that
/// never happened, which is a poor thing to hold a 200 ms criterion to. With n = 30 the
/// p99 and the max are the same observation by construction, and the report says so rather
/// than presenting them as two facts.
/// </para>
/// </remarks>
/// <param name="name">What this reservoir measures, for the report.</param>
/// <param name="unit">The unit every value is in.</param>
/// <param name="capacity">Expected number of observations.</param>
internal sealed class Samples(string name, string unit, int capacity)
{
    private readonly List<double> _values = new(capacity);

    /// <summary>What this reservoir measures.</summary>
    public string Name { get; } = name;

    /// <summary>The unit of every value in it.</summary>
    public string Unit { get; } = unit;

    /// <summary>How many observations were recorded.</summary>
    public int Count => _values.Count;

    /// <summary>Records one observation.</summary>
    /// <param name="value">The value, in <see cref="Unit"/>.</param>
    public void Add(double value) => _values.Add(value);

    /// <summary>Sorts once, so every percentile below reads the same ordering.</summary>
    public void Freeze() => _values.Sort();

    /// <summary>The nearest-rank percentile.</summary>
    /// <param name="percentile">A fraction between 0 and 1.</param>
    /// <returns>The observed value at that rank, or 0 when nothing was recorded.</returns>
    /// <remarks>Call <see cref="Freeze"/> first; an unsorted list answers nonsense.</remarks>
    public double Percentile(double percentile)
    {
        if (_values.Count == 0)
        {
            return 0;
        }

        var rank = (int)Math.Ceiling(percentile * _values.Count) - 1;

        return _values[Math.Clamp(rank, 0, _values.Count - 1)];
    }

    /// <summary>The arithmetic mean.</summary>
    public double Mean => _values.Count == 0 ? 0 : _values.Sum() / _values.Count;

    /// <summary>The best observation.</summary>
    public double Min => _values.Count == 0 ? 0 : _values[0];

    /// <summary>The worst observation.</summary>
    public double Max => _values.Count == 0 ? 0 : _values[^1];

    /// <summary>The distribution, as the results document records it.</summary>
    /// <returns>A JSON object carrying the count, the unit and the percentiles.</returns>
    public JsonObject ToJson() => new()
    {
        ["name"] = Name,
        ["unit"] = Unit,
        ["runs"] = Count,
        ["mean"] = Round(Mean),
        ["min"] = Round(Min),
        ["p50"] = Round(Percentile(0.50)),
        ["p95"] = Round(Percentile(0.95)),
        ["p99"] = Round(Percentile(0.99)),
        ["max"] = Round(Max),
    };

    /// <summary>One line of the printed report.</summary>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Name,-28} n={Count,-4} min={Min,8:F1}  p50={Percentile(0.50),8:F1}  " +
        $"p95={Percentile(0.95),8:F1}  p99={Percentile(0.99),8:F1}  max={Max,8:F1}  {Unit}");

    private static double Round(double value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);
}
