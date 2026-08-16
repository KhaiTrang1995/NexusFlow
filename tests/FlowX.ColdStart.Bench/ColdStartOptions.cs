using System.Globalization;

namespace FlowX.ColdStart.Bench;

/// <summary>How the sample under measurement is compiled.</summary>
internal enum PublishMode
{
    /// <summary>NativeAOT, which is the only mode criterion V5 is stated over.</summary>
    Aot,

    /// <summary>
    /// Self-contained ReadyToRun. A real number and <em>not</em> V5's, kept for the case
    /// where a machine has no native toolchain and the alternative is measuring nothing.
    /// </summary>
    ReadyToRun,
}

/// <summary>What one run of the rig was asked to do.</summary>
/// <remarks>
/// The defaults are the criterion's own parameters where it states them. V5 is
/// "≤ 200 ms, NativeAOT-compatible", so <see cref="Mode"/> defaults to
/// <see cref="PublishMode.Aot"/> and a run that cannot publish that way fails rather than
/// quietly measuring an easier question.
/// </remarks>
internal sealed record ColdStartOptions
{
    /// <summary>The sample the AOT job in <c>ci.yml</c> publishes, and therefore this rig's subject.</summary>
    public const string SampleProject = "samples/ecommerce/Ecommerce.csproj";

    /// <summary>The flow endpoint whose first successful response ends the measurement.</summary>
    /// <remarks>
    /// A flow route rather than <c>/health</c>. Health answers as soon as the host is up and
    /// says nothing about the engine, the generated dispatcher or the capability catalogue
    /// being ready; V5 is about a request a customer makes.
    /// </remarks>
    public const string FlowPath = "/api/v1/orders";

    /// <summary>The body <c>order.place</c> accepts, as the AOT smoke test posts it.</summary>
    public const string FlowBody = """{"sku":"SKU-1","quantity":1,"paymentToken":"tok"}""";

    /// <summary>The field the flow's declared output carries, checked so a 200 is not enough.</summary>
    public const string ExpectedField = "receiptId";

    /// <summary>
    /// The demonstration token holding <c>payment.write</c>, which <c>order.place</c> needs at
    /// its third step. A shopper token is refused there, and a refusal is not a served flow.
    /// </summary>
    public const string BearerToken = "cashier-token";

    /// <summary>How the sample is published.</summary>
    public PublishMode Mode { get; init; } = PublishMode.Aot;

    /// <summary>How many measured cold starts.</summary>
    public int Runs { get; init; } = 30;

    /// <summary>
    /// How many cold starts are performed and discarded first.
    /// </summary>
    /// <remarks>
    /// The first execution of a freshly written 18 MB binary pays for reading it off disk.
    /// That is a real cost and it is paid once per deployment, not once per start, so leaving
    /// it in a 30-sample distribution would move every percentile with an event that does not
    /// recur. Reported separately instead.
    /// </remarks>
    public int Warmup { get; init; } = 3;

    /// <summary>How long one cold start may take before the rig calls it a failure.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How often the rig asks whether the port is open or the endpoint answers.</summary>
    /// <remarks>
    /// One millisecond, which is this measurement's resolution and is stated in the results
    /// document rather than left for a reader to infer. A tighter loop would spend a core
    /// spinning next to the process it is timing, which is the worse error of the two.
    /// </remarks>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(1);

    /// <summary>Whether to reuse an existing publish rather than producing one.</summary>
    public bool SkipPublish { get; init; }

    /// <summary>Where the results document is written.</summary>
    public string JsonPath { get; init; } = Path.Combine(".artifacts", "cold-start.json");

    /// <summary>Where the publish under measurement is written.</summary>
    public string PublishDirectory =>
        Path.Combine(".artifacts", Mode == PublishMode.Aot ? "coldstart-aot" : "coldstart-r2r");

    /// <summary>The published executable this rig starts.</summary>
    public string BinaryPath => Path.Combine(PublishDirectory, "Ecommerce");

    /// <summary>Reads the options from a command line.</summary>
    /// <param name="args">The arguments, as given.</param>
    /// <returns>The options.</returns>
    /// <exception cref="ArgumentException">An argument is unknown or not a positive integer.</exception>
    public static ColdStartOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var options = new ColdStartOptions();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--runs":
                    options = options with { Runs = Positive(args, ++i) };
                    break;
                case "--warmup":
                    options = options with { Warmup = NonNegative(args, ++i) };
                    break;
                case "--timeout":
                    options = options with { Timeout = TimeSpan.FromSeconds(Positive(args, ++i)) };
                    break;
                case "--readytorun":
                    options = options with { Mode = PublishMode.ReadyToRun };
                    break;
                case "--no-publish":
                    options = options with { SkipPublish = true };
                    break;
                case "--json":
                    options = options with { JsonPath = Value(args, ++i) };
                    break;
                default:
                    throw new ArgumentException(
                        $"Unknown argument '{args[i]}'. Accepted: --runs, --warmup, --timeout, " +
                        "--readytorun, --no-publish, --json.");
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
        var value = NonNegative(args, index);

        return value > 0
            ? value
            : throw new ArgumentException($"'{args[index - 1]}' needs a positive integer, not '{value}'.");
    }

    private static int NonNegative(string[] args, int index)
    {
        var text = Value(args, index);

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new ArgumentException($"'{args[index - 1]}' needs a non-negative integer, not '{text}'.");
    }
}
