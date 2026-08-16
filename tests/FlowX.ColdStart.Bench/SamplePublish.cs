using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace FlowX.ColdStart.Bench;

/// <summary>What the publish produced, and by which command.</summary>
/// <param name="Command">The command line, verbatim, so the document can quote it.</param>
/// <param name="BinaryPath">The executable the rig starts.</param>
/// <param name="BinaryBytes">Its size on disk.</param>
/// <param name="DirectoryBytes">The whole publish output, symbols and documentation included.</param>
/// <param name="Seconds">How long the publish took.</param>
internal sealed record PublishResult(
    string Command,
    string BinaryPath,
    long BinaryBytes,
    long DirectoryBytes,
    double Seconds);

/// <summary>
/// Produces the binary under measurement, with the AOT job's own publish command.
/// </summary>
/// <remarks>
/// The rig publishes rather than accepting whatever is lying in <c>bin/</c>: a cold-start
/// figure recorded against an unknown binary is a figure about nothing. The one thing added
/// to <c>ci.yml</c>'s command is <c>--output</c>, so a rig run cannot overwrite the artifact
/// the AOT smoke test asserts.
/// </remarks>
internal static class SamplePublish
{
    /// <summary>Publishes the sample and returns what it produced.</summary>
    /// <param name="options">The run's options.</param>
    /// <param name="cancellationToken">Cancels the publish.</param>
    /// <returns>The publish result.</returns>
    /// <exception cref="InvalidOperationException">The publish failed, or produced no binary.</exception>
    public static async Task<PublishResult> RunAsync(
        ColdStartOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var arguments = Arguments(options);
        var host = DotnetHost();
        var command = "dotnet " + string.Join(' ', arguments);

        if (options.SkipPublish)
        {
            Console.WriteLine($"==> Reusing the publish in {options.PublishDirectory} (--no-publish).");
        }
        else
        {
            Console.WriteLine($"==> Publishing: {command}");
        }

        var wall = Stopwatch.StartNew();

        if (!options.SkipPublish)
        {
            var startInfo = new ProcessStartInfo(host)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var publish = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start `dotnet publish`.");

            var output = publish.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = publish.StandardError.ReadToEndAsync(cancellationToken);

            await publish.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var log = await output.ConfigureAwait(false) + await error.ConfigureAwait(false);

            if (publish.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"The publish failed with exit code {publish.ExitCode}. Nothing was measured." +
                    Environment.NewLine + Tail(log));
            }
        }

        wall.Stop();

        var binary = new FileInfo(options.BinaryPath);

        if (!binary.Exists)
        {
            throw new InvalidOperationException(
                $"The publish reported success and produced no executable at {binary.FullName}. " +
                "A cold start of nothing is not a measurement.");
        }

        return new PublishResult(
            // --no-publish measures a binary this run did not produce — the AOT job in
            // ci.yml hands the rig its own publish that way — and the results document is
            // read as evidence about one specific build. Recording the command that WOULD
            // have produced it would attribute somebody else's binary to a run that never
            // compiled anything.
            options.SkipPublish
                ? $"(none — reused the publish already in {options.PublishDirectory})"
                : command,

            // Relative, because this string is copied into a committed results document and
            // an absolute path there says more about whose machine ran it than about what
            // was measured.
            options.BinaryPath,
            binary.Length,
            DirectoryBytes(binary.Directory!),
            Math.Round(wall.Elapsed.TotalSeconds, 1, MidpointRounding.AwayFromZero));
    }

    private static string DotnetHost()
    {
        // An absolute path, because a rig that inherits whichever `dotnet` a PATH happens to
        // resolve can publish with one SDK and be read as having used another. DOTNET_HOST_PATH
        // is what the SDK sets for exactly this; the walk is the fallback for a shell that
        // does not.
        var declared = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");

        if (!string.IsNullOrEmpty(declared) && File.Exists(declared))
        {
            return declared;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, "dotnet");

            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new InvalidOperationException(
            "No `dotnet` on DOTNET_HOST_PATH or PATH, so the sample cannot be published and " +
            "nothing can be measured.");
    }

    private static List<string> Arguments(ColdStartOptions options)
    {
        // ci.yml's NativeAOT publish, verbatim, plus an output directory. PublishAot is
        // deliberately NOT passed here either: the sample sets it in its own PropertyGroup,
        // and a global property would push it into the two netstandard2.0 analyzer projects
        // and fail the publish with NETSDK1207 — see the comment on the `aot` job.
        var arguments = new List<string>
        {
            "publish",
            ColdStartOptions.SampleProject,
            "--configuration",
            "Release",
            "--runtime",
            "linux-x64",
            "--self-contained",
            "/p:TrimmerSingleWarn=false",
            "--output",
            options.PublishDirectory,
        };

        if (options.Mode == PublishMode.ReadyToRun)
        {
            // The fallback arm, and it turns the sample's own declaration off to get there.
            // Whatever it measures, it is not criterion V5.
            arguments.Add("/p:PublishAot=false");
            arguments.Add("/p:PublishReadyToRun=true");
        }

        return arguments;
    }

    private static long DirectoryBytes(DirectoryInfo directory) =>
        directory.GetFiles("*", SearchOption.AllDirectories).Sum(static file => file.Length);

    private static string Tail(string log)
    {
        var lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var builder = new StringBuilder();

        foreach (var line in lines.TakeLast(30))
        {
            _ = builder.AppendLine(CultureInfo.InvariantCulture, $"    {line.TrimEnd()}");
        }

        return builder.ToString();
    }
}
