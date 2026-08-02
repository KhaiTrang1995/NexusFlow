using System.Diagnostics;
using FlowX.Observability;
using FlowX.Runtime;

namespace FlowX.Hosting;

/// <summary>
/// One sweep for instances a dead node left running, and the takeover of as many of them as
/// this node has room for.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The scan is the other half of a lease.</strong> Acquisition makes this node the
/// only writer for an instance it started; nothing about that finds an instance whose writer
/// went away. A node that dies mid-step leaves a <c>Running</c> row with its committed prefix
/// intact — which is exactly the state <see cref="IFlowJournal"/>'s exception-versus-error
/// distinction produces on purpose — and this is what looks for it.
/// </para>
/// <para>
/// <strong>Every node running this must not become a stampede, and five things stop it.</strong>
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <strong>The candidate set is filtered at the store.</strong> A node executing an instance
/// writes its row at every step boundary, so healthy instances fall out of
/// <see cref="AbandonedInstanceQuery.IdleBefore"/> without being fetched, rejected by the
/// lease store and thrown away. In steady state the query returns nothing and the sweep is
/// one indexed read.
/// </description>
/// </item>
/// <item>
/// <description>
/// <strong>The page is bounded and the node's appetite is smaller still.</strong> At most
/// <see cref="FlowXOptions.RecoveryScanBatchSize"/> candidates are asked for and at most
/// <see cref="FlowXOptions.MaxConcurrentRecoveries"/> are attempted, and no second page is
/// requested until those have finished. After an outage the backlog is unbounded; what one
/// node pulls from it is not.
/// </description>
/// </item>
/// <item>
/// <description>
/// <strong>Nodes start at different places in the same page.</strong> The batch is walked
/// from a random offset, so ten nodes handed the same oldest-first page do not all contend
/// for its first row and then its second. Without it, acquisition would still be correct and
/// nine tenths of every sweep would be wasted round trips.
/// </description>
/// </item>
/// <item>
/// <description>
/// <strong>Losing is free.</strong> <see cref="DurabilityErrors.LeaseHeld"/> is a skip: no
/// retry, no backoff, no error. The one node that wins an instance is the arbiter, and the
/// others move to the next candidate rather than queueing behind it.
/// </description>
/// </item>
/// <item>
/// <description>
/// <strong>The caller jitters the interval.</strong> Identical nodes on an identical timer
/// converge; <see cref="FlowRecoveryService"/> spreads them out. That belongs to the loop
/// rather than to the sweep, so this method stays deterministic enough to test.
/// </description>
/// </item>
/// </list>
/// <para>
/// <strong>A candidate this node cannot run is left alone.</strong> An instance is pinned to
/// the flow version it started with, so a node that does not carry that exact version skips
/// it without acquiring — taking a lease it could not use would deny the instance to a node
/// that can, for a whole TTL, every sweep.
/// </para>
/// </remarks>
public sealed class FlowRecoveryScan
{
    private readonly FlowHost _host;
    private readonly FlowCatalog _catalog;
    private readonly FlowDurability _durability;
    private readonly FlowXOptions _options;
    private readonly IClock _clock;

    /// <summary>Builds a scan over one host's journal, lease store and catalogue.</summary>
    /// <param name="host">Where a recovered instance is resumed, so it is counted and drained.</param>
    /// <param name="catalog">Which flows, at which versions, this node can run.</param>
    /// <param name="durability">The journal and lease store, and the index to scan.</param>
    /// <param name="options">The validated host options.</param>
    /// <param name="clock">The runtime's clock, so staleness is not measured against the wall.</param>
    public FlowRecoveryScan(
        FlowHost host,
        FlowCatalog catalog,
        FlowDurability durability,
        FlowXOptions options,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(durability);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        _host = host;
        _catalog = catalog;
        _durability = durability;
        _options = options;
        _clock = clock;
    }

    /// <summary>Whether this host is able to scan at all.</summary>
    /// <remarks>
    /// False when the journal cannot answer the query. That host still runs durable flows and
    /// still recovers <em>its own</em> instances when it restarts, through whatever restarts
    /// them; what it does not do is pick up another node's. A single-node deployment can live
    /// there quite reasonably.
    /// </remarks>
    public bool IsEnabled => _durability.CanScan;

    /// <summary>Runs one sweep and returns what it did.</summary>
    /// <param name="ct">Cancels the sweep, and every takeover it started.</param>
    /// <returns>
    /// The counts, and the store's error when the query itself failed. A sweep that found
    /// nothing is the ordinary result and not an error.
    /// </returns>
    /// <remarks>
    /// Awaits the instances it took over. Resuming is executing — a recovered saga runs to
    /// completion or to its deadline — so a sweep can last as long as a flow does, and that
    /// is the property that bounds concurrency without a second counter to keep correct.
    /// </remarks>
    public async ValueTask<RecoveryScanReport> RunOnceAsync(CancellationToken ct = default)
    {
        // The sweep's own span, so a resumed instance's flow span has a parent that says why
        // it started. Without it a recovered saga appears in a trace backend as a root with no
        // caller, which is indistinguishable from a request nobody can find — and "why did this
        // instance run at 03:14" is the first question an operator asks of one.
        using var span = FlowXTelemetry.Source.StartActivity("recovery scan", ActivityKind.Internal);

        if (_durability.RecoveryIndex is not { } index || _host.IsDraining)
        {
            return Tagged(span, RecoveryScanReport.Nothing);
        }

        var query = new AbandonedInstanceQuery
        {
            // A lease TTL into the past. An instance written to more recently either has a
            // live owner or is inside a single step outliving its own lease, and only
            // acquisition can settle the second.
            IdleBefore = _clock.UtcNow - _options.LeaseTtl,
            Limit = _options.RecoveryScanBatchSize,

            // Zero unless a deployment declared a share, and zero is the page this scan always
            // asked for. The cap belongs on the query and not on what is done with its answer:
            // the page is the oldest work in the table, so a tenant whose backlog is longer than
            // the page owns every row of it, and no scheduling applied afterwards can select a
            // candidate that was never fetched.
            PerTenantLimit = _options.Fairness.PerTenantScanShare,
        };

        var listed = await index.ListAbandonedAsync(query, ct).ConfigureAwait(false);

        if (listed.IsFailure)
        {
            return Tagged(span, RecoveryScanReport.Nothing with { Error = listed.Error });
        }

        var candidates = listed.Value;

        if (candidates.Count == 0)
        {
            return Tagged(span, RecoveryScanReport.Nothing);
        }

        var offset = Random.Shared.Next(candidates.Count);
        var capacity = _options.MaxConcurrentRecoveries;
        var order = Order(candidates, offset);

        List<Task<Attempt>>? takeovers = null;
        var examined = 0;
        var notRunnable = 0;

        for (var i = 0; i < candidates.Count && (takeovers?.Count ?? 0) < capacity; i++)
        {
            var candidate = candidates[order[i]];

            examined++;

            if (!_catalog.TryGet(candidate.FlowId, candidate.FlowVersion, out var registration))
            {
                notRunnable++;
                continue;
            }

            takeovers ??= new List<Task<Attempt>>(capacity);
            takeovers.Add(
                TakeOverAsync(candidate.InstanceId, registration, candidate.TenantId, ct));
        }

        if (takeovers is null)
        {
            return Tagged(
                span, RecoveryScanReport.Nothing with { Examined = examined, NotRunnable = notRunnable });
        }

        var attempts = await Task.WhenAll(takeovers).ConfigureAwait(false);

        var resumed = 0;
        var contended = 0;
        var failed = 0;

        foreach (var attempt in attempts)
        {
            switch (attempt)
            {
                case Attempt.Resumed:
                    resumed++;
                    break;
                case Attempt.Contended:
                    contended++;
                    break;
                default:
                    failed++;
                    break;
            }
        }

        return Tagged(span, new RecoveryScanReport
        {
            Examined = examined,
            Resumed = resumed,
            Contended = contended,
            NotRunnable = notRunnable,
            Failed = failed,
        });
    }

    /// <summary>
    /// Which order to spend this scan's slots in.
    /// </summary>
    /// <remarks>
    /// <strong>The rotation is not fairness and never was.</strong> Walking the page from a
    /// random row spreads a fleet of nodes across it, which is why it exists and why it is kept;
    /// what it produces is <em>proportional</em> share, so a tenant holding nine tenths of the
    /// page takes nine tenths of this node's slots and the tenant behind it waits on a backlog it
    /// did not create. <see cref="TenantFairShare"/> interleaves the page's tenants instead, and
    /// applies the same rotation to the tenant order so that the anti-stampede property survives
    /// the change.
    /// </remarks>
    private int[] Order(IReadOnlyList<AbandonedInstance> candidates, int offset) =>
        _options.Fairness.IsEnabled
            ? TenantFairShare.Order(
                candidates,
                static candidate => candidate.TenantId,
                _options.Fairness.WeightOf,
                offset)
            : Rotated(candidates.Count, offset);

    /// <summary>The order this scan always walked: the page, from a random row.</summary>
    private static int[] Rotated(int count, int offset)
    {
        var order = new int[count];

        for (var i = 0; i < count; i++)
        {
            order[i] = (i + offset) % count;
        }

        return order;
    }

    /// <summary>Puts what a sweep did onto its span, and hands the report back unchanged.</summary>
    /// <remarks>
    /// A function rather than a block before each <c>return</c>, because this method has five
    /// exits and four of them are "there was nothing to do" — the shape that ends up tagged on
    /// one path and silent on the others. The counts are span attributes rather than metrics on
    /// purpose: docs/12-Observability.md §3 specifies no recovery metric, and inventing one
    /// here would put a series into the frozen schema by the side door.
    /// </remarks>
    private static RecoveryScanReport Tagged(Activity? span, in RecoveryScanReport report)
    {
        if (span is not null)
        {
            span.SetTag("flowx.scan.examined", report.Examined);
            span.SetTag("flowx.scan.resumed", report.Resumed);
            span.SetTag("flowx.scan.contended", report.Contended);
            span.SetTag("flowx.scan.not_runnable", report.NotRunnable);
            span.SetTag("flowx.scan.failed", report.Failed);
        }

        return report;
    }

    /// <summary>
    /// One takeover, classified by whether this node became the writer — not by how the
    /// resumed flow ended.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A recovered saga that resumes and then fails its next step has been recovered: it
    /// reached a terminal state with a compensation history instead of sitting in
    /// <c>Running</c> for ever, which is the whole objective. Counting that as a scan failure
    /// would make the one metric an operator watches say "recovery is broken" every time a
    /// downstream system is.
    /// </para>
    /// <para>
    /// <strong>So has one that resumed as far as a suspension point.</strong> A node that died
    /// before a flow's <c>AwaitSignal</c> leaves a <c>Running</c> row this sweep does pick up,
    /// and finishing the takeover means running it to where it is actually waiting and
    /// recording that. It is not a success — nothing completed — and reading
    /// <c>result.Error</c> on it would be reading a null, which is why it is answered before
    /// the switch below rather than falling into it.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// <strong>The candidate's tenant is carried straight through to the resume</strong>, so a
    /// takeover runs against a journal bound to the instance's own tenant rather than an
    /// unscoped one. The value is on the row this sweep already fetched — <c>AbandonedInstance</c>
    /// has carried it since it was written — which is why isolating a recovery costs no extra
    /// query. A scan is node-wide platform work and legitimately recovers every tenant's
    /// instances; what it must not do is run one tenant's instance on a connection that can see
    /// another's rows, and this is what stops it.
    /// </remarks>
    private async Task<Attempt> TakeOverAsync(
        Guid instanceId,
        FlowRegistration registration,
        string? tenantId,
        CancellationToken ct)
    {
        var result = await _host.ResumeAsync(instanceId, registration, tenantId, ct)
            .ConfigureAwait(false);

        if (result.IsSuccess || result.IsSuspended)
        {
            return Attempt.Resumed;
        }

        return result.Error!.Code switch
        {
            DurabilityErrors.LeaseHeldCode => Attempt.Contended,

            // The instance moved on between the query and the acquisition: another node
            // finished it, or took it and fenced this one out. Neither is this node's
            // problem and neither is worth an alert.
            DurabilityErrors.InstanceNotFoundCode
                or DurabilityErrors.InstanceTerminalCode
                or DurabilityErrors.FencedOutCode
                or DurabilityErrors.LeaseLostCode => Attempt.Contended,

            HostDrainingCode => Attempt.Contended,

            _ => Attempt.Resumed,
        };
    }

    /// <summary>The refusal a draining host gives, matched by code rather than by identity.</summary>
    private const string HostDrainingCode = "host.draining";

    /// <summary>What became of one candidate.</summary>
    private enum Attempt
    {
        Resumed,
        Contended,
        Failed,
    }
}

/// <summary>What one sweep found and did.</summary>
/// <remarks>
/// Counts rather than a list of instance ids. A report is what a metric is derived from and
/// what a test asserts on; carrying the ids would make the type grow with the backlog and
/// invite it into a log line where an instance id per recovered flow is exactly the volume
/// nobody wants during an incident.
/// </remarks>
public sealed record RecoveryScanReport
{
    /// <summary>A sweep that had nothing to do, and the base for one that did.</summary>
    public static RecoveryScanReport Nothing { get; } = new();

    /// <summary>How many candidates were considered.</summary>
    public int Examined { get; init; }

    /// <summary>How many this node took over and ran.</summary>
    public int Resumed { get; init; }

    /// <summary>
    /// How many another node had already taken, or had already finished.
    /// </summary>
    /// <remarks>
    /// The number to watch when a fleet grows. It rising in step with
    /// <see cref="Examined"/> while <see cref="Resumed"/> stays flat is what a stampede looks
    /// like from the outside — every node doing the work of finding what one node then does.
    /// </remarks>
    public int Contended { get; init; }

    /// <summary>
    /// How many were pinned to a flow version this node does not carry.
    /// </summary>
    /// <remarks>
    /// Expected during a rolling update and expected to fall to zero after it. Sustained
    /// non-zero means instances that no deployed node can finish, which is an operator
    /// question — not something a scan should paper over by resuming them against the wrong
    /// plan.
    /// </remarks>
    public int NotRunnable { get; init; }

    /// <summary>How many takeovers the stores refused for a reason worth looking at.</summary>
    public int Failed { get; init; }

    /// <summary>Why the query itself failed, when it did.</summary>
    public Error? Error { get; init; }
}
