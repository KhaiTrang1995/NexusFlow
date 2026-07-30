namespace FlowX;

/// <summary>
/// The compile-time projection of a <c>[Flow]</c> declaration.
/// </summary>
public sealed record FlowDescriptor
{
    private FlowDescriptor(string id, string version, ExecutionProfile profile, TimeSpan deadline)
    {
        Id = id;
        Version = version;
        Profile = profile;
        Deadline = deadline;
    }

    /// <summary>Business identity.</summary>
    public string Id { get; }

    /// <summary>SemVer of the flow.</summary>
    public string Version { get; }

    /// <summary>The execution profile. Defaults to ephemeral at the attribute level.</summary>
    public ExecutionProfile Profile { get; }

    /// <summary>The flow's absolute budget.</summary>
    public TimeSpan Deadline { get; }

    /// <summary>Identity and version together, as they appear in the manifest and in traces.</summary>
    public string QualifiedName => $"{Id}@{Version}";

    /// <summary>Creates a validated descriptor.</summary>
    /// <exception cref="ArgumentException">The identity or version is malformed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The deadline is not positive.</exception>
    public static FlowDescriptor Create(
        string id,
        string version,
        ExecutionProfile profile,
        TimeSpan deadline)
    {
        // A zero or negative deadline means every step starts already expired, which
        // presents as "the flow mysteriously does nothing" — worth rejecting loudly.
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(deadline, TimeSpan.Zero);

        return new FlowDescriptor(
            Identifiers.RequireIdentity(id, nameof(id)),
            Identifiers.RequireSemanticVersion(version, nameof(version)),
            profile,
            deadline);
    }

    /// <inheritdoc />
    public override string ToString() => $"{QualifiedName} ({Profile})";
}
