using FlowX;
using FlowX.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Crm;

/// <summary>
/// Applies a seed file at start-up, when one is configured and only then.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Off unless a path is set, and refused in production unless that is set too.</strong>
/// The failure this guards is not a clever attack: it is a demo dataset left configured in a
/// deployment that has real customers in it, writing plausible-looking accounts nobody can tell
/// from the genuine ones. Two variables, both of which somebody has to type, because one of them
/// gets copied between environments and the other does not.
/// </para>
/// <para>
/// <strong>A bad seed stops the process.</strong> Logging and carrying on would leave an
/// application serving a tenant configured halfway — which looks like working software until
/// somebody opens the screen whose metadata was in the part that did not apply.
/// </para>
/// </remarks>
public sealed partial class CrmSeeder : IHostedService
{
    /// <summary>Where the file is. Unset means no seeding.</summary>
    public const string PathVariable = "CRM_SEED_FILE";

    /// <summary>What must be <c>true</c> before a production deployment seeds anything.</summary>
    public const string ProductionVariable = "CRM_SEED_ALLOW_PRODUCTION";

    private readonly SeedApplier _applier;
    private readonly IHostEnvironment _environment;
    private readonly IReadOnlyList<string> _tenants;
    private readonly ILogger<CrmSeeder> _log;

    /// <summary>Creates the seeder.</summary>
    /// <param name="applier">Applies the document.</param>
    /// <param name="environment">Which environment this is, for the production guard.</param>
    /// <param name="options">The tenants this deployment serves.</param>
    /// <param name="log">Where the outcome is reported.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public CrmSeeder(
        SeedApplier applier,
        IHostEnvironment environment,
        IOptions<FlowXOptions> options,
        ILogger<CrmSeeder> log)
    {
        ArgumentNullException.ThrowIfNull(applier);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);

        _applier = applier;
        _environment = environment;
        _tenants = [.. options.Value.Tenants];
        _log = log;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Environment.GetEnvironmentVariable(PathVariable) is not { Length: > 0 } path)
        {
            return;
        }

        if (_environment.IsProduction()
            && !string.Equals(
                Environment.GetEnvironmentVariable(ProductionVariable),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{PathVariable} is set in a Production environment. Seeding writes rows that " +
                $"are indistinguishable from real ones; set {ProductionVariable}=true to say " +
                "that is intended, or unset the path.");
        }

        var file = new FileInfo(path);

        if (!file.Exists)
        {
            throw new InvalidOperationException($"{PathVariable} names '{path}', which is not a file.");
        }

        if (file.Length > SeedLimits.MaxBytes)
        {
            // Checked before the read rather than after, so an oversized file is never in memory.
            throw Refused(path, SeedErrors.TooLarge(file.Length));
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var read = SeedReader.Read(bytes);

        if (!read.IsSuccess)
        {
            throw Refused(path, read.Error!);
        }

        var document = read.Value!;

        if (!_tenants.Contains(document.Tenant, StringComparer.Ordinal))
        {
            throw Refused(path, SeedErrors.TenantNotServed(document.Tenant, _tenants));
        }

        var applied = await _applier.ApplyAsync(document, cancellationToken).ConfigureAwait(false);

        if (!applied.IsSuccess)
        {
            throw Refused(path, applied.Error!);
        }

        var outcome = applied.Value;

        Applied(_log, document.Tenant, path, outcome.Written, outcome.Present);
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Seeded tenant {Tenant} from {Path}: {Written} written, {Present} already there.")]
    private static partial void Applied(
        ILogger log, string tenant, string path, int written, int present);

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static InvalidOperationException Refused(string path, Error error) =>
        new($"The seed at '{path}' was refused: [{error.Code}] {error.Message}");
}
