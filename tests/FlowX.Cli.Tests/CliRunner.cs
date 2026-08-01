using Xunit;

namespace FlowX.Cli.Tests;

/// <summary>
/// Runs the tool in-process with its console captured.
/// </summary>
/// <remarks>
/// <para>
/// <c>Program.Main</c> writes to <see cref="Console.Out"/> and <see cref="Console.Error"/>,
/// which are process-wide. Every class that uses this therefore belongs to
/// <see cref="CliConsoleGroup"/>, or two classes running in parallel capture each
/// other's output and fail in a way that looks like a defect in the tool.
/// </para>
/// </remarks>
internal static class CliRunner
{
    /// <summary>Runs the tool and returns what it wrote and what it exited with.</summary>
    /// <param name="args">The command line, without the executable name.</param>
    /// <returns>The exit code, stdout and stderr.</returns>
    public static (int ExitCode, string Out, string Error) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);

            return (Program.Main(args), stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }

    /// <summary>Resolves a repository-relative directory from the test's output folder.</summary>
    /// <param name="relativePath">A path such as <c>src/FlowX.Cli</c>.</param>
    /// <returns>The absolute path.</returns>
    /// <exception cref="DirectoryNotFoundException">No ancestor contains it.</exception>
    public static string RepositoryPath(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);

            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate '{relativePath}' from {AppContext.BaseDirectory}.");
    }
}

/// <summary>
/// Every class that captures the console or mutates the environment.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two process-wide resources, one collection.</strong> <see cref="CliRunner"/>
/// redirects <see cref="Console.Out"/>, and the store tests set and clear
/// <c>FLOWX_POSTGRES_CONNECTION</c> to prove a verb does not need it. xUnit runs test
/// classes in parallel, so without this every one of those is a race — and the failures it
/// produces are the worst kind, because they are intermittent and they look like defects in
/// the tool rather than in the harness.
/// </para>
/// <para>
/// <c>ProgramTests</c> was safe alone and is not safe alongside the <c>replay</c> classes,
/// which is why it joins the collection in the same commit that adds them.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class CliConsoleGroup
{
    /// <summary>The collection's name.</summary>
    public const string Name = "cli-console";

    private CliConsoleGroup()
    {
        // Never constructed. xUnit reads the attribute off the type; the type itself is
        // only a place to hang the name so the three classes cannot drift onto two spellings.
    }
}
