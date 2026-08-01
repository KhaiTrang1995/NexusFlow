using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using FlowX.Observability;

namespace Ecommerce;

/// <summary>
/// What this sample does with the spans and metrics FlowX emits: prints the spans, and
/// serves the metrics at <c>/metrics</c> in Prometheus text format.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Hand-written rather than an OpenTelemetry SDK reference, and constraint C2 is
/// why.</strong> This is the repository's only NativeAOT-published assembly and CI publishes
/// it, so every dependency added here has to survive trimming and AOT — and the point being
/// demonstrated is that FlowX emits through <see cref="ActivitySource"/> and
/// <see cref="Meter"/>, which is exactly the seam an SDK attaches to. Sixty lines of
/// <see cref="ActivityListener"/> and <see cref="MeterListener"/> show that the seam is real
/// without making the sample's AOT story depend on somebody else's.
/// </para>
/// <para>
/// A real deployment replaces this file with
/// <c>.WithTracing(t =&gt; t.AddSource("FlowX"))</c> and
/// <c>.WithMetrics(m =&gt; m.AddMeter("FlowX"))</c>. That is the whole integration, and it is
/// why <see cref="FlowXTelemetry.SourceName"/> is one name for both.
/// </para>
/// <para>
/// <strong>The exposition format is deliberately minimal and deliberately honest.</strong>
/// Histograms are rendered as their <c>_count</c> and <c>_sum</c> series and no buckets: a
/// real exporter configures bucket boundaries, and inventing them here would put quantiles in
/// front of a reader that this file did not actually compute.
/// </para>
/// </remarks>
internal sealed class SampleTelemetry : IDisposable
{
    private readonly ActivityListener _spans;
    private readonly MeterListener _meters;
    private readonly Dictionary<string, Series> _series = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    private SampleTelemetry(bool printSpans)
    {
        _spans = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == FlowXTelemetry.SourceName,

            // Head-based sampling is what docs/12 §8 specifies at 1 %. A sample is a sample of
            // one request here, so everything is recorded — the point is to show the span, not
            // to demonstrate a sampler.
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,

            ActivityStopped = printSpans ? Print : null,
        };

        ActivitySource.AddActivityListener(_spans);

        _meters = new MeterListener
        {
            InstrumentPublished = static (instrument, listener) =>
            {
                if (instrument.Meter.Name == FlowXTelemetry.SourceName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };

        _meters.SetMeasurementEventCallback<long>(
            (instrument, value, tags, _) => Record(instrument, value, tags));

        _meters.SetMeasurementEventCallback<double>(
            (instrument, value, tags, _) => Record(instrument, value, tags));

        _meters.Start();
    }

    /// <summary>
    /// Starts listening, and caps the <c>tenant</c> metric label to this deployment's tenants.
    /// </summary>
    /// <param name="printSpans">
    /// Whether to print each span as it ends. False in the test host, which asserts on the
    /// spans directly and does not want them on the runner's console.
    /// </param>
    /// <remarks>
    /// The allow-list is the half of §3's cardinality rule an application owns. FlowX defaults
    /// to bucketing every tenant as <c>other</c>, because a platform that passed the tenant
    /// through by default would make "cardinality is a production incident waiting to happen"
    /// the out-of-the-box behaviour; naming the tenants that have SLOs is the deployment's
    /// decision and this is where a deployment makes it.
    /// </remarks>
    public static SampleTelemetry Start(bool printSpans = true)
    {
        FlowXTelemetry.ConfigureTenantLabels(["acme", "globex"]);

        return new SampleTelemetry(printSpans);
    }

    /// <summary>Everything collected so far, in Prometheus text exposition format.</summary>
    public string Scrape()
    {
        // Observable instruments are read on collection rather than written on an event, so a
        // scrape has to ask for them. Without this call flowx_outbox_pending and its siblings
        // would be absent from every scrape — which is the shape of bug that looks like a
        // healthy, empty outbox.
        _meters.RecordObservableInstruments();

        var text = new StringBuilder();

        lock (_gate)
        {
            foreach (var (key, series) in _series.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
            {
                text.Append(key)
                    .Append(' ')
                    .Append(series.Value.ToString("G17", CultureInfo.InvariantCulture))
                    .Append('\n');
            }
        }

        return text.ToString();
    }

    /// <summary>Stops listening.</summary>
    public void Dispose()
    {
        _spans.Dispose();
        _meters.Dispose();
    }

    private static void Print(Activity activity)
    {
        var line = new StringBuilder();

        line.Append("span ")
            .Append(activity.OperationName)
            .Append(" [")
            .Append(activity.Duration.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture))
            .Append("ms]");

        foreach (var tag in activity.TagObjects)
        {
            line.Append(' ').Append(tag.Key).Append('=').Append(tag.Value);
        }

        if (activity.Status == ActivityStatusCode.Error)
        {
            line.Append(" status=Error");
        }

        Console.WriteLine(line.ToString());
    }

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        // A counter and a histogram accumulate; a gauge replaces. The instrument's own type is
        // what says which, so a reader of this file does not have to know which names are
        // which — and neither does this file. A pattern match rather than a name check: the
        // latter is a string comparison against a framework implementation detail, and this is
        // the assembly where reflection over type names is least welcome (constraint C2).
        if (instrument is Histogram<double>)
        {
            Accumulate(Key(instrument.Name + "_count", tags), 1);
            Accumulate(Key(instrument.Name + "_sum", tags), value);

            return;
        }

        var key = Key(instrument.Name, tags);

        if (instrument is ObservableGauge<long> or ObservableGauge<double>)
        {
            Set(key, value);
        }
        else
        {
            Accumulate(key, value);
        }
    }

    private static string Key(string name, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (tags.Length == 0)
        {
            return name;
        }

        var key = new StringBuilder(name).Append('{');

        for (var i = 0; i < tags.Length; i++)
        {
            if (i > 0)
            {
                key.Append(',');
            }

            key.Append(tags[i].Key).Append("=\"").Append(tags[i].Value).Append('"');
        }

        return key.Append('}').ToString();
    }

    private void Accumulate(string key, double value)
    {
        lock (_gate)
        {
            _series[key] = new Series(_series.TryGetValue(key, out var existing) ? existing.Value + value : value);
        }
    }

    private void Set(string key, double value)
    {
        lock (_gate)
        {
            _series[key] = new Series(value);
        }
    }

    private readonly record struct Series(double Value);
}
