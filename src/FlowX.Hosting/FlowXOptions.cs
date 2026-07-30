using Microsoft.Extensions.Options;

namespace FlowX.Hosting;

/// <summary>Host-level configuration for the FlowX runtime.</summary>
/// <remarks>
/// Every setting here has a default that is safe, and none has a default that is
/// permissive. <see cref="ApplicationName"/> has no default at all, because a manifest
/// and a trace stream that cannot say which application produced them are worth
/// noticeably less than ones that can.
/// </remarks>
public sealed class FlowXOptions
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "FlowX";

    /// <summary>
    /// Identifies this application in the manifest, in traces and in audit records.
    /// Required.
    /// </summary>
    public string ApplicationName { get; set; } = string.Empty;

    /// <summary>How many flow contexts to retain between executions. See budget B2.</summary>
    public int MaxPooledContexts { get; set; } = 128;

    /// <summary>
    /// The deadline a flow gets when it declares none. Bounded on purpose: an
    /// unbounded flow is not a default anyone would choose deliberately.
    /// </summary>
    public TimeSpan DefaultDeadline { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long shutdown waits for in-flight flows before giving up.
    /// </summary>
    /// <remarks>
    /// Keep it below the orchestrator's termination grace period. A drain budget longer
    /// than the grace period is a drain that never completes — Kubernetes sends SIGKILL
    /// on its own schedule, not on this one.
    /// </remarks>
    public TimeSpan ShutdownDrainTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Validates <see cref="FlowXOptions"/> before the host starts.
/// </summary>
/// <remarks>
/// <para>
/// OWASP A05. The point is <em>when</em> this runs. Options validated lazily fail on
/// the first request after a deploy — a production incident, with traffic already
/// routed to the new pod. Validated at startup, the same mistake is a pod that never
/// becomes ready and a rollout that halts by itself.
/// </para>
/// <para>
/// Every problem is reported in one pass rather than the first one found. Fixing
/// configuration one error per deploy cycle is a loop nobody should be put through.
/// </para>
/// </remarks>
internal sealed class FlowXOptionsValidator : IValidateOptions<FlowXOptions>
{
    public ValidateOptionsResult Validate(string? name, FlowXOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ApplicationName))
        {
            failures.Add(
                $"{nameof(FlowXOptions.ApplicationName)} is required. It identifies this " +
                "application in the manifest, in traces and in audit records.");
        }

        if (options.MaxPooledContexts <= 0)
        {
            failures.Add(
                $"{nameof(FlowXOptions.MaxPooledContexts)} must be greater than zero; " +
                $"it is {options.MaxPooledContexts}. A pool of zero allocates a fresh " +
                "context per flow, which loses budget B2.");
        }

        if (options.DefaultDeadline <= TimeSpan.Zero)
        {
            failures.Add(
                $"{nameof(FlowXOptions.DefaultDeadline)} must be positive; it is " +
                $"{options.DefaultDeadline}. A zero deadline means every step starts " +
                "already expired, which presents as the service silently doing nothing.");
        }

        if (options.ShutdownDrainTimeout < TimeSpan.Zero)
        {
            failures.Add(
                $"{nameof(FlowXOptions.ShutdownDrainTimeout)} cannot be negative; it is " +
                $"{options.ShutdownDrainTimeout}.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
