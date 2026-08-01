using System.Diagnostics;

namespace FlowX.Chaos;

/// <summary>
/// A real death: <c>SIGKILL</c>, delivered by the kernel to this process, at an instruction
/// this rig chose.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Everything else that calls itself a kill in this repository is not one.</strong>
/// <c>PostgresRecoveryHostTests</c> drops a lease and moves a row into the past;
/// <c>DurableHostTests</c> stops renewing. Both leave the process alive, its finalizers
/// runnable, its sockets open and its <c>finally</c> blocks pending. A durability claim tested
/// that way is a claim about a cancellation token.
/// </para>
/// <para>
/// <strong>Why the process kills itself rather than being killed by the coordinator.</strong>
/// The kill has to land in a window measured in microseconds — after the effect has been
/// applied and before the engine's commit — and a parent process signalling a child cannot aim
/// that finely. Self-signalling takes the same kernel path: on Unix
/// <see cref="Process.Kill()"/> is <c>kill(2)</c> with <c>SIGKILL</c>, which cannot be caught,
/// blocked or ignored. No managed cleanup runs, the lease is never released, the pooled
/// PostgreSQL connections are never returned, and the journal keeps whatever prefix it had.
/// </para>
/// <para>
/// <strong>The claim is checked rather than asserted.</strong> The coordinator records the
/// exit code of every worker it spawned, and a process killed by signal <em>n</em> is reported
/// by the shell and by <see cref="Process.ExitCode"/> as <c>128 + n</c>. A run whose workers
/// exited 0 killed nothing, and <c>scripts/check-chaos-qr2.py</c> refuses such a run rather
/// than passing it.
/// </para>
/// <para>
/// <strong>Not <see cref="Environment.FailFast(string)"/> and not <c>Exit</c>.</strong> The
/// first writes a crash dump and runs the runtime's own shutdown; the second runs finalizers
/// and <c>ProcessExit</c> handlers, and would give Npgsql the chance to close its connections
/// tidily. Neither is what an orchestrator's <c>SIGKILL</c>, an OOM killer or a lost node does.
/// </para>
/// </remarks>
internal static class ProcessKill
{
    /// <summary>The exit code the operating system reports for a process killed by SIGKILL.</summary>
    public const int SigkillExitCode = 137;

    /// <summary>Whether this platform can be killed the way the rig needs.</summary>
    public static bool IsSupported => !OperatingSystem.IsWindows();

    /// <summary>Kills this process immediately. Does not return.</summary>
    /// <exception cref="PlatformNotSupportedException">
    /// The platform has no <c>SIGKILL</c>, so the rig would be simulating a death rather than
    /// causing one — which this package exists to stop happening.
    /// </exception>
    public static void KillSelf()
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException(
                "This rig has no way to SIGKILL itself on this platform. It refuses to " +
                "substitute a graceful stop, because a rig that kills nothing lets QR2 be " +
                "retired falsely.");
        }

        using var self = Process.GetCurrentProcess();

        self.Kill();

        // Unreachable: the signal is delivered before this thread is scheduled again.
        throw new InvalidOperationException("SIGKILL returned, so this process is not dead.");
    }
}
