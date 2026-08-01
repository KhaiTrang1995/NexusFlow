namespace FlowX.Cli.Replay;

/// <summary>What the journal holds about one instance.</summary>
/// <remarks>
/// <para>
/// A separate set of types from the ones <c>FlowX.Postgres</c> writes, for the same reason
/// <c>ManifestDocument</c> is separate from the compiler's model: the CLI is a consumer, and
/// modelling the read independently is what keeps
/// [ADR-0020](../../../docs/adr/ADR-0020-cli-reads-the-journal-as-rows.md)) honest. If these
/// types ever have to import something from a FlowX assembly, the journal has stopped being
/// readable from outside this repository and the decision has to be re-argued.
/// </para>
/// <para>
/// Payloads stay as raw JSON text. The CLI has no contract types to deserialise them into
/// and inventing some would be a second, unversioned model of the user's own data.
/// </para>
/// </remarks>
internal sealed record InstanceHistory
{
    /// <summary>The instance's identity.</summary>
    public required Guid InstanceId { get; init; }

    /// <summary>The flow's business identity.</summary>
    public required string FlowId { get; init; }

    /// <summary>The version the instance is pinned to for its whole life.</summary>
    public required string FlowVersion { get; init; }

    /// <summary>The partition key, or null for an untenanted instance.</summary>
    public string? TenantId { get; init; }

    /// <summary>The state the instance is in.</summary>
    public required string State { get; init; }

    /// <summary>What ties every row to the request that started it.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>The W3C trace id, when the trigger carried one.</summary>
    public string? TraceId { get; init; }

    /// <summary>When the instance was opened.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When it was last written.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// The trigger input as the column holds it, or <c>null</c> when the column is NULL.
    /// </summary>
    /// <remarks>
    /// <strong>Null here means "the column was NULL", and nothing more.</strong> It is not
    /// "the flow received nothing" — PostgreSQL cannot tell those apart and neither can this
    /// tool. Every renderer must carry the distinction through rather than collapsing it into
    /// an empty object, which is why <see cref="InputIsKnown"/> exists as its own question.
    /// </remarks>
    public string? Input { get; init; }

    /// <summary>Whether anything at all is known about the flow's input.</summary>
    public bool InputIsKnown => Input is not null;

    /// <summary>How long the instance has been open, or ran for if it is finished.</summary>
    public TimeSpan Elapsed => UpdatedAt - CreatedAt;

    /// <summary>Every committed step, in commit order.</summary>
    public IReadOnlyList<HistoryStep> Steps { get; init; } = [];
}

/// <summary>One row of the append-only history.</summary>
internal sealed record HistoryStep
{
    /// <summary>
    /// The iteration scope: empty for the flow body, <c>7</c> for the eighth element of a
    /// <c>ForEach</c>, <c>7/2</c> for an element of a loop nested inside it.
    /// </summary>
    public required string Scope { get; init; }

    /// <summary>The step's index in the compiled plan.</summary>
    public required int StepId { get; init; }

    /// <summary>Which attempt this row records. One-based.</summary>
    public required int Attempt { get; init; }

    /// <summary>Instance-local commit order.</summary>
    public required long Sequence { get; init; }

    /// <summary>The capability the step invoked.</summary>
    public required string CapabilityId { get; init; }

    /// <summary>The version resolved at the time the step ran.</summary>
    public required string CapabilityVersion { get; init; }

    /// <summary><c>Success</c>, <c>Failure</c> or <c>Compensated</c>.</summary>
    public required string Outcome { get; init; }

    /// <summary>The step's result or its error, as stored. Null when the column is NULL.</summary>
    public string? Result { get; init; }

    /// <summary>What the step read that it could not have computed. Null when nothing was.</summary>
    public string? Nondeterminism { get; init; }

    /// <summary>How long the attempt took.</summary>
    public required long DurationMs { get; init; }

    /// <summary>When the row was committed.</summary>
    public required DateTimeOffset CommittedAt { get; init; }

    /// <summary>The capability as the manifest spells it: <c>id@version</c>.</summary>
    public string Capability => $"{CapabilityId}@{CapabilityVersion}";

    /// <summary>
    /// The step as an operator names it: <c>2</c> in the flow body, <c>2[7]</c> in the eighth
    /// element of a loop, <c>2[7/2]</c> one loop deeper.
    /// </summary>
    /// <remarks>
    /// The scope is rendered rather than dropped because <c>(instance, step)</c> is not unique
    /// once a <c>ForEach</c> has run. A history that collapsed the iterations would show one
    /// line where five hundred rows exist, and hide which element failed.
    /// </remarks>
    public string Label => Scope.Length == 0 ? $"step {StepId}" : $"step {StepId}[{Scope}]";
}
