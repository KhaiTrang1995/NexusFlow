using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using FlowX;

namespace Scheduler;

/// <summary>Source-generated serialisation for every contract that crosses a boundary.</summary>
/// <remarks>
/// <para>
/// <strong>This list is a build requirement, not a convention.</strong> The flow is
/// <c>Durable</c>, so each step's result and the state bag after it are journaled, and every
/// contract in the bag needs generated metadata because the journal writes through a
/// <c>JsonSerializerContext</c> and never by reflection — which is what keeps a durable flow
/// trim- and NativeAOT-safe. <c>FLOWX1006</c> names anything missing, so the list is maintained
/// by the compiler rather than by hand.
/// </para>
/// <para>
/// <see cref="ScheduledFire"/> is here because it is the flow's <em>input</em>, journalled on
/// <c>flow_instance.input</c>: without it the row that records which occurrence fired would be
/// null, and the occurrence is the only thing distinguishing one night's instance from the
/// next. <see cref="ReconciliationCompleted"/> is here because an <c>Emit</c> body is written
/// the same way, and <c>FLOWX1024</c> reports an event contract no context declares.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ScheduledFire))]
[JsonSerializable(typeof(LedgerSnapshot))]
[JsonSerializable(typeof(BankStatement))]
[JsonSerializable(typeof(Reconcilable))]
[JsonSerializable(typeof(ReferenceMatches))]
[JsonSerializable(typeof(HeuristicMatches))]
[JsonSerializable(typeof(MatchSet))]
[JsonSerializable(typeof(ReconciliationReport))]
[JsonSerializable(typeof(ReconciliationCompleted))]
internal sealed partial class SchedulerJsonContext : JsonSerializerContext;

/// <summary>Where the day's ledger entries are read from.</summary>
public interface ILedger
{
    /// <summary>Everything booked in the day that ended at <paramref name="closedAt"/>.</summary>
    /// <param name="tenantId">Whose books, or null on a deployment that serves one.</param>
    /// <param name="closedAt">The end of the day, which is the occurrence.</param>
    /// <returns>The entries, oldest first.</returns>
    IReadOnlyList<LedgerEntry> EntriesFor(string? tenantId, DateTimeOffset closedAt);
}

/// <summary>Where the bank statement comes from. The slow, flaky side.</summary>
public interface IBank
{
    /// <summary>The statement for the day that ended at <paramref name="closedAt"/>.</summary>
    /// <param name="tenantId">Whose account, or null on a deployment that serves one.</param>
    /// <param name="closedAt">The end of the day, which is the occurrence.</param>
    /// <param name="cancellationToken">Cancels the call, and is linked to the flow's deadline.</param>
    /// <returns>The statement, or the reason there is none.</returns>
    ValueTask<Result<IReadOnlyList<StatementLine>>> StatementAsync(
        string? tenantId, DateTimeOffset closedAt, CancellationToken cancellationToken);
}

/// <summary>Where a finished report goes. Whatever an operator actually reads.</summary>
public interface IReportDesk
{
    /// <summary>Files one night's report.</summary>
    /// <param name="report">What the reconciliation concluded.</param>
    void File(ReconciliationReport report);

    /// <summary>Every report filed so far, in the order they were filed.</summary>
    IReadOnlyList<ReconciliationReport> Filed { get; }
}

/// <summary>
/// A ledger in memory, seeded per tenant. In a real deployment this is a database.
/// </summary>
/// <remarks>
/// Keyed by tenant because the schedule fans out: each tenant's firing is its own instance with
/// its own journal row, so this is what the isolation is <em>for</em> rather than a detail of
/// the fixture. An unknown tenant has an empty day, which is an ordinary answer.
/// </remarks>
internal sealed class InMemoryLedger : ILedger
{
    private readonly ConcurrentDictionary<string, List<LedgerEntry>> _books = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public IReadOnlyList<LedgerEntry> EntriesFor(string? tenantId, DateTimeOffset closedAt) =>
        _books.TryGetValue(Key(tenantId), out var entries)
            ? [.. entries.Where(entry => entry.BookedAt < closedAt).OrderBy(entry => entry.BookedAt)]
            : [];

    /// <summary>Books an entry for a tenant.</summary>
    /// <param name="tenantId">Whose books, or null.</param>
    /// <param name="entry">The entry.</param>
    public void Book(string? tenantId, LedgerEntry entry) =>
        _books.GetOrAdd(Key(tenantId), static _ => []).Add(entry);

    private static string Key(string? tenantId) => tenantId ?? string.Empty;
}

/// <summary>
/// A bank in memory, with a settable delay so a long-running night can be demonstrated.
/// </summary>
/// <remarks>
/// <strong>The delay is the whole reason this class is not a dictionary.</strong>
/// <c>OverlapPolicy</c> is a statement about a run that has not finished, so a sample that
/// cannot make a run take longer than its own interval cannot demonstrate the option at all —
/// and "make the external call slow" is where the time goes in the real system too.
/// </remarks>
internal sealed class InMemoryBank : IBank
{
    private readonly ConcurrentDictionary<string, List<StatementLine>> _accounts =
        new(StringComparer.Ordinal);

    /// <summary>How long the bank takes to answer. Zero by default.</summary>
    public TimeSpan Latency { get; set; }

    /// <inheritdoc />
    public async ValueTask<Result<IReadOnlyList<StatementLine>>> StatementAsync(
        string? tenantId, DateTimeOffset closedAt, CancellationToken cancellationToken)
    {
        if (Latency > TimeSpan.Zero)
        {
            await Task.Delay(Latency, cancellationToken).ConfigureAwait(false);
        }

        return _accounts.TryGetValue(Key(tenantId), out var lines)
            ? Result.Ok<IReadOnlyList<StatementLine>>(
                [.. lines.Where(line => line.ValuedAt < closedAt).OrderBy(line => line.ValuedAt)])
            : Result.Ok<IReadOnlyList<StatementLine>>([]);
    }

    /// <summary>Adds a line to a tenant's statement.</summary>
    /// <param name="tenantId">Whose account, or null.</param>
    /// <param name="line">The line.</param>
    public void Post(string? tenantId, StatementLine line) =>
        _accounts.GetOrAdd(Key(tenantId), static _ => []).Add(line);

    private static string Key(string? tenantId) => tenantId ?? string.Empty;
}

/// <summary>A report desk in memory.</summary>
internal sealed class InMemoryReportDesk : IReportDesk
{
    private readonly List<ReconciliationReport> _filed = [];
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public IReadOnlyList<ReconciliationReport> Filed
    {
        get
        {
            lock (_gate)
            {
                return [.. _filed];
            }
        }
    }

    /// <inheritdoc />
    public void File(ReconciliationReport report)
    {
        lock (_gate)
        {
            _filed.Add(report);
        }
    }
}

/// <summary>The errors this application's capabilities produce.</summary>
public static class ReconciliationErrors
{
    /// <summary>The bank could not be reached.</summary>
    /// <param name="reason">What the bank said.</param>
    /// <returns>The error.</returns>
    /// <remarks>
    /// <see cref="ErrorCategory.Unavailable"/> so that the retry on
    /// <see cref="Policies.ExternalRead"/> is allowed to insist: a bank that is down at 02:00 is
    /// usually up at 02:00:30, and the alternative is a night with no reconciliation at all.
    /// </remarks>
    public static Error BankUnreachable(string reason) =>
        new Error(
            "reconciliation.bank_unreachable",
            $"The bank did not answer: {reason}.",
            ErrorCategory.Unavailable)
            .With("reason", reason);
}
