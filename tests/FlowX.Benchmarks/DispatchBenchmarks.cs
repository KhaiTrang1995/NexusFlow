using BenchmarkDotNet.Attributes;

namespace FlowX.Benchmarks;

/// <summary>
/// Budget <strong>B3 — capability dispatch, p99 ≤ 150 ns</strong>.
/// </summary>
/// <remarks>
/// <para>
/// The generator does not exist yet (WP-5), so this measures the <em>floor</em>: what
/// dispatch costs when the call site is known at compile time, and what it costs
/// through the shapes a runtime might otherwise reach for. The generated code has to
/// land near <c>Direct</c>; if it lands near <c>Reflection</c>,
/// <a href="../../docs/adr/ADR-0002-compile-time-orchestration.md">ADR-0002</a> has
/// no argument left.
/// </para>
/// <para>
/// Establishing the floor before the engine exists is the point of scheduling WP-3
/// ahead of WP-4. A budget that only becomes measurable after the thing it
/// constrains is built is a budget that gets renegotiated rather than met.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[HideColumns("Job", "Error", "StdDev", "Median", "RatioSD")]
public class DispatchBenchmarks
{
    private readonly EchoCapability _concrete = new();
    private ICapability<int, int> _viaInterface = null!;
    private Func<int, CapabilityContext, CancellationToken, ValueTask<Result<int>>> _viaDelegate = null!;
    private System.Reflection.MethodInfo _viaReflection = null!;
    private object[] _reflectionArgs = null!;
    private CapabilityContext _context = null!;

    [GlobalSetup]
    public void Setup()
    {
        _viaInterface = _concrete;
        _viaDelegate = _concrete.ExecuteAsync;
        _viaReflection = typeof(EchoCapability).GetMethod(nameof(EchoCapability.ExecuteAsync))!;
        _context = new StubContext();
        _reflectionArgs = [1, _context, CancellationToken.None];
    }

    /// <summary>The floor. A direct call on a sealed type — what generated code should approach.</summary>
    [Benchmark(Baseline = true, Description = "Direct call (sealed type)")]
    public async ValueTask<int> Direct()
    {
        var result = await _concrete.ExecuteAsync(1, _context, CancellationToken.None).ConfigureAwait(false);
        return result.Value;
    }

    /// <summary>Interface dispatch — one virtual call. What a hand-written pipeline costs.</summary>
    [Benchmark(Description = "Interface dispatch")]
    public async ValueTask<int> ViaInterface()
    {
        var result = await _viaInterface.ExecuteAsync(1, _context, CancellationToken.None).ConfigureAwait(false);
        return result.Value;
    }

    /// <summary>Cached delegate — what a registration-table runtime typically does.</summary>
    [Benchmark(Description = "Cached delegate")]
    public async ValueTask<int> ViaDelegate()
    {
        var result = await _viaDelegate(1, _context, CancellationToken.None).ConfigureAwait(false);
        return result.Value;
    }

    /// <summary>
    /// Reflection with a cached <see cref="System.Reflection.MethodInfo"/> — the
    /// generous version of what a reflection-based mediator does, since it skips
    /// the lookup. The honest comparison is still an order of magnitude worse.
    /// </summary>
    [Benchmark(Description = "Reflection (cached MethodInfo)")]
    public async ValueTask<int> ViaReflection()
    {
        var task = (ValueTask<Result<int>>)_viaReflection.Invoke(_concrete, _reflectionArgs)!;
        var result = await task.ConfigureAwait(false);
        return result.Value;
    }
}

/// <summary>A capability that does nothing, so the measurement is dispatch and not work.</summary>
public sealed class EchoCapability : ICapability<int, int>
{
    /// <inheritdoc />
    public ValueTask<Result<int>> ExecuteAsync(int input, CapabilityContext ctx, CancellationToken ct)
        => ValueTask.FromResult(Result.Ok(input));
}

/// <summary>A context with fixed values, so no clock or RNG cost enters the measurement.</summary>
public sealed class StubContext : CapabilityContext
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    /// <inheritdoc />
    public override string CorrelationId => "bench";

    /// <inheritdoc />
    public override string? FlowInstanceId => null;

    /// <inheritdoc />
    public override string CapabilityId => "bench.echo";

    /// <inheritdoc />
    public override string? CompensatingFor => null;

    /// <inheritdoc />
    public override string? TenantId => null;

    /// <inheritdoc />
    public override string IdempotencyKey => "bench-key";

    /// <inheritdoc />
    public override DateTimeOffset Deadline => Now.AddMinutes(1);

    /// <inheritdoc />
    public override DateTimeOffset UtcNow => Now;

    /// <inheritdoc />
    public override Guid NewId() => Guid.Empty;

    /// <inheritdoc />
    public override Random Random { get; } = new(0);
}
