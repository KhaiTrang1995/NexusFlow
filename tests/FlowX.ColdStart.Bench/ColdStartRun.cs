using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace FlowX.ColdStart.Bench;

/// <summary>One cold start, measured.</summary>
/// <param name="ListenMs">Process start to the port accepting a connection.</param>
/// <param name="ResponseMs">Process start to the first successful flow response.</param>
/// <param name="RssKb">Resident set size at that response, or 0 where the platform will not say.</param>
internal sealed record RunResult(double ListenMs, double ResponseMs, long RssKb);

/// <summary>
/// Starts the published binary, waits for it to serve a flow, and times both halves.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The clock starts before <c>Process.Start</c> and stops on a response body.</strong>
/// Everything in between belongs to the number, <c>fork</c>/<c>exec</c> included: a customer
/// waiting for a scaled-out replica pays for that too, and V5 is written from where they
/// stand rather than from the host's first log line.
/// </para>
/// <para>
/// <strong>Why two clocks.</strong> The port opening and the flow answering are different
/// facts, and the split is free here because it needs no instrumentation inside the sample —
/// a TCP connect is observable from outside the process. A regression that moves only the
/// second half is the runtime building its plan catalogue; one that moves only the first is
/// the binary or the host. One number cannot tell those apart.
/// </para>
/// </remarks>
internal static class ColdStartRun
{
    /// <summary>Performs one cold start and returns what it cost.</summary>
    /// <param name="options">The run's options.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The two elapsed times and the resident set size.</returns>
    /// <exception cref="InvalidOperationException">
    /// The process died, or did not serve the flow endpoint inside the timeout. Either way
    /// nothing is recorded: a rig that turned a failure into a sample would report the
    /// timeout as a cold start.
    /// </exception>
    public static async Task<RunResult> MeasureAsync(
        ColdStartOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var port = FreePort();
        var startInfo = new ProcessStartInfo(Path.GetFullPath(options.BinaryPath))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        startInfo.Environment["ASPNETCORE_URLS"] =
            string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}");

        var wall = Stopwatch.StartNew();

        using var app = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {options.BinaryPath}.");

        var stdout = app.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = app.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            var listen = await ListeningAsync(app, port, wall, options, cancellationToken)
                .ConfigureAwait(false);

            var response = await RespondingAsync(app, port, wall, options, cancellationToken)
                .ConfigureAwait(false);

            return new RunResult(listen, response, ResidentKb(app.Id));
        }
        catch (InvalidOperationException failure)
        {
            // Stopped first, and only then read: the pipes do not complete while the process
            // still holds them open, so a diagnostic gathered in the other order is empty
            // exactly when it is needed.
            Stop(app);

            throw new InvalidOperationException(
                failure.Message + Environment.NewLine + await LogAsync(stdout, stderr).ConfigureAwait(false),
                failure);
        }
        finally
        {
            Stop(app);

            _ = await stdout.ConfigureAwait(false);
            _ = await stderr.ConfigureAwait(false);
        }
    }

    private static async Task<double> ListeningAsync(
        Process app,
        int port,
        Stopwatch wall,
        ColdStartOptions options,
        CancellationToken cancellationToken)
    {
        while (wall.Elapsed < options.Timeout)
        {
            Alive(app, "before it opened a port");

            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            try
            {
                await socket.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);

                return wall.Elapsed.TotalMilliseconds;
            }
            catch (SocketException)
            {
                await Task.Delay(options.PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            $"The process did not accept a connection on port {port} within {options.Timeout.TotalSeconds:F0} s.");
    }

    private static async Task<double> RespondingAsync(
        Process app,
        int port,
        Stopwatch wall,
        ColdStartOptions options,
        CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(
                string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}")),
            Timeout = options.Timeout,
        };

        var last = "no response at all";

        while (wall.Elapsed < options.Timeout)
        {
            Alive(app, "before it served the flow endpoint");

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, ColdStartOptions.FlowPath)
                {
                    Content = new StringContent(
                        ColdStartOptions.FlowBody, Encoding.UTF8, "application/json"),
                };

                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", ColdStartOptions.BearerToken);

                // Unique per attempt, so a retry is a new order rather than a replay the
                // idempotency rule can answer from a cache it filled while we were timing.
                request.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString("n"));

                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var elapsed = wall.Elapsed.TotalMilliseconds;

                if (response.IsSuccessStatusCode
                    && body.Contains(ColdStartOptions.ExpectedField, StringComparison.Ordinal))
                {
                    return elapsed;
                }

                // A 404 is what a moved route looks like, and it is not a slow start: retrying
                // until the timeout and then naming the status is how this rig refuses to
                // record a number for an endpoint that is not there.
                last = string.Create(
                    CultureInfo.InvariantCulture,
                    $"HTTP {(int)response.StatusCode} with body '{Clip(body)}'");
            }
            catch (HttpRequestException failure)
            {
                last = failure.Message;
            }

            await Task.Delay(options.PollInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"POST {ColdStartOptions.FlowPath} never answered with a success carrying " +
            $"'{ColdStartOptions.ExpectedField}' within {options.Timeout.TotalSeconds:F0} s. " +
            $"Last: {last}.");
    }

    private static void Alive(Process app, string what)
    {
        if (app.HasExited)
        {
            throw new InvalidOperationException($"The process exited with code {app.ExitCode} {what}.");
        }
    }

    private static long ResidentKb(int pid)
    {
        // Linux only, and read rather than computed: Process.WorkingSet64 answers for a
        // snapshot taken when the Process object refreshes, which is not the instant the
        // response arrived. Zero where /proc is not there, and the report says "not read"
        // rather than pretending to a figure.
        try
        {
            foreach (var line in File.ReadLines($"/proc/{pid}/status"))
            {
                if (!line.StartsWith("VmRSS:", StringComparison.Ordinal))
                {
                    continue;
                }

                var digits = line.AsSpan("VmRSS:".Length).Trim();
                var space = digits.IndexOf(' ');

                return long.TryParse(
                    space < 0 ? digits : digits[..space],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var kb)
                    ? kb
                    : 0;
            }
        }
        catch (Exception)
        {
            return 0;
        }

        return 0;
    }

    private static void Stop(Process app)
    {
        try
        {
            if (!app.HasExited)
            {
                app.Kill(entireProcessTree: true);
            }

            _ = app.WaitForExit(10_000);
        }
        catch (Exception failure)
        {
            Console.WriteLine($"    Could not stop pid {app.Id}: {failure.Message}");
        }
    }

    private static int FreePort()
    {
        // A port the kernel says is free, rather than a fixed one: the previous run's socket
        // can still be in TIME_WAIT, and a bind failure inside the sample looks from out here
        // exactly like a slow start.
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    private static async Task<string> LogAsync(Task<string> stdout, Task<string> stderr)
    {
        var both = Task.WhenAll(stdout, stderr);
        var completed = await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);

        if (completed != both)
        {
            return "    (the process is still writing; its output is not shown)";
        }

        var text = await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false);
        var builder = new StringBuilder("    --- what the process said ---").AppendLine();

        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(20))
        {
            _ = builder.AppendLine(CultureInfo.InvariantCulture, $"    {line.TrimEnd()}");
        }

        return builder.ToString();
    }

    private static string Clip(string body) =>
        body.Length <= 120 ? body.ReplaceLineEndings(" ") : body[..120].ReplaceLineEndings(" ") + "…";
}
