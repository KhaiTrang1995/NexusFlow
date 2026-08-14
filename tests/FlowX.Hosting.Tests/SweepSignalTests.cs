using FlowX.Conformance.InMemory;
using FlowX.Hosting;
using FlowX.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace FlowX.Hosting.Tests;

/// <summary>
/// What an <see cref="ISweepSignal"/> changes about the two sweeps that wait, and what it must
/// not change about a host that has none.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The half worth asserting hardest is the absent one.</strong> Every deployment running
/// today registers no signal, and an accelerator that altered their timing — an extra pass at
/// startup, a wait that no longer honours the interval, a resolution that throws when the service
/// is missing — would be a behaviour change delivered to people who did not ask for one. The rest
/// of this assembly is the standing evidence for that, because it runs unmodified; these two say
/// the same thing about the loops themselves rather than about the passes.
/// </para>
/// <para>
/// <strong>The accelerated half is asserted by a contradiction rather than by a stopwatch.</strong>
/// The interval is set to thirty seconds and the test waits ten: a pass that happens at all could
/// not have come from the interval, so there is no threshold to tune and no way for a slow machine
/// to turn the assertion into a coin toss.
/// </para>
/// <para>
/// The signal here is a double. The real one is PostgreSQL's <c>LISTEN</c>, asserted against a
/// real server in <c>tests/FlowX.Postgres.Tests/SweepSignalTests</c>.
/// </para>
/// </remarks>
public sealed class SweepSignalTests
{
    /// <summary>Long enough that a pass inside it cannot have come from the interval.</summary>
    private static readonly TimeSpan Unreachable = TimeSpan.FromSeconds(30);

    /// <summary>How long a test waits for a pass before calling it a failure.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A host with no signal registered sweeps on its interval, as it always has.</summary>
    [Fact]
    public async Task WithNoSignalTheTimerSweepRunsOnItsInterval()
    {
        var index = new CountingTimerIndex();

        using var host = Build(index, feed: null, signal: null, TimeSpan.FromMilliseconds(50));

        host.Services.GetService<ISweepSignal>().ShouldBeNull(
            "nothing registered one, and an optional service the host invented for itself " +
            "would be an accelerator no deployment could decline.");

        await host.StartAsync(Cancellation);

        await index.Swept.WaitAsync(Patience, Cancellation);

        await host.StopAsync(Cancellation);
    }

    /// <summary>A wake ends the wait, on an interval that could not have.</summary>
    [Fact]
    public async Task ASignalWakesTheTimerSweepBeforeItsInterval()
    {
        var index = new CountingTimerIndex();
        var signal = new WakingSignal();

        using var host = Build(index, feed: null, signal, Unreachable);

        await host.StartAsync(Cancellation);

        await index.Swept.WaitAsync(Patience, Cancellation);

        await host.StopAsync(Cancellation);

        signal.Waits.ShouldContain(
            wait => wait.Sweep == SweepKind.Timer,
            "the timer loop asked the signal rather than Task.Delay.");

        signal.Waits
            .Where(static wait => wait.Sweep == SweepKind.Timer)
            .ShouldAllBe(
                wait => wait.Interval >= Unreachable * 0.75 && wait.Interval <= Unreachable * 1.25,
                "the configured interval still reaches the signal, jittered, because it is the " +
                "backstop that makes a missed wake a latency cost rather than a lost timer.");
    }

    /// <summary>The change loop with no signal reads its feed on its interval, as it always has.</summary>
    [Fact]
    public async Task WithNoSignalTheChangeSweepRunsOnItsInterval()
    {
        var feed = new CountingChangeFeed();

        using var host = Build(index: null, feed, signal: null, TimeSpan.FromMilliseconds(50));

        await host.StartAsync(Cancellation);

        await feed.Read.WaitAsync(Patience, Cancellation);

        await host.StopAsync(Cancellation);
    }

    /// <summary>And a wake ends that wait too.</summary>
    [Fact]
    public async Task ASignalWakesTheChangeSweepBeforeItsInterval()
    {
        var feed = new CountingChangeFeed();
        var signal = new WakingSignal();

        using var host = Build(index: null, feed, signal, Unreachable);

        await host.StartAsync(Cancellation);

        await feed.Read.WaitAsync(Patience, Cancellation);

        await host.StopAsync(Cancellation);

        signal.Waits.ShouldContain(
            wait => wait.Sweep == SweepKind.Change,
            "the change loop asked the signal rather than Task.Delay.");
    }

    /// <summary>
    /// A host wired the way a deployment wires one: real stores, one sweep observable.
    /// </summary>
    /// <remarks>
    /// Through the container rather than by constructing the services, because what is under test
    /// is partly the registration — that the signal is resolved optionally, and that a host
    /// without one is built exactly as it was before the service type existed.
    /// </remarks>
    private static IHost Build(
        CountingTimerIndex? index, CountingChangeFeed? feed, ISweepSignal? signal, TimeSpan interval)
    {
        return new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddFlowX(options =>
                {
                    options.ApplicationName = "Sweeps";
                    options.NodeName = "node";
                    options.TimerScanInterval = interval;
                    options.ChangeScanInterval = interval;
                });

                services.AddSingleton<IFlowJournal>(new InMemoryFlowJournal());
                services.AddSingleton<ILeaseStore>(new InMemoryLeaseStore());

                if (index is not null)
                {
                    services.AddSingleton<ITimerIndex>(index);
                }

                if (feed is not null)
                {
                    services.AddSingleton<IChangeFeed>(feed);
                    services.AddSingleton(Subscribed());
                }

                if (signal is not null)
                {
                    services.AddSingleton(signal);
                }
            })
            .Build();
    }

    /// <summary>One registered change subscription, which is what makes the change sweep run.</summary>
    private static FlowChangeCatalog Subscribed() => new FlowChangeCatalog().Add(
        new ChangeSubscription("orders.project", "1.0.0", "order.placed", "projection"),
        ExecutionPlan.Create(
            FlowDescriptor.Create(
                "orders.project", "1.0.0", ExecutionProfile.Durable, TimeSpan.FromMinutes(5)),
            StepGraph.Create(
                [StepNode.ForCapability(
                    0,
                    CapabilityDescriptor.Create("orders.project", "1.0.0", isIdempotent: true))])),
        new UnusedDispatcher());

    /// <summary>A signal that wakes each sweep once and then behaves like the interval.</summary>
    /// <remarks>
    /// Once, because a wake that never stopped arriving would be a sweep loop with no pause in
    /// it — the thing <c>FlowXOptions</c> refuses a zero interval for — and the test would be
    /// measuring how fast the machine spins rather than whether the wake arrived.
    /// </remarks>
    private sealed class WakingSignal : ISweepSignal
    {
        private readonly Lock _sync = new();
        private readonly List<Wait> _waits = [];
        private readonly HashSet<SweepKind> _woken = [];

        public IReadOnlyList<Wait> Waits
        {
            get
            {
                lock (_sync)
                {
                    return [.. _waits];
                }
            }
        }

        public Task WaitAsync(
            SweepKind sweep, TimeSpan interval, CancellationToken cancellationToken)
        {
            bool first;

            lock (_sync)
            {
                _waits.Add(new Wait(sweep, interval));
                first = _woken.Add(sweep);
            }

            return first ? Task.CompletedTask : Task.Delay(interval, cancellationToken);
        }

        internal readonly record struct Wait(SweepKind Sweep, TimeSpan Interval);
    }

    /// <summary>A timer index that answers nothing and says when it was asked.</summary>
    private sealed class CountingTimerIndex : ITimerIndex
    {
        private readonly TaskCompletionSource _swept =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes on the first sweep, which is the whole observation here.</summary>
        public Task Swept => _swept.Task;

        public ValueTask<Result<IReadOnlyList<DueInstance>>> ListDueAsync(
            DueInstanceQuery query, CancellationToken cancellationToken)
        {
            _swept.TrySetResult();

            return ValueTask.FromResult(Result.Ok<IReadOnlyList<DueInstance>>([]));
        }
    }

    /// <summary>A feed with nothing in it that says when it was read.</summary>
    private sealed class CountingChangeFeed : IChangeFeed
    {
        private readonly TaskCompletionSource _read =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes on the first pass.</summary>
        public Task Read => _read.Task;

        public string Feed => "counting";

        public ValueTask<Result<IReadOnlyList<ObservedChange>>> ReadAsync(
            ChangeSubscription subscription, int max, CancellationToken cancellationToken)
        {
            _read.TrySetResult();

            return ValueTask.FromResult(Result.Ok<IReadOnlyList<ObservedChange>>([]));
        }

        public ValueTask<Result<bool>> CommitAsync(
            ChangeSubscription subscription,
            ChangePosition position,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Ok(true));
    }

    /// <summary>
    /// A dispatcher for a subscription whose feed never offers anything.
    /// </summary>
    /// <remarks>
    /// Every member throws, which is the assertion: a pass over an empty feed must start no flow,
    /// so anything reaching this double is a defect rather than a gap in the double.
    /// </remarks>
    private sealed class UnusedDispatcher : IStepDispatcher
    {
        private const string Never = "This subscription's feed offers nothing, so no flow runs.";

        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct) =>
            throw new NotSupportedException(Never);

        public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct) =>
            throw new NotSupportedException(Never);

        public bool Evaluate(int stepIndex, FlowContext ctx) => throw new NotSupportedException(Never);

        public int Select(int stepIndex, FlowContext ctx) => throw new NotSupportedException(Never);

        public IterationSource BeginIteration(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException(Never);

        public FlowContext EnterIteration(
            int stepIndex, in IterationSource source, int iteration, FlowContext ctx) =>
            throw new NotSupportedException(Never);
    }
}
