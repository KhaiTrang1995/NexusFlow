namespace FlowX;

/// <summary>
/// Base for every trigger declaration. Trigger attributes are metadata read by the
/// compiler; the flow body cannot observe them, which is what makes quality goal Q4
/// (transport portability) hold.
/// </summary>
/// <remarks>
/// A subclass must carry <see cref="TriggerKindAttribute"/> as well as overriding
/// <see cref="Kind"/>. The override is what the runtime reads; the marker is the same
/// fact expressed as attribute data, which is the only form the compiler can read out of
/// a referenced assembly. A subclass without the marker still runs, but publishes no
/// trigger in <c>flowx.manifest.json</c> — <c>FLOWX1025</c> reports that.
/// </remarks>
public abstract class TriggerAttribute : Attribute
{
    /// <summary>The transport family this trigger belongs to.</summary>
    /// <remarks>
    /// Read at run time. The compiler cannot read it — a property getter is code, not
    /// data — so the same fact must be restated as <see cref="TriggerKindAttribute"/> on
    /// the same class, and the two must agree.
    /// </remarks>
    public abstract TriggerKind Kind { get; }
}

/// <summary>Exposes a flow as an HTTP endpoint. Generates the route, binder, OpenAPI operation and RFC 7807 mapping.</summary>
/// <param name="method">HTTP method, e.g. <c>POST</c>.</param>
/// <param name="route">Route template, e.g. <c>/api/v1/orders</c>.</param>
[TriggerKind(TriggerKind.Http)]
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class HttpTriggerAttribute(string method, string route) : TriggerAttribute
{
    /// <inheritdoc />
    public override TriggerKind Kind => TriggerKind.Http;

    /// <summary>HTTP method.</summary>
    public string Method { get; } = method;

    /// <summary>Route template.</summary>
    public string Route { get; } = route;

    /// <summary>When true, an <c>Idempotency-Key</c> header is required and enforced.</summary>
    public bool Idempotent { get; init; }

    /// <summary>API version tag surfaced in the generated OpenAPI document.</summary>
    public string? Version { get; init; }
}

/// <summary>Consumes a Kafka topic. Offsets are committed after flow completion.</summary>
/// <param name="topic">Topic name.</param>
[TriggerKind(TriggerKind.Bus)]
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class KafkaTriggerAttribute(string topic) : TriggerAttribute
{
    /// <inheritdoc />
    public override TriggerKind Kind => TriggerKind.Bus;

    /// <summary>Topic name.</summary>
    public string Topic { get; } = topic;

    /// <summary>Consumer group.</summary>
    public required string Group { get; init; }

    /// <summary>Bounded in-flight records per partition — the backpressure limit.</summary>
    public int MaxInFlight { get; init; } = 32;

    /// <summary>
    /// Where terminal errors go. A poison message is dead-lettered rather than retried
    /// forever: head-of-line blocking is a bug, not a durability strategy.
    /// </summary>
    public string? DeadLetter { get; init; }
}

/// <summary>What to do when the previous run of a schedule is still executing.</summary>
public enum OverlapPolicy
{
    /// <summary>Skip this firing. The default — prevents a long run stacking on itself.</summary>
    Skip = 0,

    /// <summary>Queue this firing until the previous run finishes.</summary>
    Queue = 1,

    /// <summary>Run concurrently.</summary>
    Concurrent = 2,
}

/// <summary>What to do about firings missed while the scheduler was down.</summary>
public enum MissedFirePolicy
{
    /// <summary>Ignore them.</summary>
    Skip = 0,

    /// <summary>Run once, regardless of how many were missed. The default.</summary>
    RunOnce = 1,

    /// <summary>Run every missed firing — a two-hour outage becomes 120 minute-jobs at once.</summary>
    RunAll = 2,
}

/// <summary>Runs a flow on a cron schedule. The scheduler is leader-elected, so a schedule never double-fires.</summary>
/// <param name="cron">Standard five-field cron expression.</param>
[TriggerKind(TriggerKind.Schedule)]
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class CronTriggerAttribute(string cron) : TriggerAttribute
{
    /// <inheritdoc />
    public override TriggerKind Kind => TriggerKind.Schedule;

    /// <summary>Five-field cron expression.</summary>
    public string Cron { get; } = cron;

    /// <summary>IANA time zone id. A schedule without one is UTC — and is a DST bug waiting to happen.</summary>
    public string TimeZone { get; init; } = "UTC";

    /// <summary>Behaviour when the previous run is still going.</summary>
    public OverlapPolicy Overlap { get; init; } = OverlapPolicy.Skip;

    /// <summary>Behaviour after downtime.</summary>
    public MissedFirePolicy MissedFire { get; init; } = MissedFirePolicy.RunOnce;

    /// <summary>ISO-8601 duration spreading load across replicas and tenants, e.g. <c>PT120S</c>.</summary>
    public string? Jitter { get; init; }

    /// <summary>Fan out to one instance per active tenant.</summary>
    public bool PerTenant { get; init; }
}

/// <summary>Consumes a continuous stream with windowing and checkpointing.</summary>
/// <param name="source">Stream source, e.g. a topic name.</param>
[TriggerKind(TriggerKind.Stream)]
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class StreamTriggerAttribute(string source) : TriggerAttribute
{
    /// <inheritdoc />
    public override TriggerKind Kind => TriggerKind.Stream;

    /// <summary>Stream source.</summary>
    public string Source { get; } = source;

    /// <summary>Window specification, e.g. <c>tumbling:1m</c>.</summary>
    public required string Window { get; init; }

    /// <summary>ISO-8601 lateness allowance. Records later than this go to a side output, never silently dropped.</summary>
    public string Lateness { get; init; } = "PT0S";

    /// <summary>ISO-8601 checkpoint interval.</summary>
    public string Checkpoint { get; init; } = "PT5S";

    /// <summary>Concurrent processing degree. Bounded by construction.</summary>
    public int Parallelism { get; init; } = 1;
}

/// <summary>When an agent must obtain human confirmation before a call proceeds.</summary>
public enum ConfirmationMode
{
    /// <summary>Never prompt. Appropriate only for capabilities with no declared side effects.</summary>
    Never = 0,

    /// <summary>
    /// Prompt when any capability in the flow declares a side effect. The prompt states
    /// the real declared consequences rather than a blanket warning.
    /// </summary>
    RequiredForSideEffects = 1,

    /// <summary>Always prompt.</summary>
    Always = 2,
}

/// <summary>
/// Exposes a flow as an agent tool over MCP. The tool descriptor, its JSON Schema and
/// its permission requirement are generated from the flow and its capabilities, so the
/// agent surface is exactly the flow surface — an agent cannot reach anything a human
/// could not (docs/15-Security.md §7).
/// </summary>
[TriggerKind(TriggerKind.Agent)]
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class AgentTriggerAttribute : TriggerAttribute
{
    /// <inheritdoc />
    public override TriggerKind Kind => TriggerKind.Agent;

    /// <summary>Natural-language description shown to the model when selecting tools.</summary>
    public required string Description { get; init; }

    /// <summary>Human confirmation requirement.</summary>
    public ConfirmationMode Confirmation { get; init; } = ConfirmationMode.RequiredForSideEffects;
}
