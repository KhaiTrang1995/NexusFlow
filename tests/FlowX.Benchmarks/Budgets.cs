namespace FlowX.Benchmarks;

/// <summary>
/// The performance budgets from
/// <a href="../../docs/14-Performance.md">14-Performance</a>, as code.
/// </summary>
/// <remarks>
/// <para>
/// Duplicated from the document deliberately: a budget that lives only in prose is
/// a budget nothing can fail against. The budget-checking script reads these values
/// from the benchmark output, so this table and the document must agree — and when
/// they disagree, the document is the one that gets corrected, because it is the
/// one a human reads before deciding a number is "close enough".
/// </para>
/// <para>
/// Only the budgets that are <em>measurable today</em> appear here. The rest are
/// listed as unmeasurable with the work package that makes them real, so nobody has
/// to guess whether a missing number means "passing" or "never ran".
/// </para>
/// </remarks>
public static class Budgets
{
    /// <summary>B3 — capability dispatch, p99. The floor the generator must approach.</summary>
    public const double CapabilityDispatchP99Nanoseconds = 150;

    /// <summary>B1 — 4-step ephemeral flow overhead, p50.</summary>
    public const double FlowOverheadP50Microseconds = 1.5;

    /// <summary>B1 — 4-step ephemeral flow overhead, p99. The kill criterion for P0.</summary>
    public const double FlowOverheadP99Microseconds = 5.0;

    /// <summary>
    /// The tolerance a benchmark may drift from its committed baseline before CI
    /// fails. Five per cent is tight enough to catch a real regression and loose
    /// enough to survive a noisy shared runner.
    /// </summary>
    public const double RegressionTolerancePercent = 5.0;
}
