using System.Globalization;

namespace Crm;

/// <summary>
/// The process asking itself whether it is ready, for a container that has nothing else to ask
/// with.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists at all.</strong> The runtime image is chiselled: no shell, no
/// <c>curl</c>, no <c>wget</c>. That is the point of choosing it — there is nothing for code
/// execution to pivot with — and it also means <c>HEALTHCHECK CMD</c> has nothing to run. The
/// usual answers are to install curl, which puts a shell and a TLS stack back in the image, or
/// to give up on the check. This is the third: the runtime is already there, so the probe is
/// <c>dotnet Crm.dll --healthcheck</c>.
/// </para>
/// <para>
/// <strong>It asks the readiness endpoint, not the liveness one.</strong> A container marked
/// unhealthy is one an orchestrator stops routing to, which is what readiness means. Liveness —
/// "is this process worth restarting" — is answered by the process still being alive enough to
/// run this at all.
/// </para>
/// </remarks>
public static class CrmProbe
{
    /// <summary>The argument that turns a start into a probe.</summary>
    public const string Argument = "--healthcheck";

    /// <summary>How long the probe waits before calling it a failure.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    /// <summary>Whether this invocation is a probe rather than a start.</summary>
    /// <param name="args">The process arguments.</param>
    /// <returns>Whether to probe.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is null.</exception>
    public static bool WasAsked(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return Array.Exists(args, argument => string.Equals(argument, Argument, StringComparison.Ordinal));
    }

    /// <summary>Asks the running server whether it is ready.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Nought when ready, one otherwise — the exit code a container reads.</returns>
    public static async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        // The same variable Kestrel binds, so the probe cannot ask a port the server is not on.
        // A probe that hard-coded 8080 would report unhealthy on every deployment that moved it.
        var port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS") is { Length: > 0 } configured
            && int.TryParse(configured.Split(';')[0], CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 8080;

        // Loopback, never the container's routable address: this asks whether *this* process is
        // ready, and a probe that could reach a load balancer would answer about somebody else.
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = Patience };

        try
        {
            using var response = await client.GetAsync(
                new Uri("/health/ready", UriKind.Relative), cancellationToken).ConfigureAwait(false);

            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (HttpRequestException)
        {
            return 1;
        }
        catch (TaskCanceledException)
        {
            // A server too busy to answer within the timeout is one an orchestrator should stop
            // sending work to, which is the same answer as "not listening".
            return 1;
        }
    }
}
