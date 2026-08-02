using Xunit;

namespace FlowX.Runtime.Tests;

/// <summary>
/// The two classes that observe process-wide telemetry, serialised against each other.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A <see cref="MeterListener"/> subscribes to a meter, not to a caller.</strong>
/// <c>PolicyMetricsTests</c> attaches one to <c>FlowXTelemetry.Meter</c> and asserts on the
/// measurements it collects; <c>TelemetryCostTests</c> asserts the opposite subject — that
/// every policy instrument reports <c>Enabled == false</c> and that recording to nobody
/// allocates nothing, which is budget B6. xUnit runs test classes in parallel by default, so
/// the second class can observe the first class's listener and read <c>Enabled == true</c> for
/// an instrument nothing in its own test is listening to.
/// </para>
/// <para>
/// <strong>That is not a flaky test; it is a correct observation of a shared subject</strong> —
/// the sentence <c>FlowX.Hosting.Tests</c>'s <c>AssemblyInfo.cs</c> already had to write about
/// its own <c>ActivityListener</c>, for the same reason and with the same fix. This is the
/// narrower version of it: two classes in one collection rather than a whole assembly
/// serialised, because the other three hundred tests in this project share nothing and are
/// worth running in parallel.
/// </para>
/// <para>
/// <strong>Neither test may be relaxed instead.</strong> B6 is asserted on every PR and the
/// assertion is precisely "with no exporter attached" — a version that tolerated an attached
/// listener would be measuring the case nobody is worried about. And a metrics test that
/// filtered measurements by some correlating tag would be asserting about a tag rather than
/// about the instrument.
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
#pragma warning disable CA1711 // xUnit discovers a collection by the [CollectionDefinition] on
public static class TelemetryCollection //   a type, so the type exists to carry the attribute
#pragma warning restore CA1711          //   and its name is the one a reader expects to find.
{
    /// <summary>The collection name both classes name.</summary>
    public const string Name = "telemetry";
}
