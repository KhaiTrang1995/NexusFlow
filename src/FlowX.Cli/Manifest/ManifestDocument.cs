using System.Text.Json.Serialization;

namespace FlowX.Cli.Manifest;

/// <summary>The manifest, as the CLI reads it.</summary>
/// <remarks>
/// <para>
/// A separate set of types from the ones the compiler writes, on purpose. The CLI is
/// the first consumer of the manifest that is not the compiler, so modelling it
/// independently is what proves the document is a contract rather than an internal
/// serialisation format. If these types ever have to import something from
/// <c>FlowX.Compiler</c> to make sense of the file, the manifest has stopped being
/// self-describing.
/// </para>
/// <para>
/// Everything is nullable and nothing is required. A tool that refuses to render a
/// diagram because a manifest from a newer compiler carries a field it does not know
/// is a tool people stop upgrading.
/// </para>
/// </remarks>
public sealed class ManifestDocument
{
    /// <summary>Schema version the document was written against.</summary>
    [JsonPropertyName("schemaVersion")]
    public string? SchemaVersion { get; set; }

    /// <summary>The application that produced it.</summary>
    [JsonPropertyName("application")]
    public ManifestApplication? Application { get; set; }

    /// <summary>Every flow in the application.</summary>
    [JsonPropertyName("flows")]
    public List<ManifestFlow> Flows { get; set; } = [];

    /// <summary>Every capability, deduplicated by identity and version.</summary>
    [JsonPropertyName("capabilities")]
    public List<ManifestCapability> Capabilities { get; set; } = [];

    /// <summary>Every event type the application declares.</summary>
    [JsonPropertyName("events")]
    public List<ManifestEvent> Events { get; set; } = [];
}

/// <summary>Application identity.</summary>
public sealed class ManifestApplication
{
    /// <summary>Application name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Application version.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

/// <summary>One flow.</summary>
public sealed class ManifestFlow
{
    /// <summary>Business identity.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>SemVer.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>Execution profile.</summary>
    [JsonPropertyName("profile")]
    public string? Profile { get; set; }

    /// <summary>ISO-8601 deadline.</summary>
    [JsonPropertyName("deadline")]
    public string? Deadline { get; set; }

    /// <summary>The contract the flow accepts.</summary>
    [JsonPropertyName("input")]
    public ManifestTypeRef? Input { get; set; }

    /// <summary>The contract the flow returns.</summary>
    [JsonPropertyName("output")]
    public ManifestTypeRef? Output { get; set; }

    /// <summary>How the flow can be started from outside the process.</summary>
    [JsonPropertyName("triggers")]
    public List<ManifestTrigger> Triggers { get; set; } = [];

    /// <summary>Steps, in execution order.</summary>
    [JsonPropertyName("steps")]
    public List<ManifestStep> Steps { get; set; } = [];

    /// <summary>Events the flow publishes.</summary>
    [JsonPropertyName("emits")]
    public List<string> Emits { get; set; } = [];
}

/// <summary>A contract type, with the members of it that carry secrets.</summary>
public sealed class ManifestTypeRef
{
    /// <summary>Fully-qualified CLR type name.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>
    /// Members carrying <c>[Sensitive]</c>, present only when the contract has at least one.
    /// </summary>
    [JsonPropertyName("sensitive")]
    public List<string> Sensitive { get; set; } = [];
}

/// <summary>One way of starting a flow.</summary>
public sealed class ManifestTrigger
{
    /// <summary>Manual, Http, Bus, Schedule, Stream, Change, Agent or Cli.</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    /// <summary>HTTP verb, for <c>Http</c> triggers.</summary>
    [JsonPropertyName("method")]
    public string? Method { get; set; }

    /// <summary>HTTP route, for <c>Http</c> triggers.</summary>
    [JsonPropertyName("route")]
    public string? Route { get; set; }

    /// <summary>Broker family, for <c>Bus</c> and <c>Stream</c> triggers.</summary>
    [JsonPropertyName("transport")]
    public string? Transport { get; set; }

    /// <summary>Topic or queue, for <c>Bus</c> and <c>Stream</c> triggers.</summary>
    [JsonPropertyName("topic")]
    public string? Topic { get; set; }

    /// <summary>Consumer group, for <c>Bus</c> triggers.</summary>
    [JsonPropertyName("group")]
    public string? Group { get; set; }

    /// <summary>Cron expression, for <c>Schedule</c> triggers.</summary>
    [JsonPropertyName("cron")]
    public string? Cron { get; set; }

    /// <summary>IANA time zone the cron expression is evaluated in.</summary>
    [JsonPropertyName("timeZone")]
    public string? TimeZone { get; set; }

    /// <summary>
    /// Whether the transport demands an idempotency key before the flow is created.
    /// </summary>
    /// <remarks>
    /// Nullable, and the distinction is used: <c>null</c> means the trigger kind has no
    /// such notion, which is not the same as declaring that no key is required.
    /// </remarks>
    [JsonPropertyName("idempotent")]
    public bool? Idempotent { get; set; }

    /// <summary>The tool description an agent trigger shows to the model.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>Never, RequiredForSideEffects or Always, for <c>Agent</c> triggers.</summary>
    [JsonPropertyName("confirmation")]
    public string? Confirmation { get; set; }
}

/// <summary>One step of a flow.</summary>
public sealed class ManifestStep
{
    /// <summary>Position in the graph.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Step kind.</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    /// <summary>Capability invoked, as <c>id@version</c>.</summary>
    [JsonPropertyName("capability")]
    public string? Capability { get; set; }

    /// <summary>Compensation registered, as <c>id@version</c>.</summary>
    [JsonPropertyName("compensation")]
    public string? Compensation { get; set; }

    /// <summary>Event published.</summary>
    [JsonPropertyName("event")]
    public string? Event { get; set; }

    /// <summary>
    /// Nested blocks of a branching step: for a <c>Condition</c>, the <c>then</c> block
    /// first and the <c>Otherwise</c> block second when there is one; for a
    /// <c>Switch</c>, one block per case in declaration order and then the <c>Default</c>,
    /// which is always present and may be empty.
    /// </summary>
    /// <remarks>
    /// Positional, because that is what the schema gives — <c>branches</c> is an array of
    /// arrays with nothing naming them. A one-element array on a <c>Condition</c>
    /// therefore means a <c>When</c> with no alternative, and an empty last array on a
    /// <c>Switch</c> means a value matching nothing falls through. The reader has to know
    /// that; the alternative would be a schema change nobody has agreed to.
    /// </remarks>
    [JsonPropertyName("branches")]
    public List<List<ManifestStep>> Branches { get; set; } = [];
}

/// <summary>One capability.</summary>
public sealed class ManifestCapability
{
    /// <summary>Business identity.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>Contract version.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>Fully-qualified CLR type name of the input contract.</summary>
    [JsonPropertyName("input")]
    public string? Input { get; set; }

    /// <summary>Fully-qualified CLR type name of the output contract.</summary>
    [JsonPropertyName("output")]
    public string? Output { get; set; }

    /// <summary>Whether a retry is declared safe.</summary>
    [JsonPropertyName("idempotent")]
    public bool Idempotent { get; set; }

    /// <summary>Named external effects.</summary>
    [JsonPropertyName("sideEffects")]
    public List<string> SideEffects { get; set; } = [];

    /// <summary>Every failure this capability can return.</summary>
    [JsonPropertyName("errors")]
    public List<ManifestError> Errors { get; set; } = [];

    /// <summary>Replacement identity and removal date, when the contract is on its way out.</summary>
    [JsonPropertyName("deprecated")]
    public string? Deprecated { get; set; }

    /// <summary>Authorisation stance.</summary>
    [JsonPropertyName("authorization")]
    public ManifestAuthorization? Authorization { get; set; }
}

/// <summary>One declared failure of a capability.</summary>
public sealed class ManifestError
{
    /// <summary>Stable error code, e.g. <c>payment.declined</c>.</summary>
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    /// <summary>Validation, NotFound, Conflict, Forbidden, Unavailable or Internal.</summary>
    [JsonPropertyName("category")]
    public string? Category { get; set; }
}

/// <summary>One event type.</summary>
public sealed class ManifestEvent
{
    /// <summary>Business identity of the event.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>SemVer of the event's payload contract.</summary>
    [JsonPropertyName("schemaVersion")]
    public string? SchemaVersion { get; set; }
}

/// <summary>A capability's authorisation stance.</summary>
public sealed class ManifestAuthorization
{
    /// <summary>Public, Authenticated, Permission, Policy or Internal.</summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    /// <summary>The named permission or policy, when the mode needs one.</summary>
    [JsonPropertyName("value")]
    public string? Value { get; set; }
}

/// <summary>Source-generated serialisation, so the tool starts fast and publishes AOT.</summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ManifestDocument))]
public sealed partial class ManifestJsonContext : JsonSerializerContext;
