using System.Globalization;

namespace FlowX.Chaos;

/// <summary>Which half of the kill window the process dies in.</summary>
internal enum KillPosition
{
    /// <summary>
    /// After the step's effect has been applied and before the engine commits its row —
    /// the window <c>ADR-0006</c> and <c>docs/11-Distributed-Runtime.md §4</c> say outright is
    /// not closed.
    /// </summary>
    BeforeCommit,

    /// <summary>
    /// After the previous step's row is committed and before the next step's effect —
    /// the window the journal is supposed to make invisible.
    /// </summary>
    AfterCommit,
}

/// <summary>What this process was told to be.</summary>
internal enum ChaosRole
{
    /// <summary>Sets the run up, spawns the others, and renders the verdict.</summary>
    Coordinator,

    /// <summary>Runs flows, and dies with <c>SIGKILL</c> on schedule.</summary>
    Worker,

    /// <summary>Sweeps for instances a dead worker left running.</summary>
    Recovery,
}

/// <summary>
/// Every parameter of a chaos run, and the parsing of the command line that supplies them.
/// </summary>
/// <remarks>
/// <para>
/// The flow count and the kill schedule are parameters because QR2's 10 000 is a release
/// figure and a default that costs ten minutes is a rig nobody runs. The defaults here are
/// the smallest run that still kills real processes and still recovers real instances.
/// </para>
/// <para>
/// Every value is echoed into the results document. A measurement whose configuration is not
/// recorded beside it is a measurement that cannot be repeated.
/// </para>
/// </remarks>
internal sealed record ChaosOptions
{
    /// <summary>What this process is.</summary>
    public ChaosRole Role { get; init; } = ChaosRole.Coordinator;

    /// <summary>How many durable instances the arm runs. QR2's figure is 10 000.</summary>
    public int Flows { get; init; } = 200;

    /// <summary>
    /// How many arrivals at the kill point one worker process survives before it is killed.
    /// </summary>
    public int KillEvery { get; init; } = 10;

    /// <summary>Which step boundary the kill is taken at.</summary>
    public int KillStep { get; init; } = 1;

    /// <summary>Which arms to run.</summary>
    public IReadOnlyList<KillPosition> Positions { get; init; } =
        [KillPosition.BeforeCommit, KillPosition.AfterCommit];

    /// <summary>The kill position this process is operating under.</summary>
    public KillPosition Position { get; init; } = KillPosition.BeforeCommit;

    /// <summary>How many worker processes run at once.</summary>
    public int Workers { get; init; } = 2;

    /// <summary>How many flows one worker process runs at once.</summary>
    public int Concurrency { get; init; } = 4;

    /// <summary>How many recovery-node processes sweep.</summary>
    public int RecoveryNodes { get; init; } = 2;

    /// <summary>
    /// How many abandoned instances one recovery node takes at once
    /// (<c>FlowXOptions.MaxConcurrentRecoveries</c>).
    /// </summary>
    /// <remarks>
    /// A parameter rather than a constant because it is the thing that bounds how fast a
    /// backlog drains, and therefore what the measured resume p99 can possibly be. A sweep
    /// awaits its takeovers before asking for a second page, so a fleet clears at roughly
    /// <c>nodes × this ÷ (sweep + interval)</c> instances a second, whatever the database can
    /// do. Recording it beside the p99 is what stops that number being read as a property of
    /// FlowX rather than of a setting.
    /// </remarks>
    public int MaxConcurrentRecoveries { get; init; } = 8;

    /// <summary>The lease TTL, which is also the staleness threshold the sweep uses.</summary>
    public TimeSpan LeaseTtl { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How often a recovery node sweeps.</summary>
    public TimeSpan ScanInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long to wait for every instance to reach a terminal state.</summary>
    public TimeSpan ConvergeTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>The schema the arm's tables live in.</summary>
    public string Schema { get; init; } = string.Empty;

    /// <summary>This process's node identity, as an operator would read it.</summary>
    public string NodeName { get; init; } = "node-0";

    /// <summary>Where the machine-readable results go.</summary>
    public string JsonPath { get; init; } = "chaos-qr2.json";

    /// <summary>Whether to leave the schema behind for inspection.</summary>
    public bool KeepSchema { get; init; }

    /// <summary>The connection string, from the environment.</summary>
    public string ConnectionString { get; init; } = string.Empty;

    /// <summary>Parses a command line, leaving unset values at their defaults.</summary>
    /// <param name="args">The arguments, as <c>--key value</c> pairs.</param>
    /// <param name="connectionString">The connection string the environment supplied.</param>
    /// <returns>The options.</returns>
    /// <exception cref="ArgumentException">An argument was not understood.</exception>
    public static ChaosOptions Parse(string[] args, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(args);

        var options = new ChaosOptions { ConnectionString = connectionString };

        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length)
            {
                throw new ArgumentException($"'{args[i]}' has no value.", nameof(args));
            }

            options = Apply(options, args[i], args[i + 1]);
        }

        return options;
    }

    /// <summary>The arguments a child process needs to be this process's counterpart.</summary>
    /// <param name="role">What the child is.</param>
    /// <param name="node">The child's node identity.</param>
    /// <returns>The argument list.</returns>
    public IReadOnlyList<string> ChildArguments(ChaosRole role, string node) =>
    [
        "--role", role.ToString().ToLowerInvariant(),
        "--schema", Schema,
        "--node", node,
        "--flows", Text(Flows),
        "--kill-every", Text(KillEvery),
        "--kill-step", Text(KillStep),
        "--kill-position", Slug(Position),
        "--concurrency", Text(Concurrency),
        "--max-concurrent-recoveries", Text(MaxConcurrentRecoveries),
        "--lease-ttl", Text((int)LeaseTtl.TotalSeconds),
        "--scan-interval", Text((int)ScanInterval.TotalSeconds),
    ];

    /// <summary>The command-line spelling of a kill position.</summary>
    /// <param name="position">The position.</param>
    /// <returns>Its slug.</returns>
    public static string Slug(KillPosition position) =>
        position == KillPosition.BeforeCommit ? "before-commit" : "after-commit";

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static ChaosOptions Apply(ChaosOptions options, string key, string value) => key switch
    {
        "--role" => options with { Role = ParseRole(value) },
        "--flows" => options with { Flows = Positive(key, value) },
        "--kill-every" => options with { KillEvery = Positive(key, value) },
        "--kill-step" => options with { KillStep = NonNegative(key, value) },
        "--kill-position" => options with
        {
            Positions = ParsePositions(value),
            Position = ParsePositions(value)[0],
        },
        "--workers" => options with { Workers = Positive(key, value) },
        "--concurrency" => options with { Concurrency = Positive(key, value) },
        "--recovery-nodes" => options with { RecoveryNodes = Positive(key, value) },
        "--max-concurrent-recoveries" => options with
        {
            MaxConcurrentRecoveries = Positive(key, value),
        },
        "--lease-ttl" => options with { LeaseTtl = TimeSpan.FromSeconds(Positive(key, value)) },
        "--scan-interval" => options with { ScanInterval = TimeSpan.FromSeconds(Positive(key, value)) },
        "--converge-timeout" => options with
        {
            ConvergeTimeout = TimeSpan.FromSeconds(Positive(key, value)),
        },
        "--schema" => options with { Schema = value },
        "--node" => options with { NodeName = value },
        "--json" => options with { JsonPath = value },
        "--keep-schema" => options with { KeepSchema = Flag(key, value) },
        _ => throw new ArgumentException($"Unknown argument '{key}'.", nameof(key)),
    };

    private static ChaosRole ParseRole(string value) => value switch
    {
        "coordinator" => ChaosRole.Coordinator,
        "worker" => ChaosRole.Worker,
        "recovery" => ChaosRole.Recovery,
        _ => throw new ArgumentException($"Unknown role '{value}'.", nameof(value)),
    };

    private static IReadOnlyList<KillPosition> ParsePositions(string value) => value switch
    {
        "before-commit" => [KillPosition.BeforeCommit],
        "after-commit" => [KillPosition.AfterCommit],
        "both" => [KillPosition.BeforeCommit, KillPosition.AfterCommit],
        _ => throw new ArgumentException(
            $"Unknown kill position '{value}'. Use before-commit, after-commit or both.",
            nameof(value)),
    };

    private static bool Flag(string key, string value) =>
        bool.TryParse(value, out var parsed)
            ? parsed
            : throw new ArgumentException($"'{key}' takes true or false.", nameof(key));

    private static int Positive(string key, string value)
    {
        var parsed = NonNegative(key, value);

        return parsed > 0
            ? parsed
            : throw new ArgumentException($"'{key}' must be greater than zero.", nameof(key));
    }

    private static int NonNegative(string key, string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
        && parsed >= 0
            ? parsed
            : throw new ArgumentException($"'{key}' must be a non-negative integer.", nameof(key));
}
