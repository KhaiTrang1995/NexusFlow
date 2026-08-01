using FlowX.Chaos;

// The QR2 chaos rig. See docs/benchmarks/QR2-chaos.md for what it measures and why it is
// shaped the way it is, and scripts/run-chaos-qr2.sh for how to run it.
//
// It is not part of the ordinary test suite. It SIGKILLs operating-system processes and takes
// minutes, so it is gated on FLOWX_CHAOS the way tests/FlowX.Postgres.Tests is gated on
// FLOWX_POSTGRES_CONNECTION — skipping with a reason when nobody asked for it, and failing
// rather than skipping when somebody did and the database is not there.
var gate = ChaosDatabase.Gate();

if (gate is { } refusal)
{
    await (refusal.Skipped ? Console.Out : Console.Error)
        .WriteLineAsync(refusal.Reason)
        .ConfigureAwait(false);

    return refusal.Skipped ? ChaosDatabase.SkippedExitCode : 1;
}

ChaosOptions options;

try
{
    options = ChaosOptions.Parse(args, ChaosDatabase.ConnectionString!);
}
catch (ArgumentException failure)
{
    await Console.Error.WriteLineAsync(failure.Message).ConfigureAwait(false);

    return 2;
}

if (!ProcessKill.IsSupported)
{
    await Console.Error
        .WriteLineAsync(
            "This platform has no SIGKILL, so this rig cannot kill anything and would " +
            "certify QR2 on a simulation. Refusing to run.")
        .ConfigureAwait(false);

    return 3;
}

using var lifetime = new CancellationTokenSource();

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    lifetime.Cancel();
};

try
{
    return options.Role switch
    {
        ChaosRole.Worker => await Worker.RunAsync(options, lifetime.Token).ConfigureAwait(false),
        ChaosRole.Recovery => await RecoveryNode.RunAsync(options, lifetime.Token).ConfigureAwait(false),
        _ => await Coordinator.RunAsync(options, lifetime.Token).ConfigureAwait(false),
    };
}
catch (OperationCanceledException)
{
    await Console.Error.WriteLineAsync("Cancelled.").ConfigureAwait(false);

    return 130;
}
catch (Exception failure)
{
    await Console.Error.WriteLineAsync($"{options.Role} failed: {failure}").ConfigureAwait(false);

    return 4;
}
