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

    /// <summary>Steps, in execution order.</summary>
    [JsonPropertyName("steps")]
    public List<ManifestStep> Steps { get; set; } = [];

    /// <summary>Events the flow publishes.</summary>
    [JsonPropertyName("emits")]
    public List<string> Emits { get; set; } = [];
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
    /// first and the <c>Otherwise</c> block second when there is one.
    /// </summary>
    /// <remarks>
    /// Positional, because that is what the schema gives — <c>branches</c> is an array of
    /// arrays with nothing naming them. A one-element array therefore means a <c>When</c>
    /// with no alternative, and the reader has to know that; the alternative would be a
    /// schema change nobody has agreed to.
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

    /// <summary>Whether a retry is declared safe.</summary>
    [JsonPropertyName("idempotent")]
    public bool Idempotent { get; set; }

    /// <summary>Named external effects.</summary>
    [JsonPropertyName("sideEffects")]
    public List<string> SideEffects { get; set; } = [];

    /// <summary>Authorisation stance.</summary>
    [JsonPropertyName("authorization")]
    public ManifestAuthorization? Authorization { get; set; }
}

/// <summary>A capability's authorisation stance.</summary>
public sealed class ManifestAuthorization
{
    /// <summary>Public, Authenticated, Permission, Policy or Internal.</summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }
}

/// <summary>Source-generated serialisation, so the tool starts fast and publishes AOT.</summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ManifestDocument))]
public sealed partial class ManifestJsonContext : JsonSerializerContext;
